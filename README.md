# Modern Info Panel

[![Standards](https://github.com/gjdunga/ModernInfoPanel/actions/workflows/standards.yml/badge.svg)](https://github.com/gjdunga/ModernInfoPanel/actions/workflows/standards.yml)
[![Compile](https://github.com/gjdunga/ModernInfoPanel/actions/workflows/compile.yml/badge.svg)](https://github.com/gjdunga/ModernInfoPanel/actions/workflows/compile.yml)
[![License: GPL-3.0](https://img.shields.io/badge/License-GPL--3.0-blue.svg)](LICENSE)
![Version](https://img.shields.io/badge/version-1.0.0-informational)

A lightweight, fully configurable on-screen information panel for Rust — a clock,
a live player counter, and rotating server announcements — built on Rust's CUI.
**Compatible with both Oxide and Carbon.**

## Features

- **Clock block** — real-world server time or in-game time, in any .NET date/time format.
- **Players block** — live `online / max` player counter.
- **Rotating announcements** — cycle through any number of messages on a configurable interval.
- **Fully configurable blocks** — position (anchors), size, colors, font size, and alignment per block.
- **Per-player toggle** — players can show/hide their own panel with `/infopanel`.
- **Permission-gated or open** — show to everyone, or restrict to a permission.
- **Localized** — ships in 8 languages; all plugin text goes through the Oxide lang API.
- **Lightweight** — only dynamic labels are redrawn each tick, so static backgrounds don't flicker.
- **No Reflection** — uMod-safe; uses only the shared Rust/CUI APIs.

## Installation

1. Download `ModernInfoPanel.cs` from the [latest release](https://github.com/gjdunga/ModernInfoPanel/releases).
2. Drop it into `oxide/plugins/` (Oxide) or `carbon/plugins/` (Carbon).
3. It compiles and loads automatically and writes a default config to
   `oxide/config/ModernInfoPanel.json` (or `carbon/configs/`) on first load.

See [INSTALL.md](INSTALL.md) for updating, permissions, and troubleshooting.

## Permissions

| Permission | Description |
| --- | --- |
| `moderninfopanel.use` | See and toggle the panel (only enforced when it is **not** shown to everyone). |
| `moderninfopanel.admin` | Reload the configuration via `/infopanel reload` and the console command. |

## Commands

| Command | Arguments | Description |
| --- | --- | --- |
| `/infopanel` | `[on\|off\|toggle\|reload]` | Toggle your panel. `reload` (admin) reloads config and redraws all panels. |
| `moderninfopanel.reload` | — | Console command (admin) to reload the config and redraw all panels. |

## Configuration

A default `oxide/config/ModernInfoPanel.json` is written on first load:

```json
{
  "Update interval (seconds)": 1.0,
  "Rotator interval (seconds)": 8.0,
  "Show panel to everyone (no permission required)": true,
  "Clock mode (realtime | gametime)": "realtime",
  "Clock format (.NET date/time format string)": "HH:mm",
  "Rotating announcement messages": [
    "Welcome! Type /infopanel to toggle this panel.",
    "Be respectful - no harassment or cheating.",
    "Need help? Ask a moderator in chat."
  ],
  "Blocks": [
    { "Id": "clock",   "Type (clock | players | rotator | text)": "clock",   "...": "see file" },
    { "Id": "players", "Type (clock | players | rotator | text)": "players", "...": "see file" },
    { "Id": "rotator", "Type (clock | players | rotator | text)": "rotator", "...": "see file" }
  ]
}
```

- **Block types:** `clock`, `players`, `rotator`, `text`. A `text` block shows its
  `Static text`, or the localized welcome message when that is left empty.
- **Anchors** are screen fractions `"x y"` from `0 0` (bottom-left) to `1 1` (top-right).
- **Colors** are `"R G B A"` in the `0`–`1` range.
- **Clock mode** `gametime` uses the in-game day/night clock; `realtime` uses the host clock.

Edit the file and run `/infopanel reload` (or `o.reload ModernInfoPanel`) to apply.

## Localization

Ships with `en, es, ru, la, zh-CN, de, fr, pt`. Files live in `oxide/lang/<locale>/ModernInfoPanel.json`
and share identical keys and placeholders. Edit a locale to customize the wording.
(Rotating announcements are server content and live in the config, not the lang files.)

## Compatibility

- **Oxide** for Rust — `2.0.7022+` (verified `2.0.7423`).
- **Carbon** — fully supported; the plugin uses only the shared Rust/CUI APIs.

## Credits & License

Created and maintained by **Gabriel Dungan (DunganSoft Technologies)** — uMod handle `gjdunga`.

Licensed under **GPL-3.0** — see [LICENSE](LICENSE). Part of the
[DunganSoft Plugin Standard](https://github.com/gjdunga/rust-plugin-standard) portfolio.
