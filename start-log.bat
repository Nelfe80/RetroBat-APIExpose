@echo off
setlocal

set "PLUGIN_DIR=%~dp0"
set "API_EXE=%PLUGIN_DIR%RetroBat.Api.exe"

if not exist "%API_EXE%" (
  echo APIExpose release executable not found:
  echo   %API_EXE%
  echo Build or copy RetroBat.Api.exe to the plugin root first.
  pause
  exit /b 1
)

echo APIExpose log mode
echo Plugin: %PLUGIN_DIR%
echo Executable: %API_EXE%
echo.

powershell -NoProfile -ExecutionPolicy Bypass -Command "$exe=Get-Item -LiteralPath '%API_EXE%'; $hash=Get-FileHash -Algorithm SHA256 -LiteralPath $exe.FullName; Write-Host ('File version: ' + $exe.VersionInfo.FileVersion); Write-Host ('Product version: ' + $exe.VersionInfo.ProductVersion); Write-Host ('Size: ' + $exe.Length + ' bytes'); Write-Host ('SHA-256: ' + $hash.Hash)"

echo.
echo Stopping previous APIExpose / RetroBat / EmulationStation processes...
powershell -NoProfile -ExecutionPolicy Bypass -Command "$names=@('RetroBat.Api','emulationstation','RetroBat'); foreach($name in $names){ $procs=Get-Process -Name $name -ErrorAction SilentlyContinue; foreach($proc in $procs){ Write-Host ('Stopping ' + $proc.ProcessName + ' PID ' + $proc.Id); Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } }; Start-Sleep -Seconds 1"

echo.
echo Starting APIExpose in foreground. Close this window or press Ctrl+C to stop it.
echo URL: http://127.0.0.1:12345
echo.

"%API_EXE%" --urls http://127.0.0.1:12345

echo.
echo APIExpose process exited.
pause
endlocal
