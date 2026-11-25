# 0.5.2 - the panel brings a saved position back on screen

> Draft body for the GitHub Release (English). Chinese in [release-0.5.2.md](release-0.5.2.md).

One line: this fixes **a hole I left in my own recent versions**. If the panel's remembered position ended up
off-screen, it was restored exactly there on start and the panel was never seen again. This release clamps the
position into the monitor before showing, stops dragging from pushing it off-screen, and adds a one-click reset.

## What changed

### 0.5.2 (Windows, this release)

* **The saved position is clamped before showing** (`ShowAtClamped` / `ClampKeepVisible` in
  `src/CuktechTrayAot/PanelWindow.cs`): `MonitorFromPoint` picks the nearest monitor and the coordinates are pulled
  back into its work area; when that happens the log gets
  `[panel] 记住的位置在屏幕外 -495,1072 → 挪到 0,1035` and the corrected coordinates are written back to `settings.json`
* **Dragging is clamped too**: dragging used to push absolute coordinates straight into `SetWindowPos`, so the panel
  could be dragged fully off-screen (that is exactly how this broke); it now stops at the edge, keeping 60 dip visible
* **New tray item "Reset panel position (back to the bottom-right corner)"**: clears the stored position in one click
* The trigger is worth spelling out: with `PanelPinned` true and a stale off-screen `PanelX`/`PanelY`, the panel
  vanished **on every start**, not occasionally; 0.5.1's always-on-top switch only touches z-order and does not move
  the window, so it is unrelated
* Version 0.5.1 -> **0.5.2**

### macOS 0.1.6 (shipped with the same release)

No macOS code changed this round; the Release still carries **0.1.6** (universal, ad-hoc signed). The two platforms
keep separate version numbers.

## How to upgrade

* **Portable**: overwrite the old `CuktechTray.exe`, `settings.json` keeps working (a broken position is corrected on read)
* **Installed locally**: tray menu -> "Check for updates"; it downloads, verifies the SHA256 and restarts into the new build
* If the panel still does not show up after the update, tray menu -> "Reset panel position", then open the panel again
* **macOS**: unzip `CuktechTray-0.1.6-mac.zip`, drag `CuktechTray.app` into Applications, overwriting the old one;
  right-click -> Open on first launch

## Files

| File | Size | Notes |
| --- | --- | --- |
| `CuktechTray-0.5.2-win.exe` | **11,070,464 bytes (10.56 MiB)** | Windows: portable + installer in one file |
| `CuktechTray-0.1.6-mac.zip` | rebuilt by CI on this tag (about 1.55 MiB) | macOS 13+: unzips to `CuktechTray.app`, see [mac/README.md](../mac/README.md) |

## Verification

```
file   CuktechTray-0.5.2-win.exe   (built locally as CuktechTray.exe, same file)
size   11,070,464 bytes
SHA256 1904B3A79FC1D2580D4E01C7D946BDC8F5253F3AEEF0E70F07F2854E32B1991B
```

For `CuktechTray-0.1.6-mac.zip` use the digest GitHub shows on this release page (CI re-zips on every tag, so the
bytes differ run to run).

## Requirements

* Windows 10 2004 (build 19041) or newer, x64; or macOS 13 or newer (universal Intel / Apple Silicon)
* No runtime needed either side: Windows is a NativeAOT single file (10.56 MB), macOS is a native .app
* Hardware is still the CUKTECH 10 GaN Charger Ultra over BLE

## Known

* The exe is **not code-signed**, so SmartScreen shows the blue "Windows protected your PC" panel on first run:
  More info -> Run anyway
* The macOS bundle is **not notarized** (ad-hoc signature only), so right-click -> Open on first launch
* If the panel lands on another monitor after a display change, it is pulled into that monitor's visible area rather
  than moved to the current one; use the "Reset panel position" item when you want it back in the corner
* The two platforms keep **separate version numbers** (Windows 0.5.2 / macOS 0.1.6)
