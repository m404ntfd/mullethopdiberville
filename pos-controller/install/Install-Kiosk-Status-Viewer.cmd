@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-Kiosk-Status-Viewer.ps1"
if errorlevel 1 pause
