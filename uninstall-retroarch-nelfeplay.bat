@echo off
setlocal EnableExtensions DisableDelayedExpansion

rem RETROARCH NELFEPLAY : retour a la version de RetroArch de RetroBat. Les fichiers sauvegardes par
rem install-retroarch-nelfeplay.bat reprennent leur place, les fichiers qu'elle avait ajoutes sont retires.

set "PLUGIN_DIR=%~dp0"
for %%I in ("%PLUGIN_DIR%..\..") do set "RETROBAT_ROOT=%%~fI"
set "RA=%RETROBAT_ROOT%\emulators\retroarch"
set "ETAT=%RA%\.nelfeplay"
set "SAUVEGARDE=%ETAT%\sauvegarde"
set "AJOUTES=%ETAT%\ajoutes.txt"

echo RetroArch NelfePlay : retour a la version de RetroBat
echo   dossier : %RA%
echo.

if not exist "%SAUVEGARDE%" (
  echo Rien a desinstaller : la nocturne n'a pas ete installee par install-retroarch-nelfeplay.bat.
  goto :fin
)

for %%P in (retroarch.exe emulatorLauncher.exe) do (
  tasklist /FI "IMAGENAME eq %%P" 2>nul | find /I "%%P" >nul && (
    echo %%P tourne : fermez le jeu, puis relancez cette desinstallation.
    goto :echec
  )
)

rem Les fichiers d'origine d'abord, puis le retrait de ceux que la nocturne avait ajoutes.
robocopy "%SAUVEGARDE%" "%RA%" /E /R:2 /W:1 /NFL /NDL /NJH /NJS /NP >nul
if errorlevel 8 (
  echo Restauration incomplete : la sauvegarde reste dans %SAUVEGARDE%.
  goto :echec
)
if exist "%AJOUTES%" (
  for /f "usebackq delims=" %%L in ("%AJOUTES%") do if exist "%RA%\%%L" del /f /q "%RA%\%%L"
)
rmdir /s /q "%ETAT%"

echo Fait. RetroArch est de nouveau celui de RetroBat.
goto :fin

:echec
echo.
echo Desinstallation non terminee.
if /I not "%~1"=="/silencieux" pause
exit /b 1

:fin
if /I not "%~1"=="/silencieux" pause
exit /b 0
