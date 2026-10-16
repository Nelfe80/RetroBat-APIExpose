@echo off
setlocal EnableExtensions DisableDelayedExpansion

rem RETROARCH NELFEPLAY : la nocturne de RetroArch du 16 octobre 2026, a la place de la 1.22.2 de RetroBat.
rem
rem RetroArch 1.22.2 plante en ouvrant un replay sur un gros etat (son encodeur de points de controle lit 16 Ko au-dela
rem de la fin de l'etat) : TGM2 sous FBNeo, MAME libretro. La nocturne porte le correctif (commit 8a8cc448b) ; essai du
rem 16/10 : replay de TGM2 enregistre sans plantage, la 1.22.2 tombe 2 s apres l'ordre.
rem
rem Chaque fichier remplace est d'abord sauvegarde dans emulators\retroarch\.nelfeplay\sauvegarde, et la liste des
rem fichiers ajoutes est gardee : uninstall-retroarch-nelfeplay.bat remet le dossier tel qu'il etait.

set "PLUGIN_DIR=%~dp0"
for %%I in ("%PLUGIN_DIR%..\..") do set "RETROBAT_ROOT=%%~fI"
set "RA=%RETROBAT_ROOT%\emulators\retroarch"
set "ARCHIVE=%PLUGIN_DIR%tools\retroarch-nelfeplay\RetroArch_update-2026-10-16.7z"
set "ATTENDU=91e139b339e503da0597b8c4112dd158c992bebff9a239d7eb9ebaff6805d23b"
set "SEVENZIP=%RETROBAT_ROOT%\system\tools\7za.exe"
set "ETAT=%RA%\.nelfeplay"
set "NOUVEAU=%ETAT%\nouveau"
set "SAUVEGARDE=%ETAT%\sauvegarde"
set "AJOUTES=%ETAT%\ajoutes.txt"
set "MARQUE=%ETAT%\installe.txt"

echo RetroArch NelfePlay : installation de la nocturne du 16 octobre 2026
echo   dossier : %RA%
echo.

if exist "%MARQUE%" (
  echo Deja installee. Pour revenir a la version de RetroBat : uninstall-retroarch-nelfeplay.bat
  goto :fin
)
if not exist "%RA%\retroarch.exe" (
  echo RetroArch introuvable : %RA%\retroarch.exe
  goto :echec
)
if not exist "%ARCHIVE%" (
  echo Archive de la nocturne introuvable :
  echo   %ARCHIVE%
  goto :echec
)
if not exist "%SEVENZIP%" (
  echo Outil de decompression de RetroBat introuvable : %SEVENZIP%
  goto :echec
)

rem Rien ne doit tourner : un exe ou une DLL en cours d'utilisation ne se remplace pas.
for %%P in (retroarch.exe emulatorLauncher.exe) do (
  tasklist /FI "IMAGENAME eq %%P" 2>nul | find /I "%%P" >nul && (
    echo %%P tourne : fermez le jeu, puis relancez cette installation.
    goto :echec
  )
)

rem L'archive est-elle bien celle qui a ete essayee ?
set "LU="
for /f "skip=1 delims=" %%H in ('certutil -hashfile "%ARCHIVE%" SHA256 2^>nul') do if not defined LU set "LU=%%H"
set "LU=%LU: =%"
if /I not "%LU%"=="%ATTENDU%" (
  echo L'archive n'a pas l'empreinte attendue : rien n'est installe.
  echo   attendue : %ATTENDU%
  echo   lue      : %LU%
  goto :echec
)

if exist "%ETAT%" rmdir /s /q "%ETAT%"
mkdir "%SAUVEGARDE%" || goto :echec
"%SEVENZIP%" x "%ARCHIVE%" -o"%NOUVEAU%" -y "RetroArch-Win64\*.exe" "RetroArch-Win64\*.dll" "RetroArch-Win64\filters" "RetroArch-Win64\platforms" >nul
if errorlevel 1 (
  echo Decompression impossible : rien n'est installe.
  goto :annuler
)
set "SRC=%NOUVEAU%\RetroArch-Win64"

rem Chaque fichier qui sera remplace part dans la sauvegarde ; chaque fichier nouveau est note pour la desinstallation.
type nul > "%AJOUTES%"
setlocal EnableDelayedExpansion
for /r "%SRC%" %%F in (*) do (
  set "ABS=%%~fF"
  set "REL=!ABS:%SRC%\=!"
  if exist "%RA%\!REL!" (
    for %%D in ("%SAUVEGARDE%\!REL!") do if not exist "%%~dpD" mkdir "%%~dpD"
    copy /Y "%RA%\!REL!" "%SAUVEGARDE%\!REL!" >nul || (endlocal & echo Sauvegarde impossible : !REL! & goto :annuler)
  ) else (
    >>"%AJOUTES%" echo !REL!
  )
)
endlocal

robocopy "%SRC%" "%RA%" /E /R:2 /W:1 /NFL /NDL /NJH /NJS /NP >nul
if errorlevel 8 (
  echo Copie incomplete : retour a la version de RetroBat.
  call "%PLUGIN_DIR%uninstall-retroarch-nelfeplay.bat" /silencieux
  goto :echec
)
rmdir /s /q "%NOUVEAU%"
rem La marque garde l'empreinte de l'exe installe : APIExpose ne leve ses protections que si retroarch.exe est bien
rem celui-la (une mise a jour de RetroBat qui le remplacerait laisse la marque, pas l'exe).
set "EXE_SHA="
for /f "skip=1 delims=" %%H in ('certutil -hashfile "%RA%\retroarch.exe" SHA256 2^>nul') do if not defined EXE_SHA set "EXE_SHA=%%H"
set "EXE_SHA=%EXE_SHA: =%"
> "%MARQUE%" (
  echo nocturne=2026-10-16
  echo archive=%ATTENDU%
  echo exe=%EXE_SHA%
  echo installee=%DATE% %TIME%
)

echo Installee. RetroArch est maintenant la nocturne du 16 octobre 2026.
echo Pour revenir a la version de RetroBat : uninstall-retroarch-nelfeplay.bat
goto :fin

:annuler
if exist "%ETAT%" rmdir /s /q "%ETAT%"
:echec
echo.
echo Installation non faite.
if /I not "%~1"=="/silencieux" pause
exit /b 1

:fin
if /I not "%~1"=="/silencieux" pause
exit /b 0
