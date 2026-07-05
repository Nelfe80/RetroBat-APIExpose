@echo off
setlocal EnableExtensions DisableDelayedExpansion

set "OUTDIR=%~dp0..\..\..\..\plugins\APIExpose"

if not exist "%OUTDIR%" mkdir "%OUTDIR%" >nul 2>&1
set "APIEXPOSE_OUTDIR=%OUTDIR%"

(
  echo event=game-start
  echo %*
) > "%OUTDIR%\events.ini"

powershell -NoProfile -ExecutionPolicy Bypass -Command "try { $eventPath = Join-Path $env:APIEXPOSE_OUTDIR 'events.ini'; $raw = Get-Content -LiteralPath $eventPath -Raw; $body = $raw -replace '^\s*event=.*?\r?\n',''; Invoke-RestMethod -Uri 'http://127.0.0.1:12345/api/v1/rom-packs/on-the-fly/ensure-launch-rom' -Method Post -ContentType 'text/plain; charset=utf-8' -Body $body -TimeoutSec 900 | Out-Null } catch { }"

exit /b 0
