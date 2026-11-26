@echo off
setlocal DisableDelayedExpansion
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0maintenance.ps1"
set "maintenance_result=%errorlevel%"
echo.
pause
exit /b %maintenance_result%
