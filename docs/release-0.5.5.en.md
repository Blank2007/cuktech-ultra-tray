# 0.5.5 — the privacy notice now matches what the app does, and the Mac can export CSV

> Draft body for the GitHub Release (English). Chinese: [release-0.5.5.md](release-0.5.5.md).

One line: on Windows only one sentence changed — since 0.5.4 the app checks for updates by itself, but the
notice still said "only when you press 「检查更新」 does it reach GitHub". macOS moves to **0.1.8**: capture
results **export to CSV**, and the app **checks for updates on launch** and marks it with a red dot in the menu bar.

## What changed

### Windows: the privacy notice matches the behaviour

* 0.5.4 added "check for updates eight seconds after launch", but section three of the notice you must accept
  on first launch still read "only when you press 「检查更新」 does it reach GitHub" — untrue since 0.5.4, and
  that text is what the user agreed to
* Section three now reads "**启动时会在后台查一次更新（默认开，右键菜单里能关），只拉公开的发布列表**"
* Changed in `src/CuktechTrayAot/ConsentWindow.cs` (the first-launch gate) and the wizard's 「隐私说明」 box
  (`WizardWindow.cs`)
* Apart from that sentence, Windows 0.5.5 is the same program as 0.5.4 — no reason to upgrade if 0.5.4 works

### macOS 0.1.8: CSV export and the update check on launch

* The capture result window gains an "**导出 CSV**" button: one row per second (time / volts / amps / watts /
  loaded), you pick the location, default name `cuktech-capture-<port>-<yyyyMMdd-HHmmss>.csv`, UTF-8 with BOM
* **Checks for updates eight seconds after launch** (`checkUpdatesOnStart`, switchable from the menu): a new
  version puts a small red dot after the wattage in the menu bar and turns the menu row into
  "检查更新…（有 X）" with a leading dot — **no dialog**
* Only pressing "检查更新…" yourself shows "new version / download / later" or "already the latest"
* No dot in icon-only mode (a template image would get tinted); and when nothing is paired yet the check puts
  back the "not paired" status instead of claiming a connection
* New tests `testCsvExport` / `testSampleCap` (exact header, CRLF, decimals, idle row, 7200-entry cap)

## Upgrading

* **Windows portable**: overwrite the old `CuktechTray.exe`; `settings.json` keeps working
* **Windows installed**: tray → 「检查更新」; it downloads, verifies SHA256 and replaces itself
* **macOS**: unzip, drag `CuktechTray.app` into Applications over the old one. Still ad-hoc signed, so the first
  launch may need "仍要打开" in System Settings → Privacy & Security

## Files

| File | Size | Notes |
| --- | --- | --- |
| `CuktechTray-0.5.5-win.exe` | **11,152,384 bytes (10.64 MiB)** | Windows: portable + installer in one |
| `CuktechTray-0.1.8-mac.zip` | built by CI on this tag | macOS: CSV export + update check on launch |

## Verify

```
file   CuktechTray-0.5.5-win.exe   (the local build is called CuktechTray.exe, same bytes)
size   11,152,384 bytes
ver    0.5.5.0
SHA256 CB814463EF42D0C240C5348E8FD0AE5B34EE02BA7390267541B16209DE71D575
```

## Requirements

* Windows 10 2004 (build 19041) or newer, x64; no runtime needed (NativeAOT single file)
* macOS 13 or newer, Apple Silicon and Intel (universal)
* A CUKTECH 10 Ultra charger and Bluetooth LE

## Known

* The Windows exe is **not code signed**; SmartScreen will ask once — "More info → Run anyway"
* The macOS .app is ad-hoc signed (no Apple developer account); the first launch needs "仍要打开" in
  Privacy & Security
* The chart tab, the taskbar number and the tray badge are Windows-only; on macOS the update hint is a red dot
  after the menu-bar text

## Thanks

* [@KingGxxT](https://github.com/KingGxxT): [issue #1](https://github.com/Blank2007/cuktech-ultra-tray/issues/1) (C3+A merged display), [issue #7](https://github.com/Blank2007/cuktech-ultra-tray/issues/7) (taskbar number wouldn't move)
* [@kairui1108](https://github.com/kairui1108): the two earliest write-ups of this charger's BLE protocol
* Everyone who filed an issue or a PR
