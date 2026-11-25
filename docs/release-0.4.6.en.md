# 0.4.6 — the English sentence after a QR scan was our bug

> Draft release body (English). Chinese version: [release-0.4.6.md](release-0.4.6.md).

In one line: a bug only reachable through real use — **right after a successful QR pairing the wizard showed the English sentence**
`Reflection-based serialization has been disabled for this app`. This release also adds a privacy note to the wizard and the README,
starts macOS on its own version line, and thanks to @editwe ships a fixed "pairs but never connects" bug on macOS.

## What changed

### 0.4.6 (this release)

* **Fixed QR pairing**: after 0.4.0 moved to NativeAOT, reflection-based JSON serialization is off by default, and the pairing path used it in two places —
  the `/v2/home/home_device_list` body (inside `MiCrypto.JsonToken()`) and `blt_get_beaconkey` (`serialize(did)`).
  Both now use a hand-written escaper `MiCrypto.JsonQuote()`, with identical behaviour
* **Why it had never been caught**: local testing always used a saved `settings.json` and skipped the login path, so the pairing path had never run on a real AOT build.
  `--selftest` now covers it: seven JSON-escaping assertions plus a settings source-generation round-trip, and it prints
  `[aot] json-reflection-enabled=False` so the log proves that reflection serialization really is off under AOT
* **Privacy note in the wizard**: one small line at the bottom of the first screen; it opens a four-part explanation (what scanning does / where data stays / what is never done / what to delete).
  No checkbox, nothing blocking
* **README gained a "9. Privacy" section** (both languages), matching the dialog, with a when / what / where table
* Version 0.4.5 → 0.4.6

### Privacy in short

* Scanning exchanges this charger's `mac` and a 12-byte `token` once with Xiaomi Cloud; account, password and verification code stay on the phone
* Credentials are written only to the local `settings.json` (installed builds: `%LOCALAPPDATA%\CuktechTray\settings.json`);
  the session in `session.json` exists only to save one scan when reconfiguring
* Power readings and port switching go over Bluetooth with no network; **only an explicit "Check for updates" contacts GitHub**
* No uploads, no telemetry, no background service; uninstalling leaves nothing behind

### macOS: 0.1.1 (own version line from here on)

macOS no longer shares the Windows version number and starts at **0.1.0**: `CuktechTray-0.1.1-mac.zip`.
The two platforms move at different speeds, and one shared number only mixes unrelated changelog entries.

**0.1.1 fixes a real 0.1.0 bug**: pairing finished but the menu bar kept showing "—" and the log filled up with
"扫不到 MAC 为 … 的广播" (no advertisement with that MAC). macOS hands out a bare payload through
`CBAdvertisementDataServiceDataKey`, while the old code waited for the two-byte `95 FE` service-UUID prefix the Windows
side uses, so parsing always failed — nothing to do with the charger or your Bluetooth permission, we simply parsed it wrong.

This release also: waits 10 → 30 s for Bluetooth to come up (and says plainly when the permission is missing),
left-click opens the panel / right-click shows the menu, draws its own icon at build time (no more generic icon in Finder),
and mac/README gained a troubleshooting table. The privacy note is unchanged; see [mac/README.md](../mac/README.md).

### 0.4.5

* Promo assets replaced with the real thing: `--render-icons` / `--render-taskbar` render from the program itself, the menu is captured from the real menu; no functional change from 0.4.4

### 0.4.4

* The connected pill drops its tinted fill (dot and label stay) and the top bar moves down 8 px; panel 320×396 → **320×404**

## Upgrading

* **Portable**: overwrite the old `CuktechTray.exe`; `settings.json` keeps working
* **Installed**: tray menu → "Check for updates" downloads, verifies SHA256 and replaces itself; or replace
  `%LOCALAPPDATA%\Programs\CuktechTray\CuktechTray.exe` directly
* **Tried to pair and failed**: after upgrading, run the wizard again (tray menu → "Reconfigure device…", or `CuktechTray.exe --setup`)
* **Command line equivalents**: `--install [--dir=<path>] [--autostart] [--desktop] [--silent]`, `--uninstall [--silent]`, no UAC prompts

## Files

| File | Size | Notes |
| --- | --- | --- |
| `CuktechTray-0.4.6-win.exe` | **10,882,048 bytes (10.38 MiB)** | Windows: portable build + installer in one; 2,560 bytes more than 0.4.5 (hand-written escaping + self-checks) |
| `CuktechTray-0.1.1-mac.zip` | **999,107 bytes (976 KiB)** | macOS 13+: unpacks to `CuktechTray.app` (universal, Intel / Apple Silicon, ad-hoc signed, icon included), see [mac/README.md](../mac/README.md) |

## Checksums

```
file   CuktechTray-0.4.6-win.exe   (the local build is called CuktechTray.exe, same file)
size   10,882,048 bytes
SHA256 BEF1F61D06DA270871DD4333FA39BEFD04EAE547D0F5DCB656F9F0A835BCF051

file   CuktechTray-0.1.1-mac.zip   (universal build from macos-26-intel CI)
size   999,107 bytes
SHA256 70CABD730E4D9553CF149BCBEE8C59935B456B5CD5635CC9248BE7B0F4DBED38
```

## Requirements

* Windows 10 2004 (build 19041)+, x64; or macOS 13+ (Intel / Apple Silicon)
* No runtime needed on either platform: NativeAOT single file on Windows, a native .app on macOS
* Hardware is still a CUKTECH 10 Ultra charger over BLE; once authenticated everything stays on Bluetooth

## Known

* The exe is **not code-signed**; the first run shows the blue SmartScreen box — "More info → Run anyway"
* Auto-update only accepts release assets starting with `CuktechTray-`, ending in `.exe` and not containing `Setup`, so `-win.exe` matches and `-mac.zip` is never picked by the Windows side
* The macOS bundle is **not notarized** (no Apple Developer account, ad-hoc signature only); right-click → Open the first time — see [mac/README.md](../mac/README.md)
* macOS has its own version line from this release (currently 0.1.1); don't compare it against the Windows 0.4.x numbers
* macOS 0.1.0 pairs fine but never connects — upgrade to 0.1.1; the Windows build has **no changes** in this release and stays 0.4.6
* `--render*` / `--font-probe` / `--update-selftest` are development switches; ignore them in normal use

## Thanks

* [@editwe](https://github.com/editwe): [PR #2](https://github.com/Blank2007/cuktech-ultra-tray/pull/2) found and fixed the root cause
  behind "macOS never connects", and added the regression tests built from real advertisement bytes plus the build-time icon script.
  Nearly all macOS changes in this release are his work
