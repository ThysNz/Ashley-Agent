@echo off
rem Saves a READ-ONLY GitHub token so Ashley can pull from your private repos.
rem The token is typed hidden, written to ashley\secrets\github_read_token (never committed), and not shown again.
rem To remove her access: delete that file and revoke the token on GitHub.
echo Create a fine-grained token first (Contents: Read-only, only the repos she should read).
echo Steps are in ashley\secrets\README.md.
echo.
powershell -NoProfile -Command "$s = Read-Host 'Paste the token (it will not be shown)' -AsSecureString; $t = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($s)).Trim(); if ($t.Length -lt 20) { Write-Host 'That does not look like a token. Nothing saved.'; exit 1 }; [IO.File]::WriteAllText('%~dp0secrets\github_read_token', $t); Write-Host 'Saved. She can now pull from the repos the token allows.'"
pause
