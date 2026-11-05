@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-LocalNuGetFeed.ps1" %*
set "result=%errorlevel%"
pause
exit /b %result%
