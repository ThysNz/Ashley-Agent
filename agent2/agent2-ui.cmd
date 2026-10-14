@echo off
rem Starts the speech containers and Agent2's server, waits until it is ready, then opens its web page.
rem Web page: http://127.0.0.1:5001   (start a model first, or add one in the page)
pushd "%~dp0..\voice" && docker compose up -d && popd
pushd "%~dp0" && docker compose --profile server up -d server && popd
echo Waiting for Agent2 to be ready...
powershell -NoProfile -Command "for($i=0;$i -lt 45;$i++){ try{ Invoke-WebRequest http://127.0.0.1:5001/health -UseBasicParsing -TimeoutSec 2 | Out-Null; break }catch{ Start-Sleep 2 } }"
where msedge >nul 2>nul && (start "" msedge --app=http://127.0.0.1:5001) || (start "" http://127.0.0.1:5001)