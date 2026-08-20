@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-Mullet-Hop-POS.ps1"
if errorlevel 1 pause
