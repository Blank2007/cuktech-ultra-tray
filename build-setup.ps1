<#
  构建 CuktechTray-Setup.exe（酷态科 10 号 Ultra 托盘功率表 · 安装程序）

  用法：
    .\build-setup.ps1
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build-setup.ps1

  它会做这些事（一条命令出安装包）：
    1. 取当前最新的 dist\CuktechTray.exe；
    2. 用 Deflate 压缩成 setup\Payload\payload.bin，并算出 SHA256；
    3. 生成 setup\Payload\PayloadInfo.g.cs（版本号 / 大小 / 哈希 / 构建时间）；
    4. 从主程序里抽一个图标给安装包用（失败就用默认图标）；
    5. dotnet publish 出单文件自包含的 CuktechTray-Setup.exe，拷到 dist\；
    6. 打印大小和 SHA256。

  注意：setup 工程内嵌 payload.bin，所以必须先跑本脚本生成它，直接 dotnet build 会失败。
        本机是"绿色版 .NET"，所有缓存路径都指到工作区（与 build.ps1 保持一致）。
#>
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$tools = Join-Path (Split-Path -Parent $root) ".tools"
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

function Write-Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }

# ---------- 1. 取最新的主程序 ----------
$appExe = Join-Path $root "dist\CuktechTray.exe"
if (-not (Test-Path $appExe)) {
    throw ("找不到主程序：" + $appExe + " 。先跑 build.ps1 -Publish 发布主程序，再来打包。")
}
$appItem = Get-Item $appExe
Write-Step ("打包源：{0}（{1:N0} 字节，改动于 {2}）" -f $appItem.FullName, $appItem.Length, $appItem.LastWriteTime)

# ---------- 2. Deflate 压缩主程序 ----------
$payloadDir = Join-Path $root "setup\Payload"
if (-not (Test-Path $payloadDir)) { New-Item -ItemType Directory -Force -Path $payloadDir | Out-Null }
$payloadPath = Join-Path $payloadDir "payload.bin"

Write-Step "压缩主程序（Deflate Optimal）…"
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$inStream = [System.IO.File]::OpenRead($appExe)
try {
    $outStream = [System.IO.File]::Create($payloadPath)
    try {
        $deflate = New-Object System.IO.Compression.DeflateStream -ArgumentList @(
            $outStream,
            [System.IO.Compression.CompressionLevel]::Optimal,
            $true
        )
        try {
            $buffer = New-Object byte[] (1048576)
            while (($read = $inStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                $deflate.Write($buffer, 0, $read)
            }
        }
        finally {
            $deflate.Dispose()
        }
    }
    finally {
        $outStream.Dispose()
    }
}
finally {
    $inStream.Dispose()
}
$sw.Stop()

$payloadItem = Get-Item $payloadPath
$rawSize = $appItem.Length
$compSize = $payloadItem.Length
$ratio = 0
if ($rawSize -gt 0) { $ratio = [math]::Round($compSize / $rawSize * 100, 1) }
$savedMb = [math]::Round(($rawSize - $compSize) / 1MB, 1)
Write-Host ("    {0:N0} → {1:N0} 字节（{2}%，省 {3} MB，用时 {4:N1} 秒）" -f $rawSize, $compSize, $ratio, $savedMb, $sw.Elapsed.TotalSeconds) -ForegroundColor DarkGray

# ---------- 3. 生成 PayloadInfo.g.cs ----------
Write-Step "计算 SHA256 并生成 PayloadInfo.g.cs…"
$sha = (Get-FileHash -Path $appExe -Algorithm SHA256).Hash
$ver = $appItem.VersionInfo.FileVersion
if ([string]::IsNullOrWhiteSpace($ver)) { $ver = "0.1.0" }
$ver = $ver.Trim()
$buildTime = [DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")

$generated = @"
// 本文件由项目根的 build-setup.ps1 自动生成，不要手改。
// 对应主程序：dist\CuktechTray.exe
namespace CuktechTraySetup;

internal static class PayloadInfo
{
    public const string Sha256 = "$sha";
    public const long RawSize = $rawSize;
    public const long CompressedSize = $compSize;
    public const string AppVersion = "$ver";
    public const string AppFileName = "CuktechTray.exe";
    public const string BuildTimeUtc = "$buildTime";
}
"@
$genPath = Join-Path $payloadDir "PayloadInfo.g.cs"
[System.IO.File]::WriteAllText($genPath, $generated, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "    版本 $ver / SHA256 $sha" -ForegroundColor DarkGray

# ---------- 4. 从主程序抽图标 ----------
$icoPath = Join-Path $root "setup\app.ico"
try {
    Add-Type -AssemblyName System.Drawing
    $icon = [System.Drawing.Icon]::ExtractAssociatedIcon($appExe)
    if ($null -ne $icon) {
        $fs = [System.IO.File]::Create($icoPath)
        try { $icon.Save($fs) } finally { $fs.Dispose(); $icon.Dispose() }
        Write-Host "    已从主程序提取图标：setup\app.ico" -ForegroundColor DarkGray
    }
    else {
        throw "主程序没有关联图标"
    }
}
catch {
    Write-Warning ("提取图标失败，安装包用默认图标：" + $_.Exception.Message)
    if (Test-Path $icoPath) { Remove-Item $icoPath -Force -ErrorAction SilentlyContinue }
}

# ---------- 5. 发布安装程序 ----------
$proj = Join-Path $root "setup\CuktechTraySetup.csproj"
$pubOut = Join-Path $root "setup\bin\publish"
if (Test-Path $pubOut) { Remove-Item $pubOut -Recurse -Force }

Write-Step "发布安装程序（单文件自包含，内嵌主程序，可能要几分钟）…"
$sw2 = [System.Diagnostics.Stopwatch]::StartNew()
& $dotnet publish $proj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o $pubOut --nologo
if ($LASTEXITCODE -ne 0) { throw "安装程序发布失败（exit $LASTEXITCODE）" }
$sw2.Stop()

$builtExe = Join-Path $pubOut "CuktechTray-Setup.exe"
if (-not (Test-Path $builtExe)) { throw "发布目录里没有 CuktechTray-Setup.exe：$pubOut" }

$dist = Join-Path $root "dist"
if (-not (Test-Path $dist)) { New-Item -ItemType Directory -Force -Path $dist | Out-Null }
$setupExe = Join-Path $dist "CuktechTray-Setup.exe"
Copy-Item -Path $builtExe -Destination $setupExe -Force

$setupItem = Get-Item $setupExe
$setupSha = (Get-FileHash -Path $setupExe -Algorithm SHA256).Hash

Write-Host ""
Write-Host "安装包构建完成" -ForegroundColor Green
Write-Host "  文件          : $($setupItem.FullName)"
Write-Host ("  大小          : {0:N0} 字节（{1:N1} MB）" -f $setupItem.Length, ($setupItem.Length / 1MB))
Write-Host "  SHA256        : $setupSha"
Write-Host ("  内嵌主程序    : CuktechTray.exe v{0}，{1:N0} 字节（压缩后 {2:N0}）" -f $ver, $rawSize, $compSize)
Write-Host "  SHA256(主程序): $sha"
Write-Host ("  耗时          : 压缩 {0:N1} 秒 + 发布 {1:N1} 秒" -f $sw.Elapsed.TotalSeconds, $sw2.Elapsed.TotalSeconds)
Write-Host ""
Write-Host "用法：双击 dist\CuktechTray-Setup.exe 走图形向导；静默参数见 --help。" -ForegroundColor DarkGray
