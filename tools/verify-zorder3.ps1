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
Start-Sleep -Seconds 3

# 故意写一个不存在的邻居 MAC：只验证窗口层级，不抢真实设备的 BLE 连接
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

Write-Host "=== E) 不固定 + 正常启动：浮层应置顶、20 秒后自己收 ==="
$p = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Seconds 2
Write-Host ("  +2s  窗口: " + (Get-Flags $p.Id))
Start-Sleep -Seconds 13
Write-Host ("  +15s 窗口: " + (Get-Flags $p.Id))
Start-Sleep -Seconds 9
Write-Host ("  +24s 窗口: " + (Get-Flags $p.Id))
Write-Host ("  进程仍在: " + [bool](Get-Process -Id $p.Id -ErrorAction SilentlyContinue))
if (Get-Process -Id $p.Id -ErrorAction SilentlyContinue) { Stop-Process -Id $p.Id -Force }
Start-Sleep -Seconds 1
