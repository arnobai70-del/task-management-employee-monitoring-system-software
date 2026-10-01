@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\windows\build-employee-test-installer.ps1" %*
if errorlevel 1 (
  echo.
  echo TaskMonitoring Employee TEST installer build failed. Review the message above.
  pause
)
endlocal
