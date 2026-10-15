# Installs a skill (a folder with a SKILL.md) for the agents.
#   add-skill.cmd C:\Downloads\my-skill              a folder
#   add-skill.cmd C:\Downloads\my-skill.zip          a zip (SKILL.md at its top or one folder down)
#   add-skill.cmd C:\Drafts\SKILL.md                 a lone SKILL.md (needs a 'name:' in its header)
#   add-skill.cmd <source> -Agent agent2             only for that agent (default: shared by every agent)
#   add-skill.cmd <source> -Force                    replace an existing skill (the old one is kept as a backup)
#
# Skills are picked up by running agents within a couple of seconds; no restart is needed.
param(
  [Parameter(Mandatory = $true, Position = 0)][string]$Source,
  [string]$Agent = "",
  [switch]$Force
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

if ($Agent) {
  if (-not (Test-Path (Join-Path $root $Agent))) { throw "There is no agent folder '$Agent' in $root." }
  $destRoot = Join-Path $root "$Agent\skills"
  $scope = "agent '$Agent' only"
} else {
  $destRoot = Join-Path $root 'skills'
  $scope = "every agent"
}
New-Item -ItemType Directory -Force -Path $destRoot | Out-Null

$temp = Join-Path ([IO.Path]::GetTempPath()) ("skill-" + [guid]::NewGuid().ToString('N'))
try {
  if (-not (Test-Path -LiteralPath $Source)) { throw "Not found: $Source" }
  $Source = (Resolve-Path -LiteralPath $Source).Path

  if (Test-Path -LiteralPath $Source -PathType Leaf) {
    New-Item -ItemType Directory -Path $temp | Out-Null
    if ($Source -like '*.zip') { Expand-Archive -LiteralPath $Source -DestinationPath $temp }
    elseif ((Split-Path $Source -Leaf) -ieq 'SKILL.md') { Copy-Item -LiteralPath $Source -Destination (Join-Path $temp 'SKILL.md') }
    else { throw "Give a skill folder, a .zip, or a SKILL.md file." }
    $src = $temp
  } else {
    $src = $Source
  }

  # SKILL.md at the top, or exactly one folder down (zips usually have a folder inside)
  $skillFile = Get-ChildItem -LiteralPath $src -Filter 'SKILL.md' -File | Select-Object -First 1
  if (-not $skillFile) {
    $skillFile = Get-ChildItem -LiteralPath $src -Directory | ForEach-Object { Get-ChildItem -LiteralPath $_.FullName -Filter 'SKILL.md' -File } | Select-Object -First 1
  }
  if (-not $skillFile) { throw "No SKILL.md found in $Source (a skill is a folder that contains a SKILL.md)." }
  $skillDir = $skillFile.DirectoryName

  # read the header
  $text = (Get-Content -LiteralPath $skillFile.FullName -Raw -Encoding UTF8) -replace "`r`n", "`n"
  $name = ''; $desc = ''
  if ($text.StartsWith("---`n")) {
    $end = $text.IndexOf("`n---", 4)
    if ($end -gt 0) {
      foreach ($line in ($text.Substring(4, $end - 4) -split "`n")) {
        if ($line -match '^name:\s*(.+)$') { $name = $Matches[1].Trim().Trim('"', "'") }
        elseif ($line -match '^description:\s*(.+)$') { $desc = $Matches[1].Trim().Trim('"', "'") }
      }
    }
  } else {
    throw "SKILL.md must start with a '---' header that has a name and a description."
  }
  if (-not $name) {
    if ($skillDir -eq $temp) { throw "This SKILL.md has no 'name:' in its header, and there is no folder name to use." }
    $name = Split-Path $skillDir -Leaf
  }
  if ($name -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$') { throw "The skill name '$name' is not valid (letters, digits, dash, dot, underscore; up to 64 characters)." }
  if ($desc -eq '' -or $desc -eq '>' -or $desc -eq '|') {
    Write-Host "Warning: no one-line description. The agent decides whether to use a skill from its description, so add one." -ForegroundColor Yellow
  }

  $target = Join-Path $destRoot $name
  if (Test-Path $target) {
    if (-not $Force) { throw "A skill called '$name' is already installed for $scope. Use -Force to replace it (the old one is kept as a backup)." }
    # Backups go in a sibling folder, outside the skills folder, so they are not loaded as skills.
    $backupRoot = Join-Path (Split-Path $destRoot -Parent) 'skills-backup'
    New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null
    $backup = Join-Path $backupRoot ("$name-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    Move-Item -LiteralPath $target -Destination $backup
    Write-Host "Old version kept as $backup" -ForegroundColor DarkGray
  }

  Copy-Item -LiteralPath $skillDir -Destination $target -Recurse
  Write-Host ""
  Write-Host "Installed skill '$name' for $scope." -ForegroundColor Green
  Write-Host "  at: $target"
  if ($desc) { Write-Host "  description: $desc" }
  Write-Host "  It appears in the page's Skills list within a couple of seconds. Run it with /$name, or she will use it when a request matches."
}
catch {
  Write-Host ""
  Write-Host ("ERROR: " + $_.Exception.Message) -ForegroundColor Red
  exit 1
}
finally {
  if (Test-Path $temp) { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue }
}
