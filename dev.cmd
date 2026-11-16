@echo off
chcp 65001 >nul
cd /d "%~dp0"

echo [MeowLauncher] 停止正在运行的实例...
taskkill /IM MeowLauncher.WinUI.exe /F >nul 2>&1

echo [MeowLauncher] 增量编译（通常 2-4 秒）...
dotnet build MeowLauncher.WinUI\MeowLauncher.WinUI.csproj -c Debug -p:Platform=x64 -v:m
if errorlevel 1 (
  echo.
  echo [失败] 编译没过，先修上面报的错。
  pause
  exit /b 1
)


echo [MeowLauncher] 启动...
start "" "MeowLauncher.WinUI\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\MeowLauncher.WinUI.exe"
exit /b 0


