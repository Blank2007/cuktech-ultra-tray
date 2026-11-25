# 0.4.7 — two outlets counted as one, and a gate you have to agree to

> This is the draft body for the GitHub Release (English). The Chinese original is
> [release-0.4.7.md](release-0.4.7.md).

In one line: we fixed a reading problem that made the app look broken — **with C3 and A both plugged in the charger
reports their power merged, while we labelled the other outlet "Idle"** — and turned the privacy note from "telling you
about it" into a real **"you must agree on first launch"** gate that writes nothing if you decline. macOS ships
**0.1.2** alongside, including a community PR.

## What changed

### 0.4.7 (Windows, this release)

* **C3 and A are shown as one merged port**: the two outlets share a single power meter, and the firmware reports
  `state = 0x11` on it when it merges them (`0x00` idle / `0x01` single port / `0x11` C3+A merged — documented in
  `src/CuktechMible/MiotCodec.cs:109` for a long time, the UI simply never used it)
  * Read tab: the row that carries the numbers is labelled **C3+A**; the other row no longer claims "Idle" but says
    "merged into C3+A" in grey
  * The control tab's live-reading column does the same, while both switches stay independently clickable
  * The total is counted once (`Snapshot.TotalWatts` adds only the owner row when merged), so it never doubles
  * With nothing merged, not a pixel changes
* **A privacy notice you must agree to on first launch**: a standalone window
  (`src/CuktechTrayAot/ConsentWindow.cs`, 500×546) with four sections
  * "Agree and continue" writes `PrivacyAccepted: true` into `settings.json` and never asks again
  * "Disagree, quit", `Esc`, or closing the window exits the process — **nothing is written, Bluetooth is untouched**
  * Installations from before 0.4.7 see it once on upgrade (the field did not exist), which is intentional
* New development switches: `--render-consent` (renders the consent window to a PNG) and `--render-merged`
  (renders the read tab with fake merged data)
* README: the privacy section now shows this screen, and the read tab documents the merged display (both languages)
* Version 0.4.6 → 0.4.7

### macOS 0.1.2 (shipped together)

* Catches up with both Windows changes: the merged-port display (same rule, same wording) and the first-launch consent
  gate (`PrivacyWindow.swift`; `Esc` and the close button do not dismiss it, and
  `--version` / `--selftest` / `--wizard-demo` skip it)
* **Merges community PR [#3](https://github.com/Blank2007/cuktech-ultra-tray/pull/3)** with four fixes: the port switch
  index was off by one (USB-A reported "port index must be 0~3"), a Swift actor is re-entrant across `await` so polling
  and manual control could send commands concurrently, a SET's ACK was waited out for a full second, and the status item
  was unreadable on a dark desktop
* Also fixed a truncated read-back: GET now accepts only `op = 0x03` and SET only `op = 0x01`, so a SET ACK can no
  longer cut a GET result short
* Version 0.1.1 → 0.1.2; five merged-power test cases added

### 0.4.6

* Fixed the QR pairing failure under AOT (reflection-based JSON serialization is disabled there; replaced with a
  hand-written `MiCrypto.JsonQuote`), added AOT-specific self-tests, and documented privacy in the wizard and README;
  macOS starts its own numbering at 0.1.0

### 0.4.5

* Promo assets became the real thing: `--render-icons` / `--render-taskbar` render them from the program itself and the
  context menu is captured from the real window; behaviour is identical to 0.4.4

### 0.4.4

* The "connected" pill lost its translucent green background and kept only the dot and the text; the top row moved down
  8 px and the panel went 320×396 → **320×404**

## How to upgrade

* **Portable**: overwrite the old `CuktechTray.exe`, keep your `settings.json` (the first launch after upgrading asks
  for consent once)
* **Installed**: tray menu → "Check for updates"; it downloads, verifies SHA256 and replaces itself. Or replace
  `%LOCALAPPDATA%\Programs\CuktechTray\CuktechTray.exe` directly
* **macOS**: unzip the new `CuktechTray-0.1.2-mac.zip` and drag `CuktechTray.app` into Applications, replacing the old
  one; right-click → Open on first launch (ad-hoc signed, not notarised)
* **CLI equivalents** (Windows): `--install [--dir=<path>] [--autostart] [--desktop] [--silent]`,
  `--uninstall [--silent]`; neither prompts for UAC

## Files

| File | Size | Notes |
| --- | --- | --- |
| `CuktechTray-0.4.7-win.exe` | **10,898,944 bytes (10.39 MiB)** | Windows: portable build and installer in one; 16,896 bytes more than 0.4.6 (consent window + merged display) |
| `CuktechTray-0.1.2-mac.zip` | 1,073,719 字节（1.02 MiB） | macOS 13+: unzips to `CuktechTray.app` (universal, Intel / Apple Silicon, ad-hoc signed, icon included); see [mac/README.md](../mac/README.md) |

## Checksums

```
file   CuktechTray-0.4.7-win.exe   (a local build produces CuktechTray.exe — same file)
size   10,898,944 bytes
SHA256 60F3E377F3D6548EF22B06831C8F63372C6246748A00B71185B78EEE1794FF25

file   CuktechTray-0.1.2-mac.zip   (universal build produced by CI on macos-26-intel)
size   1,073,719 字节（1.02 MiB）
SHA256 B9AAF0B6CDB83CD177AB32BD93AD297213879B72A4C92E19720D7ECAA4C0B955
```

On Windows use `Get-FileHash .\CuktechTray-0.4.7-win.exe -Algorithm SHA256`, on macOS
`shasum -a 256 CuktechTray-0.1.2-mac.zip`; the GitHub asset page also shows the digest.

## Requirements

* Windows 10 2004 (build 19041) or newer, x64; or macOS 13 or newer (universal Intel / Apple Silicon)
* Neither needs a runtime installed: the Windows build is a single NativeAOT file (10.4 MB), the macOS build is a native
  .app
* Hardware is still the "CUKTECH 10 GaN Charger Ultra" over Bluetooth (BLE); once authenticated only Bluetooth is used

## Known

* The exe is **not code signed**, so the first run may show the blue "Windows protected your PC" dialog — click
  "More info → Run anyway"
* The macOS bundle is **not notarised** (no Apple developer account, only ad-hoc signing); right-click → Open on first
  launch, see [mac/README.md](../mac/README.md)
* Auto-update only considers release assets whose name starts with `CuktechTray-`, ends with `.exe` and does not contain
  `Setup`, so `-win.exe` matches naturally and `-mac.zip` can never be picked
* The two platforms **version independently** (Windows 0.4.7 / macOS 0.1.2) — don't compare the numbers across platforms
* The consent gate only appears on the first launch: to see it again, delete `PrivacyAccepted` (macOS: `privacyAccepted`)
  from `settings.json` / `config.json` or set it to `false`
* `--render*` / `--font-probe` / `--update-selftest` are development switches; normal use never needs them

## Thanks

* [@KingGxxT](https://github.com/KingGxxT) for [issue #1](https://github.com/Blank2007/cuktech-ultra-tray/issues/1) —
  "C3 and A should be shown merged; the official firmware does not distinguish them either" plus a screenshot made both
  platforms' merged display straightforward
* [@a1287448852](https://github.com/a1287448852) for
  [PR #3](https://github.com/Blank2007/cuktech-ultra-tray/pull/3) — fixed the macOS port switch index offset, the BLE
  command concurrency and the invisible status item on dark wallpapers, with runnable tests. Most of the macOS fixes in
  0.1.2 are his work
