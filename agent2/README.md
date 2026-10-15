# Agent2 - an agent cloned from Ashley

Same program and abilities as Ashley (files in its workspace, shell, web, notes, todo, git), but with its **own** workspace,
memory, notes, saved chats, settings and port. Nothing of Ashley's is shared except the Docker image and the speech containers.
The full documentation is in `..\ashley\README.md`; everything there applies with the differences below.

| | |
|---|---|
| Web page | http://127.0.0.1:5001 |
| Start | `agent2-ui.cmd` (web page), `agent2.cmd` (console), `agent2-voice.cmd` (Windows voice client) |
| Stop | `agent2-stop.cmd` (this agent only; the speech containers are shared, `ashley\stop-voice.cmd` stops them) |
| Model | **none yet.** Add one in the web page (chip button, then Add next to an Ollama model) or in `config\models.json` |
| Persona | `config\personas\agent2.md` (edit it to change who this agent is) |
| Git | commits as `Agent2 <agent2@ai-agents.local>` (`config\agent.json`); a GitHub read token goes in via `set-github-token.cmd` |
| Workspace / memory | `workspace\` and `memory\` in this folder (not committed to git) |
| Skills | the shared ones in `..\skills\` plus this agent's own in `skills\`; add with `..\add-skill.cmd SOURCE -Agent agent2` |