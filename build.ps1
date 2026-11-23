<#
  构建 CuktechTray（酷态科 10 号 Ultra 托盘功率表）
  用法：  .\build.ps1                仅构建 WinForms 版 Release
          .\build.ps1 -Publish       构建并把 WinForms 版发布成单文件 exe 到 dist\
          .\build.ps1 -Aot           构建 NativeAOT 版（0.4.0），产物在 src\CuktechTrayAot\out\
          .\build.ps1 -Aot -Publish  再把 AOT 产物拷成 dist\CuktechTray.exe（会覆盖绿色版）
  说明：
    * 本机是"绿色版 .NET"，所有缓存路径都必须指到工作区，否则会用系统目录而报权限错误。
    * AOT 需要 C 链接器。本机没有 Visual Studio / MSVC，用的是工作区里的 lld-link + xwin 的
      MSVC 库（.tools\lld、.tools\xwin）。ILC 默认会去找 vcvarsall.bat，找不到就报
      "Platform linker not found"，所以这里显式传 CppLinker / CppLibCreator，并用 LIB 指路。
#>
[CmdletBinding()]
param([switch]$Publish, [switch]$Aot)

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

if ($Aot) {
    # ---- NativeAOT 版：直接产出单文件，体积只有自包含版的十分之一 ----
    $projA = Join-Path $root "src\CuktechTrayAot\CuktechTrayAot.csproj"
    $outA  = Join-Path $root "src\CuktechTrayAot\out"

    $lld  = Join-Path $tools "lld\lld-link.exe"
    $llib = Join-Path $tools "lld\llvm-lib.exe"
    $extra = @()
    if ((Test-Path $lld) -and (Test-Path $llib)) {
        $libDirs = @(
            (Join-Path $tools "xwin\crt\lib\x86_64"),
            (Join-Path $tools "xwin\sdk\lib\ucrt\x86_64"),
            (Join-Path $tools "xwin\sdk\lib\um\x86_64")
        )
        foreach ($d in $libDirs) { if (-not (Test-Path $d)) { throw "缺 MSVC 库目录（xwin 没解压全）：$d" } }
        $env:LIB = ($libDirs -join ";")
        $extra = @(
            "-p:IlcUseEnvironmentalTools=true",
            "-p:CppLinker=$lld",
            "-p:CppLibCreator=$llib"
        )
        Write-Host "链接器：$lld（MSVC 库来自 .tools\xwin）" -ForegroundColor DarkGray
    } else {
        Write-Host "没找到 .tools\lld，交给 ILC 自己找 vcvarsall（本机大概会失败）" -ForegroundColor Yellow
    }

    & $dotnet publish $projA -c Release -r win-x64 -o $outA --nologo @extra
    if ($LASTEXITCODE -ne 0) { throw "AOT 发布失败" }

    $exe = Join-Path $outA "CuktechTray.exe"
    $mb  = [math]::Round((Get-Item $exe).Length / 1MB, 2)
    Write-Host "AOT 发布完成：$exe ($mb MB，单文件，目标机无需装 .NET)" -ForegroundColor Green
    Write-Host "自测：& '$exe' --selftest   退出码 0/1，明细在 $outA\logs\app.log" -ForegroundColor DarkGray

    if ($Publish) {
        $dist = Join-Path $root "dist"
        if (-not (Test-Path $dist)) { New-Item -ItemType Directory -Path $dist | Out-Null }
        Copy-Item $exe (Join-Path $dist "CuktechTray.exe") -Force
        Write-Host "已覆盖绿色版：$(Join-Path $dist 'CuktechTray.exe') ($mb MB)" -ForegroundColor Green
    }
    return
}

# ---- WinForms 版（0.3.3 及以前的老路子，留着做对照）----
$projW = Join-Path $root "src\CuktechTray\CuktechTray.csproj"
& $dotnet build $projW -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "构建失败" }

$outW = Join-Path $root "src\CuktechTray\bin\Release\net8.0-windows10.0.19041.0\CuktechTray.exe"
Write-Host "构建完成：$outW" -ForegroundColor Green

if ($Publish) {
    $dist = Join-Path $root "dist"
    $args = @(
        "publish", $projW, "-c", "Release", "-r", "win-x64", "--self-contained", "true",
        "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:EnableCompressionInSingleFile=true", "-o", $dist, "--nologo"
    )
    & $dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "发布失败" }
    $exe = Join-Path $dist "CuktechTray.exe"
    $mb  = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host "发布完成：$exe ($mb MB，自包含运行时，目标机无需装 .NET)" -ForegroundColor Green
}
