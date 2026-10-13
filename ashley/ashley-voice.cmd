@echo off
rem Starts the speech containers + Ashley's API server (if not already up), then the voice client.
rem Start a model first (host\start-qwen30b.bat, or Ollama + /model in the console).
pushd "%~dp0..\voice" && docker compose up -d && popd
pushd "%~dp0" && docker compose --profile server up -d server && popd
pushd "%~dp0..\clients\VoiceClient" && dotnet run -c Release -- %* && popd
