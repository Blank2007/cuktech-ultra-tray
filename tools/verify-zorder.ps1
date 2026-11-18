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

Write-Host "=== A) 旧配置残留 PanelTopMost=true + --pin（常驻）==="
@"
{
  "ConfigVersion": 1,
  "Mac": "AA:BB:CC:DD:EE:FF",
  "TokenHex": "00112233445566778899aabb",
  "PollMs": 1000,
  "AutoStart": false,
  "ShowTenthsOfWatt": true,
  "IconStyle": "Dark",
  "ShowedTrayTip": true,
  "PanelPinned": false,
  "PanelX": -2147483648,
  "PanelY": -2147483648,
  "PanelTopMost": true
}
"@ | Set-Content -Path $cfg -Encoding UTF8

$p = Start-Process -FilePath $Exe -ArgumentList "--pin" -PassThru
Start-Sleep -Seconds 6
$alive = Get-Process -Id $p.Id -ErrorAction SilentlyContinue
Write-Host ("  进程存活: " + [bool]$alive)
Write-Host ("  窗口: " + (Get-Flags $p.Id))
$after = Get-Content $cfg -Raw | ConvertFrom-Json
Write-Host ("  配置迁移后: ConfigVersion=" + $after.ConfigVersion + " PanelPinned=" + $after.PanelPinned + " PanelTopMost=" + $after.PanelTopMost)
if ($alive) { Stop-Process -Id $p.Id -Force }
Start-Sleep -Seconds 3

Write-Host ""
Write-Host "=== B) 正常启动（临时浮层）==="
$p2 = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Seconds 3
Write-Host ("  +3s  窗口: " + (Get-Flags $p2.Id))
Start-Sleep -Seconds 12
Write-Host ("  +15s 窗口: " + (Get-Flags $p2.Id))
if (Get-Process -Id $p2.Id -ErrorAction SilentlyContinue) { Stop-Process -Id $p2.Id -Force }
Start-Sleep -Seconds 3

Write-Host ""
Write-Host "=== C) --panel 截图模式（应置顶，开发用）==="
$p3 = Start-Process -FilePath $Exe -ArgumentList "--panel" -PassThru
Start-Sleep -Seconds 5
Write-Host ("  窗口: " + (Get-Flags $p3.Id))
if (Get-Process -Id $p3.Id -ErrorAction SilentlyContinue) { Stop-Process -Id $p3.Id -Force }
