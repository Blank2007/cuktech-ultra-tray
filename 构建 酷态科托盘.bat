@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"
echo 正在构建酷态科托盘功率表（Release）...
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" -Publish
if errorlevel 1 (
  echo.
  echo 构建失败，往上翻看错误。
  pause
  exit /b 1
)
echo.
echo 构建完成：dist\CuktechTray.exe
echo 想打安装包就再运行「构建安装包.bat」。
pause
