@echo off
rem Starts the speech containers and Ashley's server, waits until the page is ready, then opens it.
rem Start a model first (host\start-qwen30b.bat, or Ollama). The page shows if the model is not running.
pushd "%~dp0..\voice" && docker compose up -d && popd
pushd "%~dp0" && docker compose --profile server up -d server && popd
echo Waiting for Ashley to be ready...
powershell -NoProfile -Command "for($i=0;$i -lt 45;$i++){ try{ Invoke-WebRequest http://127.0.0.1:5000/health -UseBasicParsing -TimeoutSec 2 | Out-Null; break }catch{ Start-Sleep 2 } }"
where msedge >nul 2>nul && (start "" msedge --app=http://127.0.0.1:5000) || (start "" http://127.0.0.1:5000)
