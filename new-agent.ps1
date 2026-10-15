# Creates a new agent folder next to ashley\, cloned from her setup but with:
#   - its own name, persona, port, volume and launchers
#   - an EMPTY model list (add models later in the web page or in config\models.json)
#   - an empty workspace, memory and secrets folder (nothing of Ashley's is copied)
#
# Usage:   new-agent.cmd agent2            (or: new-agent.cmd coder -Port 5003 -DisplayName "Coder")
#
param(
  [Parameter(Mandatory = $true)][string]$Name,
  [int]$Port = 0,
  [string]$DisplayName = ""
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$template = Join-Path $root 'ashley'
$utf8 = New-Object System.Text.UTF8Encoding($false)

if ($Name -notmatch '^[a-z][a-z0-9-]{1,30}$') { throw "The name must be lowercase letters, digits and dashes, starting with a letter (for example: agent2)." }
if ($Name -eq 'ashley') { throw "'ashley' already exists." }
$dest = Join-Path $root $Name
if (Test-Path $dest) { throw "$dest already exists - nothing was changed." }
if (-not (Test-Path $template)) { throw "Template folder not found: $template" }
if (-not $DisplayName) { $DisplayName = (Get-Culture).TextInfo.ToTitleCase($Name) }

# Pick a free port (each agent's web page and API need their own).
$used = @()
foreach ($dir in Get-ChildItem $root -Directory) {
  $f = Join-Path $dir.FullName 'docker-compose.yml'
  if (Test-Path $f) {
    foreach ($m in [regex]::Matches((Get-Content $f -Raw), '127\.0\.0\.1:(\d+):5000')) { $used += [int]$m.Groups[1].Value }
  }
}
if ($Port -eq 0) { $Port = 5001; while ($used -contains $Port) { $Port++ } }
elseif ($used -contains $Port) { throw "Port $Port is already used by another agent ($($used -join ', ') are taken)." }

function Write-Text($path, $text) { [IO.File]::WriteAllText($path, $text, $utf8) }
function Write-Cmd($path, $text) { [IO.File]::WriteAllText($path, (($text -replace "`r?`n", "`r`n")), $utf8) }
function Fill($text) { $text.Replace('__NAME__', $Name).Replace('__DISPLAY__', $DisplayName).Replace('__PORT__', [string]$Port) }

# ---- folders -----------------------------------------------------------------
New-Item -ItemType Directory -Path $dest, "$dest\config\personas", "$dest\workspace", "$dest\memory", "$dest\secrets", "$dest\skills" | Out-Null
New-Item -ItemType File -Path "$dest\workspace\.gitkeep", "$dest\memory\.gitkeep", "$dest\skills\.gitkeep" | Out-Null
# Explanation only, never a token; the texts are re-worded for this agent.
Write-Text "$dest\secrets\README.md" ((Get-Content "$template\secrets\README.md" -Raw -Encoding UTF8).Replace('Ashley', $DisplayName))
Write-Text "$dest\set-github-token.cmd" ((Get-Content "$template\set-github-token.cmd" -Raw -Encoding UTF8).Replace('ashley\secrets', "$Name\secrets").Replace('Ashley', $DisplayName))

# ---- personas: Ashley's set, with the name swapped, plus one for this agent ------
foreach ($p in Get-ChildItem "$template\config\personas" -Filter *.md) {
  $text = (Get-Content $p.FullName -Raw -Encoding UTF8).Replace('Ashley', $DisplayName)
  Write-Text (Join-Path "$dest\config\personas" $p.Name) $text
}
Copy-Item "$dest\config\personas\ashley.md" "$dest\config\personas\$Name.md"
Remove-Item "$dest\config\personas\ashley.md"        # this agent is not Ashley

# ---- config -------------------------------------------------------------------------
Write-Text "$dest\config\agent.json" (Fill @'
{
  // Which persona file in config/personas to use, and the default model name from models.json.
  "name": "__DISPLAY__",
  "persona": "__NAME__",
  "defaultModel": "",
  "maxToolRounds": 80,
  "historyTurns": 12,
  "commandTimeoutSeconds": 120,
  "maxNotesChars": 8000,
  // Author of the git commits made in the workspace (the agent commits, you push).
  "gitName": "__DISPLAY__",
  "gitEmail": "__NAME__@ai-agents.local"
}
'@)

Write-Text "$dest\config\models.json" @'
{
  // No models yet. Add some later, either:
  //  - in the web page: click the chip button (Manage models) and add one installed in Ollama, or
  //  - here: copy profiles from ..\..\ashley\config\models.json (that file explains the fields).
  // Models run outside the container; contextSize must not exceed what the model server runs with.
  "models": []
}
'@

# ---- docker compose: Ashley's file with this agent's names and port ---------------------
$compose = (Get-Content "$template\docker-compose.yml" -Raw -Encoding UTF8)
$compose = $compose.Replace('ashley', $Name).Replace('Ashley', $DisplayName).Replace('127.0.0.1:5000:5000', "127.0.0.1:${Port}:5000")
Write-Text "$dest\docker-compose.yml" $compose

# ---- launchers -------------------------------------------------------------------------------
Write-Cmd "$dest\$Name.cmd" (Fill @'
@echo off
rem Console chat with __DISPLAY__. Needs a model: add one in the web page (Manage models) or in config\models.json.
cd /d "%~dp0"
docker compose run --rm --build __NAME__ %*
'@)

Write-Cmd "$dest\$Name-ui.cmd" (Fill @'
@echo off
rem Starts the speech containers and __DISPLAY__'s server, waits until it is ready, then opens its web page.
rem Web page: http://127.0.0.1:__PORT__   (start a model first, or add one in the page)
pushd "%~dp0..\voice" && docker compose up -d && popd
pushd "%~dp0" && docker compose --profile server up -d server && popd
echo Waiting for __DISPLAY__ to be ready...
powershell -NoProfile -Command "for($i=0;$i -lt 45;$i++){ try{ Invoke-WebRequest http://127.0.0.1:__PORT__/health -UseBasicParsing -TimeoutSec 2 | Out-Null; break }catch{ Start-Sleep 2 } }"
where msedge >nul 2>nul && (start "" msedge --app=http://127.0.0.1:__PORT__) || (start "" http://127.0.0.1:__PORT__)
'@)

Write-Cmd "$dest\$Name-voice.cmd" (Fill @'
@echo off
rem Windows voice client for __DISPLAY__ (the web page can do voice too). Extra options are passed on, e.g. --mic HyperX
pushd "%~dp0..\voice" && docker compose up -d && popd
pushd "%~dp0" && docker compose --profile server up -d server && popd
pushd "%~dp0..\clients\VoiceClient" && dotnet run -c Release -- --agent http://127.0.0.1:__PORT__ %* && popd
'@)

Write-Cmd "$dest\$Name-stop.cmd" (Fill @'
@echo off
rem Stops __DISPLAY__'s server only. The speech containers are shared by all agents: stop them with ashley\stop-voice.cmd.
pushd "%~dp0" && docker compose --profile server stop server && popd
'@)

# ---- a short README for this agent --------------------------------------------------------------
Write-Text "$dest\README.md" (Fill @'
# __DISPLAY__ - an agent cloned from Ashley

Same program and abilities as Ashley (files in its workspace, shell, web, notes, todo, git), but with its **own** workspace,
memory, notes, saved chats, settings and port. Nothing of Ashley's is shared except the Docker image and the speech containers.
The full documentation is in `..\ashley\README.md`; everything there applies with the differences below.

| | |
|---|---|
| Web page | http://127.0.0.1:__PORT__ |
| Start | `__NAME__-ui.cmd` (web page), `__NAME__.cmd` (console), `__NAME__-voice.cmd` (Windows voice client) |
| Stop | `__NAME__-stop.cmd` (this agent only; the speech containers are shared, `ashley\stop-voice.cmd` stops them) |
| Model | **none yet.** Add one in the web page (chip button, then Add next to an Ollama model) or in `config\models.json` |
| Persona | `config\personas\__NAME__.md` (edit it to change who this agent is) |
| Git | commits as `__DISPLAY__ <__NAME__@ai-agents.local>` (`config\agent.json`); a GitHub read token goes in via `set-github-token.cmd` |
| Workspace / memory | `workspace\` and `memory\` in this folder (not committed to git) |
| Skills | the shared ones in `..\skills\` plus this agent's own in `skills\`; add with `..\add-skill.cmd SOURCE -Agent __NAME__` |
'@)

Write-Host ""
Write-Host "Created $Name ($DisplayName) in $dest" -ForegroundColor Green
Write-Host "  web page : http://127.0.0.1:$Port"
Write-Host "  start    : $Name\$Name-ui.cmd        stop: $Name\$Name-stop.cmd"
Write-Host "  models   : none yet - add one in the web page (chip button) or in $Name\config\models.json"
