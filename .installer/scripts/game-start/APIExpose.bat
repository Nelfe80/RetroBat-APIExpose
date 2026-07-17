@echo off
setlocal EnableExtensions DisableDelayedExpansion
for %%i in ("%~dp0.") do set "eventName=%%~nxi"

set "OUTDIR=%~dp0..\..\..\..\plugins\APIExpose"

if not exist "%OUTDIR%" mkdir "%OUTDIR%" >nul 2>&1
rem Ecriture atomique : tmp prive puis rename (voir game-selected).
set "TMPFILE=%OUTDIR%\events.%RANDOM%%RANDOM%.tmp"
(
  echo event=%eventName%
  echo %*
) > "%TMPFILE%"
move /y "%TMPFILE%" "%OUTDIR%\events.ini" >nul 2>&1
if exist "%TMPFILE%" move /y "%TMPFILE%" "%OUTDIR%\events.ini" >nul 2>&1
if exist "%TMPFILE%" del "%TMPFILE%" >nul 2>&1
exit /b 0
