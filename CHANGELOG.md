# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres
to [Semantic Versioning](https://semver.org/spec/v2.0.0.html). Dates are UTC.

## [1.0.0] - 2026-06-08

### Added
- Initial release of **Modern Info Panel** — a security- and performance-minded
  rebuild of the classic InfoPanel concept (originally by Gonzi), conformant with
  the DunganSoft Plugin Standard.
- Four configurable corner **docks** (top-left, top-right, bottom-left,
  bottom-right); panels tile within a dock by order, with per-panel width,
  alignment, background color, refresh interval, and optional permission.
- Built-in panels: `Clock`, `Messages` (rotating announcements), `Balance`
  (Economics), `Points` (ServerRewards), `Coordinates` (X/Z, grid, or both),
  `Compass` (text or degrees), `OnlinePlayers`, `Sleepers`, and live event
  indicators `AirdropEvent`, `HelicopterEvent`, `ChinookEvent`, `CargoShipEvent`,
  `BradleyEvent`, and `RadiationEvent`.
- A single universal `mipanel` command, registered via covalence so it runs from
  chat (`/mipanel …`), the in-game F1 console, the **server console**, and **RCON**
  (consoles use `mipanel …`): `hide`/`show`, `clock game`, `clock server [offset]`,
  `timeformat [index]`, and admin/server `reload`. Per-player choices persist across
  reconnects; `reload` works from any context.
- Permission `moderninfopanel.admin` (server console/RCON are always authorized);
  dynamic per-panel permissions (`moderninfopanel.<suffix>`).
- Reflection-free developer API: `PanelRegister`, `PanelUnregister`,
  `SetPanelText`, `SetPanelImage`, `ShowPanel`, `HidePanel`, `RefreshPanel`,
  `IsPlayerGUILoaded`. Panels registered by a plugin are removed when it unloads.
- Localization in 8 locales: `en, es, ru, la, zh-CN, de, fr, pt`.

### Performance & security
- Single 1-second master tick with per-panel cadence; only changed labels/icons
  are pushed (values cached per player), so backgrounds never flicker and idle
  ticks send nothing.
- Event indicators are driven by `OnEntitySpawned`/`OnEntityKill` with a periodic
  validity prune instead of constant polling.
- All numeric UI values are formatted with `InvariantCulture`; config colors,
  anchors, font sizes, and offsets are validated and clamped on use.
- No `System.Reflection`; uses only the shared Rust/CUI APIs (Oxide + Carbon),
  and guards all optional plugin calls (Economics/ServerRewards).
