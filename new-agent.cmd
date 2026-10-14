@echo off
rem Creates a new agent folder cloned from ashley (without her memory, workspace, token or models).
rem   new-agent.cmd agent2
rem   new-agent.cmd coder -Port 5003 -DisplayName "Coder"
if "%~1"=="" (
  echo Usage: new-agent.cmd NAME [-Port 5003] [-DisplayName "Display Name"]
  echo   NAME: lowercase letters, digits and dashes, for example agent2
  exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0new-agent.ps1" -Name %*
