# 0.5.8 — the macOS panel grows the same four tabs

> Draft release body (English). Chinese version: [release-0.5.8.md](release-0.5.8.md).

One line: **the macOS panel goes from one long page to four tabs (Readings / Control / Modes / Chart)**, matching
the Windows layout, and the chart tab picks up "you can see which port is delivering at a glance". Windows only
follows the version number this time — the code is identical to 0.5.7.

## What changed

### macOS 0.1.9: four tabs and a chart

The mac panel used to be a single column: four port readings, four switches and the four charging modes all crammed
into 340x424, with no graph anywhere. It is tabbed now, same as Windows:

* **Readings**: voltage / current / watts per port, with the single-port capture button on the right of each row
* **Control**: one switch per port, one row each (they used to share a row with the readings and were easy to mis-hit)
* **Modes**: AI / Digital ecosystem / Single port / Balanced, the current one ticked with a green border
* **Chart**: new — one line per port (C1 blue / C2 green / C3 orange / A purple; when C3 and A report a merged meter
  that line reads **C3+A**)

The chart tab answers the same question Windows 0.5.7 did: **with a single port delivering, which line is it?**

* **The subtitle names the delivering ports**: "在输出：C3", "在输出：C1、C3"; with nothing delivering it falls back to
  "one line per port, click the right side to change the span"
* **Each line end carries its port name** in the line's own colour, kept apart vertically, flipped to the left of the
  point near the right edge, on a translucent plate
* Ports that never delivered are only dimmed in the legend — no line
* Four spans: tap the capsule in the top right to cycle **1 / 5 / 15 / 30 minutes**, stored as `chartSeconds` in `config.json`
* History lives **in memory only** (1 Hz x 30 minutes = 1800 points capped, about 72 KB) and is gone when the app quits.
  For data you want to keep, use single-port capture and export the CSV
* A port that is unplugged, not answering, or merged away records -1 and the line **breaks** there instead of drawing
  a smooth ramp down

Also fixed a bug of my own making: when the clock jumps backwards (sleep, time sync) the new sample was treated as
"too soon" and dropped, so the chart froze on stale data. It now clears the buffer first, then applies the throttle.

### Windows: version number only

* 0.5.7 -> **0.5.8**, no code changes (everything in this release is on the mac side)
* If you already run 0.5.7 you can stay there; overwrite once if you want "check for updates" to line up

## Upgrading

* **macOS**: download `CuktechTray-0.1.9-mac.zip`, unzip and drag `CuktechTray.app` into Applications, replacing the old one;
  right-click -> Open the first time (ad-hoc signed, no Apple developer account)
* **Windows portable**: replace the old `CuktechTray.exe`, `settings.json` keeps working
* **Windows installed**: tray menu -> "检查更新", it downloads, verifies SHA256 and swaps itself in

## Files

| File | Size | Notes |
| --- | --- | --- |
| `CuktechTray-0.5.8-win.exe` | **10.6 MB** (11,151,360 bytes) | Windows: portable + installer in one (same code as 0.5.7, version bumped) |
| `CuktechTray-0.1.9-mac.zip` | **PENDING** | macOS: four-tab panel and the chart |

## Checksums

```
file    CuktechTray-0.5.8-win.exe   (built locally as CuktechTray.exe, same file)
size    11,151,360 bytes
version 0.5.8.0
sha256  AA7FD194F2B98EDD78815FEE3D0B4A00349052D65835A23CC3DB80B469BB65B0

file    CuktechTray-0.1.9-mac.zip     (packed by CI, unzips to CuktechTray.app)
size    PENDING
sha256  PENDING
```

## Requirements

* Windows 10 2004 (build 19041) or newer, x64; no runtime needed (NativeAOT single file)
* macOS 13 or newer, Apple Silicon or Intel (universal)
* Hardware is still the CUKTECH 10 GaN Charger Ultra plus Bluetooth (BLE)

## Known

* The Windows exe is **not code signed**: the first launch shows the blue SmartScreen box, click "More info -> Run anyway"
* The macOS .app is ad-hoc signed; the first open needs "Open Anyway" under Privacy & Security
* The mac chart history is memory only and resets when the app quits (same as Windows)

## Thanks

* [@kairui1108](https://github.com/kairui1108) for the first public write-ups of this charger's BLE protocol
* [@editwe](https://github.com/editwe), [@a1287448852](https://github.com/a1287448852) and everyone who filed issues or PRs
