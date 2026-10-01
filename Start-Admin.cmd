@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\windows\start-admin-local.ps1" %*
if errorlevel 1 (
  echo.
  echo TaskMonitoring Admin failed to start. Review the message above.
  pause
)
endlocal
