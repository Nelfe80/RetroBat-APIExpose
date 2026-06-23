@echo off
setlocal

set "PLUGIN_DIR=%~dp0"
for %%I in ("%PLUGIN_DIR%..\..") do set "RETROBAT_ROOT=%%~fI"

set "API_EXE=%PLUGIN_DIR%RetroBat.Api.exe"
set "RETROBAT_EXE=%RETROBAT_ROOT%\RetroBat.exe"

if not exist "%API_EXE%" (
  echo APIExpose release executable not found:
  echo   %API_EXE%
  echo Build or copy RetroBat.Api.exe to the plugin root first.
  pause
  exit /b 1
)

if not exist "%RETROBAT_EXE%" (
  echo RetroBat executable not found:
  echo   %RETROBAT_EXE%
  echo This launcher expects APIExpose to be installed in RetroBat\plugins\APIExpose.
  pause
  exit /b 1
)

echo Stopping previous APIExpose / RetroBat / EmulationStation processes...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
"$names=@('RetroBat.Api','emulationstation','RetroBat'); ^
 foreach($name in $names){ ^
   $procs=Get-Process -Name $name -ErrorAction SilentlyContinue; ^
   foreach($proc in $procs){ ^
     Write-Host ('Stopping ' + $proc.ProcessName + ' PID ' + $proc.Id); ^
     Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue; ^
   } ^
 }; ^
 Start-Sleep -Seconds 1"

echo Starting APIExpose...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
"$api='%API_EXE%'; ^
 $wd='%PLUGIN_DIR%'; ^
 Unblock-File -Path $api -ErrorAction SilentlyContinue; ^
 try { ^
   $proc = Start-Process -FilePath $api -ArgumentList @('--urls','http://127.0.0.1:12345') -WorkingDirectory $wd -WindowStyle Hidden -PassThru -ErrorAction Stop; ^
   if ($null -eq $proc) { throw 'Start-Process returned no process.' } ^
   Write-Host ('APIExpose started as hidden process PID ' + $proc.Id); ^
   exit 0; ^
 } catch { ^
   Write-Host 'ERROR: APIExpose failed to start.' -ForegroundColor Red; ^
   Write-Host $_.Exception.Message -ForegroundColor Red; ^
  exit 1; ^
 }"

if errorlevel 1 (
  echo APIExpose startup failed.
  pause
  exit /b 1
)

echo Waiting for APIExpose startup processing to complete...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
"$deadline=(Get-Date).AddMinutes(10); ^
 do { ^
   try { ^
     $ready=Invoke-RestMethod -Uri 'http://127.0.0.1:12345/api/v1/startup/ready' -TimeoutSec 2; ^
     if ($ready.ready -eq $true) { exit 0 } ^
   } catch {} ^
   Start-Sleep -Milliseconds 500; ^
 } while ((Get-Date) -lt $deadline); ^
 exit 1"

if errorlevel 1 (
  echo APIExpose startup readiness endpoint did not become ready.
  echo RetroBat was not started because APIExpose did not finish its gamelist startup processing.
  pause
  exit /b 1
)

echo Starting RetroBat...
start "RetroBat" /D "%RETROBAT_ROOT%" "%RETROBAT_EXE%"

endlocal
exit /b 0
