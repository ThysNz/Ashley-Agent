# Ashley - containerised AI agent

Ashley is a text and voice AI agent that runs inside a Docker container. She can read and write
files in a workspace folder, run shell commands, browse the web and keep long-term notes. The
language model runs **outside** the container on the host PC, so everything else moves with the
`F:\AI-Agents` folder to any machine that has Docker. You talk to her in a **web page** (recommended),
a console, or the Windows voice client.

Contents: [Start everything](#start-everything) | [Architecture](#architecture) | [Folders](#folders) |
[Web page](#web-page) | [Console commands](#console-commands) | [Tools](#tools) | [Configuration](#configuration) |
[Memory and files](#memory-and-files) | [Voice client](#voice-client-windows) | [HTTP API](#http-api) | [Safety](#safety-and-sandbox) |
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

### 2b. Web page (recommended)

```
F:\AI-Agents\ashley\ashley-ui.cmd
```

This starts the speech containers and Ashley's server, waits until it is ready, and opens the page
(`http://127.0.0.1:5000`; in an app-style Edge window if Edge is installed). The page has the chat, model / persona / voice
drop-downs, a context meter, hold-to-talk voice and side panels. See [Web page](#web-page). You can also just open
`http://127.0.0.1:5000` in any browser once the server container is running.

### 2c. Voice client (Windows)

```
F:\AI-Agents\ashley\ashley-voice.cmd
```

This is the older console-style voice client. The web page can do the same job in the browser, without the .NET SDK.

This starts three containers (speech-to-text, text-to-speech, Ashley's API server) and then the voice client.
The client first shows which microphone it uses, then checks the three services. Press **Enter** to start recording,
speak, press **Enter** to stop. It reports the recording length and loudness (e.g. `Recorded 4.2s, loudest point 38% of full scale`)
and warns if the mic recorded silence. You can also type a message instead.
Ctrl+C stops a task; any key cuts off her speech; `quit` exits the client (the containers keep running).
Type `/help` for the voice and personality commands (see [Voice client](#voice-client-windows)).

The first start downloads the Whisper model (about 150 MB), so the speech-to-text service takes a minute
or two to answer its first health check; later starts are quick.

### 3. Stop everything

| What | How |
|---|---|
| Console chat | `/quit` |
| Web page | close the tab (the server keeps running; your chat is saved) |
| Voice client | `quit` |
| Voice containers + API server (what the web page uses) | `F:\AI-Agents\ashley\stop-voice.cmd` |
| Model server | Close the `start-qwen30b.bat` window (frees the GPU) |

> The API server container is set to `restart: unless-stopped`, so after a reboot it comes back up on its
> own when Docker starts. Run `stop-voice.cmd` if you do not want that.

### Quick status check

```
docker ps --format "table {{.Names}}\t{{.Status}}\t{{.Ports}}"
```

| Container | Meaning | Port (127.0.0.1 only) |
|---|---|---|
| `ashley-server-1` | Ashley's web page and API | 5000 |
| `voice-stt-1` | Whisper speech-to-text | 9001 |
| `voice-tts-1` | Piper text-to-speech | 9002 |
| `ashley-ashley-run-...` | A console chat session | none |

Health URLs: `http://127.0.0.1:5000/health`, `:9001/health`, `:9002/health`.

### Typical order, start to finish
1. Docker Desktop running.
2. `start-qwen30b.bat` (wait for it to load).
3. `ashley-ui.cmd` (web page, text and voice) **or** `ashley.cmd` (console) **or** `ashley-voice.cmd` (Windows voice client).
4. When finished: close the page or quit, then `stop-voice.cmd` if you used the page or voice, and close the model window.

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
  src\AgentCore\ui\          the web page: index.html, app.css, app.js (no libraries, no build step, works offline)
  host\
    start-qwen30b.bat        starts the 30B model on the host
  voice\
    docker-compose.yml       speech containers (shared by all agents)
    stt\  tts\               service code; tts\models holds the Piper voice files
  clients\VoiceClient\       Windows mic + speaker client
  ashley\                    THIS AGENT
    README.md                this file
    ashley.cmd               console chat
    ashley-ui.cmd            web page (starts containers, opens the page)
    ashley-voice.cmd         Windows voice client (starts containers + client)
    stop-voice.cmd           stops the voice containers and API server
    docker-compose.yml       services: ashley (console), server (API, profile "server")
    config\                  agent.json, models.json, personas\*.md      (read-only inside the container)
    workspace\               the ONLY folder on the host she can change
    memory\                  notes.md, todo.md, state.json, sessions\, backups\, chats\, models.local.json
    secrets\                 optional github_read_token (read-only, mounted read-only, never committed); see its README.md
  docker\git\                git helpers baked into the image: read-only token helper and the push guard
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
| `gitName`, `gitEmail` | Ashley, ashley@ai-agents.local | who her git commits are made as (see [Git](#git-she-commits-you-push)) |

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
| `memory\chats\` | the web page's conversations: `current.json` (survives restarts) and one file per saved chat |
| `memory\models.local.json` | models added from the web page (the ones in `config\models.json` are not touched) |

Back up by copying the `ashley` folder. Tell her "remember that ..." to add a note, or edit `notes.md` directly.

---

## Web page

Open it with `ashley-ui.cmd`, or go to `http://127.0.0.1:5000` while the server container is running. It is served by Ashley's own
container (plain HTML, CSS and JavaScript, no libraries and no internet needed), so it moves with the folder. It follows the browser's
light or dark setting.

### What is on the screen

| Area | What it does |
|---|---|
| **Top bar** | Drop-downs for **model**, **persona** and **voice**. The coloured dot is green when the model server is reachable. Buttons: saved chats, new chat, inspect, manage models, show/hide side panel |
| **Banner** | Appears if the model server (or the agent itself) can't be reached, with the command to start it |
| **Context meter** | Tokens used out of the model's window, with the share taken by system prompt, tools, notes and chat. Turns amber at 70% and red at 90%. Updates live while she works |
| **Chat** | Your messages and hers. While she works you see each tool call appear live (`write_file`, `run_command`, ...) as a collapsible row with arguments, result, status and time. Replies are formatted (code blocks with Copy). Under each reply: speak it again, copy it, time taken, number of tool calls |
| **Message box** | Type and press Enter (Shift+Enter for a new line). The send button becomes **Stop** while she works |
| **Side panel** | Her todo list, her notes, and the workspace file browser (click a file to read it). Updates as she works |

### Talking to her (hold to talk)

- **Hold the mic button, or hold the space bar** (the space bar works whenever the message box is empty, including right after a reply, when the cursor is back in the box; once you have typed something, a space is just a space), speak, and let go. A red level bar and a timer show that the
  microphone is hearing you. The page then transcribes what you said and sends it. Tick or untick **Send after talking** to choose between sending
  straight away and putting the text in the box so you can fix it first.
- **Esc** while recording cancels the recording; **Esc** while she is speaking cuts her off. Pressing the mic starts a new recording and stops any speech.
- Very short taps (under 0.4 s) are ignored. If the microphone records silence you see `No sound heard from "<device>" (level N%)`.
- When you press, the button dims for a moment while the microphone opens, then turns **red** with the level bar. Start speaking when it is red.
  Below the message box the page shows which microphone it used and how long and how loud your last recording was
  (e.g. `Mic: HyperX Cloud Core · last recording 3.4 s, level 62%`), which makes a silent or wrong microphone easy to spot.
- A safety check ends any recording whose release was missed (lost focus, odd key handling), and a recording is also cancelled if you
  switch to another window or tab. A recording stops by itself after two minutes.
- **Speak replies** (on by default): she reads each reply aloud in the chosen voice and answers in one to three plain sentences. Turn it off for normal,
  longer written answers.
- The first time, the browser asks permission to use the microphone. The page opens the microphone fresh for each recording and releases it
  when you let go, so the browser's "microphone in use" marker only shows while you hold the button.
- It only works at `http://127.0.0.1:5000` or `http://localhost:5000` (browsers allow the microphone only on secure or local addresses).
- The microphone and speaker are the browser's: pick them in the browser's site settings (or Windows sound settings). The `--mic` options of
  the Windows voice client do not apply here.

### Models, voices, chats

- **Model drop-down:** pick any model; the conversation continues and the meter's window size changes to match. `(offline)` means that model's
  server is not running. **Manage models...** (or the chip button) opens a list showing which servers are reachable, with **Use** buttons, and a
  second list of models that are installed in Ollama but not in your list, with an **Add** button. Added models are saved in `memory\models.local.json`;
  edit `config\models.json` on disk for anything else. The page cannot start the 30B for you (containers cannot start programs on the host).
- **Persona drop-down:** switches personality immediately; the conversation continues.
- **Voice drop-down:** switches voice and plays a sample. **Add a voice...** opens the catalog search and download (about 60 MB each).
- **Saved chats:** the current chat is kept on disk and comes back after a restart or reload (the last 12 turns, as in the console). **New chat** saves
  the current one to the list and starts empty; the clock button lists saved chats and reopens them.
- **Inspect** (eye button): shows exactly what is sent to the model: the system prompt, the tools offered, and the conversation as the model sees it.
  Useful for working out why she behaved a certain way.

### Notes
- Her reply appears when she finishes the step; the model is not streamed word by word, but every tool call and the live token count are.
- Only one task runs at a time. Selecting a model, persona or voice is disabled while she is working.
- The page and API only answer to `localhost` and `127.0.0.1` (other host names are refused), because she has a shell.

---

## Voice client (Windows)

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

The same server serves the web page at `/` and these endpoints (JSON unless noted):

| Request | Body | Response |
|---|---|---|
| `GET /health` | | `{"ok":true,"agent":"Ashley","model":"qwen3-coder-30b"}` |
| `GET /status` | | model, persona, voice, `reachable`, `busy`, and `context` (window, used, per-part token counts) |
| `POST /chat` | `{"text":"...", "voice":false}` | `{"reply":"...","spoken":"...","tools":["write_file x.py => Created..."]}` |
| `POST /chat/stream` | same | newline-delimited JSON events as she works: `thinking`, `usage`, `text`, `tool_start`, `tool_end`, then `final` (or `error` / `stopped`). Closing the connection stops the task |
| `GET /history` | | the current conversation as turns (user text, her tool calls with results, her reply) |
| `GET /inspect` | | the system prompt, tool names, the messages sent to the model, and the context numbers |
| `POST /reset`, `POST /chats/new` | | saves the current chat to the archive and starts a new one |
| `GET /chats` | | saved chats (id, title, time) |
| `POST /chats/open` | `{"id":"20261014-093000"}` | saves the current chat and opens that one |
| `GET /models` | | every model with `reachable`, plus `discovered` Ollama models that have no profile |
| `POST /model` | `{"name":"yld"}` | switches model (conversation kept) |
| `POST /models/add`, `POST /models/remove` | `{"model":"ashrp:latest","name":"ashrp",...}` / `{"name":"ashrp"}` | adds or removes a model saved in `memory\models.local.json` |
| `GET /personas` | | `{"current":"ashley","personas":["ashley","mentor",...]}` |
| `POST /persona` | `{"name":"mentor"}` | `{"ok":true,"current":"mentor"}`; `404` if there is no such persona |
| `GET /voice` | | `{"voice":"en_US-hfc_female-medium"}` - the voice used for speaking |
| `POST /voice` | `{"name":"en_GB-alba-medium"}` | `{"ok":true,"voice":"..."}` (the agent only stores the choice) |
| `GET /notes`, `GET /todo` | | her notes / todo items |
| `GET /files?path=`, `GET /file?path=` | | a workspace folder listing / a text file (first 200 KB); paths stay inside the workspace |
| `POST /stt`, `POST /tts`, `GET /voices`, `GET /voices/catalog?q=`, `POST /voices/add` | as the speech service | forwarded to the speech containers so the page talks to one address |

The speech services can also be called directly: text-to-speech on `127.0.0.1:9002` (`POST /tts {"text","voice"}` -> WAV,
`GET /voices`, `GET /voices/catalog?q=...`, `POST /voices/add {"name"}`) and speech-to-text on `127.0.0.1:9001` (`POST /stt` with an
audio body -> `{"text"}`).

Errors: `400` missing text, `502` model server problem (`{"error":...}`), `499` request cancelled. Closing the connection
cancels the running task.

Example:

```
curl -X POST http://127.0.0.1:5000/chat -H "Content-Type: application/json" -d "{\"text\":\"what files are in the workspace?\"}"
```

---

## Git (she commits, you push)

She can use git on the projects in her workspace. The arrangement: **she commits locally, you push from your own PC**, and she can pull
from your private repos with a read-only token.

| What | How it works |
|---|---|
| **Commit** | Ask her ("commit these changes"). She runs `git status` and `git diff --stat`, tells you what changed, then commits. She commits **only when asked**, one clear message per change |
| **Who the commits are from** | `gitName` / `gitEmail` in `config\agent.json` (default `Ashley <ashley@ai-agents.local>`), so her work is easy to tell apart in the history. Change them to your own name if you prefer |
| **Pull / clone** | Public repos work as they are. For **private** repos create a read-only token and save it as `ashley\secrets\github_read_token` (steps in `ashley\secrets\README.md`). It is used only for `github.com`, and only for reading |
| **Push** | **Turned off** inside the container. She tells you which repo folder and branch to push; you run `git push` from your own PC, in `F:\AI-Agents\ashley\workspace\<repo>` |
| **Repos made on your PC** | Work as they are (the container accepts the workspace folders, which it otherwise reports as "dubious ownership") |

How the push block works, honestly:
- A pre-push hook refuses `git push` and says why. It is a **guard rail against accidents**, not a lock: she has a shell and could bypass it
  (for example with `--no-verify`).
- The real protection is that **the container holds no credential that can write to GitHub**: the only token it can see is read-only,
  and your own GitHub login stays on your PC.
- She cannot reach the `F:\AI-Agents` repository itself, only the workspace. This is for the projects she works on.
- Her rules also say: never push, never force anything or rewrite history, never commit secrets.

To give her read access: create the token as described, drop the file in `ashley\secrets\`, and she can pull straight away (no restart). To remove it,
delete the file and revoke the token on GitHub. If a token expires, pulls from **public** repos also fail until it is replaced or deleted,
because GitHub rejects an invalid token.

---

## Safety and sandbox

- The container sees only these host folders: `config` and `secrets` (both read-only), `workspace` and `memory`. Nothing else on your
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
| Web page shows "Can't reach the agent" | The server container is not running. Run `ashley-ui.cmd` (or `docker compose --profile server up -d server` in `ashley\`). |
| Web page shows "Can't reach the model ..." | The model server is not running. Start `start-qwen30b.bat` (or Ollama), or pick another model in the drop-down. The dot beside the name is amber until it answers. |
| Web page: "Microphone access was blocked" | Allow the microphone for `127.0.0.1:5000` in the browser's site settings (lock icon in the address bar), then hold the button again. It also fails if the page is opened with another address than `127.0.0.1` or `localhost`. |
| Web page: "No sound heard from ..." | Wrong input chosen in Windows or the browser, or muted. Check the browser's site settings and Windows Settings > Sound > Input. |
| Web page loads but is blank or an old version | Hard-refresh (Ctrl+F5). After changing the page's files, rebuild with `docker compose build` and restart the server container. |
| A reply was not spoken | Check that **Speak replies** is ticked and the speech containers are running (`voice-tts-1`). The page shows the reason in a message above the box. Browsers may block sound until you have clicked once in the page. |
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
- Voice: hold-to-talk or push-to-talk only (no wake word); no streaming speech while she is still thinking; replies are spoken only once complete.
- Replies are not shown word by word (tool calls and the token count are live). Streaming the model's words is a possible next step.
- The Windows voice client cannot switch models; the web page can.
- The web page's chat keeps the last 12 turns (the same limit as the console); older turns are only in the session log.
- The web page cannot start or stop the 30B, because containers cannot run programs on the host. A small host-side helper would be needed.
- One conversation at a time per server. Several browser tabs share the same conversation.
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
| **Web page** (2026-10-14, browser pane): load, chat with live tool rows and meter, model switch (meter window changes), models manager with Ollama discovery, voices dialog with catalog search, saved chats (new, list, reopen, survives a server restart), inspect, Stop mid-command (process killed), history restored on reload | works |
| Web page: audio path with a browser-format (WebM/Opus) recording through `/stt`, and `/tts` through the proxy | works (sentence transcribed correctly) |
| Web page: blocked-microphone message from both the mic button and the space bar | works; recording indicator resets |
| Web page: three recordings in a row against a **simulated microphone playing real speech** (space bar, space bar again with the cursor back in the empty box, then the mouse button) | works: red level bar while holding, cleared on release, mic released each time, transcribed, sent and answered |
| Web page: space bar after a reply (found when you tried it) | fixed: the cursor returns to the message box after each reply, and the space bar now also records when the box is empty |
| Host-header check (`Host: evil.example.com`) | refused with 400 |
| **Git** (2026-10-14): she created a repo and committed on request (author `Ashley <ashley@ai-agents.local>`); asked to push she checked and declined | works |
| Git: read-only token helper (dummy token) | offers the token for `github.com` only, nothing for other hosts; the secrets folder is read-only |
| Git: repo created on the PC used inside the container; push guard | works; `git push` refused, `--no-verify` bypasses the hook (as documented) |

Not tested: pulling a **private** repo with a real read-only token (needs your token), pushing from your PC, **actual microphone recording in the browser** (the development browser blocks the microphone; try it with real speech and tell me how it behaves),
the Microsoft Edge app-window launch from `ashley-ui.cmd`, very long web pages or binary downloads through `web_fetch`, models other than those above,
running on a second PC.

---

## Status and history

Built and verified on 2026-10-13. In order: console agent with tools and memory -> context trimming and honesty fixes (found by the
long-job tests) -> HTTP API and voice containers -> voice client -> voice and personality switching -> microphone selection and diagnostics ->
live microphone test passed -> web front end (2026-10-14): live events, context meter, model / persona / voice drop-downs, saved chats, inspect,
side panels, hold-to-talk in the browser. Lip sync / Unity link is parked. Wake word and word-by-word streaming are not started.
