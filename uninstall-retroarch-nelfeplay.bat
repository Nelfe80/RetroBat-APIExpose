@echo off
setlocal EnableExtensions DisableDelayedExpansion

rem RETROARCH NELFEPLAY : retour a la stable de RetroBat. retroarch.exe reprend, octet pour octet, la copie gardee par
rem install-retroarch-nelfeplay.bat (son empreinte est verifiee AVANT d'ecraser quoi que ce soit), la copie
rem retroarch.patched.*.exe d'emulatorLauncher est effacee (il la refait depuis le bon exe), la marque et la sauvegarde partent.
rem
rem Si RetroBat a remplace retroarch.exe depuis l'installation (mise a jour), RIEN n'est restaure par-dessus : on se retire
rem seulement (marque et sauvegarde perimee).
rem
rem Options : /silencieux (ni pause), /annulation (retrait par l'API : ne pose pas de refus), /blanc (dit ce qui serait fait).
rem Lancee a la main (sans /annulation), elle pose state\retroarch-nelfeplay-refus.txt : l'API ne reinstalle plus.
rem Codes : 0 fait ; 1 echec ; 2 un jeu ou RetroArch tourne.

set "PLUGIN_DIR=%~dp0"
for %%I in ("%PLUGIN_DIR%..\..") do set "RETROBAT_ROOT=%%~fI"
set "RA=%RETROBAT_ROOT%\emulators\retroarch"
set "STABLE=81c11b6f24932bf7918f05eee8928035bff3887335fd2a081507c75e9d94d06a"
set "NOTRE=aff5d5747bbaa0e533036d025b757667ee1866417b8cf8e46e803f37ca72d0cf"
set "ESSAI="
if defined NELFEPLAY_ESSAI_DOSSIER (
  set "ESSAI=1"
  set "RA=%NELFEPLAY_ESSAI_DOSSIER%"
  if defined NELFEPLAY_ESSAI_STABLE set "STABLE=%NELFEPLAY_ESSAI_STABLE%"
  if defined NELFEPLAY_ESSAI_NOTRE set "NOTRE=%NELFEPLAY_ESSAI_NOTRE%"
)
set "ETAT=%RA%\.nelfeplay"
set "SAUV=%ETAT%\sauvegarde"
set "MARQUE=%ETAT%\build.txt"
set "REFUS=%PLUGIN_DIR%state\retroarch-nelfeplay-refus.txt"
set "TMP_EXE=%RA%\retroarch.exe.nelfeplay-tmp"
set "SILENCIEUX="
set "ANNULATION="
set "BLANC="
for %%A in (%*) do (
  if /I "%%~A"=="/silencieux" set "SILENCIEUX=1"
  if /I "%%~A"=="/annulation" set "ANNULATION=1"
  if /I "%%~A"=="/blanc" set "BLANC=1"
)

echo RetroArch NelfePlay : retour a la stable de RetroBat
echo   dossier : %RA%
echo.

if not exist "%MARQUE%" if not exist "%SAUV%\retroarch.exe" (
  echo Rien a desinstaller : RetroArch NelfePlay n'est pas installe.
  set "CODE=0"
  goto :sortie
)

if not defined NELFEPLAY_ESSAI_SANS_GARDE (
  tasklist /FI "IMAGENAME eq retroarch*" 2>nul | find /I "retroarch" >nul && (
    echo Un RetroArch tourne : fermez le jeu, puis relancez cette desinstallation.
    set "CODE=2"
    goto :sortie
  )
  tasklist /FI "IMAGENAME eq emulatorLauncher.exe" 2>nul | find /I "emulatorLauncher" >nul && (
    echo emulatorLauncher tourne : fermez le jeu, puis relancez cette desinstallation.
    set "CODE=2"
    goto :sortie
  )
)

set "EXE_MARQUE="
if exist "%MARQUE%" for /f "usebackq tokens=1,* delims==" %%A in ("%MARQUE%") do if /I "%%A"=="exe" set "EXE_MARQUE=%%B"
if not defined EXE_MARQUE set "EXE_MARQUE=%NOTRE%"
call :sha "%RA%\retroarch.exe" EXE_ICI

if /I not "%EXE_ICI%"=="%EXE_MARQUE%" goto :remplace

rem retroarch.exe est bien le notre : la sauvegarde doit etre la stable intacte AVANT qu'on ecrase quoi que ce soit.
call :sha "%SAUV%\retroarch.exe" SAUV_LU
if /I not "%SAUV_LU%"=="%STABLE%" (
  echo La sauvegarde de la stable manque ou est alteree : rien n'est touche.
  echo   attendue : %STABLE%
  echo   lue      : %SAUV_LU%
  goto :echec
)
if defined BLANC (
  echo A BLANC : la stable de %SAUV% reprendrait la place de retroarch.exe, la copie retroarch.patched.*.exe serait effacee.
  echo A BLANC : rien n'a ete touche.
  set "CODE=0"
  goto :sortie
)
copy /Y "%SAUV%\retroarch.exe" "%TMP_EXE%" >nul || goto :echec
call :sha "%TMP_EXE%" TMP_LU
if /I not "%TMP_LU%"=="%STABLE%" (
  del /f /q "%TMP_EXE%"
  echo La copie de la stable est incorrecte : rien n'est touche.
  goto :echec
)
move /Y "%TMP_EXE%" "%RA%\retroarch.exe" >nul
call :sha "%RA%\retroarch.exe" EXE_FINAL
if /I not "%EXE_FINAL%"=="%STABLE%" (
  if exist "%TMP_EXE%" del /f /q "%TMP_EXE%"
  echo Restauration impossible ^(exe tenu ?^) : la sauvegarde reste dans %SAUV%.
  goto :echec
)
del /f /q "%RA%\retroarch.patched.*.exe" 2>nul
rmdir /s /q "%ETAT%"
echo Fait. retroarch.exe est de nouveau la stable 1.22.2 de RetroBat ^(%STABLE%^).
goto :refus

:remplace
echo RetroArch a deja ete remplace depuis l'installation ^(mise a jour de RetroBat^) : il reste tel quel.
if defined BLANC (
  echo A BLANC : seules la marque et la sauvegarde perimee seraient retirees.
  set "CODE=0"
  goto :sortie
)
rmdir /s /q "%ETAT%"

:refus
rem Le choix du joueur : APIExpose ne la reinstallera pas d'elle-meme. Relancer l'installeur a la main la reprend.
if not defined ANNULATION (
  if not exist "%PLUGIN_DIR%state" mkdir "%PLUGIN_DIR%state"
  > "%REFUS%" echo Desinstallee le %DATE% %TIME% : APIExpose ne reinstalle pas RetroArch NelfePlay.
  echo APIExpose ne la reinstallera pas. Pour la reprendre : install-retroarch-nelfeplay.bat
)
set "CODE=0"
goto :sortie

:echec
echo.
echo Desinstallation non terminee.
set "CODE=1"

:sortie
if not defined SILENCIEUX pause
exit /b %CODE%

:sha
rem :sha fichier variable : le SHA-256 du fichier, sans espaces, dans la variable ; vide si le fichier manque.
set "%~2="
for /f "skip=1 delims=" %%H in ('certutil -hashfile "%~1" SHA256 2^>nul') do if not defined %~2 set "%~2=%%H"
if defined %~2 call set "%~2=%%%~2: =%%"
exit /b 0
