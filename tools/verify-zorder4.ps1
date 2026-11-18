param([string]$Exe = "D:\dsh projects\cuktech-ultra-tray\src\CuktechTray\bin\Release\net8.0-windows10.0.19041.0\CuktechTray.exe")
$ErrorActionPreference = "Stop"
$env:DOTNET_ROOT = "D:\dsh projects\.tools\dotnet"
$root = Split-Path -Parent $Exe
$cfg  = Join-Path $root "settings.json"

function Get-Flags([int]$procId) {
    $out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File "D:\dsh projects\cuktech-ultra-tray\tools\window-flags.ps1" -ProcId $procId
    return ($out -join " ")
}

Get-Process -Name CuktechTray -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process -Name notepad -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 3

@"
{
  "ConfigVersion": 2,
  "Mac": "AA:BB:CC:DD:EE:01",
  "TokenHex": "00112233445566778899aabb",
  "PollMs": 1000,
  "AutoStart": false,
  "ShowTenthsOfWatt": true,
  "IconStyle": "Dark",
  "ShowedTrayTip": true,
  "PanelPinned": false,
  "PanelX": -2147483648,
  "PanelY": -2147483648,
  "PanelTopMost": false
}
"@ | Set-Content -Path $cfg -Encoding UTF8

Write-Host "=== F) 浮层挂着时被别的程序抢走前台（模拟锁屏/切窗口）==="
$p = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Seconds 3
Write-Host ("  启动后 : " + (Get-Flags $p.Id))

Start-Sleep -Seconds 11
Write-Host ("  +14s   : " + (Get-Flags $p.Id))

Write-Host "  -- 现在开记事本抢前台 --"
$n = Start-Process notepad -PassThru
Start-Sleep -Seconds 1
$ws = New-Object -ComObject WScript.Shell
$ws.AppActivate($n.Id) | Out-Null
Start-Sleep -Seconds 3
Write-Host ("  抢前台后: " + (Get-Flags $p.Id))

if (Get-Process -Id $n.Id -ErrorAction SilentlyContinue) { Stop-Process -Id $n.Id -Force }
if (Get-Process -Id $p.Id -ErrorAction SilentlyContinue) { Stop-Process -Id $p.Id -Force }
Start-Sleep -Seconds 1
