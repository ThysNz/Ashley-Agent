@echo off
rem Stops the speech containers and Ashley's API server.
pushd "%~dp0" && docker compose --profile server stop server && popd
pushd "%~dp0..\voice" && docker compose stop && popd
