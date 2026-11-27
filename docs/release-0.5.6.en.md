# 0.5.6 — the context menu is a real Windows menu again, and submenus work

> This is the GitHub Release body draft (English). Chinese version: [release-0.5.6.md](release-0.5.6.md).

In one line: **the submenus in the right-click menu could not be clicked (a 0.5.x bug) — fixed by no longer
drawing the menu ourselves and handing it back to Windows**; plus a fix for "an unpaired config used to make the
app forget the whole settings.json". macOS unchanged, still 0.1.8.

## What changed

### Windows: the context menu is native again

* **The bug**: since 0.4.0 the menu was self-drawn. Top-level items worked, but every row inside
  「充电模式」/「任务栏数字」/「功率曲线时长」 did nothing when clicked — the menu just closed — and
  "click outside to close" broke with it
* **Root cause**: the self-drawn menu relied on the root window holding the mouse capture and forwarding to the
  submenu window, while the base class `WinBase` releases capture unconditionally on `WM_LBUTTONUP`. The moment you
  let go, capture was gone, and mouse messages went to the submenu window, whose first line is
  `if (!IsRoot) return;` — so a submenu row could never receive a click
* **The fix**: the 439-line self-drawn menu is gone; the menu is a real Windows menu
  (`CreatePopupMenu` + `TrackPopupMenuEx(TPM_RETURNCMD)`). Submenu direction, keyboard, Esc,
  click-outside, high DPI and accessibility are all user32's job — **exactly like every other tray app**
  (TranslucentTB's native menu was the reference)
* It follows the system theme: light theme gives a light menu, dark theme a dark one. Toggles carry the system
  check mark, the one-of-many rows carry a dot, and the row that has news gets a leading "● "
* **Another bug fixed on the way**: `Settings.Load()` used to use the file contents only when MAC + token were
  both present, otherwise it started from a blank config — so a user who accepted the privacy notice and quit
  before pairing was asked again on the next launch, and lost the taskbar position, the chart duration and the
  "check for updates on startup" switch. Now the file is kept as long as it parses; only the login fields are refilled
* Docs: the menu screenshot in the READMEs is now a real screen capture of the native menu (light theme)

### macOS: unchanged

* macOS always used the system menu (`NSMenu`), so it never had this problem; version stays at **0.1.8**
* CI will re-pack a `CuktechTray-0.1.8-mac.zip` for this tag as usual; the contents are the same as 0.5.5

## How to update

* **Windows portable**: overwrite the old `CuktechTray.exe`, keep your `settings.json`
* **Windows installed**: tray menu → 「检查更新」; it downloads, verifies SHA256 and replaces itself
* **macOS**: nothing to do, still 0.1.8

## Files

| File | Size | Notes |
| --- | --- | --- |
| `CuktechTray-0.5.6-win.exe` | **11,137,536 字节（10.62 MiB）** | Windows: portable app + installer in one file |
| `CuktechTray-0.1.8-mac.zip` | **1,623,251 字节** | macOS: still 0.1.8 (unchanged this time) |

## Checksums

```
file   CuktechTray-0.5.6-win.exe   (built locally as CuktechTray.exe, same bytes)
size   11,137,536 bytes
ver    0.5.6.0
sha256 C7C58523566CA954BFFB5C6B3566896D3201E39EE9C74EC3AF58422BBC555DEF

file   CuktechTray-0.1.8-mac.zip     (packed by CI; unzips to CuktechTray.app)
size   1,623,251 字节
sha256 BEDD82CE0C5EB5263B0A666CEF9BFEBE47D02DC7608E7B3BE53ABE4543AF7206
```
## Requirements

* Windows 10 2004 (build 19041) or newer, x64; no runtime needed (NativeAOT single file)
* macOS 13 or newer, Apple Silicon and Intel (universal)
* Hardware is still a CUKTECH 10 GaN Charger Ultra plus Bluetooth (BLE)

## Known

* The Windows exe is **not code signed**; the first run shows the blue SmartScreen box — click "More info → Run anyway"
* The macOS .app is ad-hoc signed (no Apple developer account); the first launch needs "Open Anyway" under Privacy & Security
* A system menu can only express check / dot / greyed-out, so the "new version" hint moved from a red dot at the end of
  the row to a leading "● " in the text

## Thanks

* [@KingGxxT](https://github.com/KingGxxT): [issue #1](https://github.com/Blank2007/cuktech-ultra-tray/issues/1) (C3+A merged reading), [issue #7](https://github.com/Blank2007/cuktech-ultra-tray/issues/7) (taskbar number could not be moved)
* [@kairui1108](https://github.com/kairui1108): the first projects to write down the CUKTECH 10 Ultra BLE protocol
* TranslucentTB: the reference for how a native tray menu should feel
* Everyone who filed an issue or sent a pull request
