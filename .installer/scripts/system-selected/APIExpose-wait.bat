@echo off
setlocal EnableExtensions DisableDelayedExpansion
for %%i in ("%~dp0.") do set "eventName=%%~nxi"

set "OUTDIR=%~dp0..\..\..\..\plugins\APIExpose"

if not exist "%OUTDIR%" mkdir "%OUTDIR%" >nul 2>&1
:: event.txt (brut) - SAFE pour ! et parenthèses

(
  echo event=%eventName%
  echo %*
  echo(timestamp=%date% %time%
) > "%OUTDIR%\events.ini"
exit /b 0