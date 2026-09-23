@echo off
setlocal EnableExtensions

set "SCRIPT_DIR=%~dp0"
powershell -NoProfile -ExecutionPolicy RemoteSigned -File "%SCRIPT_DIR%scripts\update-local.ps1" %*
exit /b %ERRORLEVEL%
