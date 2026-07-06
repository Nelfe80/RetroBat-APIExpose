@echo off
setlocal EnableExtensions DisableDelayedExpansion

rem APIExpose EmulationStation start hook.
rem This script is intended to be copied to:
rem   emulationstation\.emulationstation\scripts\start\APIExpose-start-wait.bat
rem EmulationStation runs the "start" event synchronously before its normal
rem metadata/window/view initialization, so waiting here blocks ES startup.
rem Pure batch + curl.exe (built into Windows 10+) on purpose: PowerShell
rem one-liners doing web requests are flagged as Trojan:Win32/ClickFix.

for %%I in ("%~dp0..\..\..\..\plugins\APIExpose") do set "PLUGIN_DIR=%%~fI"
set "API_EXE=%PLUGIN_DIR%\RetroBat.Api.exe"
set "LOG_DIR=%PLUGIN_DIR%\.log"
set "LOG_FILE=%LOG_DIR%\es-start-hook.log"
set "READY_URL=http://127.0.0.1:12345/api/v1/startup/ready"
set "HEALTH_URL=http://127.0.0.1:12345/api/v1/health"

if not exist "%LOG_DIR%" mkdir "%LOG_DIR%" >nul 2>&1
echo %date% %time% ES start hook entered.>> "%LOG_FILE%"

if not exist "%API_EXE%" (
  echo %date% %time% ERROR missing API executable: %API_EXE%>> "%LOG_FILE%"
  exit /b 2
)

rem Already fully started?
curl.exe -s -m 2 "%READY_URL%" 2>nul | findstr /C:"\"ready\":true" /C:"\"ready\": true" >nul
if not errorlevel 1 (
  echo %date% %time% APIExpose already ready.>> "%LOG_FILE%"
  exit /b 0
)

rem Healthy but still starting? Then just wait below. Otherwise (re)start it.
curl.exe -s -m 2 "%HEALTH_URL%" 2>nul | find /I "healthy" >nul
if not errorlevel 1 (
  echo %date% %time% API already running, waiting for readiness.>> "%LOG_FILE%"
  goto waitready
)

rem Not answering: clear any stale process, then start fresh.
tasklist /FI "IMAGENAME eq RetroBat.Api.exe" 2>nul | find /I "RetroBat.Api.exe" >nul
if not errorlevel 1 (
  echo %date% %time% Stopping stale RetroBat.Api process.>> "%LOG_FILE%"
  taskkill /IM RetroBat.Api.exe /F >nul 2>&1
  ping -n 2 127.0.0.1 >nul
)

start "APIExpose" /D "%PLUGIN_DIR%" /MIN "%API_EXE%" --urls http://127.0.0.1:12345 --hide-console
echo %date% %time% APIExpose started.>> "%LOG_FILE%"

:waitready
rem Up to 10 minutes: 600 x (2s curl timeout max + 1s pause).
set /a TRIES=600
:waitloop
curl.exe -s -m 2 "%READY_URL%" 2>nul | findstr /C:"\"ready\":true" /C:"\"ready\": true" >nul
if not errorlevel 1 (
  echo %date% %time% APIExpose startup ready. ES can continue.>> "%LOG_FILE%"
  exit /b 0
)
ping -n 2 127.0.0.1 >nul
set /a TRIES-=1
if %TRIES% GTR 0 goto waitloop

echo %date% %time% ERROR timeout waiting for startup/ready.>> "%LOG_FILE%"
exit /b 4
