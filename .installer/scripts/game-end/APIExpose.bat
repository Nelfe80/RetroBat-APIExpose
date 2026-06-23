@echo off
setlocal EnableExtensions DisableDelayedExpansion
for %%i in ("%~dp0.") do set "eventName=%%~nxi"

set "API=http://127.0.0.1:1234"
set "OUTDIR=%~dp0..\..\..\..\plugins\APIExpose"

if not exist "%OUTDIR%" mkdir "%OUTDIR%" >nul 2>&1
:: event.txt (brut) - SAFE pour ! et parenthèses

(
  echo event=%eventName%
  echo %*
) > "%OUTDIR%\events.ini"
exit /b 0