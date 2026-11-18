@echo off
rem Installs XrShift for you (no administrator rights needed). Double-click to run.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup\install-app.ps1" -From "%~dp0XrShift"
echo.
pause
