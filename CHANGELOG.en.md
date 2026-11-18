# Changelog

This project has only ever run on one of my own machines and was never released publicly, so
what follows is organized by "what was done" rather than by release; the version numbers just
kept climbing so they line up with the file versions inside the installers.

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
