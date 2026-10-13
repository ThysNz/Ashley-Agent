# Speech-to-text service: POST a WAV (any common format) to /stt, get {"text": "..."}.
# faster-whisper on CPU. The model is downloaded on first start into /models
# (a named volume), so later starts are fast and need no internet.
import io
import os

from fastapi import FastAPI, Request
from faster_whisper import WhisperModel

MODEL = os.environ.get("WHISPER_MODEL", "base.en")
DEVICE = os.environ.get("WHISPER_DEVICE", "cpu")
COMPUTE = os.environ.get("WHISPER_COMPUTE", "int8")
# Names Whisper should expect (it otherwise hears "Thys" as "guys").
PROMPT = os.environ.get("WHISPER_PROMPT", "Thys. Ashley.")

print(f"Loading Whisper model {MODEL} ({DEVICE}, {COMPUTE})...", flush=True)
model = WhisperModel(MODEL, device=DEVICE, compute_type=COMPUTE, download_root="/models")
print("Whisper ready.", flush=True)

app = FastAPI()


@app.get("/health")
def health():
    return {"ok": True, "model": MODEL}


@app.post("/stt")
async def stt(request: Request):
    data = await request.body()
    if len(data) < 1000:  # nothing recorded
        return {"text": ""}
    segments, _ = model.transcribe(io.BytesIO(data), language="en", beam_size=1, vad_filter=True,
                                  initial_prompt=PROMPT or None)
    return {"text": " ".join(s.text.strip() for s in segments).strip()}
