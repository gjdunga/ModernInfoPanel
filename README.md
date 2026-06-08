# Modern Info Panel

[![Standards](https://github.com/gjdunga/ModernInfoPanel/actions/workflows/standards.yml/badge.svg)](https://github.com/gjdunga/ModernInfoPanel/actions/workflows/standards.yml)
[![Compile](https://github.com/gjdunga/ModernInfoPanel/actions/workflows/compile.yml/badge.svg)](https://github.com/gjdunga/ModernInfoPanel/actions/workflows/compile.yml)
[![License: GPL-3.0](https://img.shields.io/badge/License-GPL--3.0-blue.svg)](LICENSE)
![Version](https://img.shields.io/badge/version-1.3.1-informational)

A configurable corner-HUD information panel for Rust — a modern, security- and
performance-minded rebuild of the classic InfoPanel concept. Four corner "docks"
host individually configurable panels: a clock, rotating announcements,
balance/points read-outs, coordinates, a compass, online/sleeper counts, and live
event indicators. **Compatible with both Oxide and Carbon.**

## Features

- **Four corner docks** — top-left, top-right, bottom-left, bottom-right; each
  with its own edge, size, and offset. Panels tile within a dock by order.
- **Clock** — in-game or server time, per-player offset, and selectable format.
- **Rotating announcements** — cycle messages in `normal` or `random` order.
- **Economy read-outs** — `Balance` (via Economics) and `Points` (via ServerRewards),
  shown only when those plugins are installed.
- **Coordinates** — X/Z, map grid, or both.
- **Compass** — eight-point text direction (localized) or raw degrees.
- **Player counts** — live `online / max` and sleeper counters.
- **Live event indicators** — airdrop, patrol helicopter, chinook, cargo ship,
  bradley, and radiation icons that recolor while the event is active.
- **Per-player control** — players hide/show their panel and tune their clock with
  `/mipanel`; their choices persist across reconnects.
- **Per-panel permissions** — optionally gate any panel behind a permission.
- **Reflection-free developer API** — other plugins can register, update, show,
  hide, and remove their own panels.
- **Localized** — ships in 8 languages; all chat text goes through the Oxide lang API.
- **Lightweight** — only changed labels/icons are pushed each tick (value-cached
  per player), so backgrounds never flicker and idle ticks send nothing.

## Installation

1. Download `ModernInfoPanel.cs` from the [latest release](https://github.com/gjdunga/ModernInfoPanel/releases/latest).
2. Drop it into `oxide/plugins/` (Oxide) or `carbon/plugins/` (Carbon).
3. It compiles and loads automatically and writes a default config to
   `oxide/config/ModernInfoPanel.json` (or `carbon/configs/`) on first load.

See [INSTALL.md](INSTALL.md) for updating, permissions, and troubleshooting.

## Permissions

| Permission | Description |
| --- | --- |
| `moderninfopanel.admin` | Reload the config (`mipanel reload`) and import an InfoPanel config (`mipanel import`) from chat/console/RCON. The server console and RCON are always authorized. |
| `moderninfopanel.<suffix>` | Dynamic — registered for any panel that sets a `Permission suffix`; only holders see that panel. |

## Commands

A single command, `mipanel`, works from **every** context: chat (`/mipanel …`),
the in-game F1 console, the **server console**, and **RCON** (in consoles, omit the
slash: `mipanel …`).

| Command | Arguments | Description |
| --- | --- | --- |
| `mipanel` | *(none)* | Show the command help. |
| `mipanel` | `hide` \| `show` | Hide or show your panel (persists across reconnects). Player-only. |
| `mipanel` | `clock game` | Use in-game time. Player-only. |
| `mipanel` | `clock server [offset]` | Use server time; optional hour offset `-23..23`. Player-only. |
| `mipanel` | `timeformat [index]` | List or pick a clock format. Player-only. |
| `mipanel` | `reload` | **Admin/server** — reload config and redraw all panels. Works from chat, console, RCON, and the server console. |
| `mipanel` | `import [file]` | **Admin/server** — import an existing InfoPanel config (a filename or path **inside the config folder**; default `oxide/config/InfoPanel.json`) into MIP, then reload. Works from chat, console, RCON, and the server console. |

> The per-player subcommands (`hide`, `show`, `clock`, `timeformat`) need a player,
> so from the server console/RCON only `reload` and `import` apply; the rest reply
> with a hint to run them in-game.

## Configuration

A default `oxide/config/ModernInfoPanel.json` is written on first load. It has four
sections:

- **General** — coordinate format (`0` X/Z, `1` grid, `2` both), compass as text vs
  degrees, message rotation order, and whether panels show by default.
- **Docks** — `TopLeftDock`, `TopRightDock`, `BottomLeftDock`, `BottomRightDock`,
  each with an `Enabled` flag, horizontal/vertical edge, edge offsets, size, and
  background color.
- **Panels** — each panel sets `Enabled`, its `Dock`, `Order`, `Width within dock`,
  alignment, background color, an optional `Permission suffix`, a `Refresh interval`
  (`0` = static/event-driven), and an optional `Image` and/or `Text` element.
  Some panels carry extra `Settings` (e.g. the clock's `Mode`/`Format`, or an event
  icon's `ActiveColor`/`InactiveColor`).
- **Rotating announcement messages** — the list shown by the `Messages` panel.

Built-in panel names: `Clock`, `Messages`, `Balance`, `Points`, `Coordinates`,
`Compass`, `OnlinePlayers`, `Sleepers`, `AirdropEvent`, `HelicopterEvent`,
`ChinookEvent`, `CargoShipEvent`, `BradleyEvent`, `RadiationEvent`. `Compass`,
`BradleyEvent`, and `RadiationEvent` ship disabled.

> **Icons:** event/icon panels load PNGs from external `imgur` URLs by default
> (the original InfoPanel artwork). Clients fetch them directly; swap the `Url`
> values for self-hosted images if you prefer not to depend on a third party.

Edit the file and run `mipanel reload` (or `o.reload ModernInfoPanel`) to apply.

### Developer API

Other plugins can manage their own panels without any Reflection:

```csharp
// Register a panel (json is a serialized panel config like one entry under "Panels")
ModernInfoPanel?.Call("PanelRegister", Title, "MyPanel", json);
ModernInfoPanel?.Call("SetPanelText", "MyPanel", "Hello", playerIdOrNull);
ModernInfoPanel?.Call("SetPanelImage", "MyPanel", url, "1 1 1 1", playerIdOrNull);
ModernInfoPanel?.Call("ShowPanel", "MyPanel", playerIdOrNull);
ModernInfoPanel?.Call("HidePanel", "MyPanel", playerIdOrNull);
ModernInfoPanel?.Call("RefreshPanel", "MyPanel", playerIdOrNull);
ModernInfoPanel?.Call("PanelUnregister", Title, "MyPanel");
bool loaded = (bool)(ModernInfoPanel?.Call("IsPlayerGUILoaded", playerId) ?? false);
```

Panels a plugin registers are removed automatically when that plugin unloads.

The API is sandboxed to a plugin's **own** panels: `SetPanelText`/`SetPanelImage`/`ShowPanel`/
`HidePanel` only act on panels created via `PanelRegister` — the built-in panels can't be
targeted. Registrations are capped at **25 panels per plugin**, panel names at 64 characters,
panel text at 256 characters, and image URLs must be `http(s)`.

## Migrating from InfoPanel

Moving from the classic **InfoPanel**? Modern Info Panel can adopt your existing
setup so you don't have to rebuild it by hand:

- **On first load** — if there's no `ModernInfoPanel.json` yet but an
  `oxide/config/InfoPanel.json` exists, MIP folds its docks and panels into the
  config it generates.
- **On demand** — an admin (or the server console/RCON) can run
  `mipanel import [path]` at any time; `path` defaults to `oxide/config/InfoPanel.json`.

InfoPanel shares MIP's four dock names, and recognized panels are matched by name
(via an alias table) and merged onto MIP's defaults. Panels InfoPanel had that MIP
doesn't are logged and skipped, so nothing breaks. Your `InfoPanel.json` is read
only and never modified — review the result and run `mipanel reload` to apply tweaks.

## Localization

Ships with `en, es, ru, la, zh-CN, de, fr, pt`. Files live in `oxide/lang/<locale>/ModernInfoPanel.json`
and share identical keys and placeholders. Edit a locale to customize the wording.
(Rotating announcements are server content and live in the config, not the lang files.)

## Compatibility

- **Oxide** for Rust — `2.0.7022+` (verified `2.0.7423`).
- **Carbon** — fully supported; the plugin uses only the shared Rust/CUI APIs.

## Credits & License

A modern rebuild inspired by the original **InfoPanel** (by Gonzi). Rebuilt and
maintained by **Gabriel Dungan (DunganSoft Technologies)** — uMod handle `gjdunga`.

Licensed under **GPL-3.0** — see [LICENSE](LICENSE). Part of the
[DunganSoft Plugin Standard](https://github.com/gjdunga/rust-plugin-standard) portfolio.
