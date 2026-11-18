<#
  构建 CuktechTray（酷态科 10 号 Ultra 托盘功率表）
  用法：  .\build.ps1            仅构建 Release
          .\build.ps1 -Publish   构建并发布为单文件 exe 到 dist\
  说明：本机是"绿色版 .NET"，所有缓存路径都必须指到工作区，否则会用系统目录而报权限错误。
#>
[CmdletBinding()]
param([switch]$Publish)

$ErrorActionPreference = "Stop"
$root   = Split-Path -Parent $MyInvocation.MyCommand.Path
$tools  = Join-Path (Split-Path -Parent $root) ".tools"
$dotnet = Join-Path $tools "dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { throw "找不到 .NET SDK：$dotnet" }

$env:DOTNET_ROOT     = Join-Path $tools "dotnet"
$env:DOTNET_CLI_HOME = Join-Path $tools "dotnet-home"
$env:NUGET_PACKAGES  = Join-Path $tools "nuget"
$env:TEMP            = Join-Path $tools "tmp"
$env:TMP             = Join-Path $tools "tmp"
$env:APPDATA         = Join-Path $tools "userprof\AppData\Roaming"
$env:LOCALAPPDATA    = Join-Path $tools "userprof\AppData\Local"
$env:USERPROFILE     = Join-Path $tools "userprof"
foreach ($d in @($env:TEMP, $env:APPDATA, $env:LOCALAPPDATA)) {
    if (-not (Test-Path $d)) { New-Item -ItemType Directory -Force -Path $d | Out-Null }
}

$proj = Join-Path $root "src\CuktechTray\CuktechTray.csproj"
& $dotnet build $proj -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "构建失败" }

$out = Join-Path $root "src\CuktechTray\bin\Release\net8.0-windows10.0.19041.0\CuktechTray.exe"
Write-Host "构建完成：$out" -ForegroundColor Green

if ($Publish) {
    $dist = Join-Path $root "dist"
    & $dotnet publish $proj -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true -o $dist --nologo
    if ($LASTEXITCODE -ne 0) { throw "发布失败" }
    $exe = Join-Path $dist "CuktechTray.exe"
    $mb  = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host "发布完成：$exe ($mb MB，自包含运行时，目标机无需装 .NET)" -ForegroundColor Green
}
