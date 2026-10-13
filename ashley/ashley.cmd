@echo off
rem Starts Ashley (builds the image first if needed). Start a model server first - see ..\host
cd /d "%~dp0"
docker compose run --rm --build ashley %*
