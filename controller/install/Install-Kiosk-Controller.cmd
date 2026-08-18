@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-Kiosk-Controller.ps1"
if errorlevel 1 (
  echo.
  echo The controller installation did not finish successfully.
  pause
)
