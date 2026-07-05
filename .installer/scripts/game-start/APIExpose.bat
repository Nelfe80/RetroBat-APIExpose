@echo off
setlocal EnableExtensions DisableDelayedExpansion

for %%i in ("%~dp0.") do set "eventName=%%~nxi"

set "OUTDIR=%~dp0..\..\..\..\plugins\APIExpose"
set "OUTFILE=%OUTDIR%\events.ini"
set "TMPFILE=%OUTDIR%\events.tmp.%RANDOM%%RANDOM%"

> "%TMPFILE%" (
  echo(event=%eventName%
  echo(%*
  echo(timestamp=%date% %time%
)

move /y "%TMPFILE%" "%OUTFILE%" >nul 2>&1

exit /b 0