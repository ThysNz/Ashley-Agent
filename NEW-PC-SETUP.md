# Setting up AI-Agents on a new PC

This sets up the repo on another computer and starts one agent there (the examples use `agent2`; any agent folder works the same way).
The agents run in Docker, so the only machine-specific part is the model, which runs outside Docker on that PC.

## What comes with the repo, and what does not

| Comes with the repo | Does **not** come over (set up again on the new PC) |
|---|---|
| All the code and the shared Docker setup | Model files: the 30B GGUF, Ollama models |
| `ashley\`, `agent2\`, `agent3\` (settings, personas, launchers, their own `skills\`) | Each agent's **memory, notes, chats and workspace** |
| `skills\` (the skills shared by every agent) | |
| The speech containers, with the default voice | A GitHub token (make a new one per machine) |
| `new-agent.cmd` to create more agents | The Whisper model (downloads on first start) and extra voices (re-add from the page) |

## 1. Install the prerequisites (once)

| Needed | Notes |
|---|---|
| **Docker Desktop** | WSL2 backend. Start it and wait until it says running. Virtualization must be on in the BIOS |
| **Git for Windows** | to clone and update the repo |
| A browser | Edge or Chrome |
| A model server | simplest: **Ollama** (ollama.com). Alternative: `llama-server` and a `.gguf` file |
| .NET 8 SDK | **only** if you want the old Windows voice client; the web page does not need it |

## 2. Get the repo

```powershell
git clone https://github.com/ThysNz/Ashley-Agent.git D:\AI-Agents
```

Any folder works; nothing depends on the drive letter. If the repo is private, Git asks you to sign in to GitHub the first time.

## 3. Put a model on this PC

```powershell
ollama pull qwen2.5:7b
```

Choose a model that fits this PC's graphics card; you can pull several. Ollama listens on its default address (`127.0.0.1:11434`), which the
containers reach without any change.

For the 30B instead: copy the `.gguf` and `llama-server.exe` over, then edit the paths and context size in `host\start-qwen30b.bat`
(it points at `F:\Ai-Models`) and add a profile to the agent's `config\models.json` (copy it from `ashley\config\models.json`).

## 4. Build the image once

```powershell
cd D:\AI-Agents\agent2
docker compose build
```

This builds the shared image (about 2 GB, a few minutes, needs internet). Doing it explicitly avoids a slow, silent first launch.

## 5. Start the agent

```powershell
D:\AI-Agents\agent2\agent2-ui.cmd
```

It builds the speech containers (the Whisper model, about 150 MB, downloads on the first run), starts the agent's server, waits until it is ready,
and opens its page: `http://127.0.0.1:5001` for agent2 (5002 for agent3, 5000 for ashley).

## 6. Give it a model

1. The page shows **"No model is set up for this agent yet."**
2. Click the chip button (Manage models). Under "Installed in Ollama, not in your list", click **Add** beside the model you pulled.
3. The agent switches to it at once and the banner clears. Send a message to check.

(Or edit `agent2\config\models.json` and restart the agent. The page's Add button saves into the agent's `memory\` folder instead.)

## 7. Optional, for this machine

- **Name and persona:** edit `config\personas\agent2.md`, and `name` / `gitName` in `config\agent.json`.
- **Time zone:** the compose files use `Pacific/Auckland`; change it in `docker-compose.yml` if the PC is elsewhere.
- **Microphone:** the browser asks permission once for `127.0.0.1:5001`. Allow it.
- **Voices:** extra voices come from the page's "Add a voice...".
- **Private git repos:** run `agent2\set-github-token.cmd` with a **new read-only** fine-grained token made for this machine (steps in `agent2\secrets\README.md`).
  Do not copy the token file from another PC.
- **Skills:** the shared skills come with the repo (`skills\`). Add your own with `add-skill.cmd C:\path\to\skill` (add `-Agent agent2` for just that agent).
- **Projects:** clone them into `agent2\workspace\` (or tell the agent to). She commits there; you push from your own PC.
- **Keeping an existing agent's memory:** copy that agent's `memory\` and `workspace\` folders across by hand (they are not in git).

## Check it worked

- `docker ps` lists `agent2-server-1`, `voice-stt-1` and `voice-tts-1`.
- `http://127.0.0.1:5001/health` answers.
- The dot beside the agent's name is green once a model is added, and a message gets a reply.

## Updating that PC later

```powershell
cd D:\AI-Agents
git pull
cd agent2
docker compose build
docker compose --profile server up -d --force-recreate server
```

`git pull` alone is not enough: the page and the program live inside the Docker image, so the image has to be rebuilt and the server recreated.

## Stopping

- `agent2\agent2-stop.cmd` stops that agent's server.
- `ashley\stop-voice.cmd` stops the speech containers, which every agent shares.
- Close the Ollama or `llama-server` window to free the graphics card.

## If something goes wrong

| Problem | Fix |
|---|---|
| Docker commands fail, or "Docker Desktop is unable to start" | Start Docker Desktop and wait; check that virtualization is enabled in the BIOS and WSL2 is installed |
| The first build is very slow or fails | It downloads about 2 GB; check the internet and re-run `docker compose build` |
| Page says "Can't reach the agent" | The server is not running: run `agent2-ui.cmd` |
| Page says it can't reach the model | Start Ollama (or `llama-server`), or pick another model in the drop-down |
| "Port is already allocated" | Another program uses that port: stop it, or create the agent again with another port (`new-agent.cmd NAME -Port 5010`) |
| No sound, or "microphone blocked" | Allow the microphone for the page in the browser's site settings; open the page as `http://127.0.0.1:PORT` or `http://localhost:PORT` |
| Page looks old after an update | You pulled but did not rebuild: run the update steps above, then press Ctrl+F5 |

Full documentation: `ashley\README.md`. Everything there applies to the other agents too.
