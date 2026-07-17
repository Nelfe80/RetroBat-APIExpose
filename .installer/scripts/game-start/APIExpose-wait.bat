@echo off
setlocal EnableExtensions DisableDelayedExpansion

rem Pure batch + curl.exe on purpose: PowerShell one-liners doing web requests
rem are flagged as Trojan:Win32/ClickFix by antivirus heuristics.

set "OUTDIR=%~dp0..\..\..\..\plugins\APIExpose"

if not exist "%OUTDIR%" mkdir "%OUTDIR%" >nul 2>&1

rem Ecriture atomique : tmp prive puis rename (voir game-selected).
set "TMPFILE=%OUTDIR%\events.%RANDOM%%RANDOM%.tmp"
(
  echo event=game-start
  echo %*
) > "%TMPFILE%"
move /y "%TMPFILE%" "%OUTDIR%\events.ini" >nul 2>&1
if exist "%TMPFILE%" move /y "%TMPFILE%" "%OUTDIR%\events.ini" >nul 2>&1
if exist "%TMPFILE%" del "%TMPFILE%" >nul 2>&1

rem Body = the event payload without the event= line (same as before).
set "BODY_FILE=%TEMP%\apiexpose-launch-body.txt"
(
  echo %*
) > "%BODY_FILE%"

rem Blocks until an on-the-fly ROM is extracted (up to 15 minutes), so the
rem emulator only launches once the ROM exists.
curl.exe -s -m 900 -X POST -H "Content-Type: text/plain; charset=utf-8" --data-binary "@%BODY_FILE%" "http://127.0.0.1:12345/api/v1/rom-packs/on-the-fly/ensure-launch-rom" >nul 2>&1

del "%BODY_FILE%" >nul 2>&1
exit /b 0
