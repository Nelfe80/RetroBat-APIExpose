@echo off
setlocal

echo ===================================================
echo  RetroBat APIExpose - Build Release
echo ===================================================
echo.

where dotnet >nul 2>nul
if %ERRORLEVEL% neq 0 (
    echo [ERROR] .NET SDK introuvable dans le PATH.
    pause
    exit /b 1
)

set "PLUGIN_ROOT=%~dp0"
set "RELEASE_SCRIPT=%PLUGIN_ROOT%tools\release-framework-dependent.ps1"

if not exist "%RELEASE_SCRIPT%" (
    echo [ERROR] Script release introuvable:
    echo   %RELEASE_SCRIPT%
    pause
    exit /b 1
)

echo [0/2] Arret de l'instance en cours...
taskkill /IM RetroBat.Api.exe /F >nul 2>nul
if %ERRORLEVEL% equ 0 (
    echo   - Instance arretee. Attente...
    timeout /t 2 /nobreak >nul
) else (
    echo   - Aucune instance active.
)

echo.
echo [1/2] Publication Release win-x64...
echo.
powershell -NoProfile -ExecutionPolicy RemoteSigned -File "%RELEASE_SCRIPT%"

if %ERRORLEVEL% neq 0 (
    echo.
    echo [ERROR] Echec de la publication.
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo [2/2] Verification de l'executable racine...
if exist "%PLUGIN_ROOT%RetroBat.Api.exe" (
    echo   - RetroBat.Api.exe mis a jour a la racine.
) else (
    echo [ERROR] RetroBat.Api.exe introuvable a la racine.
    pause
    exit /b 1
)

echo.
echo ===================================================
echo  Build termine avec succes !
echo  Lancez RetroBat.Api.exe depuis la racine.
echo ===================================================
echo.
pause
