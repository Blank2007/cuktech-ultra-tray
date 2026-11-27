# 0.5.4 — chart tab, CSV export, quiet update check

> Release body draft (English). Chinese: [release-0.5.4.md](release-0.5.4.md).

In one line: the panel gained a **fourth tab, Chart**; per-port capture can **export CSV**; the update check now
**runs by itself on startup** and puts a red dot on the tray icon; and the "I can't move the taskbar digits to the
right" report in [issue #7](https://github.com/Blank2007/cuktech-ultra-tray/issues/7) is fixed — position and size
are menu choices now.

## What changed

### Chart tab (fourth panel tab)

* One line per port: **C1 blue / C2 green / C3+A amber / A violet**, the same palette as the bars on the Read tab
* Four spans: tap the pill in the top right to cycle `1 → 5 → 15 → 30` minutes; also in the right-click menu
  ("Chart span"), stored as `ChartSeconds` in `settings.json`
* The vertical axis picks a round top in 20 W steps, so a 65 W laptop does not flatten a phone's line onto the
  floor; the window's peak and average are printed at the bottom
* **Memory only**: one point per second, capped at 1800 points for 30 minutes (about 72 KB); closing the app
  loses it and no file is written
* An unplugged port, an idle one, and the one folded into C3+A read "no value", so the line **breaks** there;
  a port with no output at all in the window is not drawn (its legend entry is dimmed), which stops an all-zero
  port from adding a line that looks like an axis
* Samples closer than 0.4 s apart are skipped: the device sometimes pushes twice in a row and that would only
  add duplicate points

### CSV export for per-port capture

* Every second of capture also records voltage / current / watts / loaded into memory (7200 entries ≈ 2 hours)
* The buttons became two rows: "start / stop" + "close" on top, "copy summary" + "**export CSV**" below
  (the window grows from 482 to 534 high)
* Saved to `Documents\CuktechTray\cuktech-capture-<port>-<yyyyMMdd-HHmmss>.csv`, **UTF-8 with BOM** (without it
  Excel mangles the Chinese header), numbers always written culture-invariantly (a comma decimal separator would
  shift every column)
* Capture itself still writes nothing; a file only appears when you press "export CSV"

### Update check on startup

* On by default, **8 seconds after launch** (so Bluetooth connects first, without competing for CPU or network);
  `CheckUpdatesOnStart` in the settings file, also switchable from the menu
* New version found: a **red badge** on the tray icon's top-right corner, the menu entry becomes
  "检查更新…（有 0.5.5）" with a **small red dot**, plus one balloon
* The automatic check never blocks you with a dialog: only pressing "Check for updates" yourself shows
  "already the latest" or offers to replace now; if you are current the badge quietly disappears

### Taskbar digits wouldn't move right (issue #7)

* Position is now a choice: **auto** (left of Start) / **centre** / **right** (left of the tray) / **custom left offset**
* Four sizes: small / medium / large / custom width (56..160); **the font follows the width** (11 × width / 84),
  so widening never clips the digits
* The real cause: after docking, the z-order was never touched again, so dragging it into the tray area put it
  behind the tray window — it looked like it refused to move. Every re-layout now raises it to the front of its
  siblings, and a stolen capture ends the drag instead of hanging in "dragging"

### Also

* **No Bluetooth loop before pairing**: with an empty Mac the client used to throw
  `MAC 格式应为 AA:BB:CC:DD:EE:FF` every round, filling the log and holding the adapter
* `tools/check-ui.ps1` baselines re-recorded (two chart images added, capture window now 360×534)
* Version 0.5.3 → **0.5.4**

## How to upgrade

* **Portable**: overwrite the old `CuktechTray.exe`; `settings.json` keeps working as is
* **Installed**: tray → "Check for updates"; it downloads, verifies the SHA256 and restarts itself
* From this version on, doing nothing is enough: 8 seconds after launch it tells you whether there is a newer one

## Files

| File | Size | Notes |
| --- | --- | --- |
| `CuktechTray-0.5.4-win.exe` | **11,152,384 bytes (10.64 MiB)** | Windows: portable build and installer in one |
| `CuktechTray-0.1.7-mac.zip` | built by CI on this tag | macOS: unchanged this time, still 0.1.7; no need to update if you have it |

## Verification

```
file    CuktechTray-0.5.4-win.exe   (built locally as CuktechTray.exe, same file)
size    11,152,384 bytes
version 0.5.4.0
SHA256  E703B79F121B14E1794F6BBE18CE9651283BA18329B987E11FEA0F48A6015C5C
```

Compare with `Get-FileHash .\CuktechTray-0.5.4-win.exe -Algorithm SHA256`.

## Requirements

* Windows 10 2004 (build 19041) or newer, x64
* No runtime to install: NativeAOT single file
* The hardware is still the CUKTECH 10 Ultra charger over Bluetooth (BLE)

## Known

* The exe is **not code signed**, so the first run shows the blue "Windows protected your PC" box; click
  "More info → Run anyway"
* macOS was not touched this time (still 0.1.7): the chart tab and the taskbar strip are Windows-only, CSV export
  will land in the next mac build
* The taskbar digits' position and size apply immediately after you pick them, no restart

## Thanks

* [@KingGxxT](https://github.com/KingGxxT): [issue #7](https://github.com/Blank2007/cuktech-ultra-tray/issues/7) —
  describing the "won't drag to the right" case is what produced the four positions
* [@kairui1108](https://github.com/kairui1108): one of the first two projects to write the CUKTECH 10 Ultra BLE
  protocol down
* and everyone who filed an issue or a PR here
