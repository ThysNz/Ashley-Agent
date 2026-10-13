# Ashley - containerised AI agent

Ashley is a text and voice AI agent that runs inside a Docker container. She can read and write
files in a workspace folder, run shell commands, browse the web and keep long-term notes. The
language model runs **outside** the container on the host PC, so everything else moves with the
`F:\AI-Agents` folder to any machine that has Docker.

Contents: [Start everything](#start-everything) | [Architecture](#architecture) | [Folders](#folders) |
[Console commands](#console-commands) | [Tools](#tools) | [Configuration](#configuration) |
[Memory and files](#memory-and-files) | [Voice](#voice) | [HTTP API](#http-api) | [Safety](#safety-and-sandbox) |
[Troubleshooting](#troubleshooting) | [Moving to another PC](#moving-to-another-pc) | [Limits](#known-limits)

---

## Start everything

### Requirements (once per PC)
- **Docker Desktop**, running. Everything below needs it.
- **.NET 8 SDK or newer** - only for the voice client (`dotnet run`). The agent itself builds inside Docker.
- A **model server** on the host - see step 1.
- For voice: a working microphone and speakers.

### 1. Start a model (host, outside Docker)

| Model | How | Notes |
|---|---|---|
| **qwen3-coder-30b** (default, best at tools) | Run `F:\AI-Agents\host\start-qwen30b.bat` and **leave the window open** | Loads about 18 GB; wait until the console says the server is listening. Port 8080, 32k context. Log: `F:\Ai-Models\logs\llama-server.log` |
| swe-ut-14b, yld, qwen2.5-7b, llama3.2 | Start **Ollama** (`ollama serve`, or the tray app) | Port 11434. Smaller models are weaker at tools - the 7B makes mistakes on multi-step jobs |

Check it is up: open `http://127.0.0.1:8080/v1/models` (30B) or `http://127.0.0.1:11434/api/tags` (Ollama).

### 2a. Text chat in the console

Double-click or run:

```
F:\AI-Agents\ashley\ashley.cmd
```

This builds the image if needed (first time takes a few minutes and needs internet), starts a container
and opens the chat. Type a request. `/help` lists commands, **Ctrl+C stops a running task**, `/quit` exits.
The container is removed when you quit; your files and notes stay on disk.

### 2b. Voice

```
F:\AI-Agents\ashley\ashley-voice.cmd
```

This starts three containers (speech-to-text, text-to-speech, Ashley's API server) and then the voice client.
The client first shows which microphone it uses, then checks the three services. Press **Enter** to start recording,
speak, press **Enter** to stop. It reports the recording length and loudness (e.g. `Recorded 4.2s, loudest point 38% of full scale`)
and warns if the mic recorded silence. You can also type a message instead.
Ctrl+C stops a task; any key cuts off her speech; `quit` exits the client (the containers keep running).
Type `/help` for the voice and personality commands (see [Voice](#voice)).

The first start downloads the Whisper model (about 150 MB), so the speech-to-text service takes a minute
or two to answer its first health check; later starts are quick.

### 3. Stop everything

| What | How |
|---|---|
| Console chat | `/quit` |
| Voice client | `quit` |
| Voice containers + API server | `F:\AI-Agents\ashley\stop-voice.cmd` |
| Model server | Close the `start-qwen30b.bat` window (frees the GPU) |

> The API server container is set to `restart: unless-stopped`, so after a reboot it comes back up on its
> own when Docker starts. Run `stop-voice.cmd` if you do not want that.

### Quick status check

```
docker ps --format "table {{.Names}}\t{{.Status}}\t{{.Ports}}"
```

| Container | Meaning | Port (127.0.0.1 only) |
|---|---|---|
| `ashley-server-1` | Ashley's API (used by voice) | 5000 |
| `voice-stt-1` | Whisper speech-to-text | 9001 |
| `voice-tts-1` | Piper text-to-speech | 9002 |
| `ashley-ashley-run-...` | A console chat session | none |

Health URLs: `http://127.0.0.1:5000/health`, `:9001/health`, `:9002/health`.

### Typical order, start to finish
1. Docker Desktop running.
2. `start-qwen30b.bat` (wait for it to load).
3. `ashley.cmd` (text) **or** `ashley-voice.cmd` (voice).
4. When finished: quit, then `stop-voice.cmd` if you used voice, and close the model window.

---

## Architecture

```
                       HOST (Windows)                          DOCKER (Linux containers)
  +---------------------------------------+        +------------------------------------------------+
  |  llama-server + Qwen3-Coder-30B  :8080|<------>|  ai-agents/core  (Ashley)                      |
  |  Ollama (small models)          :11434|        |   agent loop, tools, memory, model clients      |
  |                                       |        |   mounts: config (ro) | workspace | memory       |
  |  VoiceClient  (mic + speakers)        |        |                                                |
  |     |    ^                            |        |  server  - HTTP API :5000 (voice mode)         |
  +-----|----|----------------------------+        |  stt     - Whisper (faster-whisper)    :9001   |
        |    |                                     |  tts     - Piper, voice en_US-hfc_female :9002 |
        v    |                                     +------------------------------------------------+
   stt -> agent /chat -> tts
```

- **One image for every agent** (`ai-agents/core`). An agent is just a folder with its own config, workspace and memory.
- **Models stay outside** so the container is small and portable, and the GPU is used directly. The container
  reaches the host at `host.docker.internal`.
- **Voice is split**: speech recognition and synthesis run in containers (portable); only microphone and
  speaker access needs the Windows client.

### The agent loop
1. Your message is added to the history.
2. The model is called with the system prompt (persona, environment, rules, notes) and the tool list.
3. If it asks for tools, they run, results go back to the model, repeat (up to `maxToolRounds`, default 80).
4. When it answers without tools, that answer is returned.

The history is trimmed by turns (`historyTurns`, default 12) and by size. The size limit learns characters-per-token
from the model server's own counts and shrinks old tool output in stages, so a long job cannot overflow the model's
context window. Interrupting (Ctrl+C) drops the half-finished turn so the model never sees a tool request without its result.

---

## Folders

```
F:\AI-Agents\
  README.md                  overview of the whole project
  Dockerfile                 the shared image (.NET SDK + git, python, node, ripgrep, build tools, sudo)
  src\AgentCore\             agent source (C#): loop, tools, memory, model clients, HTTP API, speech clean-up
  host\
    start-qwen30b.bat        starts the 30B model on the host
  voice\
    docker-compose.yml       speech containers (shared by all agents)
    stt\  tts\               service code; tts\models holds the Piper voice files
  clients\VoiceClient\       Windows mic + speaker client
  ashley\                    THIS AGENT
    README.md                this file
    ashley.cmd               console chat
    ashley-voice.cmd         voice mode (starts containers + client)
    stop-voice.cmd           stops the voice containers and API server
    docker-compose.yml       services: ashley (console), server (API, profile "server")
    config\                  agent.json, models.json, personas\*.md      (read-only inside the container)
    workspace\               the ONLY folder on the host she can change
    memory\                  notes.md, todo.md, state.json, sessions\, backups\
```

---

## Console commands

Anything not starting with `/` is sent to Ashley.

| Command | What it does |
|---|---|
| `/help` | list commands |
| `/models` | list models (`*` marks the current one) |
| `/model <name>` | switch model; the conversation is kept; the choice is saved in `memory\state.json` |
| `/personas`, `/persona <name>` | list / switch persona |
| `/tools` | list her tools |
| `/notes` | show her long-term notes |
| `/todo` | show her task list |
| `/reset` | forget this conversation (notes and files untouched) |
| `/status` | current model, URL, context size, last prompt size, folders |
| `/quit` | exit |
| `Ctrl+C` | stop the running task (while idle it exits) |

The prompt shows context use, e.g. `you [4413/32768]>` = tokens used / model window.

One-shot mode, no chat: `docker compose run --rm ashley --prompt "list the files"` (run in `ashley\`).

---

## Tools

| Tool | Purpose |
|---|---|
| `list_files`, `find_files`, `search_files` | look around; name patterns; regex search in file contents |
| `read_file` | read a text file (line-numbered, with start/limit for big files) |
| `write_file`, `append_file`, `edit_file` | create/replace, add to, or exact-text replace in a file |
| `delete_file`, `move_file` | delete (files backed up first) / move or rename |
| `run_command` | **bash shell inside the container**: run code, tests, git, install packages. Default timeout 120 s (max 900) |
| `web_fetch`, `web_search` | read a web page as text; search (DuckDuckGo page scrape - can break) |
| `remember`, `forget` | long-term notes about Thys, projects, preferences (not task logs) |
| `todo` | task list for multi-step work (add / done / undone / remove / clear / list) |

The file tools only work inside `workspace`; paths are relative to it. `run_command` is not limited by that - the
container is the sandbox (see [Safety](#safety-and-sandbox)).

---

## Configuration

All configuration is in `ashley\config\` and is mounted read-only; edit on the host and restart the chat
(for the API server also run `docker compose --profile server restart server` in `ashley\`).

### `agent.json`

| Key | Default | Meaning |
|---|---|---|
| `name` | Ashley | display name, used in prompts |
| `persona` | ashley | file in `personas\` (without `.md`) |
| `defaultModel` | qwen3-coder-30b | model used when none was chosen before |
| `maxToolRounds` | 80 | tool/model round trips per message before she stops and says so |
| `historyTurns` | 12 | past user turns kept |
| `commandTimeoutSeconds` | 120 | default for `run_command` |
| `maxNotesChars` | 8000 | most recent notes included in each prompt |
| `disabledTools` | [] | tool names to switch off, e.g. `["run_command","web_fetch"]` |

### `models.json`

A list of model profiles. Fields: `name`, `description`, `provider` (`openai` = llama-server / LM Studio,
`ollama` = native Ollama API), `baseUrl`, `model`, `contextSize`, `temperature`, `maxTokens`, `supportsTools`,
`think` (Ollama: false disables hidden reasoning), `apiKey`, `launch` (hint shown when the server cannot be reached).

Current profiles:

| Name | Provider | URL | Context |
|---|---|---|---|
| qwen3-coder-30b | openai | `http://host.docker.internal:8080/v1` | 32768 |
| swe-ut-14b | ollama | `http://host.docker.internal:11434` | 16384 |
| qwen2.5-7b | ollama | same | 16384 |
| llama3.2 | ollama | same | 8192 |
| yld | ollama | same | 16384 (`think` off) |

`contextSize` must not be larger than what the server runs with (the 30B bat uses `-c 32768`). To add a model,
copy a profile and change `name` / `model`. Ollama models get `num_ctx` on every request; for llama-server the
size is fixed by the bat file.

### Personas

Plain text files in `personas\` (written as the opening of the system prompt). Included: `ashley` (default),
`thys-mode` (terse engineering peer), `assistant`, `default`, `friendly`, `mentor`, `playful`, `professional`,
`robotic`. Add your own by dropping in a new `.md` file (it shows up in `/personas` immediately). Switch with `/persona <name>`
in the console or the voice client. The "be brief / verify before claiming success" rules
live in the code, not the persona, and apply to all personas.

### Which model/persona is used on start
`memory\state.json` stores the last `/model` and `/persona`. Delete it to fall back to `agent.json`. The voice API
server reads it only when it starts, so after changing model in the console restart the server.

---

## Memory and files

| Path (under `ashley\`) | What |
|---|---|
| `workspace\` | her working folder; everything she creates or edits goes here |
| `memory\notes.md` | long-term notes, plain text, read into every prompt (edit freely) |
| `memory\todo.md` | her task list |
| `memory\sessions\<date>.jsonl` | log of every message, tool call and result, written as it happens |
| `memory\backups\<time>\` | copy of any file before it is overwritten, edited or deleted (files only, not whole folders) |
| `memory\state.json` | last chosen model, persona and voice |

Back up by copying the `ashley` folder. Tell her "remember that ..." to add a note, or edit `notes.md` directly.

---

## Voice

Flow: microphone -> **VoiceClient** -> **stt** (Whisper `base.en`, CPU, about 0.6 s) -> Ashley (`/chat` with `voice: true`)
-> **tts** (Piper voice `en_US-hfc_female-medium`, about 0.3 s) -> speakers.

- In voice mode she answers in one to three plain spoken sentences. The server also strips markdown, code and URLs
  and caps the spoken text at about 600 characters. The full written reply is still printed.
- Client options (pass them to `ashley-voice.cmd`):

  | Option | Meaning |
  |---|---|
  | `--agent URL`, `--stt URL`, `--tts URL` | service addresses (defaults `http://127.0.0.1:5000`, `:9001`, `:9002`) |
  | `--voice-name NAME` | start with this voice instead of the remembered one |
  | `--list-devices` | list microphones and mark Windows' default, then exit |
  | `--test-mics` | record every input for 5 seconds and show how loud each was (speak while it runs); finds the live mic |
  | `--mic NUMBER_OR_NAME` | use this input instead of the Windows default, e.g. `--mic HyperX` |

  Example: `ashley\ashley-voice.cmd --mic HyperX`. This PC has two inputs: the HyperX Cloud Core headset (the Windows default, used and
  working in the live test) and a virtual Steam Streaming Microphone (not tested).

### Voices and personalities (voice client commands)

Type these at the voice client's `>` prompt instead of speaking. Changes are remembered in `memory\state.json`.

| Command | What it does |
|---|---|
| `/voices` | list installed voices (`*` = current) |
| `/voice <name>` | switch voice; a part of the name is enough (`/voice alba`). She says a sample sentence in the new voice |
| `/findvoice <words>` | search the public Piper catalog, e.g. `/findvoice en_GB`, `/findvoice amy`, `/findvoice de_DE`. With no words it lists English voices |
| `/addvoice <name>` | download a voice from the catalog (about 60 MB, checksum verified), then `/voice` it |
| `/personas` | list personalities (`*` = current) |
| `/persona <name>` | switch personality; the conversation continues with the new style |
| `/reset` | forget the conversation |
| `/help` | list these commands |

Installed now: `en_US-hfc_female-medium` (default), `en_GB-alba-medium`, `en_US-amy-medium`.

- **Voices** are Piper files (`<name>.onnx` + `<name>.onnx.json`) in `F:\AI-Agents\voice\tts\models`, so they travel with the folder.
  You can also copy in your own or downloaded voice files there; no restart needed. Browse all voices and samples at
  <https://rhasspy.github.io/piper-samples/>. Multi-speaker voices use their first speaker.
- **Personalities** are the `.md` files in `ashley\config\personas\`. To add one, create a file there (it appears in `/personas` straight away);
  no restart needed. See [Personas](#personas).
- Voice and personality are independent: any voice can be used with any personality.
- Whisper vocabulary hint (`WHISPER_PROMPT`, default "Thys. Ashley.") and model size (`WHISPER_MODEL`) are set in `voice\stt\server.py` / `voice\stt\Dockerfile`; rebuild with `docker compose up -d --build stt` in `voice\`.
- Voice and console are **separate conversations** (the API server has its own history) but share workspace, notes and `state.json`.

---

## HTTP API

Served by the `server` container on `127.0.0.1:5000` (never exposed to the network). One conversation, one request at a time.

| Request | Body | Response |
|---|---|---|
| `GET /health` | | `{"ok":true,"agent":"Ashley","model":"qwen3-coder-30b"}` |
| `POST /chat` | `{"text":"...", "voice":false}` | `{"reply":"...","spoken":"...","tools":["write_file x.py => Created..."]}` |
| `POST /reset` | | `{"ok":true}` |
| `GET /personas` | | `{"current":"ashley","personas":["ashley","mentor",...]}` |
| `POST /persona` | `{"name":"mentor"}` | `{"ok":true,"current":"mentor"}`; `404` if there is no such persona |
| `GET /voice` | | `{"voice":"en_US-hfc_female-medium"}` - the voice the client speaks with |
| `POST /voice` | `{"name":"en_GB-alba-medium"}` | `{"ok":true,"voice":"..."}` (the agent only stores the choice) |

Text-to-speech service (`127.0.0.1:9002`): `POST /tts {"text","voice"}` -> WAV, `GET /voices`, `GET /voices/catalog?q=...`,
`POST /voices/add {"name"}`.

Errors: `400` missing text, `502` model server problem (`{"error":...}`), `499` request cancelled. Closing the connection
cancels the running task.

Example:

```
curl -X POST http://127.0.0.1:5000/chat -H "Content-Type: application/json" -d "{\"text\":\"what files are in the workspace?\"}"
```

---

## Safety and sandbox

- The container sees only three host folders: `config` (read-only), `workspace` and `memory`. Nothing else on your
  drives is visible to her.
- `run_command` is a real shell with `sudo` and internet access **inside the container**. She can install packages,
  run builds and delete things there. `apt-get` installs last until the image is rebuilt; `pip`/`npm` user installs
  persist in the `ashley-home` volume.
- File tools refuse paths outside the workspace; overwritten or deleted files are copied to `memory\backups` first.
  A deleted **folder** is not backed up - ask her to be careful, or copy folders you care about.
- The API server publishes only on `127.0.0.1` because it controls a shell. Do not change it to `0.0.0.0` without adding authentication.
- She cannot reach the model server's host files, only its HTTP port.
- Treat web pages she fetches as untrusted text; she is told to follow your instructions, not the page's.

---

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| "Can't reach the model server at http://host.docker.internal:8080" | The 30B is not running or still loading. Run `start-qwen30b.bat` and wait. If the page `http://127.0.0.1:8080/v1/models` works but the container still cannot connect, change `--host 127.0.0.1` to `0.0.0.0` in the bat and allow it in Windows Firewall. |
| Same, for an Ollama model | Ollama is not running. Start it, then retry. Note: Ollama on this PC can be slow to answer the first request (model load). |
| `docker` commands fail / "Docker Desktop is unable to start" | Start Docker Desktop and wait until it says running. |
| Image build is slow / fails | First build downloads about 2 GB and needs internet. Re-run `ashley.cmd`. |
| Voice client says `DOWN stt` right after start | Whisper is still downloading its model. Wait a minute and re-run. Check `docker logs voice-stt-1`. |
| "Didn't catch that" | Nothing recorded or too quiet. Look at the line `Recorded Xs, loudest point N%` the client prints: under 1% means the wrong or muted microphone. Run `ashley-voice.cmd --test-mics` while speaking, then pick the live one with `--mic`. Also check Windows Settings > Sound > Input. |
| "The microphone recorded silence" | Same cause. Note a headset mic cannot hear the PC's own speakers, so a "play a sound and record it" test only works with open speakers and a desk mic. |
| She heard the wrong word | Add the word to `WHISPER_PROMPT`, or use a larger Whisper model (`WHISPER_MODEL=small.en`, rebuild `stt`). |
| Port already in use (5000, 9001, 9002) | Another program uses it; stop it or change the port mapping in the compose file. |
| She says "Stopped after N tool rounds" | Task needed more steps; say "continue", or raise `maxToolRounds`. |
| She claims something is done that is not | Smaller models do this. Use the 30B, ask her to show the result (`read the file back`), and check `memory\sessions\*.jsonl` for what actually ran. |
| Replies are slow | The 30B partly runs on CPU (`--n-cpu-moe 16`). Lower it in the bat if GPU memory allows; avoid other GPU-heavy apps (ComfyUI) at the same time. |
| `localhost` is slow, `127.0.0.1` fast (host browser/curl) | IPv6 fallback quirk on this PC; use `127.0.0.1`. Containers are not affected. |

Logs: `docker logs ashley-server-1`, `docker logs voice-stt-1`, `docker logs voice-tts-1`, and the model log
`F:\Ai-Models\logs\llama-server.log`.

---

## Moving to another PC

1. Install Docker Desktop (and the .NET 8+ SDK if you want voice).
2. Copy `F:\AI-Agents` (leave out `ashley\workspace` and `ashley\memory` for a fresh start, or keep them to take her memory along).
3. Provide a model there: edit `host\start-qwen30b.bat` (paths to `llama-server.exe` and the `.gguf`), or install Ollama and pull the
   models. If model names or context sizes differ, edit `ashley\config\models.json`. Nothing else is machine-specific.
4. Run `ashley.cmd`. The image and voice containers build themselves; the Whisper model downloads once.

On Linux/macOS hosts the `.cmd` and `.bat` files do not apply: run the `docker compose` commands in them directly, start
`llama-server` by hand, and run the voice client with `dotnet run` (microphone support depends on the OS).

## Adding another agent

Copy `ashley\` to a new folder next to it (for example `coder\`), change the service names and ports in its `docker-compose.yml`,
then edit its `config\` (name, persona, default model, `disabledTools`). It uses the same image and can share the voice containers
(give its API server a different port).

---

## Known limits

- The model server must be started by you; the container cannot start programs on the host.
- Voice: push-to-talk only (no wake word); no streaming speech while she is still thinking; replies are spoken only once complete.
- The voice client cannot switch models; use the console `/model`, then restart the API server.
- `web_search` scrapes a web page and can stop working; `web_fetch` on a known URL is the fallback.
- Small Ollama models are unreliable on multi-step tool work. Qwen3-Coder-30B is the tested default.
- **Parked: Unity / 3D character link and lip sync.** Not part of this version. If resumed, the old `AshleyLink` TCP hub
  (`127.0.0.1:6789`, JSON events: state, transcript, reply, amplitude) in `F:\Claude\Projects\Ashley\TalkToAshleyByVoice` is the starting
  point, and the voice client is where the audio level would be computed.

### What has been tested

| Area | Result |
|---|---|
| 40-file read-and-collect job across a full 32k context window | all 40 codes correct (about 10 minutes; prompts reached 25-27k of the 32k tokens without errors) |
| Context trimming with a deliberately tiny window | prompt stays near the limit; learns characters-per-token from the server |
| Honesty when a needed tool is disabled | she now says so instead of claiming success (a false claim was found and fixed during testing) |
| Ctrl+C mid-task | task stops, she stays alive, child process killed |
| `web_search`, `web_fetch` | work; page title now included |
| Voice: synthesised speech through the whole chain | works |
| Voice: **live microphone speech** (HyperX headset, 2026-10-13) | works: every question transcribed correctly, replies short and spoken, a file question used `list_files` correctly |
| Voice and personality switching, adding a voice from the catalog | works (voice installed and spoken; switching persona changed her style) |
| Models: 30B (full tests), `yld`, `qwen2.5-7b` (short tests) | 30B reliable; small models only on short tasks |

Not tested: very long web pages or binary downloads through `web_fetch`; models other than those above; running on a second PC.

---

## Status and history

Built and verified on 2026-10-13. In order: console agent with tools and memory -> context trimming and honesty fixes (found by the
long-job tests) -> HTTP API and voice containers -> voice client -> voice and personality switching -> microphone selection and diagnostics ->
live microphone test passed. Lip sync / Unity link is parked. Wake word and streaming speech are not started.
