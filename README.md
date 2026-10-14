# AI-Agents

Containerised AI agents. One Docker image (`ai-agents/core`) runs every agent; each
agent is a folder that supplies its own config, workspace and memory. Models run
**outside** the container, on the host.

```
F:\AI-Agents\
  Dockerfile              the shared image (Linux, .NET 8 + git/python/node/build tools)
  src\AgentCore\          the agent program (C#): loop, tools, memory, model clients
  voice\                  speech containers shared by all agents: stt (Whisper), tts (Piper)
  src\AgentCore\ui\       the web page (chat, model/persona/voice drop-downs, context meter, hold-to-talk)
  clients\VoiceClient\    Windows mic + speaker client (optional; the web page can do voice too)
  host\                   things that run on the Windows host, not in Docker
    start-qwen30b.bat       starts the 30B model (llama-server) on port 8080
  ashley\                 first agent
    ashley-ui.cmd           open the web page (starts everything it needs)
    ashley.cmd              console chat (builds the image if needed)
    docker-compose.yml
    config\                 agent.json, models.json, personas\*.md   (mounted read-only)
    workspace\              the ONLY host folder she can write to
    memory\                 notes.md, todo.md, sessions\, backups\, state.json
```

Full documentation for Ashley (start-up steps, configuration, tools, voice, API, troubleshooting):
[ashley\README.md](ashley/README.md).

## Run Ashley

1. Start Docker Desktop.
2. Start a model on the host:
   - the 30B: run `host\start-qwen30b.bat` (leave the window open), or
   - a smaller one: just have Ollama running (`ollama serve`).
3. Run **`ashley\ashley-ui.cmd`**. It starts the speech containers and Ashley's server, then opens the web page
   (`http://127.0.0.1:5000`). Hold the mic button or the space bar to talk, or type.

Prefer a console? Run `ashley\ashley.cmd` (or `docker compose run --rm --build ashley` in `ashley\`).

Console commands: `/help`, `/models`, `/model <name>`, `/personas`, `/persona <name>`, `/tools`,
`/notes`, `/todo`, `/reset`, `/status`, `/quit`. Ctrl+C stops a running task.
Switching model keeps the conversation; the choice is remembered in `memory\state.json`.

## Voice

Speech runs in containers too, so it moves with the folder. The web page uses the browser's microphone and
speakers. The Windows client below is the older alternative.

```
mic -> VoiceClient -> stt container (Whisper) -> agent container (/chat) -> tts container (Piper) -> speaker
```

Start a model, then run `ashley\ashley-voice.cmd`. It starts the containers and the client.
Press Enter to start recording and Enter to stop (or type a message instead). Ctrl+C stops a
running task; any key cuts off speech. `ashley\stop-voice.cmd` stops the voice containers.

- Ports (all on 127.0.0.1 only): agent API 5000, stt 9001, tts 9002. The agent has a shell, so the
  API is never published to the network.
- In voice mode the agent answers in 1-3 plain sentences, and the server also strips markdown and
  caps the spoken text (`spoken` in the `/chat` reply). The full text is still printed.
- The first start downloads the Whisper `base.en` model (~150 MB, kept in a volume). The Piper voice
  (`voice\tts\models`) is bundled. In the voice client, `/findvoice`, `/addvoice`, `/voice` and `/persona`
  search, download and switch voices and personalities; `--list-devices`, `--test-mics` and `--mic` pick the
  microphone. Details: [ashley\README.md](ashley/README.md#voice-client-windows).
- Whisper runs on CPU (about 0.6 s per sentence). Names it should expect are in `WHISPER_PROMPT`.

## What she can do

File tools (confined to `workspace`: list, read, write, append, edit, delete, move, find,
search), `run_command` (a real bash shell in the container, sudo allowed, internet on),
`web_fetch`, `web_search`, `remember`/`forget` (long-term notes read into every session),
and a `todo` list. Overwritten/deleted files are copied to `memory\backups\` first.
Every message is logged to `memory\sessions\<date>.jsonl`.

## Moving to another PC

Copy `F:\AI-Agents` (without `workspace`/`memory` if you want a fresh start), install Docker,
and run `ashley.cmd`. Only the model side is machine-specific: edit `ashley\config\models.json`
(model names, context size) and `host\start-qwen30b.bat` (paths to llama-server and the GGUF).

## Adding another agent

Copy `ashley\` to a new folder (e.g. `coder\`), rename the service in its compose file,
change `config\agent.json` / persona / models. It reuses the same image.

## Notes

- The container reaches host models at `host.docker.internal`. The bat binds llama-server to
  127.0.0.1, which Docker Desktop forwards to. If the container can't connect, change `--host`
  to `0.0.0.0` in the bat and allow it in the Windows firewall.
- `contextSize` in `models.json` should match what the server was started with
  (the 30B bat uses `-c 32768`). Ollama models get `num_ctx` sent with every request.
- Sandbox: the container sees only `config` (read-only), `workspace` and `memory`.
  Packages installed with `sudo apt-get` last until the image is rebuilt; pip/npm user installs
  persist (named volume `ashley-home`).
- `web_search` scrapes DuckDuckGo's HTML page and can break; `web_fetch` on a known URL is the fallback.
- Parked: Unity link / lip sync. Not started: wake word, streaming replies, speaking while she is still thinking.
