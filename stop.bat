@echo off
setlocal EnableExtensions

rem Stops APIExpose. Pure batch on purpose (no PowerShell):
rem AV heuristics flag powershell one-liners as ClickFix trojans.

echo Stopping APIExpose...
taskkill /IM RetroBat.Api.exe /F >nul 2>&1
if errorlevel 1 (
  echo No APIExpose process found.
) else (
  echo APIExpose stopped.
)

endlocal
