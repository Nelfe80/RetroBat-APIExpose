@echo off
setlocal EnableExtensions DisableDelayedExpansion

rem APIExpose EmulationStation start hook.
rem This script is intended to be copied to:
rem   emulationstation\.emulationstation\scripts\start\APIExpose-start-wait.bat
rem EmulationStation runs the "start" event synchronously before its normal
rem metadata/window/view initialization, so waiting here blocks ES startup.

for %%I in ("%~dp0..\..\..\..\plugins\APIExpose") do set "PLUGIN_DIR=%%~fI"
set "API_EXE=%PLUGIN_DIR%\RetroBat.Api.exe"
set "LOG_DIR=%PLUGIN_DIR%\.log"
set "LOG_FILE=%LOG_DIR%\es-start-hook.log"

if not exist "%LOG_DIR%" mkdir "%LOG_DIR%" >nul 2>&1

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
"$ErrorActionPreference='SilentlyContinue'; ^
 $api='%API_EXE%'; ^
 $wd='%PLUGIN_DIR%'; ^
 $log='%LOG_FILE%'; ^
 function Log([string]$m){ $stamp=(Get-Date).ToString('yyyy-MM-dd HH:mm:ss.fff'); Add-Content -LiteralPath $log -Value ($stamp + ' ' + $m) -Encoding UTF8 }; ^
 Log 'ES start hook entered.'; ^
 if (-not (Test-Path -LiteralPath $api)) { Log ('ERROR missing API executable: ' + $api); exit 2 }; ^
 $ready=$false; ^
 try { $r=Invoke-RestMethod -Uri 'http://127.0.0.1:12345/api/v1/startup/ready' -TimeoutSec 2; if ($r.ready -eq $true) { $ready=$true } } catch { }; ^
 if (-not $ready) { ^
   $healthy=$false; ^
   try { $h=Invoke-RestMethod -Uri 'http://127.0.0.1:12345/api/v1/health' -TimeoutSec 2; if ($h.status) { $healthy=$true; Log ('API already running, health=' + $h.status) } } catch { }; ^
   if (-not $healthy) { ^
     $procs=Get-Process -Name 'RetroBat.Api' -ErrorAction SilentlyContinue; ^
     foreach($p in $procs){ try { Log ('Stopping stale RetroBat.Api PID ' + $p.Id); Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch { } }; ^
     Start-Sleep -Milliseconds 500; ^
     try { ^
       Unblock-File -LiteralPath $api -ErrorAction SilentlyContinue; ^
       $p=Start-Process -FilePath $api -ArgumentList @('--urls','http://127.0.0.1:12345') -WorkingDirectory $wd -WindowStyle Hidden -PassThru -ErrorAction Stop; ^
       Log ('APIExpose started PID ' + $p.Id); ^
     } catch { Log ('ERROR failed to start APIExpose: ' + $_.Exception.Message); exit 3 } ^
   } ^
 }; ^
 $deadline=(Get-Date).AddMinutes(10); ^
 do { ^
   try { $r=Invoke-RestMethod -Uri 'http://127.0.0.1:12345/api/v1/startup/ready' -TimeoutSec 2; if ($r.ready -eq $true) { Log 'APIExpose startup ready. ES can continue.'; exit 0 } } catch { }; ^
   Start-Sleep -Milliseconds 500; ^
 } while ((Get-Date) -lt $deadline); ^
 Log 'ERROR timeout waiting for /api/v1/startup/ready'; ^
 exit 4"

exit /b %ERRORLEVEL%
