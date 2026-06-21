@echo off
setlocal EnableExtensions DisableDelayedExpansion

rem Removes only the APIExpose EmulationStation start hook.
rem RetroBat's updatestores.bat is never modified.

set "PLUGIN_DIR=%~dp0"
for %%I in ("%PLUGIN_DIR%..\..") do set "RETROBAT_ROOT=%%~fI"

set "TARGET=%RETROBAT_ROOT%\emulationstation\.emulationstation\scripts\start\APIExpose-start-wait.bat"

if exist "%TARGET%" (
  del "%TARGET%"
  echo Removed APIExpose ES start hook:
  echo   %TARGET%
) else (
  echo APIExpose ES start hook was not installed:
  echo   %TARGET%
)

echo.
echo RetroBat's updatestores.bat was not modified.
pause
exit /b 0
