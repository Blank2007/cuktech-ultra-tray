# 0.4.4 — the connected pill loses its fill, the panel gets 8 px taller

> GitHub Release body draft (English). Chinese original: [release-0.4.4.md](release-0.4.4.md).

In one line: the 已连接 pill in the top bar drops its translucent green fill and keeps only the dot and the label,
and the title row and tab row are pulled apart by 8 px — panel design size 320×396 → **320×404**.

## What changed

### 0.4.4 (this release)

* **No more fill behind the status**: it used to be a 30%-opacity green fill + status dot + the label; now there is no
  fill and no outline — an 8 px dot plus the original 11.5f status label, right-aligned exactly where it was
  (the fill is what had to go, the text stays)
* **Top bar breathing room**: the status sits 6 px → 10 px away from the pin on its right
* **Panel 8 px taller**: the title row and the tab row were only 4 px apart and looked glued together, so the tabs moved
  down 8 px (38 → 46) and the big number, divider, header, four reading rows and footer all follow by 8 —
  320×396 → **320×404**
* Version 0.4.3 → 0.4.4; the three panel screenshots are re-rendered for the new top bar
  (`docs/shot-panel.png`, `docs/shot-control.png`, `docs/shot-mode.png`)

### 0.4.2

* **Black corners gone**: the rounded look was only a rounded-rectangle background painted inside a still-rectangular
  window, and the double buffer is a screen-compatible DDB with no alpha channel, so the four corners kept the cleared
  colour. The window itself is now clipped with `SetWindowRgn` + `CreateRoundRectRgn`, same as the menu and the wizard
  (same integer radius as the painted background, so no half pixel leaks at the anti-aliased edge); `--render` output
  has transparent corners too
* **More spacing**: panel 320x360 → 320x396, reading row pitch 42 → 48, footer 36 lower; mode cards 44 → 50 tall with a
  58 px pitch (was 52); the captions on the control and mode pages sit 8 px lower, back in line with the taller header;
  zebra bands 32 → 36 tall

### 0.4.1

* **The setup wizard is back in the native shell**: new `src/CuktechTrayAot/WizardWindow.cs` (welcome / scan /
  pick-device, 425 lines, hand-drawn at a 460x520 design size). The QR code is fetched from the Mi Home server and drawn
  on the spot; picking a device writes MAC + token into `settings.json` and reconnects without a restart. When
  `settings.json` has no credentials the wizard pops up 0.8 s after start; tray menu → "Reconfigure device…" runs the
  same code
* **Closer to 0.3.3 pixel for pixel**: the caption line on the mode and control pages moved 4 logical pixels each, so all
  12 text rows now line up exactly with 0.3.3
* New developer switch `--render-wizard` (renders the three wizard pages to 575x650 PNGs with no visible screen), and a
  fix for backslashes being eaten in the render output path

## How to upgrade

* **Portable**: overwrite the old `CuktechTray.exe` with the new one and keep your `settings.json` — configuration is
  never reset by an upgrade
* **Installed copy**: tray menu → "Check for updates" downloads, verifies the SHA256 and swaps the exe in place;
  or replace `%LOCALAPPDATA%\Programs\CuktechTray\CuktechTray.exe` yourself
* **Command line equivalents**: `--install [--dir=<path>] [--autostart] [--desktop] [--silent]` installs,
  `--uninstall [--silent]` removes everything (shortcut, HKCU uninstall entry, directory), no UAC at any point
* **Re-pairing a device**: `--setup` opens the scan wizard directly (same path as tray menu → "Reconfigure device…")

## Files

| File | Size | Contents |
| --- | --- | --- |
| `CuktechTray-0.4.4-win-x64.zip` | **5,034,205 bytes (4.80 MiB)** | `CuktechTray.exe` + `README.md` + `README.en.md` + `LICENSE` + `CHANGELOG.md`, all at the zip root, no wrapper folder |
| `CuktechTray.exe` | **10,874,880 bytes (10.37 MiB)** | Portable build and installer in one; 1,241,088 bytes more than 0.4.0's 9,633,792 (the wizard has been built in since 0.4.1) |
| `CuktechTray-0.4.4.exe` | **10,874,880 bytes (same file)** | Byte-identical copy under the name "Check for updates" looks for — upload this one as the release asset |

## Checksums

```
file   CuktechTray.exe
size   10874880 bytes
SHA256 D22967B8FB9441A4BF79A5416AD2EE95833EA155E97D10A0FEE228D932E1F3EA

file   CuktechTray-0.4.4.exe            (same bytes, different name)
size   10874880 bytes
SHA256 D22967B8FB9441A4BF79A5416AD2EE95833EA155E97D10A0FEE228D932E1F3EA

file   CuktechTray-0.4.4-win-x64.zip
size   5034205 bytes
SHA256 C7AF0F70621928FDD321EDD9DEABB371798A4433D3AA8F82B7BC39B86142B60E
```

`SHA256SUMS.txt` next to them carries those three lines (uppercase SHA256, two spaces, filename, LF endings), so
`sha256sum -c SHA256SUMS.txt` works wherever you have git for Windows or WSL; on plain Windows use
`Get-FileHash .\CuktechTray-0.4.4-win-x64.zip -Algorithm SHA256`.

## Requirements

* Windows 10 2004 (build 19041) or later, x64
* No .NET runtime needed: single-file NativeAOT, nothing to install on the target machine
* Hardware is still a CUKTECH 10 Ultra charger over Bluetooth LE; once authenticated, runtime traffic is BLE only

## Known issues

* The exe is **not code-signed**, so the first launch may show the blue "Windows protected your PC" prompt —
  click "More info → Run anyway". It does not ask again
* Auto-update only picks a release asset named like `CuktechTray-<version>.exe` (anything containing `Setup` is
  skipped), so `dist\CuktechTray-0.4.4.exe` is a byte-identical copy saved under that name — upload it as the
  release asset, not `CuktechTray.exe`
* `--render`, `--font-probe`, `--alpha-probe`, `--update-selftest` are developer self-check switches; ignore them in
  normal use. Never run `--apply-update` by hand — it replaces the exe you point it at with the payload
