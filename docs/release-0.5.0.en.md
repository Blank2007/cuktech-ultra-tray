# 0.5.0 — Someone else built it, we ported it

> This is the draft for the GitHub release body (English). Chinese is in [release-0.5.0.md](release-0.5.0.md).

In one line: [@editwe](https://github.com/editwe) built **device settings**, **per-port capture**, a **pinnable panel**
and an **about window** for the macOS build in [PR #4](https://github.com/Blank2007/cuktech-ultra-tray/pull/4).
This release ports the same features to Windows over the same protocol, and merges that PR into macOS as **0.1.3**.
The two platforms still number their versions independently.

## What changed

### 0.5.0 (Windows, this build)

* **Device settings** (new window, `src/CuktechTrayAot/DeviceSettingsWindow.cs`), tray menu -> "Device settings"
  * Screen: screen timeout (1 / 5 / 10 / 30 min or always on), idle screen-off, orientation lock, screen language (Chinese / English)
  * Ports: countdown shutoff per port (off / 15 min / 30 min / 1 h / 2 h / 4 h, click to cycle) and USB-A low-current mode
  * Fast charge: which handshakes each port may negotiate - PD / PPS / UFCS on C1 and C2, UFCS / SCP on C3 and A
  * Every write is read back and compared; a mismatch says "the device only accepted 0x..." instead of pretending
  * Two bits of the protocol mask are reserved and hard-wired by firmware, so the value is read first and only the target
    bits change. Without that baseline the write is refused
  * Anything that cannot be read (just disconnected, unsupported firmware) is dimmed and unclickable - "not read" is
    never shown as "off"
* **Per-port capture** (new window, `src/CuktechTrayAot/CaptureWindow.cs`), tray menu -> "Per-port capture"
  * Pick a port, it samples once a second; stopping reports **Wh / average / peak / voltage range / voltage changes / why it ended**,
    with a one-click summary copy
  * The integration is conservative: trapezoidal inside one tier; on a tier change (5 V -> 20 V) that second uses the
    pre-jump power, because averaging invents a 12.5 V state and roughly doubles the energy
  * Gaps longer than 4 s are not integrated; the average divides by **seconds actually delivering power**
  * 30 s without output or without readings ends the capture on its own, and the reason is always reported
* **About** (new window, `src/CuktechTrayAot/AboutWindow.cs`): version, project page, tested firmware and who helped.
  Opening it makes **no network request**; only "Check for updates" talks to GitHub
* The tray menu gains "Device settings", "Per-port capture" and "About"; all three custom windows share one modal gate so
  stacking them cannot make Esc close the wrong one
* One false alarm cleared up: the 0.4.8 `--selftest` printed one `[start]` line and exited - another instance held the
  single-instance mutex and the process yielded by design. Self-check arguments now expect `--standalone`
* Version 0.4.8 -> **0.5.0**

### macOS 0.1.3 (same release)

* Everything from PR #4: the settings panel, per-port capture, a pinnable panel, the about window with update check,
  the poll interval moved into the footer, and the port-switch index fix
* His command queue was folded into the 0.1.2 `withTransactionLock(urgent:)`, and his port numbering was reconciled
* Version 0.1.2 -> **0.1.3**; `swift test` passes 72 tests and CI builds the universal bundle

### 0.4.7

* C3 and A shown as one merged row, and a first-launch privacy consent gate

## How to upgrade

* **Portable**: overwrite the old `CuktechTray.exe`; `settings.json` keeps working
* **Installed**: tray menu -> "Check for updates" downloads, verifies SHA256 and swaps the running exe
* **macOS**: unpack `CuktechTray-0.1.3-mac.zip` and drag `CuktechTray.app` into Applications; right-click -> Open the first time
* One caveat: **only one copy of the program can talk to the charger at a time**. Bluetooth is exclusive, so while the tray
  copy is running `--selftest` reports "enumerating characteristics failed: AccessDenied" - that is expected, not a defect

## Files

| File | Size | Notes |
| --- | --- | --- |
| `CuktechTray-0.5.0-win.exe` | **11,064,320 bytes (10.55 MiB)** | Windows: portable and installer in one file; 165,376 bytes more than 0.4.7 (three new windows plus protocol reads/writes) |
| `CuktechTray-0.1.3-mac.zip` | see the release assets | macOS 13+: unpacks to `CuktechTray.app` (universal, ad-hoc signed), see [mac/README.md](../mac/README.md) |

## Verification

```
File   CuktechTray-0.5.0-win.exe   (the local build calls it CuktechTray.exe, same file)
Size   11,064,320 bytes
SHA256 7DC069BDB4266003915D66A6F98A625C65392DA4E35AC33BB64FD97C43CDE4CE
```

On Windows compare with `Get-FileHash .\CuktechTray-0.5.0-win.exe -Algorithm SHA256`; the GitHub asset page also shows the digest.

## Requirements

* Windows 10 2004 (build 19041) or newer, x64; or macOS 13+ (universal, Intel and Apple Silicon)
* No runtime on either side: a NativeAOT single file (10.55 MB) on Windows, a native .app on macOS
* The hardware is still a CUKTECH 10 Ultra charger over Bluetooth LE

## Known

* The exe is **not code-signed**; the first run may show the blue "Windows protected your PC" box - click "More info -> Run anyway"
* The macOS bundle is **not notarised** (ad-hoc signature only); right-click -> Open the first time
* The updater only accepts assets starting with `CuktechTray-`, ending in `.exe` and not containing `Setup`, so `-win.exe` matches and `-mac.zip` is ignored
* The two platforms use **separate version lines** (Windows 0.5.0 / macOS 0.1.3)
* The countdown read-back may be the remaining time rather than the configured value, so that item is logged but not treated as a failure

## Thanks

* [@editwe](https://github.com/editwe): [PR #4](https://github.com/Blank2007/cuktech-ultra-tray/pull/4) - device settings,
  per-port capture, the pinnable panel and the about window all came from his macOS work; the Windows side follows the same protocol
* [@KingGxxT](https://github.com/KingGxxT): [issue #1](https://github.com/Blank2007/cuktech-ultra-tray/issues/1) - the merged C3+A reading
* [@a1287448852](https://github.com/a1287448852): [PR #3](https://github.com/Blank2007/cuktech-ultra-tray/pull/3) - macOS port switching,
  BLE command concurrency and the light-mode menu bar