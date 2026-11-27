@echo off
chcp 65001 >nul
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\dev_start.ps1" %*
if errorlevel 1 (
  echo.
  echo [失败] 编译没过，先修上面报的错。
  pause
  exit /b 1
)
exit /b 0
