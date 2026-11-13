@echo off
chcp 65001 >nul
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0BuildMonitor.ps1"
set "RESULT=%ERRORLEVEL%"
if /i not "%~1"=="--no-ui" pause
exit /b %RESULT%
