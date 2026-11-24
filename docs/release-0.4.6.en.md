# 0.4.6 — the English sentence after a QR scan was our bug

> Draft release body (English). Chinese version: [release-0.4.6.md](release-0.4.6.md).

In one line: a bug only reachable through real use — **right after a successful QR pairing the wizard showed the English sentence**
`Reflection-based serialization has been disabled for this app`. This release also adds a privacy note to the wizard and the README,
and macOS starts its own version line.

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

### macOS: 0.1.0 (own version line from here on)

macOS no longer shares the Windows version number and starts at **0.1.0**: `CuktechTray-0.1.0-mac.zip`.
The two platforms move at different speeds, and one shared number only mixes unrelated changelog entries.
macOS also got the privacy note in this release (a paragraph on the welcome screen plus a full-note dialog); see [mac/README.md](../mac/README.md).

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
| `CuktechTray-0.1.0-mac.zip` | **911,811 bytes (890 KiB)** | macOS 13+: unpacks to `CuktechTray.app` (universal, Intel / Apple Silicon, ad-hoc signed), see [mac/README.md](../mac/README.md) |

## Checksums

```
file   CuktechTray-0.4.6-win.exe   (the local build is called CuktechTray.exe, same file)
size   10,882,048 bytes
SHA256 BEF1F61D06DA270871DD4333FA39BEFD04EAE547D0F5DCB656F9F0A835BCF051

file   CuktechTray-0.1.0-mac.zip   (universal build from macos-26-intel CI)
size   911,811 bytes
SHA256 42621A1F1D1E3FF6BE18542F4C15B5E636400119906272E3791AFEE5EDCECA8F
```

## Requirements

* Windows 10 2004 (build 19041)+, x64; or macOS 13+ (Intel / Apple Silicon)
* No runtime needed on either platform: NativeAOT single file on Windows, a native .app on macOS
* Hardware is still a CUKTECH 10 Ultra charger over BLE; once authenticated everything stays on Bluetooth

## Known

* The exe is **not code-signed**; the first run shows the blue SmartScreen box — "More info → Run anyway"
* Auto-update only accepts release assets starting with `CuktechTray-`, ending in `.exe` and not containing `Setup`, so `-win.exe` matches and `-mac.zip` is never picked by the Windows side
* The macOS bundle is **not notarized** (no Apple Developer account, ad-hoc signature only); right-click → Open the first time — see [mac/README.md](../mac/README.md)
* macOS has its own version line from this release (0.1.0); don't compare it against the Windows 0.4.x numbers
* `--render*` / `--font-probe` / `--update-selftest` are development switches; ignore them in normal use
