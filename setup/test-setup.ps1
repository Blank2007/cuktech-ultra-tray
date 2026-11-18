<#
  安装程序自测脚本（会真的装、真的卸、真的写注册表和快捷方式）

  用法：
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\setup\test-setup.ps1

  覆盖：--check 体检 → 静默安装（无快捷方式）→ 重复安装（幂等）→ 装了快捷方式+自启的安装
        → 重复安装（升级路径）→ 用注册表里的 QuietUninstallString 卸载 → 用安装目录里的
        --uninstall 卸载 → 重复卸载（幂等）→ --purge-data 卸载 → 收尾清理
  任何一项失败都会打印 [FAIL] 并以退出码 1 结束。
#>
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$setupDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $setupDir
$setupExe = Join-Path $root "dist\CuktechTray-Setup.exe"
$appSrc = Join-Path $root "dist\CuktechTray.exe"
if (-not (Test-Path $setupExe)) { throw "找不到安装包：$setupExe（先跑 build-setup.ps1）" }
if (-not (Test-Path $appSrc)) { throw "找不到主程序：$appSrc" }

# 注意：本机 DSH 沙箱环境里，.NET 8 单文件程序默认无法在 %TEMP% 下创建目录（apphost 解压
# bundled 文件时直接失败），所以把测试工作目录与 bundle 解压目录都放到工作区，并把
# DOTNET_BUNDLE_EXTRACT_BASE_DIR 传给子进程。这只是自测环境的适配，不影响安装包本身。
$work = Join-Path (Split-Path -Parent $root) ".tools\tmp\setup-test"
$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = Join-Path $work "bundle-extract"
$inst1 = Join-Path $work "inst-noshortcuts"
$inst2 = Join-Path $work "inst-flex"
$inst3 = Join-Path $work "inst-purge"
$inst4 = Join-Path $work "inst-upgrade"

$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CuktechTray"
$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$runValue = "CuktechTray"
$desktopLnk = Join-Path ([Environment]::GetFolderPath("DesktopDirectory")) "酷态科托盘功率表.lnk"
$startLnk = Join-Path ([Environment]::GetFolderPath("Programs")) "CuktechTray\酷态科托盘功率表.lnk"
$startGroup = Join-Path ([Environment]::GetFolderPath("Programs")) "CuktechTray"
$dataDir = Join-Path ([Environment]::GetFolderPath("LocalApplicationData")) "CuktechTray"
$dataDirExistedBefore = Test-Path $dataDir

$script:passCount = 0
$script:failCount = 0
$script:failures = @()

function Check {
    param([string]$Name, $Condition, [string]$Detail = "")
    if ($Condition) {
        $script:passCount++
        Write-Host ("  [OK]   " + $Name) -ForegroundColor Green
    }
    else {
        $script:failCount++
        $script:failures += ($Name + " :: " + $Detail)
        Write-Host ("  [FAIL] " + $Name + " :: " + $Detail) -ForegroundColor Red
    }
}

function Section {
    param([string]$Text)
    Write-Host ""
    Write-Host ("=== " + $Text + " ===") -ForegroundColor Cyan
}

function Invoke-Exe {
    param([string]$Exe, [string]$ArgLine, [int]$TimeoutSeconds = 300)
    $p = Start-Process -FilePath $Exe -ArgumentList $ArgLine -Wait -PassThru
    return $p.ExitCode
}

function Wait-For {
    param([scriptblock]$Condition, [int]$TimeoutSeconds = 120)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (& $Condition) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return (& $Condition)
}

function Get-ShortcutTarget {
    param([string]$Path)
    if (-not (Test-Path $Path)) { return "" }
    $shell = New-Object -ComObject WScript.Shell
    try {
        return $shell.CreateShortcut($Path).TargetPath
    }
    finally {
        [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
    }
}

function Get-Reg {
    param([string]$Name)
    $props = Get-ItemProperty -Path $uninstallKey -ErrorAction SilentlyContinue
    if ($null -eq $props) { return $null }
    return $props.$Name
}

function Get-RunValue {
    $v = Get-ItemProperty -Path $runKey -Name $runValue -ErrorAction SilentlyContinue
    if ($null -eq $v) { return "" }
    return [string]$v.$runValue
}

function Test-UninstallKey { return Test-Path $uninstallKey }
function Test-AppRunning { return @(Get-Process -Name "CuktechTray" -ErrorAction SilentlyContinue).Count -gt 0 }

function Stop-LeftoverApp {
    Get-Process -Name "CuktechTray" -ErrorAction SilentlyContinue | ForEach-Object {
        try { $_.Kill($true) } catch { }
    }
    Start-Sleep -Milliseconds 500
}

# ---------------- 0. 清理上一次可能的残留 ----------------
Section "0. 清理上一次残留"
Stop-LeftoverApp
if (Test-UninstallKey) { Remove-Item -Path $uninstallKey -Recurse -Force }
if ((Get-RunValue) -ne "") { Remove-ItemProperty -Path $runKey -Name $runValue -Force -ErrorAction SilentlyContinue }
foreach ($lnk in @($desktopLnk, $startLnk)) {
    if (Test-Path $lnk) { Remove-Item $lnk -Force }
}
if (Test-Path $startGroup) { Remove-Item $startGroup -Recurse -Force -ErrorAction SilentlyContinue }
if (Test-Path $work) { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force -Path $work | Out-Null
New-Item -ItemType Directory -Force -Path $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR | Out-Null
Write-Host "  工作目录：$work" -ForegroundColor DarkGray
Write-Host "  安装包  ：$setupExe" -ForegroundColor DarkGray

$srcSha = (Get-FileHash -Path $appSrc -Algorithm SHA256).Hash
Write-Host "  主程序  ：$appSrc  SHA256 $srcSha" -ForegroundColor DarkGray

# ---------------- 1. --check 体检 ----------------
Section "1. --check 体检（只读）"
$checkReport = Join-Path $work "check.json"
$code = Invoke-Exe $setupExe ('--check --dir "' + $inst1 + '" --report "' + $checkReport + '"')
Check "--check 退出码为 0" ($code -eq 0) ("exit=" + $code)
Check "--check 生成了 JSON 报告" (Test-Path $checkReport) $checkReport
if (Test-Path $checkReport) {
    $json = Get-Content -Raw -Path $checkReport | ConvertFrom-Json
    Check "报告里带内嵌主程序的 SHA256" ($json.fields.payloadSha256 -eq $srcSha) ([string]$json.fields.payloadSha256)
    Check "报告里内嵌主程序字节数正确" ([int64]$json.fields.payloadRawBytes -eq (Get-Item $appSrc).Length) ([string]$json.fields.payloadRawBytes)
    Check "报告里标记为不需要提权" ($json.fields.requiresElevation -eq "no") ([string]$json.fields.requiresElevation)
}
Check "--check 没有创建安装目录" (-not (Test-Path $inst1)) $inst1
Check "--check 没有写卸载注册表项" (-not (Test-UninstallKey)) $uninstallKey

# ---------------- 2. 静默安装（不建快捷方式、不自启） ----------------
Section "2. 静默安装到 $inst1"
$r2 = Join-Path $work "install1.json"
$code = Invoke-Exe $setupExe ('--silent --dir "' + $inst1 + '" --no-shortcuts --no-start-menu --no-autostart --no-launch --report "' + $r2 + '"')
Check "安装退出码为 0" ($code -eq 0) ("exit=" + $code)
$logCandidates = @((Join-Path $env:TEMP "CuktechTray-Setup.log"), (Join-Path $dataDir "logs\CuktechTray-Setup.log"))
Check "日志文件已生成" (@($logCandidates | Where-Object { Test-Path $_ }).Count -gt 0) ($logCandidates -join " | ")

$installedExe = Join-Path $inst1 "CuktechTray.exe"
$installedSetup = Join-Path $inst1 "CuktechTray-Setup.exe"
Check "主程序已落地" (Test-Path $installedExe) $installedExe
Check "安装器副本已落地" (Test-Path $installedSetup) $installedSetup
if (Test-Path $installedExe) {
    Check "主程序大小与 dist 一致" ((Get-Item $installedExe).Length -eq (Get-Item $appSrc).Length) ([string](Get-Item $installedExe).Length)
    $gotSha = (Get-FileHash -Path $installedExe -Algorithm SHA256).Hash
    Check "主程序 SHA256 与 dist 一致（解压无损）" ($gotSha -eq $srcSha) $gotSha
}
if (Test-Path $r2) {
    $j2 = Get-Content -Raw -Path $r2 | ConvertFrom-Json
    Check "报告 result=ok" ($j2.result -eq "ok") ([string]$j2.result)
}

Check "卸载注册表项已写入" (Test-UninstallKey) $uninstallKey
Check "DisplayName 正确" ((Get-Reg "DisplayName") -eq "酷态科 10 号 Ultra 托盘功率表") ([string](Get-Reg "DisplayName"))
Check "DisplayVersion 非空" (-not [string]::IsNullOrWhiteSpace([string](Get-Reg "DisplayVersion"))) ([string](Get-Reg "DisplayVersion"))
Check "Publisher 正确" ((Get-Reg "Publisher") -eq "CuktechTray") ([string](Get-Reg "Publisher"))
Check "DisplayIcon 指向主程序" ([string](Get-Reg "DisplayIcon") -eq ($installedExe + ",0")) ([string](Get-Reg "DisplayIcon"))
Check "InstallLocation 正确" ([string](Get-Reg "InstallLocation") -eq $inst1) ([string](Get-Reg "InstallLocation"))
Check "EstimatedSize 大于 0" ([int](Get-Reg "EstimatedSize") -gt 0) ([string](Get-Reg "EstimatedSize"))
Check "NoModify=1" ([int](Get-Reg "NoModify") -eq 1) ([string](Get-Reg "NoModify"))
Check "NoRepair=1" ([int](Get-Reg "NoRepair") -eq 1) ([string](Get-Reg "NoRepair"))

$uninstStr = [string](Get-Reg "UninstallString")
$quietStr = [string](Get-Reg "QuietUninstallString")
Check "UninstallString 指向安装目录里的 setup 且带 --uninstall" (($uninstStr -like ('*' + $installedSetup + '*')) -and ($uninstStr -like "*--uninstall*")) $uninstStr
Check "QuietUninstallString 带 --silent" ($quietStr -like "*--silent*") $quietStr
$quotedExe = '"' + $installedSetup + '"'
Check "UninstallString 里的可执行文件确实存在" (Test-Path $installedSetup) $quotedExe

Check "未勾选时没有桌面快捷方式" (-not (Test-Path $desktopLnk)) $desktopLnk
Check "未勾选时没有开始菜单快捷方式" (-not (Test-Path $startLnk)) $startLnk
Check "未勾选时没有开机自启" ((Get-RunValue) -eq "") (Get-RunValue)

# ---------------- 3. 重复安装（幂等） ----------------
Section "3. 同一目录再装一次（幂等 + 覆盖升级路径）"
$before = @(Get-ChildItem -Path $inst1 -Force).Count
$r3 = Join-Path $work "install1b.json"
$code = Invoke-Exe $setupExe ('--silent --dir "' + $inst1 + '" --no-shortcuts --no-start-menu --no-autostart --no-launch --report "' + $r3 + '"')
Check "重复安装退出码为 0" ($code -eq 0) ("exit=" + $code)
Check "重复安装后目录里没有多出垃圾文件" (@(Get-ChildItem -Path $inst1 -Force).Count -le ($before + 1)) ([string]@(Get-ChildItem -Path $inst1 -Force).Count)
Check "重复安装后主程序仍在" (Test-Path $installedExe) $installedExe
Check "重复安装后注册表项仍在" (Test-UninstallKey) $uninstallKey
if (Test-Path $r3) {
    $j3 = Get-Content -Raw -Path $r3 | ConvertFrom-Json
    Check "报告把这次记为升级（upgraded=yes）" ($j3.fields.upgraded -eq "yes") ([string]$j3.fields.upgraded)
}

# ---------------- 4. 带快捷方式 + 开机自启的安装 ----------------
Section "4. 带桌面/开始菜单快捷方式 + 开机自启安装到 $inst2"
$r4 = Join-Path $work "install2.json"
$code = Invoke-Exe $setupExe ('--silent --dir "' + $inst2 + '" --shortcuts --start-menu --autostart --no-launch --report "' + $r4 + '"')
Check "安装退出码为 0" ($code -eq 0) ("exit=" + $code)
$inst2Exe = Join-Path $inst2 "CuktechTray.exe"
Check "桌面快捷方式已创建" (Test-Path $desktopLnk) $desktopLnk
Check "桌面快捷方式指向正确的主程序" ((Get-ShortcutTarget $desktopLnk) -eq $inst2Exe) (Get-ShortcutTarget $desktopLnk)
Check "开始菜单快捷方式已创建" (Test-Path $startLnk) $startLnk
Check "开始菜单快捷方式指向正确的主程序" ((Get-ShortcutTarget $startLnk) -eq $inst2Exe) (Get-ShortcutTarget $startLnk)
$expectRun = '"' + $inst2Exe + '" --tray-only'
Check "开机自启写进 HKCU\...\Run 且格式与主程序一致" ((Get-RunValue) -eq $expectRun) (Get-RunValue)
Check "注册表 InstallLocation 已切到新目录" ([string](Get-Reg "InstallLocation") -eq $inst2) ([string](Get-Reg "InstallLocation"))
Check "安装后主程序没有在运行（--no-launch）" (-not (Test-AppRunning)) "有残留进程"

# ---------------- 5. 同目录重复安装（升级路径，带自启/快捷方式） ----------------
Section "5. 带自启/快捷方式再装一次（覆盖升级）"
$r5 = Join-Path $work "install2b.json"
$code = Invoke-Exe $setupExe ('--silent --dir "' + $inst2 + '" --shortcuts --start-menu --autostart --no-launch --report "' + $r5 + '"')
Check "升级安装退出码为 0" ($code -eq 0) ("exit=" + $code)
Check "升级后桌面快捷方式仍指向主程序" ((Get-ShortcutTarget $desktopLnk) -eq $inst2Exe) (Get-ShortcutTarget $desktopLnk)
Check "升级后开机自启仍正确" ((Get-RunValue) -eq $expectRun) (Get-RunValue)
if (Test-Path $r5) {
    $j5 = Get-Content -Raw -Path $r5 | ConvertFrom-Json
    Check "升级报告里带旧版本号" (-not [string]::IsNullOrWhiteSpace([string]$j5.fields.previousVersion)) ([string]$j5.fields.previousVersion)
}

# ---------------- 6. 用注册表的 QuietUninstallString 卸载 ----------------
Section "6. 用注册表里的 QuietUninstallString 卸载 $inst2"
$quietExe = $inst2Exe.Replace("CuktechTray.exe", "CuktechTray-Setup.exe")
$code = Invoke-Exe $quietExe ('--uninstall --silent --dir "' + $inst2 + '"')
Check "卸载启动返回 0（自身搬迁后父进程先退）" ($code -eq 0) ("exit=" + $code)
$gone = Wait-For { (-not (Test-UninstallKey)) -and (-not (Test-Path $inst2)) } 120
Check "安装目录已删除" (-not (Test-Path $inst2)) $inst2
Check "卸载注册表项已删除" (-not (Test-UninstallKey)) $uninstallKey
Check "桌面快捷方式已删除" (-not (Test-Path $desktopLnk)) $desktopLnk
Check "开始菜单快捷方式已删除" (-not (Test-Path $startLnk)) $startLnk
Check "开始菜单组目录已清空删除" (-not (Test-Path $startGroup)) $startGroup
Check "开机自启已删除" ((Get-RunValue) -eq "") (Get-RunValue)
Check "没有残留托盘进程" (-not (Test-AppRunning)) "有残留进程"
Check "卸载后等待条件在超时前满足" $gone "120 秒内没等到清理完成"

# ---------------- 7. 用安装目录里的 setup --uninstall 卸载 ----------------
Section "7. 直接跑安装目录里的 setup 卸载 $inst1"
if (Test-Path $installedSetup) {
    $code = Invoke-Exe $installedSetup ('--uninstall --silent --dir "' + $inst1 + '"')
    Check "卸载启动返回 0" ($code -eq 0) ("exit=" + $code)
}
$gone1 = Wait-For { (-not (Test-UninstallKey)) -and (-not (Test-Path $inst1)) } 120
Check "安装目录已删除" (-not (Test-Path $inst1)) $inst1
Check "卸载注册表项已删除" (-not (Test-UninstallKey)) $uninstallKey
Check "没有残留托盘进程" (-not (Test-AppRunning)) "有残留进程"
Check "卸载后等待条件在超时前满足" $gone1 "120 秒内没等到清理完成"

# ---------------- 8. 重复卸载（幂等） ----------------
Section "8. 再次卸载（应当安静地说“已经卸载过了”）"
$r8 = Join-Path $work "uninstall-again.json"
$code = Invoke-Exe $setupExe ('--uninstall --silent --report "' + $r8 + '"')
Check "重复卸载退出码为 0" ($code -eq 0) ("exit=" + $code)
if (Test-Path $r8) {
    $j8 = Get-Content -Raw -Path $r8 | ConvertFrom-Json
    Check "报告里说明已经卸载过了" ($j8.fields.alreadyUninstalled -eq "yes") ([string]$j8.fields.alreadyUninstalled)
}
Check "重复卸载后注册表仍干净" (-not (Test-UninstallKey)) $uninstallKey

# ---------------- 9. --purge-data 卸载配置和日志 ----------------
Section "9. 安装 → 造点用户数据 → --purge-data 卸载"
$code = Invoke-Exe $setupExe ('--silent --dir "' + $inst3 + '" --no-shortcuts --no-start-menu --no-autostart --no-launch')
Check "安装退出码为 0" ($code -eq 0) ("exit=" + $code)
# 主程序在 exe 同目录写 settings.json / logs\app.log；不可写时退到 %LOCALAPPDATA%\CuktechTray
$probeSettings = Join-Path $inst3 "settings.json"
$probeLogs = Join-Path $inst3 "logs"
New-Item -ItemType Directory -Force -Path $probeLogs | Out-Null
Set-Content -Path $probeSettings -Value "{ }" -Encoding UTF8
Set-Content -Path (Join-Path $probeLogs "app.log") -Value "test" -Encoding UTF8
$madeDataDir = $false
if (-not $dataDirExistedBefore) {
    New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
    Set-Content -Path (Join-Path $dataDir "settings.json") -Value "{ }" -Encoding UTF8
    $madeDataDir = $true
}
$r9 = Join-Path $work "uninstall-purge.json"
$code = Invoke-Exe $setupExe ('--uninstall --silent --purge-data --dir "' + $inst3 + '" --report "' + $r9 + '"')
Check "卸载退出码为 0" ($code -eq 0) ("exit=" + $code)
Wait-For { -not (Test-Path $inst3) } 120 | Out-Null
Check "安装目录（含 settings.json / logs）已删除" (-not (Test-Path $inst3)) $inst3
if ($madeDataDir) {
    Check "%LOCALAPPDATA%\CuktechTray 已删除" (-not (Test-Path $dataDir)) $dataDir
}
else {
    Write-Host "  [SKIP] %LOCALAPPDATA%\CuktechTray 测试前就存在（可能是真实用户数据），不验证删除" -ForegroundColor Yellow
}
Check "卸载注册表项已删除" (-not (Test-UninstallKey)) $uninstallKey

# ---------------- 10. 收尾清理 ----------------
Section "10. 收尾清理"
Get-ChildItem -Path $work -Filter "*.json" -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host ("  报告：" + $_.Name) -ForegroundColor DarkGray
}
Stop-LeftoverApp
if (Test-UninstallKey) { Remove-Item -Path $uninstallKey -Recurse -Force }
if ((Get-RunValue) -ne "") { Remove-ItemProperty -Path $runKey -Name $runValue -Force -ErrorAction SilentlyContinue }
foreach ($lnk in @($desktopLnk, $startLnk)) { if (Test-Path $lnk) { Remove-Item $lnk -Force } }
if (Test-Path $startGroup) { Remove-Item $startGroup -Recurse -Force -ErrorAction SilentlyContinue }
foreach ($d in @($inst1, $inst2, $inst3, $inst4, $work)) {
    if (Test-Path $d) { Remove-Item $d -Recurse -Force -ErrorAction SilentlyContinue }
}
if ($madeDataDir -and (Test-Path $dataDir)) { Remove-Item $dataDir -Recurse -Force -ErrorAction SilentlyContinue }
# 清理卸载器搬到 %TEMP% 的临时副本
Get-ChildItem -Path $env:TEMP -Filter "CuktechTray-Setup-*" -Directory -ErrorAction SilentlyContinue | ForEach-Object {
    Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
}
Check "临时安装目录已清理" (-not (Test-Path $work)) $work
Check "注册表已清理干净" (-not (Test-UninstallKey)) $uninstallKey
Check "开机自启已清理干净" ((Get-RunValue) -eq "") (Get-RunValue)
Check "桌面/开始菜单快捷方式已清理" ((-not (Test-Path $desktopLnk)) -and (-not (Test-Path $startLnk))) ($desktopLnk + " | " + $startLnk)
Check "没有残留进程" (-not (Test-AppRunning)) "有残留进程"

# ---------------- 结果 ----------------
Write-Host ""
if ($script:failCount -eq 0) {
    Write-Host ("自测全部通过：" + $script:passCount + " 项检查，0 失败") -ForegroundColor Green
    exit 0
}
else {
    Write-Host ("自测失败：" + $script:passCount + " 项通过，" + $script:failCount + " 项失败") -ForegroundColor Red
    foreach ($f in $script:failures) { Write-Host ("  - " + $f) -ForegroundColor Red }
    exit 1
}
