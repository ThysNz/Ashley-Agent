@echo off
rem Console chat with Agent3. Needs a model: add one in the web page (Manage models) or in config\models.json.
cd /d "%~dp0"
docker compose run --rm --build agent3 %*