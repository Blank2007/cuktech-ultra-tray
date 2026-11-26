<#
    UI self-check for the Windows tray app (0.5.4).

    What it guards
    --------------
    * hit rects of the device-settings window  (--probe-hits  -> tools/baseline/hits.txt)
    * sizes of every self-rendered window shot (--render-*    -> tools/baseline/renders.tsv)
    * optionally the pixel hash of those shots (-StrictHash)

    Why
    ---
    0.5.3 fixed a bug where "idle screen off" toggled "orientation lock", because the
    paint code and the hit-test code each carried their own copy of the same layout.
    --render only ever proved "it draws right", never "it clicks right", so that bug
    shipped. From here on every hit rect and every shot size is compared against a
    checked-in baseline: a layout change that was not recorded on purpose fails this.

    Usage
    -----
      tools\check-ui.ps1                 # hit rects + shot sizes (this is what CI runs)
      tools\check-ui.ps1 -StrictHash     # also compare pixel hashes (local, before release)
      tools\check-ui.ps1 -UpdateBaseline # rewrite the baselines after an intended change

    Notes
    -----
    * Render sizes are font independent; pixel hashes are not. The CI runner may not
      have "Microsoft YaHei UI", so hashes are opt-in.
    * Everything lands in a temp folder, never in docs\.
#>
[CmdletBinding()]
param(
    [switch]$StrictHash,
    [switch]$UpdateBaseline,
    [string]$OutDir = ""
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$tools = Join-Path (Split-Path -Parent $root) ".tools"
$baselineDir = Join-Path $root "tools\baseline"
$hitsBaseline = Join-Path $baselineDir "hits.txt"
$rendersBaseline = Join-Path $baselineDir "renders.tsv"

function Write-Step($text) { Write-Host ("==> " + $text) -ForegroundColor Cyan }

# ---------- 1. build (framework dependent, ~5 s; no AOT here) ----------
$dotnet = Join-Path $tools "dotnet\dotnet.exe"
if (Test-Path $dotnet) {
    $env:DOTNET_ROOT     = Join-Path $tools "dotnet"
    $env:DOTNET_CLI_HOME = Join-Path $tools "dotnet-home"
    $env:NUGET_PACKAGES  = Join-Path $tools "nuget"
    $env:APPDATA         = Join-Path $tools "userprof\AppData\Roaming"
    $env:LOCALAPPDATA    = Join-Path $tools "userprof\AppData\Local"
    $env:USERPROFILE     = Join-Path $tools "userprof"
    # MSBuild and the app both want a writable TEMP; the workspace keeps one next to the SDK.
    $env:TEMP            = Join-Path $tools "tmp"
    $env:TMP             = Join-Path $tools "tmp"
    foreach ($d in @($env:TEMP, $env:APPDATA, $env:LOCALAPPDATA)) {
        if (-not (Test-Path $d)) { New-Item -ItemType Directory -Force -Path $d | Out-Null }
    }
}
else {
    $dotnet = "dotnet"
}

$proj = Join-Path $root "src\CuktechTrayAot\CuktechTrayAot.csproj"
Write-Step ("building " + $proj)
& $dotnet build $proj -c Release --nologo -v q | Out-Null
if ($LASTEXITCODE -ne 0) { throw "build failed (exit $LASTEXITCODE)" }

$binDir = Join-Path $root "src\CuktechTrayAot\bin\Release"
$exe = Get-ChildItem $binDir -Recurse -Filter "CuktechTray.exe" |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $exe) { throw ("no CuktechTray.exe under " + $binDir) }
Write-Step ("exe: " + $exe.FullName)

# ---------- 2. run every self-check into a temp folder ----------
if ([string]::IsNullOrWhiteSpace($OutDir)) {
    $OutDir = Join-Path ([System.IO.Path]::GetTempPath()) ("cuktech-ui-check-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
}
if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# The app is a GUI subsystem exe, so "& $exe args" does not block in PowerShell:
# start it with -PassThru and wait on the process object instead.
$runs = @(
    @{ name = "probe-hits";      args = @("--probe-hits") },
    @{ name = "render-demo";     args = @("--render", "--render-demo", "--render-scale=1.25") },
    @{ name = "render-merged";   args = @("--render", "--render-demo", "--render-merged", "--render-scale=1.25") },
    @{ name = "render-settings"; args = @("--render-settings", "--render-scale=1.25") },
    @{ name = "render-capture";  args = @("--render-capture", "--render-scale=1.25") },
    @{ name = "render-about";    args = @("--render-about", "--render-scale=1.25") },
    @{ name = "render-consent";  args = @("--render-consent", "--render-scale=1.25") },
    @{ name = "render-wizard";   args = @("--render-wizard", "--render-scale=1.25") }
)

foreach ($r in $runs) {
    # Start-Process joins ArgumentList with spaces and does no quoting, so a path with
    # spaces would arrive as two arguments; quote it here.
    $q = [char]34
    $argv = @("--standalone", ("--out=" + $q + $OutDir + $q)) + $r.args
    Write-Step ("run " + $r.name + " " + ($argv -join " "))
    $p = Start-Process -FilePath $exe.FullName -ArgumentList $argv -PassThru
    if (-not $p.WaitForExit(180000)) {
        try { $p.Kill() } catch { }
        throw ($r.name + " timed out")
    }
    if ($p.ExitCode -ne 0) { throw ($r.name + " exited " + $p.ExitCode) }
}

$hitsPath = Join-Path $OutDir "tmp-hits.txt"
if (-not (Test-Path $hitsPath)) { throw ("probe-hits wrote nothing: " + $hitsPath) }

$shots = Get-ChildItem $OutDir -Filter "tmp-*.png" | Sort-Object Name
if ($shots.Count -lt 8) {
    throw ("expected at least 8 self-rendered shots, got " + $shots.Count)
}

# ---------- 3. collect what we got ----------
function Get-Sha256([string]$path) {
    return (Get-FileHash -Path $path -Algorithm SHA256).Hash
}

function Get-PngSize([string]$path) {
    $bytes = [System.IO.File]::ReadAllBytes($path)
    if ($bytes.Length -lt 24) { throw ("not a png: " + $path) }
    # PNG: 8 byte signature, then the IHDR chunk, then width/height as big endian ints.
    $w = ([int]$bytes[16] -shl 24) -bor ([int]$bytes[17] -shl 16) -bor ([int]$bytes[18] -shl 8) -bor [int]$bytes[19]
    $h = ([int]$bytes[20] -shl 24) -bor ([int]$bytes[21] -shl 16) -bor ([int]$bytes[22] -shl 8) -bor [int]$bytes[23]
    return @($w, $h)
}

$nlChar = [char]10
$tabChar = [char]9

$actualHits = ((@(Get-Content -Path $hitsPath)) -join $nlChar) + $nlChar
$actualRenders = New-Object System.Collections.Generic.List[string]
foreach ($s in $shots) {
    $wh = Get-PngSize $s.FullName
    $actualRenders.Add(($s.Name + $tabChar + $wh[0] + "x" + $wh[1] + $tabChar + (Get-Sha256 $s.FullName)))
}
$actualRendersText = ($actualRenders -join $nlChar) + $nlChar

# ---------- 4. compare (or record) ----------
if ($UpdateBaseline) {
    New-Item -ItemType Directory -Force -Path $baselineDir | Out-Null
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($hitsBaseline, $actualHits, $utf8)
    [System.IO.File]::WriteAllText($rendersBaseline, $actualRendersText, $utf8)
    Write-Step ("baseline written: " + $hitsBaseline)
    Write-Step ("baseline written: " + $rendersBaseline)
    Write-Host ("    " + $shots.Count + " shots, " + (@($actualHits -split $nlChar).Count - 1) + " hit rects") -ForegroundColor Green
    exit 0
}

if (-not (Test-Path $hitsBaseline)) { throw ("missing baseline: " + $hitsBaseline + " (run with -UpdateBaseline once)") }
if (-not (Test-Path $rendersBaseline)) { throw ("missing baseline: " + $rendersBaseline + " (run with -UpdateBaseline once)") }

$problems = New-Object System.Collections.Generic.List[string]

$wantHits = ((@(Get-Content -Path $hitsBaseline)) -join $nlChar) + $nlChar
if ($wantHits -ne $actualHits) {
    $problems.Add("hit rects changed")
    $wantLines = $wantHits -split $nlChar
    $gotLines = $actualHits -split $nlChar
    $max = [Math]::Max($wantLines.Count, $gotLines.Count)
    for ($i = 0; $i -lt $max; $i++) {
        $a = if ($i -lt $wantLines.Count) { $wantLines[$i] } else { "<none>" }
        $b = if ($i -lt $gotLines.Count) { $gotLines[$i] } else { "<none>" }
        if ($a -ne $b) {
            Write-Host ("    line " + ($i + 1) + ": baseline [" + $a + "] -> now [" + $b + "]") -ForegroundColor Yellow
        }
    }
}

$wantRenders = @{}
foreach ($line in ((Get-Content -Path $rendersBaseline) | Where-Object { $_.Trim().Length -gt 0 })) {
    $parts = $line -split $tabChar
    $wantRenders[$parts[0]] = @{ size = $parts[1]; hash = $parts[2] }
}
$gotNames = @{}
foreach ($s in $shots) {
    $gotNames[$s.Name] = $true
    if (-not $wantRenders.ContainsKey($s.Name)) {
        $problems.Add("new shot without baseline: " + $s.Name)
        continue
    }
    $wh = Get-PngSize $s.FullName
    $size = "" + $wh[0] + "x" + $wh[1]
    if ($size -ne $wantRenders[$s.Name].size) {
        $problems.Add($s.Name + " size " + $wantRenders[$s.Name].size + " -> " + $size)
    }
    elseif ($StrictHash) {
        $hash = Get-Sha256 $s.FullName
        if ($hash -ne $wantRenders[$s.Name].hash) {
            $problems.Add($s.Name + " pixels changed (" + $wantRenders[$s.Name].hash.Substring(0, 12) + " -> " + $hash.Substring(0, 12) + ")")
        }
    }
}
foreach ($name in $wantRenders.Keys) {
    if (-not $gotNames.ContainsKey($name)) { $problems.Add("shot missing: " + $name) }
}

Write-Host ""
if ($problems.Count -gt 0) {
    Write-Host "UI CHECKS FAILED" -ForegroundColor Red
    foreach ($pb in $problems) { Write-Host ("  - " + $pb) -ForegroundColor Red }
    Write-Host ""
    Write-Host "If the change is intended, re-record with: tools\check-ui.ps1 -UpdateBaseline" -ForegroundColor Yellow
    exit 1
}

$hashNote = "sizes only"
if ($StrictHash) { $hashNote = "pixel hashes compared" }
Write-Host ("UI CHECKS OK  (" + (@($actualHits -split $nlChar).Count - 1) + " hit rects, " + $shots.Count + " shots, " + $hashNote + ")") -ForegroundColor Green
exit 0
