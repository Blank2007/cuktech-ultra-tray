# 0.4.5 — same UI, different evidence

> Draft body for the GitHub Release (English). 中文见 [release-0.4.5.md](release-0.4.5.md).

One line: this is the same program as 0.4.4 (no functional or visual change). What changed is the evidence behind the
docs — the tray icon is rendered by the program itself, the context menu is captured from the real thing, and the taskbar
digits are rendered with the runtime font and colour rules and composited over a real taskbar.

## What changed

### 0.4.5 (this release)

* **Taskbar image redone**: the README taskbar picture was a 0.2.2 demo capture and even had the wrong colour — 6.0 W is
  **light blue** under the current buckets, the old picture drew it green. A new `--render-taskbar` switch renders that strip
  to a transparent PNG with the same font (Segoe UI Bold 11 pt × scale) and the same colour rules (`WattStyle.Total`) the
  runtime uses, then it is composited over a real taskbar capture → `docs/shot-taskbar.png`
* **README "Power on the taskbar" got a new image and new wording**: a colour-bucket table (grey ≈0 W / light blue 0–20 W /
  green 20–60 W / amber 60–100 W / red above 100 W) and a note on why the `6.0W` in the picture is light blue (both languages)
* **Tray icon rendered by the program**: the drawing inside `WattIcon` was split out as
  `public static Bitmap CreateBitmap(int size, Color accent)` and a new `--render-icons` switch writes a 608×256 transparent
  PNG (`docs/shot-tray-icons.png`) instead of relying on a 125%-scaled screenshot
* **Context menu captured from the real thing**: `--menu-demo` now writes "已连接 · 总 6.0 W" before showing the menu, so the
  captured header is no longer "connecting…"; the new `docs/shot-menu-aot.png` replaces the 0.3.3 WinForms capture
* Version 0.4.4 → 0.4.5; **no functional or visual change** — upgrading from 0.4.4 looks exactly the same (the exe is 4,608
  bytes larger, purely the self-check switches)

### 0.4.4

* The connected pill in the top bar drops its translucent green fill; the dot and the label stay. The top bar gets room to
  breathe and the panel grows 320×396 → **320×404**

### 0.4.2

* **Black corners fixed**: the rounded corners used to be just "a rounded path painted as the background" while the window
  itself stayed rectangular; it is now clipped with `SetWindowRgn` + `CreateRoundRectRgn` (same integer radius for drawing and
  clipping), and `--render` exports transparent corners
* **Roomier layout**: panel 320x360 → 320x396, row pitch 42 → 48, mode cards 44 → 50 tall with 52 → 58 spacing

## Upgrading

* **Portable**: overwrite the old `CuktechTray.exe`, `settings.json` keeps working, nothing is reset
* **Installed**: tray menu → "Check for updates"; it downloads, verifies SHA256 and swaps itself, or just replace
  `%LOCALAPPDATA%\Programs\CuktechTray\CuktechTray.exe`
* **Command line**: `--install [--dir=<path>] [--autostart] [--desktop] [--silent]` installs, `--uninstall [--silent]`
  removes everything (shortcuts, HKCU uninstall entry, folder) without ever raising UAC
* **Re-pair the device**: `--setup` opens the QR wizard (same path as the tray menu item)

## Files

| File | Size | Notes |
| --- | --- | --- |
| `CuktechTray-0.4.5.exe` | **10,879,488 bytes (10.38 MiB)** | Portable build and installer in one file — the only asset in the release; 4,608 bytes more than 0.4.4 purely from the self-check switches |

## Checksums

```
file   CuktechTray-0.4.5.exe    (the local copy is called CuktechTray.exe; same bytes, same hash)
size   10,879,488 bytes
SHA256 7A14EFE8E71B9BB2696D00ECFAD4418B526F428D0675E7DAC508229B4A310EEA
```

On plain Windows use `Get-FileHash .\CuktechTray.exe -Algorithm SHA256`; GitHub also shows the digest on the asset page.

## Requirements

* Windows 10 2004 (build 19041) or newer, x64
* No .NET runtime needed: NativeAOT single file
* A CUKTECH 10 GaN Charger Ultra over BLE; after authentication only Bluetooth is used

## Known

* The exe is **not code-signed**, so the first run shows the blue "Windows protected your PC" box — click "More info →
  Run anyway"; it will not ask again
* Auto-update only accepts assets named `CuktechTray-<version>.exe` (anything with `Setup` is skipped), which is why
  `dist\CuktechTray-0.4.5.exe` exists — use it for the release asset rather than `CuktechTray.exe`
* `--render`, `--render-icons`, `--render-taskbar`, `--font-probe`, `--update-selftest` are developer self-check switches;
  do not hand-run `--apply-update` (it swaps the exe it is pointed at)
