<#
  用项目 venv 的 Python 启动 fetch_token.py
  用法:
    .\run.ps1                # 前台运行 (Ctrl+C 结束)
    .\run.ps1 -Background    # 后台独立进程, 日志写 out\poll.log, 打印 PID
#>
[CmdletBinding()]
param(
    [switch]$Background
)

$ErrorActionPreference = "Stop"

$Here   = Split-Path -Parent $MyInvocation.MyCommand.Path
$Python = "D:\dsh projects\.tools\pyvenv\Scripts\python.exe"
$Script = Join-Path $Here "fetch_token.py"
$OutDir = Join-Path $Here "out"

if (-not (Test-Path -LiteralPath $Python)) {
    throw "未找到 venv Python: $Python"
}
if (-not (Test-Path -LiteralPath $Script)) {
    throw "未找到脚本: $Script"
}
if (-not (Test-Path -LiteralPath $OutDir)) {
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
}

# 控制台与文件统一 UTF-8
$env:PYTHONIOENCODING = "utf-8"

if ($Background) {
    $logOut = Join-Path $OutDir "poll.log"
    $logErr = Join-Path $OutDir "poll.err.log"
    $argList = @("-u", "fetch_token.py")
    $proc = Start-Process -FilePath $Python -ArgumentList $argList -WorkingDirectory $Here -RedirectStandardOutput $logOut -RedirectStandardError $logErr -PassThru -WindowStyle Hidden
    Write-Host "PID=$($proc.Id)"
    Write-Host "LOG=$logOut"
    Write-Host "ERR=$logErr"
    Write-Host "STATUS=$(Join-Path $OutDir 'status.txt')"
}
else {
    & $Python -u $Script
    exit $LASTEXITCODE
}
