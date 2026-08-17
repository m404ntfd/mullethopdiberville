@echo off
setlocal
title Mullet Hop Waiver Kiosk Installer
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-Waiver-Kiosk.ps1"
if errorlevel 1 (
  echo.
  echo Installation did not finish. Review the message above.
  pause
)
endlocal
