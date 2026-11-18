@echo off
rem 双击即可构建（绕过 PowerShell 脚本执行策略）
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
pause
