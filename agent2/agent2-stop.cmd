@echo off
rem Stops Agent2's server only. The speech containers are shared by all agents: stop them with ashley\stop-voice.cmd.
pushd "%~dp0" && docker compose --profile server stop server && popd