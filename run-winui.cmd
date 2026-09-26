@echo off
powershell.exe -NoLogo -NoProfile -File "%~dp0run-winui.ps1" %*
exit /b %errorlevel%
