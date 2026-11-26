# 0.5.3 — a misaligned row in device settings, and the macOS glass panel washing out

> Draft body for the GitHub Release (English). Chinese original: [release-0.5.3.md](release-0.5.3.md).

In one line: on Windows, clicking the "Idle screen off" switch toggled "Orientation lock" instead, so that is fixed and the cause of that
whole bug class (drawing and hit testing each keeping their own coordinate formula) is gone; on macOS, this fixes
[issue #6](https://github.com/Blank2007/cuktech-ultra-tray/issues/6), where 0.1.6 washed the dark panel out to mid grey on macOS 26/27
and made the dim text unreadable.

## What changed

### 0.5.3 (Windows, this release)

* **The click misalignment is fixed**: in the device settings window, the "Idle screen off" switch actually toggled the "Orientation lock"
  row below it, and that row did nothing. The cause is in `src/CuktechTrayAot/DeviceSettingsWindow.cs`: **the drawing code and the hit
  testing code each had their own copy of the same formula**. Drawing was right; hit testing started counting from the first row and was
  therefore one row short (the first row, "Screen timeout", only has chips and no switch), so the whole hit table sat 32 dip above the picture
* **The cause is gone**: six rectangle helpers — `ScreenChipRect`, `SwitchRect`, `LangChipRect`, `CountdownChipRect`,
  `ProtocolChipRect`, `ButtonRect` — are now the only source for both drawing and hit testing, scaled through `R()`.
  This window was the only place in the tree with two copies
* **`DrawSwitchRow` now takes an explicit `Kind`**: it used to infer the kind from `rowY`, so adding a row in between would silently fall
  through to `Kind.UsbSwitch` and bring back "click A, trigger B"
* **New self-check `--probe-hits`**: dumps all 25 hit rectangles at scale 1, used together with `--render-settings`. Both were compared
  before and after the refactor: hit rects identical line by line, rendered PNG byte-for-byte identical
* The other eight self-drawn windows (panel, control page, mode page, per-port capture, about, privacy notice, pairing wizard, context
  menu) were audited the same way; they all share one set of rectangle methods, so there is no second misalignment
* Version 0.5.2 -> **0.5.3**

### macOS 0.1.7 (same release)

* **Fixes issue #6**: 0.1.6 used `NSGlassEffectView` behind `#available(macOS 26.0, *)`, and inside an `NSPopover`'s ordinary container
  that view **cannot sample the window behind it**, so it degenerates into a neutral translucent layer; on top of that the SwiftUI
  backdrop was dropped to **0.55**, so any light window behind turned the dark panel mid grey and the `.secondary` text (subtitles,
  "idle / 0.0W") all but disappeared
* **The glass layer is a plain `NSVisualEffectView` again** (`.popover` material, `.behindWindow` blending): 13-15 always used that path
  and 26/27 sample correctly through it too, so there is no more system-version branching
* **The backdrop is no longer crushed**: it stays at **0.9** while glass is on (was 0.55 on 26+)
* **Glass is off by default**: fresh installs and upgrades no longer hit the effect on first launch. An explicit `true` in an existing
  config still wins, and the tray menu toggles it
* Version 0.1.6 -> **0.1.7**

## How to upgrade

* **Portable build**: drop the new `CuktechTray.exe` over the old one; `settings.json` keeps working as-is
* **Installed build**: right-click the tray icon -> "Check for updates"; it downloads, verifies the SHA256 and replaces itself
* **macOS**: unzip `CuktechTray-0.1.7-mac.zip` and drag `CuktechTray.app` into Applications, replacing the old one. First launch needs
  right-click -> Open. If glass was on in your config it stays on, now with normal contrast; turn off "面板玻璃质感" in the tray menu
  if you do not want it

## Files

| File | Size | What it is |
| --- | --- | --- |
| `CuktechTray-0.5.3-win.exe` | **11,073,024 bytes (10.56 MiB)** | Windows: portable build + installer in one file |
| `CuktechTray-0.1.7-mac.zip` | **1,590,315 bytes (1.52 MiB)** | macOS 13+: unzips to `CuktechTray.app`, see [mac/README.md](../mac/README.md) |

## Verify

```
file   CuktechTray-0.5.3-win.exe   (the local build is called CuktechTray.exe, same file)
size   11,073,024 bytes
SHA256 9BA1574943DA6F3EB33D87A554A603F8F053E926DB64471156A8CB453693EA96
```

```
file   CuktechTray-0.1.7-mac.zip   (packed by CI on this tag, so the zip carries a build timestamp)
size   1,590,315 bytes
SHA256 40281BF96BBEE8252A8324F2102E46D1BFFF8B5342F407A1380A828F90E5F562
```

`Get-FileHash .\CuktechTray-0.5.3-win.exe -Algorithm SHA256` on Windows, `shasum -a 256 CuktechTray-0.1.7-mac.zip` on macOS.

## Requirements

* Windows 10 2004 (build 19041) or newer, x64; or macOS 13 or newer (universal Intel / Apple Silicon)
* No runtime needed on either side: NativeAOT single file on Windows, a native `.app` on macOS
* Hardware is still a CUKTECH 10 Ultra charger over BLE

## Known

* The exe is **not code signed**; the first run shows the blue "Windows protected your PC" prompt — click "More info -> Run anyway"
* The macOS bundle is **not notarized** (ad-hoc signed only); right-click -> Open on first launch
* The macOS "window-snap capture cannot frame the panel" report is not reproducible on our side (the CI runners have no Bluetooth hardware;
  they only verify compilation and that the UI comes up). Going by the `CGWindowListCopyWindowInfo` check from issue #6, the process has
  only the popover window, and dropping `NSGlassEffectView` removes the extra glass layer — please try again
* The two platforms are **versioned separately** (Windows 0.5.3 / macOS 0.1.7)

## Thanks

* [@editwe](https://github.com/editwe): [issue #6](https://github.com/Blank2007/cuktech-ultra-tray/issues/6) — traced the glass problem
  to `NSGlassEffectView` not sampling the window behind it and the backdrop being pushed down to 0.55, and supplied the window-list check
* [@KingGxxT](https://github.com/KingGxxT): [issue #1](https://github.com/Blank2007/cuktech-ultra-tray/issues/1)
* [@a1287448852](https://github.com/a1287448852): [CuktechMac10Ultra](https://github.com/a1287448852/CuktechMac10Ultra)
* [@kairui1108](https://github.com/kairui1108): the two earliest projects that documented the CUKTECH 10 Ultra BLE protocol
* and everyone who filed issues and pull requests
