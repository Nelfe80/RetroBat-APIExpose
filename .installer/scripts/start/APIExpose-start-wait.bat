@echo off
setlocal EnableExtensions DisableDelayedExpansion

rem APIExpose EmulationStation start hook.
rem This script is intended to be copied to:
rem   emulationstation\.emulationstation\scripts\start\APIExpose-start-wait.bat
rem EmulationStation runs the "start" event synchronously before its normal
rem metadata/window/view initialization, so waiting here blocks ES startup.
rem Pure batch + curl.exe (built into Windows 10+) on purpose: PowerShell
rem one-liners doing web requests are flagged as Trojan:Win32/ClickFix.
rem
rem It must never keep ES frozen. A missing API, a missing .NET 8 runtime, an API
rem process that quits, a startup that takes too long: each case is written to the
rem log and ES continues. The API keeps starting on its own if it can.

for %%I in ("%~dp0..\..\..\..\plugins\APIExpose") do set "PLUGIN_DIR=%%~fI"
set "API_EXE=%PLUGIN_DIR%\RetroBat.Api.exe"
set "LOG_DIR=%PLUGIN_DIR%\.log"
set "LOG_FILE=%LOG_DIR%\es-start-hook.log"
set "READY_URL=http://127.0.0.1:12345/api/v1/startup/ready"
set "HEALTH_URL=http://127.0.0.1:12345/api/v1/health"
rem 64-bit Program Files even if the caller is a 32-bit process.
set "PF64=%ProgramW6432%"
if not defined PF64 set "PF64=%ProgramFiles%"
set "DOTNET_SHARED=%PF64%\dotnet\shared"

if not exist "%LOG_DIR%" mkdir "%LOG_DIR%" >nul 2>&1
echo %date% %time% ES start hook entered.>> "%LOG_FILE%"

if not exist "%API_EXE%" (
  echo %date% %time% ERROR missing API executable: %API_EXE%. ES continues.>> "%LOG_FILE%"
  exit /b 0
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

rem RetroBat.Api.exe needs the ASP.NET Core and Windows Desktop runtimes 8 (x64). Without
rem them Windows shows its own message and the API never becomes ready: start it anyway,
rem so that message is seen, but do not make ES wait for it.
set "DOTNET_OK=1"
if not exist "%DOTNET_SHARED%\Microsoft.AspNetCore.App\8.*" set "DOTNET_OK="
if not exist "%DOTNET_SHARED%\Microsoft.WindowsDesktop.App\8.*" set "DOTNET_OK="

start "APIExpose" /D "%PLUGIN_DIR%" /MIN "%API_EXE%" --urls http://127.0.0.1:12345 --hide-console
echo %date% %time% APIExpose started.>> "%LOG_FILE%"

if not defined DOTNET_OK (
  echo %date% %time% WARNING .NET 8 runtime not found in %DOTNET_SHARED%: reinstall APIExpose or install ASP.NET Core and Desktop Runtime 8 x64. ES continues.>> "%LOG_FILE%"
  exit /b 0
)

rem Give the new process a moment to appear before checking that it is still alive.
ping -n 3 127.0.0.1 >nul

:waitready
rem At most 40 tries, about 40 s to 2 min depending on curl: the API is measured ready
rem 7 to 23 s after its start. Past that, ES continues and the API finishes on its own.
set /a TRIES=40
:waitloop
tasklist /FI "IMAGENAME eq RetroBat.Api.exe" 2>nul | find /I "RetroBat.Api.exe" >nul
if errorlevel 1 (
  echo %date% %time% WARNING RetroBat.Api.exe is not running anymore. ES continues.>> "%LOG_FILE%"
  exit /b 0
)

curl.exe -s -m 2 "%READY_URL%" 2>nul | findstr /C:"\"ready\":true" /C:"\"ready\": true" >nul
if not errorlevel 1 (
  echo %date% %time% APIExpose startup ready. ES can continue.>> "%LOG_FILE%"
  exit /b 0
)

ping -n 2 127.0.0.1 >nul
set /a TRIES-=1
if %TRIES% GTR 0 goto waitloop

echo %date% %time% WARNING timeout waiting for startup/ready. ES continues, the API keeps starting.>> "%LOG_FILE%"
exit /b 0
