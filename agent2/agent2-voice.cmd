@echo off
rem Windows voice client for Agent2 (the web page can do voice too). Extra options are passed on, e.g. --mic HyperX
pushd "%~dp0..\voice" && docker compose up -d && popd
pushd "%~dp0" && docker compose --profile server up -d server && popd
pushd "%~dp0..\clients\VoiceClient" && dotnet run -c Release -- --agent http://127.0.0.1:5001 %* && popd