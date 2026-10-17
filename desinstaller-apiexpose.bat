@echo off
rem DESINSTALLATION COMPLETE D'APIEXPOSE (2026-10-17). Arrete l'API, retire la collection World Scoring, les reglages
rem d'APIExpose dans ES, le lancement avec ES, le menu d'ES et le montage core_proxy, puis vide le dossier du plugin.
rem GARDE : media (les gamelists y pointent), l'identite de la borne et les replays. Lance ensuite le desinstalleur
rem de Windows s'il existe (installation par l'installeur). Fermer RetroBat avant : EmulationStation reecrit ses
rem reglages en quittant.
setlocal
cd /d "%~dp0"
tasklist /FI "IMAGENAME eq emulationstation.exe" /NH 2>nul | find /I "emulationstation.exe" >nul
if not errorlevel 1 (
  echo Fermez RetroBat ^(EmulationStation^) avant de desinstaller APIExpose, puis relancez ce fichier.
  pause
  exit /b 1
)
if not exist "RetroBat.Api.exe" (
  echo RetroBat.Api.exe introuvable : rien a faire.
  pause
  exit /b 1
)
taskkill /f /im RetroBat.Api.exe >nul 2>&1
taskkill /f /im NelfePlay.LiveStreaming.exe >nul 2>&1
echo Nettoyage d'APIExpose...
"RetroBat.Api.exe" --desinstaller
if exist "unins000.exe" (
  echo Desinstalleur de Windows...
  start "" /wait "unins000.exe"
)
if exist "RetroBat.Api.exe" del /f /q "RetroBat.Api.exe" >nul 2>&1
echo APIExpose est desinstalle. Les medias, l'identite de la borne et les replays restent dans ce dossier.
echo Journal : %TEMP%\apiexpose-desinstallation.log
pause
(goto) 2>nul & del "%~f0"
