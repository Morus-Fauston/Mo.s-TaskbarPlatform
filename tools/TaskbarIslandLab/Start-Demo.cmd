@echo off
powershell.exe -NoProfile -File "%~dp0Demo.ps1"
if errorlevel 1 pause
