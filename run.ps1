<#
  启动托盘程序（构建产物）。加 -Panel 直接弹出功率面板，方便确认能连上。
  加 -Aot 启动 NativeAOT 版（0.4.0，src\CuktechTrayAot\out\CuktechTray.exe，不需要 .NET 运行时）。
#>
param([switch]$Panel, [switch]$Aot)

$root  = Split-Path -Parent $MyInvocation.MyCommand.Path
$tools = Join-Path (Split-Path -Parent $root) ".tools"
if ($Aot) {
    $exe = Join-Path $root "src\CuktechTrayAot\out\CuktechTray.exe"
    if (-not (Test-Path $exe)) { throw "先运行 .\build.ps1 -Aot 生成 $exe" }
} else {
    $exe = Join-Path $root "src\CuktechTray\bin\Release\net8.0-windows10.0.19041.0\CuktechTray.exe"
    if (-not (Test-Path $exe)) { throw "先运行 .\build.ps1 生成 $exe" }
    # apphost 需要知道运行时在哪；AOT 版是原生程序，不需要
    $env:DOTNET_ROOT = Join-Path $tools "dotnet"
    $env:PATH = "$env:DOTNET_ROOT;$env:PATH"
}

$argList = @()
if ($Panel) { $argList += "--panel" }
if ($argList.Count) { Start-Process -FilePath $exe -ArgumentList $argList } else { Start-Process -FilePath $exe }
$label = "WinForms 版"
if ($Aot) { $label = "AOT 版" }
Write-Host "已启动 $label。托盘区会多一个数字图标；右键有菜单。" -ForegroundColor Green
