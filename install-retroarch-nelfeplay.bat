@echo off
setlocal EnableExtensions DisableDelayedExpansion

rem RETROARCH NELFEPLAY (lot 1, 1.22.2-nelfeplay.1) : la 1.22.2 de RetroBat plus trois correctifs de l'amont (replay des gros
rem etats, GET_STATUS sans systemid, hote netplay contre un pair sans compression). SEUL retroarch.exe change : les DLL sont
rem celles de RetroBat. emulatorLauncher ne lance que retroarch.exe : notre exe en prend le nom.
rem
rem GARDES : jamais pendant une partie (tout processus retroarch*, donc aussi retroarch.patched.RETROBAT.exe et la copie du
rem spectateur, ni emulatorLauncher) ; seulement par-dessus l'exe officiel connu (sha de la stable 1.22.2) : une RetroBat plus
rem recente n'est jamais retrogradee ; seulement si notre exe a l'empreinte essayee au banc (aucune signature, l'empreinte fait foi).
rem
rem La stable est copiee dans emulators\retroarch\.nelfeplay\sauvegarde puis restauree a l'identique par
rem uninstall-retroarch-nelfeplay.bat. La copie retroarch.patched.RETROBAT.exe d'emulatorLauncher est effacee : il ne la refait
rem que si la TAILLE de retroarch.exe change, et il la refait alors depuis notre exe.
rem La marque est NEUVE (.nelfeplay\build.txt) : jamais installe.txt, que les API 1.9.61 a 1.9.215 desinstallent en le voyant.
rem
rem Options : /silencieux (ni pause ni question), /blanc (dit ce qui serait fait, ne touche a rien).
rem Sources (GPLv3) : https://github.com/Nelfe80/RetroArch/tree/v1.22.2-nelfeplay.1
rem Codes : 0 fait ou deja fait ; 1 echec ; 2 un jeu ou RetroArch tourne ; 3 la base n'est pas la stable connue (rien pose).

set "PLUGIN_DIR=%~dp0"
for %%I in ("%PLUGIN_DIR%..\..") do set "RETROBAT_ROOT=%%~fI"
set "RA=%RETROBAT_ROOT%\emulators\retroarch"
set "STABLE=81c11b6f24932bf7918f05eee8928035bff3887335fd2a081507c75e9d94d06a"
set "NOTRE=aff5d5747bbaa0e533036d025b757667ee1866417b8cf8e46e803f37ca72d0cf"
set "BUILD=1.22.2-nelfeplay.1"
set "SOURCES=https://github.com/Nelfe80/RetroArch/tree/v1.22.2-nelfeplay.1"
set "EXE_SRC=%PLUGIN_DIR%tools\retroarch-nelfeplay\retroarch-1.22.2-nelfeplay.1.exe"
set "ESSAI="
rem Essais : NELFEPLAY_ESSAI_DOSSIER (autre dossier de RetroArch ; + _STABLE, _NOTRE, _EXE) ; NELFEPLAY_ESSAI_SANS_GARDE saute la garde
rem des processus (dossiers de fabrication seulement : sur la borne, la garde reste).
if defined NELFEPLAY_ESSAI_DOSSIER (
  set "ESSAI=1"
  set "RA=%NELFEPLAY_ESSAI_DOSSIER%"
  if defined NELFEPLAY_ESSAI_STABLE set "STABLE=%NELFEPLAY_ESSAI_STABLE%"
  if defined NELFEPLAY_ESSAI_NOTRE set "NOTRE=%NELFEPLAY_ESSAI_NOTRE%"
  if defined NELFEPLAY_ESSAI_EXE set "EXE_SRC=%NELFEPLAY_ESSAI_EXE%"
)
set "ETAT=%RA%\.nelfeplay"
set "SAUV=%ETAT%\sauvegarde"
set "MARQUE=%ETAT%\build.txt"
set "REFUS=%PLUGIN_DIR%state\retroarch-nelfeplay-refus.txt"
set "TMP_EXE=%RA%\retroarch.exe.nelfeplay-tmp"
set "SILENCIEUX="
set "BLANC="
for %%A in (%*) do (
  if /I "%%~A"=="/silencieux" set "SILENCIEUX=1"
  if /I "%%~A"=="/blanc" set "BLANC=1"
)

echo RetroArch NelfePlay : installation de %BUILD%
echo   dossier : %RA%
echo   sources : %SOURCES%
echo.

if not exist "%RA%\retroarch.exe" (
  echo RetroArch introuvable : %RA%\retroarch.exe
  goto :echec
)

rem Rien ne doit tourner : un exe en cours d'utilisation ne se remplace pas, et on ne touche jamais une partie.
if not defined NELFEPLAY_ESSAI_SANS_GARDE (
  tasklist /FI "IMAGENAME eq retroarch*" 2>nul | find /I "retroarch" >nul && (
    echo Un RetroArch tourne : fermez le jeu, puis relancez cette installation.
    set "CODE=2"
    goto :sortie
  )
  tasklist /FI "IMAGENAME eq emulatorLauncher.exe" 2>nul | find /I "emulatorLauncher" >nul && (
    echo emulatorLauncher tourne : fermez le jeu, puis relancez cette installation.
    set "CODE=2"
    goto :sortie
  )
)

call :sha "%RA%\retroarch.exe" EXE_ICI

rem Deja posee ? La marque dit l'empreinte de l'exe qu'elle a installe.
set "EXE_MARQUE="
if exist "%MARQUE%" for /f "usebackq tokens=1,* delims==" %%A in ("%MARQUE%") do if /I "%%A"=="exe" set "EXE_MARQUE=%%B"
if defined EXE_MARQUE if /I "%EXE_ICI%"=="%EXE_MARQUE%" goto :deja
if exist "%MARQUE%" call :perimee
goto :suite

:deja
echo Deja installee. Pour revenir a la stable de RetroBat : uninstall-retroarch-nelfeplay.bat
set "CODE=0"
goto :sortie

:perimee
rem La marque reste mais retroarch.exe a change ^(mise a jour de RetroBat^) : la sauvegarde est perimee, on ne restaure rien.
echo RetroArch a change depuis l'installation precedente : sauvegarde perimee ecartee.
if not defined BLANC rmdir /s /q "%ETAT%"
exit /b 0

:suite
rem Seulement par-dessus la stable connue : jamais de retrogradation d'une RetroArch plus recente.
if /I not "%EXE_ICI%"=="%STABLE%" (
  echo retroarch.exe n'est pas la 1.22.2 officielle connue : rien n'est pose.
  echo   attendue : %STABLE%
  echo   en place : %EXE_ICI%
  set "CODE=3"
  goto :sortie
)

if not exist "%EXE_SRC%" (
  echo Notre exe est introuvable : %EXE_SRC%
  goto :echec
)
call :sha "%EXE_SRC%" EXE_LU
if /I not "%EXE_LU%"=="%NOTRE%" (
  echo Notre exe n'a pas l'empreinte attendue : rien n'est pose.
  echo   attendue : %NOTRE%
  echo   lue      : %EXE_LU%
  goto :echec
)

if defined BLANC (
  echo A BLANC : la stable serait copiee dans %SAUV%\retroarch.exe,
  echo A BLANC : %EXE_SRC%
  echo A BLANC : prendrait la place de %RA%\retroarch.exe, la copie retroarch.patched.*.exe serait effacee.
  echo A BLANC : rien n'a ete touche.
  set "CODE=0"
  goto :sortie
)

rem Le refus du joueur : une installation lancee a la main le reprend.
if exist "%REFUS%" del /f /q "%REFUS%"

if exist "%ETAT%" rmdir /s /q "%ETAT%"
mkdir "%SAUV%" || goto :echec
copy /Y "%RA%\retroarch.exe" "%SAUV%\retroarch.exe" >nul || goto :annuler
call :sha "%SAUV%\retroarch.exe" SAUV_LU
if /I not "%SAUV_LU%"=="%STABLE%" (
  echo La sauvegarde de la stable est alteree : rien n'est pose.
  goto :annuler
)
copy /Y "%EXE_SRC%" "%TMP_EXE%" >nul || goto :annuler
call :sha "%TMP_EXE%" TMP_LU
if /I not "%TMP_LU%"=="%NOTRE%" (
  echo La copie de notre exe est incorrecte : rien n'est pose.
  goto :annuler
)

rem La marque avant le remplacement : un arret en route laisse de quoi revenir en arriere.
> "%MARQUE%" (
  echo build=%BUILD%
  echo exe=%NOTRE%
  echo stable=%STABLE%
  echo sources=%SOURCES%
  echo installee=%DATE% %TIME%
)
move /Y "%TMP_EXE%" "%RA%\retroarch.exe" >nul
call :sha "%RA%\retroarch.exe" EXE_FINAL
if /I not "%EXE_FINAL%"=="%NOTRE%" (
  echo Remplacement impossible ^(exe tenu ?^) : retour a la stable.
  copy /Y "%SAUV%\retroarch.exe" "%RA%\retroarch.exe" >nul
  goto :annuler
)

rem emulatorLauncher refait sa copie d'hote netplay depuis le bon exe.
del /f /q "%RA%\retroarch.patched.*.exe" 2>nul

echo Installee. retroarch.exe est maintenant %BUILD% ^(empreinte %NOTRE%^).
echo La stable de RetroBat est gardee dans %SAUV%
echo Pour y revenir : uninstall-retroarch-nelfeplay.bat
set "CODE=0"
goto :sortie

:annuler
if exist "%TMP_EXE%" del /f /q "%TMP_EXE%"
if exist "%ETAT%" rmdir /s /q "%ETAT%"
:echec
echo.
echo Installation non faite.
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
