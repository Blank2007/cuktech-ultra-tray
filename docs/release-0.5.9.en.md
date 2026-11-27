# 0.5.9 — the file name follows the update, and captured data stays inside the program folder

> Draft for the GitHub Release body (English). Chinese version: [release-0.5.9.md](release-0.5.9.md).

In one line: **self-update no longer leaves the old version number in the file name**
(`CuktechTray-0.5.8-win.exe` becomes `CuktechTray-0.5.9-win.exe` after the update, with the shortcuts, the autostart
value and the uninstall entry repointed along with it), and **per-port capture CSVs now default to the program's own
folder** instead of the system Documents folder. Nothing changed on the macOS side; it stays at 0.1.9.

## What changed

### The version number in the file name follows the update

The old updater copied the new binary **over the same path**, so the name stayed at whatever version it was first
downloaded as: after a few update checks the binary was 0.5.x while the file still said `0.4.6`.

* Before replacing, the current name is checked for an `x.y.z`; if present, a new target name is built from the
  **new exe's** version, the file is replaced under that name and the old name is deleted
* Four places remember the old name and are repointed: the desktop shortcut, the Start-menu shortcut, the
  `HKCU\...\Run` autostart value and the uninstall entry (DisplayIcon / UninstallString / InstallLocation /
  DisplayVersion). Each one is only touched when it really points at the old file
* The installed copy keeps its fixed `CuktechTray.exe` name — it never had a version in it

### Generated files stay next to the program

* **Capture CSV exports**: installed copies write to `%LOCALAPPDATA%\Programs\CuktechTray\captures\`, the portable
  build writes to `captures\` next to the exe; only when that folder is not writable does it fall back to
  `Documents\CuktechTray\`, and the log says why
* `settings.json` and `logs\app.log` already followed the exe (install dir, or next to the portable exe); unchanged
* Update staging files stay in `%TEMP%` — the exe being replaced may still be mapped, so it cannot be moved from its
  own folder

### Verification

* `--update-selftest` gained two assertions and now prints
  `[update-selftest] ok 替换=True 未误伤=True 固定名不动=True 带版本号改名=True 自身版本=0.5.9`
* Verified end to end: `--apply-update` with `CuktechTray-0.5.7-win.exe` as the target left only
  `CuktechTray-0.5.8-win.exe` in the folder — old name and `.old` backup both gone

## How to update

* **Windows, installed copy**: tray menu -> "Check for updates"; it downloads, verifies SHA256 and replaces itself.
  Note that **this update changes the update path itself**, so the name starts following along from 0.5.9 onwards;
  the 0.5.8 -> 0.5.9 hop is performed by the old code, which leaves the name alone
* **Windows, portable**: overwrite the old `CuktechTray.exe`, keep your `settings.json`
* **macOS**: no mac changes this time; 0.1.9 can stay as is

## Files

| File | Size | Notes |
| --- | --- | --- |
| `CuktechTray-0.5.9-win.exe` | **10.7 MB** (11,197,440 bytes) | Windows: portable build and installer in one file |
| `CuktechTray-0.1.9-mac.zip` | **PENDING** | macOS: same as the 0.5.8 release (0.1.9 sources unchanged) |

## Checksums

```
file   CuktechTray-0.5.9-win.exe   (built locally as CuktechTray.exe, same bytes)
size   11,197,440 bytes
ver    0.5.9.0
SHA256 6F94829BE572CC7DE88FBFE458C8757D17C94D7DE9F8930CB6786C31449DB60C

file   CuktechTray-0.1.9-mac.zip     (packed by CI, unzips to CuktechTray.app)
size   PENDING
SHA256 PENDING
```

## Requirements

* Windows 10 2004 (build 19041) or newer, x64; no runtime needed (NativeAOT single file)
* macOS 13 or newer, Apple Silicon and Intel (universal)
* Hardware: a CUKTECH 10 Ultra charger with Bluetooth (BLE)

## Known

* The Windows exe is **not code-signed**; the first run shows the blue SmartScreen box — "More info -> Run anyway"
* The macOS app is ad-hoc signed; the first launch needs "Open Anyway" under Privacy & Security
* For the 0.5.8 -> 0.5.9 hop the file name is still decided by the old code (the rename logic only takes effect once
  0.5.9 is running); from 0.5.9 to 0.6.x you will see the name follow the update

## Thanks

* [@kairui1108](https://github.com/kairui1108): the two earliest write-ups of the CUKTECH 10 Ultra BLE protocol
* [@editwe](https://github.com/editwe), [@a1287448852](https://github.com/a1287448852) and everyone who filed issues or PRs
