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
set "REFUS=%PLUGIN_DIR%state\retroarch-nelfeplay-refus.txt"
set "SILENCIEUX="
set "ANNULATION="
for %%A in (%*) do (
  if /I "%%~A"=="/silencieux" set "SILENCIEUX=1"
  if /I "%%~A"=="/annulation" set "ANNULATION=1"
)

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

rem RetroBat a-t-il remplace RetroArch depuis l'installation ? Alors sa sauvegarde est perimee : on ne la remet pas.
set "EXE_MARQUE="
set "EXE_ICI="
if exist "%ETAT%\installe.txt" for /f "usebackq tokens=1,* delims==" %%A in ("%ETAT%\installe.txt") do if /I "%%A"=="exe" set "EXE_MARQUE=%%B"
for /f "skip=1 delims=" %%H in ('certutil -hashfile "%RA%\retroarch.exe" SHA256 2^>nul') do if not defined EXE_ICI set "EXE_ICI=%%H"
set "EXE_ICI=%EXE_ICI: =%"
if defined EXE_MARQUE if /I not "%EXE_ICI%"=="%EXE_MARQUE%" (
  echo RetroArch a deja ete remplace depuis l'installation ^(mise a jour de RetroBat^) : il reste tel quel.
  rmdir /s /q "%ETAT%"
  goto :refus
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
rem La nocturne recopie les identifiants (RetroAchievements...) dans un trousseau a cote de sa cle ; la 1.22.2 ne s'en
rem sert pas, RetroBat les garde dans retroarch.cfg. On ne laisse pas cette copie derriere.
for %%K in (retroarch-keychain.cfg retroarch-keychain.key) do if exist "%RA%\%%K" del /f /q "%RA%\%%K"
rmdir /s /q "%ETAT%"

:refus
rem Le choix du joueur : APIExpose ne la reinstallera pas d'elle-meme. Relancer l'installeur a la main la reprend.
if not defined ANNULATION (
  if not exist "%PLUGIN_DIR%state" mkdir "%PLUGIN_DIR%state"
  > "%REFUS%" echo Desinstallee le %DATE% %TIME% : APIExpose ne reinstalle pas RetroArch NelfePlay.
)

echo Fait. RetroArch est de nouveau celui de RetroBat.
if not defined ANNULATION echo APIExpose ne la reinstallera pas. Pour la reprendre : install-retroarch-nelfeplay.bat
goto :fin

:echec
echo.
echo Desinstallation non terminee.
if not defined SILENCIEUX pause
exit /b 1

:fin
if not defined SILENCIEUX pause
exit /b 0
