@echo off
setlocal
chcp 65001 >nul
title Cat Assistant - AI Passport Flasher

set "SCRIPT_DIR=%~dp0"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%Flash-CatPassport.ps1" %*
set "RESULT=%ERRORLEVEL%"

echo.
if "%RESULT%"=="0" (
  echo [DONE] The flashing workflow completed successfully.
) else (
  echo [FAILED] Review the message above, correct the issue, and run again.
)
echo Logs: %SCRIPT_DIR%logs
pause
exit /b %RESULT%
