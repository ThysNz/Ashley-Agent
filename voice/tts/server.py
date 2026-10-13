# Text-to-speech service (Piper).
#
#   POST /tts                {"text": "...", "voice": "optional"}  -> audio/wav
#   GET  /voices             installed voices
#   GET  /voices/catalog     search the public Piper catalog   ?q=amy  (matches the voice name, e.g. en_GB, amy)
#   POST /voices/add         {"name": "en_GB-alba-medium"}  -> downloads the voice into /models
#   GET  /health
#
# Voices live in /models as <name>.onnx + <name>.onnx.json (mounted from ./models, so they
# persist and travel with the folder). You can also drop your own voice files in there.
import hashlib
import io
import json
import os
import re
import urllib.request
import wave

from fastapi import FastAPI
from fastapi.responses import JSONResponse, Response
from piper import PiperVoice
from pydantic import BaseModel

MODELS = "/models"
DEFAULT_VOICE = os.environ.get("VOICE", "en_US-hfc_female-medium")
CATALOG_BASE = "https://huggingface.co/rhasspy/piper-voices/resolve/main/"
NAME_RE = re.compile(r"^[A-Za-z0-9_-]{1,80}$")  # also keeps requests from escaping /models

_voices = {}
_catalog = None


def installed():
    return sorted(
        f[:-5] for f in os.listdir(MODELS)
        if f.endswith(".onnx") and os.path.exists(os.path.join(MODELS, f + ".json"))
    )


def get_voice(name):
    if name not in _voices:
        _voices[name] = PiperVoice.load(os.path.join(MODELS, name + ".onnx"))
    return _voices[name]


def fetch(url, timeout=600):
    req = urllib.request.Request(url, headers={"User-Agent": "ai-agents-tts"})
    with urllib.request.urlopen(req, timeout=timeout) as response:
        return response.read()


def catalog():
    global _catalog
    if _catalog is None:
        _catalog = json.loads(fetch(CATALOG_BASE + "voices.json", timeout=60))
    return _catalog


get_voice(DEFAULT_VOICE)  # fail at startup, not on the first sentence
print(f"Piper ready, default voice {DEFAULT_VOICE}, installed: {installed()}", flush=True)

app = FastAPI()


class SpeakRequest(BaseModel):
    text: str
    voice: str | None = None


class AddRequest(BaseModel):
    name: str


@app.get("/health")
def health():
    return {"ok": True, "default": DEFAULT_VOICE, "voices": installed()}


@app.get("/voices")
def voices():
    return {"default": DEFAULT_VOICE, "voices": installed()}


@app.post("/tts")
def tts(req: SpeakRequest):
    name = req.voice or DEFAULT_VOICE
    if not NAME_RE.match(name) or name not in installed():
        return JSONResponse({"error": f"voice '{name}' is not installed"}, status_code=404)
    buf = io.BytesIO()
    with wave.open(buf, "wb") as wav:
        get_voice(name).synthesize_wav(req.text, wav)
    return Response(buf.getvalue(), media_type="audio/wav")


@app.get("/voices/catalog")
def voice_catalog(q: str = "", limit: int = 40):
    try:
        entries = catalog()
    except Exception as ex:  # no internet, Hugging Face down...
        return JSONResponse({"error": f"couldn't load the Piper voice catalog: {ex}"}, status_code=502)

    have = set(installed())
    words = q.lower().split()
    found = []
    for key, info in sorted(entries.items()):
        if words and not all(w in key.lower() for w in words):
            continue
        if not words and info["language"]["family"] != "en":
            continue  # with no search, show English voices only
        found.append({
            "name": key,
            "language": info["language"].get("name_english", "") + " (" + info["language"].get("country_english", "") + ")",
            "quality": info["quality"],
            "speakers": info["num_speakers"],
            "installed": key in have,
        })
    return {"total": len(found), "voices": found[:limit]}


@app.post("/voices/add")
def add_voice(req: AddRequest):
    name = req.name.strip()
    if not NAME_RE.match(name):
        return JSONResponse({"error": "invalid voice name"}, status_code=400)
    if name in installed():
        return {"ok": True, "name": name, "note": "already installed"}
    try:
        info = catalog().get(name)
    except Exception as ex:
        return JSONResponse({"error": f"couldn't load the Piper voice catalog: {ex}"}, status_code=502)
    if info is None:
        return JSONResponse({"error": f"'{name}' is not in the Piper catalog - try /voices/catalog?q=..."}, status_code=404)

    # Download the model and its config; only names from the catalog are ever fetched.
    wanted = {path: meta for path, meta in info["files"].items()
              if path.endswith(".onnx") or path.endswith(".onnx.json")}
    saved = []
    try:
        for path, meta in wanted.items():
            data = fetch(CATALOG_BASE + path)
            digest = meta.get("md5_digest")
            if digest and hashlib.md5(data).hexdigest() != digest:
                raise RuntimeError(f"download of {os.path.basename(path)} was corrupted")
            target = os.path.join(MODELS, os.path.basename(path))
            with open(target + ".part", "wb") as f:
                f.write(data)
            os.replace(target + ".part", target)
            saved.append(target)
    except Exception as ex:
        for target in saved:
            os.remove(target)
        return JSONResponse({"error": f"download failed: {ex}"}, status_code=502)

    get_voice(name)  # make sure it actually loads
    return {"ok": True, "name": name}
