@echo off
rem Starts the main model for the agents: Qwen3-Coder-30B-A3B on llama-server.
rem Runs on the HOST (Windows), outside Docker. Containers reach it at
rem http://host.docker.internal:8080/v1
rem --jinja is required: it makes llama-server understand tool calls.
rem Bound to 127.0.0.1 (Docker Desktop forwards host.docker.internal to it).
rem If the container can't connect, change --host to 0.0.0.0 (and allow it in the firewall).

if not exist F:\Ai-Models\logs mkdir F:\Ai-Models\logs

F:\Ai-Models\AI-Tools\llama-bin\llama-server.exe ^
  -m F:\Ai-Models\Qwen3-Coder-30B\Qwen3-Coder-30B-A3B-Instruct-Q4_K_M.gguf ^
  --alias qwen3-coder-30b ^
  -ngl 99 --n-cpu-moe 16 -np 1 -c 32768 -fa on -ctk q8_0 -ctv q8_0 ^
  --host 127.0.0.1 --port 8080 --jinja ^
  >> F:\Ai-Models\logs\llama-server.log 2>&1
