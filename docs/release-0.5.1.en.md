# 0.5.1 - capture gains average and peak, the panel can stay on top

> This is the draft body for the GitHub Release (English). Chinese: [release-0.5.1.md](release-0.5.1.md).

In one line: Windows **per-port capture** now reports average and peak voltage/current (same rules as macOS),
the panel gains an **always-on-top** switch, and toggling a port **waits one second less**; macOS absorbs
[@editwe](https://github.com/editwe) [PR #5](https://github.com/Blank2007/cuktech-ultra-tray/pull/5) and ships **0.1.6**.
The two platforms still number their versions independently.

## What changed

### 0.5.1 (Windows, this release)

* **Capture gains average / peak columns** (`src/CuktechTrayAot/CaptureWindow.cs`): voltage, current and power,
  average on the left, peak on the right. Before this there was only average power and a voltage range
* **Peak current only counts loaded samples**, so an idle port's 5 V standby does not inflate it; empty readings
  show "-" instead of `0.00`
* **Average voltage / current share the power integration rule**: across a tier change the pre-jump value is used,
  so average voltage x average current agrees with average power
* **Unplugging no longer counts as a tier change**: only two consecutive loaded samples 0.5 V or more apart count
* **The voltage range only covers loaded samples**
* **Panel always on top** (tray menu): the panel stays above other windows. This is separate from "pin to desktop",
  which only stops auto-hiding. Both can be on at once; stored as `PanelAlwaysOnTop` in `settings.json`
* **Toggling a port waits one second less**: `src/CuktechMible/MibleSession.cs` returns as soon as the SET ACK
  arrives - measured **1215 ms -> 179 ms** for one mask write, with the same read-back verification
* Version 0.5.0 -> **0.5.1**

### macOS 0.1.6 (shipped alongside)

* Absorbs PR #5 (0.1.5 + 0.1.6): a single central, stale connections dropped first, an already-connected charger
  adopted during the scan, a second adoption attempt after the scan timeout, **reconnect after wake from sleep**,
  and `peripheralId` remembered (optional field, so old configs keep decoding)
* Capture gains the same average / peak voltage and current, shown as two columns; `--capture-demo` renders a sample
* Panel always on top (`NSWindow.level = .floating`, independent of pinning)
* **The menu bar item stops jittering**: fixed 58 pt width plus content-fingerprint redraw; can be turned off
* **Glass panel**: `NSGlassEffectView` on macOS 26, vibrancy fallback on 13-15; can be turned off (on by default)
* Version 0.1.3 -> **0.1.6**

### 0.5.0 / macOS 0.1.3

* Device settings, per-port capture and an about window (Windows); macOS merged the same PR

## How to upgrade

* **Portable**: drop the new `CuktechTray.exe` over the old one, `settings.json` keeps working
* **Installed**: tray menu -> "Check for updates", it downloads, verifies the SHA256 and swaps itself
* **macOS**: unzip `CuktechTray-0.1.6-mac.zip`, drag `CuktechTray.app` into Applications; first launch needs right-click -> Open
* Only **one** copy can talk to the charger at a time - BLE is exclusive, so `--selftest` reports
  "enumerating characteristics failed: AccessDenied" while the tray instance is running. That is expected

## Files

| File | Size | Notes |
| --- | --- | --- |
| `CuktechTray-0.5.1-win.exe` | **11,068,928 bytes (10.56 MiB)** | Windows: portable + installer in one; 4,608 bytes more than 0.5.0 |
| `CuktechTray-0.1.6-mac.zip` | **1,590,954 bytes (1.52 MiB)** | macOS 13+: contains `CuktechTray.app` (universal, ad-hoc signed); see [mac/README.md](../mac/README.md) |

## Checksums

```
file   CuktechTray-0.5.1-win.exe   (built locally as CuktechTray.exe - same file)
size   11,068,928 bytes
SHA256 1652C5E09FB0B1CA91BE0E2E82F8BCCD359DE90728CB006ACAA3ABB37A17A014

file   CuktechTray-0.1.6-mac.zip   (universal build from CI on macos-26-intel)
size   1,590,954 bytes (1.52 MiB)
SHA256 28C290AA0CBD06FD1D0FEF7F669E6E5400B256EE626168F4D57E7463A1A08BD5
```

Use `Get-FileHash .\CuktechTray-0.5.1-win.exe -Algorithm SHA256` on Windows and `shasum -a 256 CuktechTray-0.1.6-mac.zip`
on macOS; the GitHub asset page also shows a digest.

## Requirements

* Windows 10 2004 (build 19041) or newer, x64; or macOS 13 or newer (universal Intel / Apple Silicon)
* Neither needs a runtime installed: Windows is a NativeAOT single file (10.56 MB), macOS is a native .app
* Hardware is still a CUKTECH 10 GaN Charger Ultra plus BLE

## Known

* The exe is **not code signed**, so the first run shows the blue "Windows protected your PC" prompt - click
  "More info" -> "Run anyway"
* The macOS build is **not notarised** (ad-hoc signature only); first launch needs right-click -> Open
* On Windows "always on top" is `WS_EX_TOPMOST`, and exclusive-fullscreen games can push it behind for a moment
* The glass effect is a macOS 26 system effect; on 13-15 you get vibrancy instead. Both can be turned off in the tray menu
* The two platforms number their versions **independently** (Windows 0.5.1 / macOS 0.1.6)

## Credits

* [@editwe](https://github.com/editwe): [PR #5](https://github.com/Blank2007/cuktech-ultra-tray/pull/5) - adopting
  live connections, reconnecting after wake, and the capture average/peak stats, all done on macOS first; the Windows
  side follows the same rules
* [@a1287448852](https://github.com/a1287448852): [CuktechMac10Ultra](https://github.com/a1287448852/CuktechMac10Ultra),
  a derivative of this project's macOS side. The fixed-width menu bar item, wake reconnection and glass panel on macOS
  were inspired by it; the implementations here are rewritten (see [NOTICE](../NOTICE))
* [@kairui1108](https://github.com/kairui1108): the first two projects to document this charger's BLE protocol
* And everyone who filed an issue or a PR against this project
