@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"
if not exist "dist\CuktechTray.exe" (
  echo 还没构建主程序，先跑「构建 酷态科托盘.bat」。
  pause
  exit /b 1
)
echo 正在把 dist\CuktechTray.exe 打进安装包...
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-setup.ps1"
if errorlevel 1 (
  echo.
  echo 打包失败，往上翻看错误。
  pause
  exit /b 1
)
echo.
echo 安装包好了：dist\CuktechTray-Setup.exe
pause
