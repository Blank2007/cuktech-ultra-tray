# Changelog

This project first ran on just one of my own machines and was later put on GitHub, so
what follows is organized by "what was done" rather than by release; the version numbers just
kept climbing so they line up with the file versions inside the installers.

## macOS 0.1.6 — 2026-11-25

Capture results now report average/peak voltage and current, so they can be checked against a USB meter.

**How**: voltage and current use the *same* piecewise integration as the energy total (two-point average
on the same rail, pre-step value on the second a rail change happens), divided by the time actually
delivering power. That keeps "average volts × average amps" consistent with average watts instead of
producing three numbers that disagree, and the 30 idle seconds at the end don't dilute the averages.

The result window shows a two-column average/max table for voltage and current; the copied summary
gained two lines.

Two related counting bugs fixed along the way:

- **Unplugging counted as a rail change**: when capture ended, voltage dropped 9.1V → 0V, which blew
  past the 0.5V threshold, so a session that never changed rails still reported "1 rail change".
  Now it only counts when *both* samples are actually delivering power.
- **Idle voltage leaked into the voltage range**: the 5V standby of an empty port and the 0V after
  unplugging were counted in min/max voltage. Now only loaded samples count.

**Measured against a meter** (C1 charging a phone, 44 min 53 s, 2689 samples): this app 13.01 Wh /
17.6 W average / 21.8 W peak; a 维简 K2 meter on the same session logged 12.48 Wh / 16.9 W / 20.85 W,
both at 1.93 A average current. About 4% apart.

## macOS 0.1.5 — 2026-11-25

Fixes "disconnect once and it can never reconnect". A connection-layer bug present since 0.1.0;
nothing to do with the charger's firmware.

**Symptom**: after the Mac sleeps and wakes (or the Bluetooth stack restarts on its own) the app
drops the link and then reports "no device found" forever; pressing "reconnect" does not help.
Meanwhile System Settings → Bluetooth shows the charger as **connected**.

**Two overlapping causes**:

- Every reconnect created a new `CBMibleLink` (and therefore a new `CBCentralManager`). The old one
  only got `cancelPeripheralConnection`, never real cleanup. The log shows 2–3 centrals reporting
  state at once (two `central state=` lines in the same second, 2405 times). Those orphaned centrals
  keep holding the peripheral.
- A BLE peripheral stops advertising once connected. With a stale connection holding it, the new
  central can never see an advertisement, so it just retries. Quitting the app made the "connected"
  entry vanish instantly and relaunching reconnected in under a second — which pins the cause.

**Changes**:

- One `CBCentralManager` for the whole process, reused across reconnects; shutdown/reconnect go
  through the new `shutdown()` (stop scanning + cancel), so nothing piles up.
- Before connecting, `dropStaleConnection()` tears down any half-finished link from the previous
  attempt and waits for it to really disconnect, so the device starts advertising again.
- Before scanning we check `retrieveConnectedPeripherals`: if the device is already attached at the
  system level (stale link, wake from sleep), we **adopt that connection** instead of waiting for a
  broadcast; checked once more as a fallback after a scan timeout.
- The peripheral UUID is saved to `config.json` as `peripheralId` on first successful connect, so
  adoption knows which device to take over (it will not grab one at random when several FE95 devices
  are connected). The field is optional on purpose: synthesised decoding only uses `decodeIfPresent`
  for optionals, and a `String = ""` default would make older configs fail to decode — and that
  failure path falls back to defaults, wiping the stored mac/token.

**Verified**: while the app holds the link and the system reports "connected", a second instance logs
"device is already connected in the system, adopting it (not waiting for a broadcast)" and finds the
services — exactly the state that used to be unreachable.

## macOS 0.1.4 — 2026-11-25

Fixes one misleading bit of UI. No new features.

- The "charging mode" header used to carry the **polling interval** switch ("every 1 s") on its right.
  Two unrelated things on one line read as "the charging mode switches every second", as if the app
  were changing gears for you. The polling interval is the app's own refresh rate, nothing to do with
  the charger, so it moved down to the footer next to "重新连接" and now reads "读数 1 秒"; the header
  now says "点一下切换", making it clear those four tiles are buttons, not live indicators.
- The current mode also gets a check mark next to the green border, so it reads as **the charger's
  current gear** rather than a button you just pressed.
- Also verified the mode does not drift on its own: two screenshots 5 s apart are pixel-identical on
  firmware 2.1.2_0079. If the border ever moves by itself, the charger really did change modes.

## macOS 0.1.3 — 2026-11-25

The parts of the protocol that are actually controllable — beyond power readings — now have a UI,
and the capture sample count finally adds up. **Windows is untouched and stays on 0.4.6.**

- **Settings window** (the "设置" button at the bottom of the panel, or "设备设置…" in the right-click
  menu). These used to be reachable only through the Mi Home app; now you change them next to the menu
  bar and they're written straight into the charger, so Mi Home sees them too:
  - **Screen**: screen timeout (1/5/10/30 min, always-on), turn screen off when idle, orientation lock
  - **Power**: USB-A low-current mode; per-port **fast-charge protocol switches** (C1/C2 have
    PD / PPS / UFCS, C3/A have UFCS / SCP) — turning one off tells the charger not to negotiate it
  - **Timers**: per-port auto-cutoff at 15/30 min, 1/2/4 h (0 = off)
  - Each port row also shows **what it actually negotiated right now** (PIID 17/18, e.g. "PD 70W"),
    which is what explains the switches above it
- Settings are read **one GET at a time, and anything that doesn't answer stays nil**: firmwares differ
  in which properties they expose, so the row says "device didn't answer" and the control is disabled.
  A failed read is **never** treated as "off" — that would show a fake state and write an unknown value
  on the first click
- The protocol mask (PIID 21) is read-modify-write like the port switches: read a baseline first, refuse
  to write if the read fails, and verify by reading back instead of trusting the ACK
- **Capture samples now match the clock**: 30 seconds used to yield 25 samples because each loop was
  "sample, then sleep 1s" while a single GET round-trip costs 0.1–0.3s, so a cycle took ~1.2s. Samples
  are now scheduled on absolute times (the n-th sample lands n seconds after the start), which absorbs
  the command latency; the stop check moved ahead of sampling so 30 seconds is exactly 30 samples
- The countdown menu follows the dark appearance instead of dropping a white sheet on a dark window
- Unit tests now cover the settings bit offsets and unpacking (one bit off turns off somebody else's
  protocol), plus the reversed screen-timeout values (1 = 5 min, 5 = 1 min)

## macOS 0.1.2 — 2026-11-25

A handful of small "this feels wrong" fixes on the macOS side, all in one go. **Windows is untouched
in this release** and stays on 0.4.6.

- **The panel can be pinned now**: there is a pin button in the top-right corner. Unpinned, the panel
  **closes as soon as you click anywhere outside it**. Pinned, it stays put when you switch to another
  app and works as a small always-on window
- **"Close on outside click" needed a second safety net**: `.transient` fires when the popover window
  resigns key, but a menu-bar app is usually **not active** while its panel is up (keyboard focus is
  still in whatever app you were using), so the click outside never produces a resignKey and the panel
  just sits there. It now also watches mouse-down events (one global and one local monitor, covering
  "clicked another app" and "clicked another window of ours") and dismisses on any click outside; the
  status item's own rect is excluded and left to the toggle, so a "click to close" doesn't turn into
  close-then-reopen flicker
- **Clicking the status item again closes the panel**: with the panel open, one click on the icon now
  dismisses it instead of reopening it, so there's no flicker
- The panel is more compact: the big gap that used to sit between the charging-mode grid and the
  "Reconnect / Quit" row is gone, dropping the height from 486 to 424
- The panel footer gained "**About**" (Reconnect on the left; About / Quit on the right), and the
  right-click menu entry was shortened to just "About"
- A **polling interval** control now sits at the right end of the "Charging mode" row
  (1/2/3/5/10 s). Previously the only way to change `pollInterval` was hand-editing `config.json`;
  it now applies immediately and is written back to the config
- Fixed a "looks broken" detail: when the panel pops up while the app is not yet active, the system
  switch renders in the **inactive gray-green**, only turning blue after you click inside the panel.
  The switch is now drawn by the app, so its color no longer depends on window activation
- Fixed an **off-by-one in the port switches**: the panel hands over 1–4 (C1/C2/C3/A) while the
  session API is 0-based, and the value used to be passed straight through — clicking C1 disabled
  C2, and clicking A failed with "port index must be 0–3"
- Polling and the "switch / mode" writes now **queue on the command channel**: they used to
  interleave, the device would swallow the later commands, and you got "written but read-back
  timed out" or "no port answered" — occasionally dropping the link too
- Turning off "show watts in the menu bar" now shows a **bolt icon** instead of the character 「瓦」
  (SF Symbol `bolt.fill` + `isTemplate`, so it inverts with light/dark menu bars). Hovering still shows the
  live reading
- The right-click menu gained "**About CUKTECH Power Meter…**": version, a link to the GitHub repo, and
  an **update check**. The window itself is just the version block plus two buttons — the result row
  only **unfolds after you press "Check for Updates"** (top edge stays put, the window grows downward),
  with an inline "Download" when a newer build exists. The old explanatory paragraphs and the "OK"
  button are gone; the red traffic light closes it
- The update check **only touches the network when you click "Check for Updates"**: opening the About
  window sends no requests at all. The unauthenticated GitHub API allows 60 requests per hour per IP, so an
  automatic check burns that quota just for glancing at the version number — and hits the limit easily
- It compares the release asset name `CuktechTray-<version>-mac.zip` and **ignores the tag** — tags follow
  the Windows version (v0.4.x), so comparing tags would report "new version available" on every check.
  On a 403 rate limit it converts `x-ratelimit-reset` into local time and says when to retry
- **Per-port capture**: each port row has a small waveform button. One click starts reading that
  port once a second; the header shows elapsed time, Wh so far and average watts live, and the
  button turns red to stop
- Capture **ends** after 30 s with no output from that port, 30 s with no data at all, a dropped
  link, or a manual stop. A small window then shows Wh, average and peak power, active duration,
  voltage range with rail-change count, and sample count. The numbers live **in memory only** —
  closing the window discards them, nothing is written to disk or to the log
- Energy is **integrated per rail**: trapezoidal while the voltage stays put, and on a rail change
  the old power is used for that last second. Averaging 5V→20V as 12.5V would overstate that
  second by more than double; the conservative choice under-reports slightly but never inflates
- Normal polling steps aside during a capture: two command streams on one channel make the device
  swallow later commands, which is what used to produce "written but read-back timed out"
- Version 0.1.1 → 0.1.2

## macOS 0.1.1 — 2026-11-25

A macOS-only release: **0.1.0 could not connect to the charger at all on real hardware**, and the bug was in the
advertisement parser. Thanks to [@editwe](https://github.com/editwe) for PR
[#2](https://github.com/Blank2007/cuktech-ultra-tray/pull/2) — correct root cause plus a regression test built from
bytes captured off a real device. **Windows is untouched in this release** and stays on 0.4.6.

- **Fixed the connection failure**: `MiBeaconAdvertisement.parse` required the first two bytes to be `95 FE`
  (the service-UUID header), but macOS hands over the **bare payload** through
  `CBAdvertisementDataServiceDataKey` (`10 59 | 0E 66 | 00 | 30 8B 31 73 CD 3C` = frame control / productId /
  counter / little-endian MAC), so the guard always failed and the scan result stayed empty — the menu bar showed
  `—` forever while the log kept saying "no advertisement for MAC …". Both layouts are accepted now; the MAC bit in
  the frame control is still what decides whether a MAC is present
- Bluetooth readiness wait on first launch **10 s → 30 s**: state stays `unknown` until the permission dialog is
  dismissed, and 10 s was tight enough that users were declared "can't connect" right as the dialog appeared
- **Left click on the menu bar opens the panel, right click shows the menu** (the menu used to be attached to the
  status item, so left click could only ever show the menu — the opposite of what the README says)
- The app icon is now **drawn at build time** (`mac/scripts/make-icon.swift` → `iconutil` → `.icns`); no binary in
  git. The gradient fills solid colour row by row instead of using NSGradient/CGGradient, whose dithering bloated the
  icns from 132 KB to 1.2 MB
- New `Tests/CuktechBleTests` (advertisement-parsing regressions from real-device bytes: with/without the UUID
  header, MAC bit clear, truncated); **the macOS CI job now runs `swift test` too** — previously only the Linux job
  ran tests, and this suite only compiles on macOS, so it would never have executed
- mac/README gained a "troubleshooting" table (nine symptoms → cause)

## 0.4.6 — 2026-11-25

Fixes a bug a real user hit: **right after a successful QR pairing the wizard showed the English sentence**
`Reflection-based serialization has been disabled for this app`. That is a NativeAOT trap — the SDK turns reflection-based
JSON serialization off for AOT publishes, and the pairing path used it in two places. This version also adds a privacy note
to the wizard, and macOS gets its own version number.

- Fixed the pairing failure: the `/v2/home/home_device_list` body is built with `MiCrypto.JsonToken()`, which used
  `JsonSerializer.Serialize(raw)` internally; under AOT that call throws `NotSupportedException: Reflection-based serialization has been disabled for this app`.
  Replaced with a hand-written escaper `MiCrypto.JsonQuote()` (quotes / backslash / control characters / `\u00XX`, CJK kept as is);
  the `JsonSerializer.Serialize(did)` in the `blt_get_beaconkey` path now uses `MiCrypto.JsonQuote(did)` too
- `--selftest` gained AOT-specific checks: seven JSON-escaping assertions plus a settings source-generation round-trip, one `[aot] … ok` line each;
  it also prints `[aot] json-reflection-enabled=False`, so "reflection serialization really is off under AOT" ends up in the log
- The bug survived a whole release because local testing always used a saved `settings.json` and skipped the login path —
  the pairing path had never been exercised on a real AOT build. The self-test now covers it
- The wizard's first screen has a privacy-note line (opens a full dialog): scanning only exchanges this charger's Mac + token
  once with Xiaomi Cloud, credentials stay on this machine, normal operation reads power over Bluetooth with no network,
  and which two files to delete to clean up
- The legacy standalone installer `CuktechTray-Setup.exe` was rebuilt once (68 MB, embedding this very AOT binary) purely to
  exercise the in-place-upgrade path; it is **not uploaded with the release** — install/uninstall still happen inside the main exe
- Version 0.4.5 → 0.4.6

### macOS (own versioning from 0.1.0)

macOS no longer shares the Windows version number; it starts at **0.1.0**: `CuktechTray-0.1.0-mac.zip`.
The two platforms move at different speeds, and tying them to one number only mixes unrelated entries in the changelog.
macOS also got the privacy note in this version (a paragraph on the welcome screen plus a "see the full note" dialog).

## 0.4.5 — 2026-11-23

The UI and the feature set are identical to 0.4.4. What changed here is the evidence: the promo assets stop reusing older captures, the tray icon is rendered by the program itself, and the menu is captured from the real thing.

- New `--render-icons`: the drawing inside `WattIcon` was split out as `public static Bitmap CreateBitmap(int size, Color accent)`, so the shell can render both icons (green = connected, grey = disconnected) straight to a 608×256 transparent PNG at `docs/shot-tray-icons.png` instead of relying on a 125%-scaled screenshot
- `--menu-demo` now writes "已连接 · 总 6.0 W" before showing the menu, so the captured header no longer says "not connected / connecting…"
- README gained two images: `docs/shot-menu-aot.png` under "Tray menu" (real capture of the self-drawn menu) and `docs/shot-tray-icons.png` under "Tray icon" (both languages)
- New `--render-taskbar`: the taskbar digits are rendered to a transparent PNG with the same font (Segoe UI Bold 11 pt × scale) and the same colour rules (`WattStyle.Total`) the runtime uses, then composited over a real taskbar capture.
  The old README taskbar image was a 0.2.2 demo capture and even had the wrong colour — 6.0 W is light blue under the current buckets, the old picture drew it green
- README "Power on the taskbar" got a new image and new wording: `docs/shot-taskbar.png` plus a colour-bucket table (grey / light blue / green / amber / red) explaining why the 6.0W in the picture is light blue (both languages)
- The two stale promo assets (the 0.2/0.3 "five icon styles" matrix and the 0.3.3 WinForms menu capture) were replaced with the 0.4.4 originals (the video project is not part of this repo)
- The download lines used to hard-code `CuktechTray-0.4.0.exe` / 9.19 MB; they now say `CuktechTray-<version>.exe` / 10.4 MB so they stop going stale on every release
- Version 0.4.4 → 0.4.5; **no functional or visual change** — upgrading from 0.4.4 looks exactly the same

## 0.4.4 — 2026-11-23

The connected pill in the top bar drops its fill; the dot and the label stay. The top bar also gets room to breathe.

- It used to be a 30%-opacity green fill + status dot + 已连接. What was actually wanted was no tinted fill — the label was
  meant to stay (an intermediate build dropped the label as well; the answer was "I did not tell you to delete the text")
- No fill, no outline now: an 8 px dot plus the original 11.5f status label, right-aligned exactly where it was
- Breathing room: the status sits 6 px → 10 px away from the pin
- The title row and the tab row were only 4 px apart and looked glued together, so the tabs moved down 8 px (38 → 46) and
  the big number, divider, header, four reading rows and footer all follow by 8 — panel 320×396 → **320×404**
- Version 0.4.3 → 0.4.4; the three panel screenshots are re-rendered

## 0.4.2 — 2026-11-23

Two complaints: black squares in the corners, and everything felt cramped. Both fixed here.

### The black corners

- Cause: the rounded look was only a rounded-rectangle background painted inside a still-rectangular window.
  The double buffer is a screen-compatible DDB with no alpha channel, so whatever the paint did not cover stayed at the
  cleared colour — visually, "four black corners padding a rounded panel"
- The window itself is now clipped to a rounded region with `SetWindowRgn` + `CreateRoundRectRgn`, same as the menu and the wizard
  (the region radius uses exactly the same integer as the painted background, so the anti-aliased edge cannot leak a half pixel)
- `--render` output now has transparent corners too, so the diagnostic PNGs no longer carry a dark square into docs or video assets

### Spacing

- Panel design size 320x360 → **320x396**, row pitch 42 → 48, footer moved down 36
- Mode cards 44 → 50 tall with a 58 px pitch (was 52); card title and hint each shifted down 3 px
- The one-line captions on the control and mode pages sit 8 px lower, back in line with the taller header
- Zebra band 32 → 36 tall, following the new row pitch

### Misc

- Version 0.4.1 → 0.4.2; `docs/shot-panel.png` and `docs/shot-control.png` re-rendered for the new layout

## 0.4.1 — 2026-11-23

Moving to the native shell in 0.4.0 left the setup wizard behind in the old one, so first-time use still meant
downloading 0.3.3 to pair. This release brings it back, and aligns the three panel pages pixel-for-pixel with 0.3.3.

### Wizard

- New `src/CuktechTrayAot/WizardWindow.cs`: welcome / scan / pick-device pages, 425 lines, hand-drawn at a 460x520 design size
- The QR code is fetched from the Mi Home server and drawn on the spot; picking a device writes MAC + token into `settings.json` and reconnects without restarting
- When `settings.json` has no credentials the wizard pops up 0.8 s after start; tray menu → "Reconfigure device…" runs the same code
- `--setup` on the command line opens it directly (`--wizard-demo` is the same thing, used for self-checks)
- Verified on real hardware: 460x520 design lands as 575x650 physical at 125% scaling, mean per-pixel difference 0.89 against the offscreen render;
  after clicking "Scan with the Mi Home app" the QR area really showed a code (15% dark modules), so the Mi Home cloud path works in the AOT build too

### Interface

- The subtitle on the mode and control pages moved 4 logical pixels each, so all 12 text rows now line up exactly with 0.3.3
- `docs/aot-demo-*.png` redrawn: they used to be rendered at 2.5x, whose font quantization does not match the 1.25x of real hardware and looked like a discrepancy;
  now they are rendered at 1.25x and upscaled by 2, sharing the 0.3.3 screenshot baseline
- Added `docs/shot-wizard.png` / `docs/shot-wizard-scan.png`: the wizard's welcome and scan pages captured on real hardware (the latter with a live QR code)

### Engineering

- `--render-wizard`: renders the three wizard pages to 575x650 PNGs under `docs/` so layout can be checked without a visible screen
- Fixed backslashes being eaten in the render output path (it used to surface only as `GDI+ A generic error occurred`)

## 0.4.0 — 2026-11-23

Nothing was dropped; two things happened: the "72 MB self-contained exe" became a "9.19 MB native exe",
and both the installer and self-update were folded into the main program — **one exe is the portable build,
the installer, and its own updater**.

### Size

- The portable build went from 72,715,094 bytes (69.3 MiB) down to **9,633,792 bytes (9.19 MiB)** — 1/7.55 of the old size — and it still needs no .NET runtime on the target machine.
- The installer went from 132,813,041 bytes (126.7 MiB) to "just those 9.19 MiB": the separate `CuktechTray-Setup.exe` is no longer needed.
- Once installed, later upgrades no longer download an installer: the program checks GitHub, downloads, verifies and replaces itself — 9 MB and change, end to end.
- Why: most of that 72 MB was the whole .NET runtime plus the JIT compiler, not the program itself. NativeAOT compiles everything to win-x64 native code up front, so none of it ships.
- The pdb is still tens of megabytes, but it is not part of the release.

### Implementation

- New `src/CuktechTrayAot/`: 13 files, 4,967 lines — a bare Win32 + GDI+ shell. Window classes, the message loop, the tray icon, the three-tab panel, the taskbar digits and the rounded dark context menu are all drawn by hand; WinForms is gone.
- The business layer was not rewritten: `Models` / `Settings` / `DeviceClient` / `SelfTest` / `UpdateCheck` / `WattStyle` / `LogSink`, plus `src/CuktechMible/` and `src/probe-cs/WinRtMibleLink.cs`, are shared through `<Compile Include>` in the csproj.
- Behaviour matches 0.3.3: tray wattmeter, four-port readings, the control and mode tabs, taskbar digits, the context menu (rounded / translucent / green checks), run-at-startup, update check and `--selftest` are all there. `--selftest` passes on real hardware (exit code 0, 3.7 s authentication, identical port readings).

### UI

- The tray icon no longer draws wattage — it is a status badge now: green when connected, gray when not.
  The power figure lives on the taskbar strip where it is always visible; stacking a number on the icon as well
  was a second place to read the same thing, and it forced a repaint per frame. The hover tooltip still carries
  "total X W" plus per-port detail.
- Icon styles collapsed from five to one (`WattIcon.cs` 247 → 107 lines), and the "tray icon style" submenu is gone.
- Fixed the pin button in the panel's top-right corner being clickable but invisible (font name was misspelled
  `Microsoft MDL2 Assets`; GDI silently fell back to a font without the glyphs).
- The pin button lines up with the "connected" chip; the "current" chip on
  the mode page is more opaque; the hint line on all three pages sits on the same baseline.
- Re-checked every page pixel by pixel against the 0.3.3 on-screen captures: bold weight moved from 600 to 700
  (600 actually selected Segoe UI Semibold), the double-buffer bitmap dropped its alpha channel so ClearType works
  again (it used to degrade to grayscale and look washed out), and the pin / "switched off / idle" grays were
  reverted to their 0.3.3 values.
- A second pass fixed three real mismatches: font sizes round up now (GDI+ turned 13×1.25 into 17 px; rounding down
  to 16 shrank the title and tab captions by a notch), translucent fills no longer go through gamma-corrected
  blending (the tab chip background was one step brighter than 0.3.3 — `CompositingQuality` is back to default),
  and coordinate scaling truncates again like `(int)(v * _s)` (rounding accumulated a 2 px offset across the tab
  spacing); the table header uses `Microsoft YaHei UI` again (a linked fallback font has a different ascent and
  lifted the header by 2 px) and the pin button is back to 20×20 with no outline. Difference against the 0.3.3
  on-screen capture of the read page is down to 772 pixels (0.1% of the frame).
- `1` / `2` / `3` jump straight to the read / control / mode page (previously only `Tab` cycled and `Esc` closed).

### Installation

- The program now carries `Installer.cs`: the tray menu's "Install on this PC…" copies the running exe to
  `%LOCALAPPDATA%\Programs\CuktechTray`, creates a Start-menu shortcut and writes the HKCU uninstall entry plus an
  optional run-at-startup value. Afterwards the menu reads "Uninstall from this PC…".
- Current user only (HKCU + user profile) — **no administrator rights, no UAC prompt**. Installs over an existing 0.3.x directory.
- `settings.json` travels along, so there is no re-pairing after installing.
- Uninstall validates the target first (it must contain `CuktechTray.exe` and must not be Windows / Program Files /
  the user profile root / the desktop), then removes shortcuts, the registry entry and the directory. When the program
  itself lives in that directory, a temporary copy plus `MOVEFILE_DELAY_UNTIL_REBOOT` removes the last file.
- Shortcuts go through `CoCreateInstance` and a raw `IShellLinkW` / `IPersistFile` vtable instead of `[ComImport]`
  (built-in COM interop is shaky under NativeAOT).
- A shortcut that cannot be written is a warning, not a failed install, so antivirus or a locked-down folder does not veto everything.
- CLI equivalents: `--install [--dir=<path>] [--autostart] [--desktop] [--silent]`, `--uninstall [--silent]`.

### Updates

"Check for updates" in the context menu is no longer "here is a new version, go download it" — it installs it:

- After finding a new release it picks the `CuktechTray-<version>.exe` asset (skipping `-Setup-`) and downloads it to
  `CuktechTray.exe.download` next to the exe (same volume, so the final rename is atomic).
- **SHA256 is computed while downloading**; both the byte count and the digest published by the GitHub API must match
  before anything is replaced. A mismatch deletes the `.part` file and leaves the current exe untouched.
- **A running exe cannot be replaced** (the image is mapped; even renaming is refused), so the program copies itself to
  `%TEMP%\CuktechTray-update-<pid>.exe` and lets that copy wait for the main process to exit, then:
  back up to `CuktechTray.exe.old` → overwrite → start the new build → drop the backup → schedule itself for deletion.
- If the overwrite fails, the `.old` file is restored. The helper retries up to 60 times, 500 ms apart, which is enough
  time for antivirus to release a handle.
- The main process drops Bluetooth before exiting and leaves through the normal `ExitApp` path, so the new build
  reconnects to the charger on its own (about 4 seconds).
- When in-place updating is impossible it falls back to the release page — and decides **before downloading**: no usable
  asset, no size published, or a non-writable exe directory (e.g. `Program Files`).
- Measured: the 72,715,094-byte 0.3.3 asset downloaded from GitHub in **7.35 s** with a matching SHA256; a deliberately
  wrong digest produced "SHA256 mismatch" and removed the partial file.
- Automatic updates only work between releases that ship an identically named asset, so **0.4.0 has to be installed
  manually once**; from then on it can update itself.

### Tooling

- `--shortcut-test <lnk path>`: creates a single .lnk and reads it back, touching no system folder.
- `build.ps1 -Aot`: one command for the AOT single file; the script wires up lld-link and the xwin library paths itself (no Visual Studio needed).
- `build.ps1 -Aot -Publish`: also copies the result over `dist\CuktechTray.exe`.
- `run.ps1 -Aot [-Panel]`: launch the AOT build (optionally opening the panel).
- `--render <dir>`: creates no windows at all and paints all three pages straight into 400x450 PNGs, so layout can be regression-tested even on a locked desktop.
- `--render --render-demo`: same, but fed a fake "connected" snapshot, so the operable colour scheme and switch states can be checked without the device.
- `--update-selftest <dir>`: file-level check of "back up → overwrite → a missing payload must not clobber the target"; no network, no child processes.
- `--update-download-test <dir> --url= --sha= --size=`: one real download with SHA256 verification, used to test the proxy/direct fallback.
- `--apply-update=<target> --payload=<new file> --pid=<old process>`: the update helper itself (normally started by the program; do not call by hand).

### Known

- Restore warns `NU1903`: `Microsoft.DotNet.ILCompiler` 8.0.32 has a known high-severity advisory `GHSA-p3jp-9v83-76w9`; it will clear with a later .NET 8 SDK.
- `LNK4099` during linking (missing MSVC-internal pdbs) is noise.
- The `setup/` WinForms wizard is legacy now (62.9 MB host + 63.8 MB payload) and will not be maintained;
  installing and uninstalling are done by the program itself. For 0.4.6 it was rebuilt once (68 MB, embedding that version's main
  exe) just to verify the in-place upgrade, but it **never ships in a release**, and that release route should not come back.

## 0.3.3 — 2026-11-22

The context menu got a new look, and "is this switch on or not" turned into something you can just see.

### Tray menu

- **Rounded corners + acrylic translucency**: the context menu was repainted with DWM's rounded-corner attribute plus the acrylic variant of `SetWindowCompositionAttribute`,
  the base colour was set to the menu's own dark gray (30,34,41) at F2 alpha, and the item spacing was switched to Win11's proportions (5 px top and bottom, 24 px on the left reserved for the check).
  It comes close to the semi-transparent menu in TranslucentTB; Win11 24H2 only gives popup windows a translucent tint rather than real blur, so it is "see-through", not "frosted glass".
- **A green check on the left of items that are on**: toggle items such as `Pin panel to desktop` / `Show power on taskbar` / `Start with Windows` get a green check while they are on and stay blank while they are off,
  so you no longer have to click in to find out the current state. The check is drawn by the program itself (with `ShowImageMargin = false`, ToolStrip's built-in check margin no longer takes effect),
  painted on the menu background layer and repainted once more when the mouse passes over.
- The submenus (Charge mode / Icon style) get the same skin too, check marks and rounded corners included.

### Implementation

- New `MenuEffect.cs`: `Apply(ToolStripDropDown)` puts the rounded corners and the acrylic on the menu window and registers it in the `IsBlurred` set; the renderer uses that to decide
  "it is translucent already, don't lay an opaque background underneath"
- `Skin()` in `UiTheme.cs` now does four things: set the renderer, set the font, assign Padding by item type, and attach the effect to the menu
- `UiTheme.Skin()` must be called after every menu item has been added — it walks the items to set the spacing, and calling it too early just gets an empty list

## 0.3.2 — 2026-11-22

The "charge mode" from the Mi Home app is now built in, in the panel and in the tray menu.

### Added

- **Charge mode (scene mode)**: the panel gained a third tab, "Mode", with four profiles — AI / Digital ecosystem / Single port / Balanced
  (protocol SIID 2 / PIID 5, values 1–4). Clicking a card applies it immediately; after the write one GET reads the value back and verifies it, and the bottom line reports
  "switched to X (device reads N)"; if the device and the request disagree it raises an error instead of pretending it worked.
  All four round-tripped on real hardware (it was sitting on Single port 3 → writing 1 / 3 / 4 / 2 each read back the same value), and 3 was written back afterwards.
- Switching to the "Mode" tab reads the real value from the device first, no cached value pretending.
- The tray right-click menu gained a "Charge mode" submenu, four check-marked items that follow the current profile; the whole entry is greyed out when the charger is not connected.

### Fixed

- The GET reply for scene mode is a single byte (`op=0x03 type=16 raw=03`), and going through `MiotMessage.UIntValue` reads it as 0;
  you must read `ValueBytes[0]`. Copying how the four-port switches were written silently reads "don't know" — I walked into this once.

## 0.3.1 — 2026-11-20

Two things: the "it is charging, yet it never connects" trap is plugged, and update checking was added.

### Fixed

- **The real reason it could not connect to the charger**: connecting called `BluetoothLEDevice.FromBluetoothAddressAsync` directly to get the device object,
  but Windows' BLE device cache expires — restarting the Bluetooth service, swapping adapters, leaving it a day, or powering the device off for a while all clear it.
  With an empty cache that call returns `null` every single time, **whether or not the charger is anywhere near**, so the log filled with
  `FromBluetoothAddressAsync returned null` every 8 seconds and could keep that up all day (my own log had it for 22 hours, 8683 times).
  What it does now: if the device cannot be fetched the first time, it runs one round of active advertising scanning (at most 8 s) to get it into the system cache, then fetches again, retrying at most 5 times.
  Measured in the "null for 22 hours" state, a single scan and it authenticated immediately.
- The error when no device can be scanned was reworded as well: "cannot find this device: make sure the charger is powered, within Bluetooth range, and that no phone is holding its connection",
  which beats the old exception that carried nothing but an English API name.

### Added

- **Check for updates**: tray context menu → "Check for updates…", which asks GitHub Releases once; if there is a newer version it pops up the version number and asks whether to open the download page.
  It only goes online when you click it — startup, connecting to the charger and polling readings never touch the network.
  - .NET 8's HttpClient does not read the system proxy settings (a direct connection to api.github.com from mainland China mostly gets no JSON back), so the program reads
    `HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings` from the registry itself, tries once through the proxy and once direct if that fails.
  - It also works from the command line: `CuktechTray.exe --check-update`, which prints the current version, the latest version and which route it took.

## 0.3.0 — 2026-11-20

Readings and controls are one window now, and a drawing bug that made the switch invisible at all is fixed.

### Power Panel

- **Two separate windows merged into one**: previously "read the power" was the panel and "flip the switches" was a separate control-page window, so switching between them took two clicks;
  the panel now has a 24 px tab strip under its title — **Read / Control** — and switching tabs does not change the window, does not move it and does not re-authenticate
- Panel height went from 328 to 360 px (the extra 32 px is exactly the tab strip); the width stays at 320 px
- Every coordinate on the Read tab moved down 32 px; row heights, font sizes, color bars and zebra striping are all unchanged
- Switching to the Control tab **does not break the panel's collapse-on-blur / pin / `Esc` behavior** — both tabs behave exactly the same

### Control Page (now the panel's second tab)

- Each row gives up 48×28 px on its right for a **pill switch**: on = green track + white knob, off = gray track + knob on the left
- The tab got a hint line at the top: "click the switch on the right and the charger cuts / restores power immediately, no Save needed" — the control page used to require "Apply"
- The column headers became Port / Live reading / Switch
- **With the charger disconnected the switches are dimmed** (the track turns dark green and the knob turns gray): the state is still readable, but they don't respond — with no device state to read we don't write anything into the device
- Write failures, being busy and device rejections are all explained in the bottom line

### Fixed

- **The switch never drew at all**: the rounded-rectangle radius was passed as `height / 2`, and GDI+'s `AddArc` degenerates around a 1:2 aspect ratio,
  so the whole path came out empty — a pixel scan confirmed that area held nothing but the background color, with no white knob. It now computes
  `Math.Min(width, height) / 2` first and clamps the knob diameter, and after the rewrite it draws correctly
- The switch sat **8 px out of vertical alignment** with the row text beside it: the switch rectangle used to be `row top + 7 px`; it is now derived
  from "the row text occupies 22 px and is vertically centered" — the switch center belongs at row top + 11 px, i.e. rectangle y = row top − 3 px

### Added and Changed

- The meaning of `--control` changed: it no longer opens a separate window, it **flips the panel straight to the control page as it appears**
  (with `--tray-only --control` only the control page opens; the behavior is unchanged)
- New context menu entry "**Show control page (4 ports)**" that jumps straight to that tab
- The standalone `ControlForm.cs` was deleted

## 0.2.3 — 2026-11-17

UI polish + dark context menus.

### Power Panel

- The connection status in the top-right corner is now a **semi-transparent pill** (status-colored dot + text) instead of a bare line of text, so connected / disconnected is obvious at a glance
- The four data rows got **zebra striping**, and each row now starts with a **power color bar**: the color rules match the wattage number (gray when disconnected, pale blue <20 W,
  green 20–60 W, yellow 60–100 W, red ≥100 W)
- The `W` unit next to the big number is now centered on the digits (it used to sit 6 px high)
- The pin button got a hover background so "pin / unpin" looks clickable
- Bottom padding increased from 6 px to 14 px

### Control Page

- A **connection status dot** was added in front of the subtitle in the header (green = connected, gray = not connected)
- Each port card now has a **status color bar** on its left edge: gray when powered off, dim while idle / reading, green while outputting
- The card border brightens when the pointer is over its switch
- The footer hint was shortened to "auto-refresh every N seconds · switches take effect immediately, independently"

### Context Menus

- The tray menu and the panel context menu switched to a **dark skin**: background `#1E2229`, hover `#343A46`, green check marks, dark separators
- Menu font is Microsoft YaHei UI 9.5 throughout
- This only affects this program's own menus; the system-wide appearance is left alone

### Added

- New command-line switch `--control` that opens the control page right after launch (combined with `--tray-only` the panel no longer pops up)

## 0.2.2 — 2026-11-17

The little power readout in the taskbar is finally clean.

### Fixed

- **Ghosting from two stacked layers of digits**: the root cause was that the window keyed out its
  background in one flat pass, while the taskbar background is a gradient, so the punched-out edge
  overlapped the real icons underneath. The window is now drawn per-pixel with alpha — only the text
  is solid and everything else is fully transparent — and the ghosting is gone. The investigation is
  written up in section 12 of [docs/taskbar-component.md](docs/taskbar-component.md)

### Added

- The digits in the taskbar **can be dragged left and right**; releasing the mouse remembers the position, and it is still there on the next boot
- New "Reset taskbar number position" entry in the context menu, returning it to the left of the Start button
- New `TaskbarLeftDip` setting: a negative value means automatic placement

### Changed

- Automatic placement now hugs the left side of the Start button instead of sitting on top of the row of app icons next to the tray icons

## 0.2.1 — 2026-11-17

Transitional release. Only two small issues between the taskbar widget and the panel z-order on Win11 24H2 were fixed; the UI is unchanged.

## 0.2.0 — 2026-11-17

### Added

- **Control page**: one switch per port (C1 / C2 / C3 / A) that can both cut and restore power;
  every write is followed by a read-back verification, and failures are explained in the status line
  at the bottom. There are also "Refresh" and "Auto-refresh"
- **Installer**: a WinForms wizard (welcome → choose folder → install → finish) with the main
  program embedded; it ships with an uninstaller that removes the startup entry, the desktop /
  Start menu shortcuts, and the uninstall registry key
- Assembly version numbers now follow the release version (every exe used to be hardcoded to 0.1.0, so old and new builds were indistinguishable)

### Fixed

- "After unlocking the screen / switching users, the panel still sits on top of other windows": root cause and fix are in [docs/zorder-fix.md](docs/zorder-fix.md)

## 0.1.0

The first version that actually worked:

- Xiaomi MIoT BLE authentication (HKDF + HMAC + AES-CCM); measured authentication time 3.7–4.6 s
- Polls voltage / current / power on all four ports every second and displays the summed total power
- The tray icon draws the power number directly, colored by wattage; five icon styles to choose from
- Borderless dark power panel, pinnable to the desktop, auto-collapses on focus loss
- First-run QR-code wizard (scan with the Mi Home app → MAC and Bluetooth token are read back automatically)
- Automatic reconnect after a disconnect
