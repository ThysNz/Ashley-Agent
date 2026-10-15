@echo off
rem Installs a skill (a folder with a SKILL.md, a .zip, or a lone SKILL.md) for the agents.
rem   add-skill.cmd C:\Downloads\my-skill
rem   add-skill.cmd C:\Downloads\my-skill.zip -Agent agent2
if "%~1"=="" (
  echo Usage: add-skill.cmd SOURCE [-Agent NAME] [-Force]
  echo   SOURCE: a skill folder, a .zip, or a SKILL.md file
  echo   -Agent: install for that agent only ^(default: every agent^)
  exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0add-skill.ps1" %*
