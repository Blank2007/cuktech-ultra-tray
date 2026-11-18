<#
  启动托盘程序（构建产物）。加 -Panel 直接弹出功率面板，方便确认能连上。
#>
param([switch]$Panel)

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$tools = Join-Path (Split-Path -Parent $root) ".tools"
$exe  = Join-Path $root "src\CuktechTray\bin\Release\net8.0-windows10.0.19041.0\CuktechTray.exe"
if (-not (Test-Path $exe)) { throw "先运行 .\build.ps1 生成 $exe" }

# apphost 需要知道运行时在哪
$env:DOTNET_ROOT = Join-Path $tools "dotnet"
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"

if ($Panel) { Start-Process -FilePath $exe -ArgumentList "--panel" } else { Start-Process -FilePath $exe }
Write-Host "已启动。托盘区会多一个数字图标；右键有菜单。" -ForegroundColor Green
