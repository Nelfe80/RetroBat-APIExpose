@echo off
setlocal EnableExtensions DisableDelayedExpansion
for %%i in ("%~dp0.") do set "eventName=%%~nxi"

set "OUTDIR=%~dp0..\..\..\..\plugins\APIExpose"
rem Ecriture atomique : tmp prive puis rename (move = metadonnees, pas de
rem double ecriture des donnees). Les lecteurs ne voient jamais un fichier
rem partiel et l'ecriture ne peut plus echouer pendant qu'APIExpose lit.
set "TMPFILE=%OUTDIR%\events.%RANDOM%%RANDOM%.tmp"
(
  echo event=%eventName%
  echo %*
  echo(timestamp=%date% %time%
) > "%TMPFILE%"
move /y "%TMPFILE%" "%OUTDIR%\events.ini" >nul 2>&1
if exist "%TMPFILE%" move /y "%TMPFILE%" "%OUTDIR%\events.ini" >nul 2>&1
if exist "%TMPFILE%" del "%TMPFILE%" >nul 2>&1
exit /b 0
