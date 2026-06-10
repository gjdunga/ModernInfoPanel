# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres
to [Semantic Versioning](https://semver.org/spec/v2.0.0.html). Dates are UTC.

## [2.1.0] - 2026-06-10

### Added
- **Paged, drill-down help system.** `/mipanel help` shows a paged command index;
  `/mipanel help <topic>` opens a detailed entry (usage, examples, "see also"), itself
  paged via `/mipanel help <topic> <page>`. Every command plus concept pages (panels,
  placeholders, status, theming) has an entry, sized to fit the Rust chat box and
  localized across all 8 locales. Replaces the old single-dump help.

## [2.0.1] - 2026-06-09

### Fixed
- **HUD no longer blocks the in-game map (and other mouse input).** The full-screen
  root container was a transparent but raycast-targetable panel, so it swallowed
  mouse events the game needs — most visibly the map's scroll-to-zoom and drag, which
  appeared "stuck" while panels were shown. The root is now a graphic-less container
  (no image = no raycast target); the map works normally with panels visible.

## [2.0.0] - 2026-06-09

### Changed
- Version aligned to a whole-number release (1.6.0 -> 2.0.0). No functional changes.

### Security
- Release is code-signed: a detached OpenPGP signature (`ModernInfoPanel.cs.asc`) and the public
  key (`gjdunga.asc`) are attached and verifiable.

## [1.6.0] - 2026-06-09

### Added
- **In-game admin editor** — `/mipanel admin` (permission `moderninfopanel.admin`) opens a
  cursor UI to toggle panels and docks on/off, reassign a panel's dock, nudge its width, and
  set its background color from a swatch palette; changes save and redraw live. No ImageLibrary.
- **Per-player timezones** — `/mipanel tz <IANA zone>` (e.g. `America/Denver`) sets a
  DST-correct per-player clock; `/mipanel tz off` reverts. Resolves on hosts with an IANA
  timezone database (Linux/mono Rust servers).
- **Theming kit** — `THEMING.md` plus a layered, editable `assets/theme/panel-template.svg`
  for building custom panel art and color themes.

## [1.5.0] - 2026-06-09

### Added
- **Per-player status glow** — a new built-in `Status` panel (disabled by default) whose icon tints
  to reflect the *viewing player's* own live state: hostile-in-safezone (red), being raided (yellow),
  in a safe zone (green), AFK (blue), or building-authed (white). The state→color mapping, the AFK
  threshold (`AfkSeconds`), the raid window (`RaidWindowSeconds`), and the evaluation `Priority` are
  all configurable in the panel's `Settings`. Raid state is driven by explosive damage to a player's
  building blocks/doors/tool cupboards (no plugin dependency).
- **Progress-bar panel element** — panels can carry a `Progress` element (track + fill); the fill
  width tracks a 0–1 value. Designed for modded events and timers to drive live.
- **Developer API additions** — `SetPanelProgress(panel, value[, playerId])` updates a progress
  bar's fill in place, and `SetPanelColor(panel, color[, playerId])` flashes a panel's image glow in
  place. Both act on third-party (`PanelRegister`) panels only, consistent with the existing API.

## [1.4.0] - 2026-06-08

### Added
- **Dynamic placeholders** in panel `Static content` and rotating announcements — `{name}`,
  `{online}`, `{max}`, `{sleepers}`, `{grid}`, `{coords}`, `{x}`, `{z}`, `{time}`, `{balance}`,
  `{points}`, `{server}`, `{wipe}`, `{lastwipe}` — resolved per-viewer. Optional pass-through to
  the **PlaceholderAPI** plugin if it's installed (`General → Resolve {tokens} via PlaceholderAPI`).
- **Clickable panels** — set a panel's `Run command on click` to a console command (run as the
  clicking player; placeholders resolved) to turn it into a launcher.
- **Three new built-in panels** (all disabled by default): `ServerFPS`, `Ping`, and
  `WipeCountdown`.
- **Wipe countdown** — shows `Wipe in Xd Yh`. The cadence is auto-detected from the server's
  browser tags (`weekly`/`biweekly`/`monthly`; override or `custom` in `Wipe schedule`), and the
  anchor is the **actual last map wipe** — detected via the `OnNewSave` hook (map wipe, *not* a
  blueprint wipe) and the save-file timestamp on first run. Warns once if the cadence is unset.
- **Panel fade-in** — `General → Panel fade-in seconds` (0 = off); fades panels in on draw without
  re-fading on value updates.

## [1.3.2] - 2026-06-08

### Changed
- All input-rejection and config errors now route through a single `LogProblem` path that
  emits to the **server console** (as an error) **and** the durable `oxide/logs/ModernInfoPanel/`
  errors logfile: invalid config, malformed `PanelRegister` JSON, built-in-name collisions,
  over-long panel names, the per-plugin panel cap, and rejected image URLs.

## [1.3.1] - 2026-06-08

### Added
- Rejected or malformed third-party image URLs are now logged. `PanelRegister` (image
  dropped) and `SetPanelImage` (call rejected) emit a warning to the server console **and**
  a durable logfile under `oxide/logs/ModernInfoPanel/`, naming the plugin, panel, and URL.

## [1.3.0] - 2026-06-08

### Added
- **Third-party API hardening.** Panel registrations are capped (25 per plugin, 64-char
  names), registered/updated text is bounded to 256 characters, and image URLs must be
  `http(s)` (other schemes are rejected on `SetPanelImage` and dropped on `PanelRegister`).

### Changed
- The mutating API (`SetPanelText`, `SetPanelImage`, `ShowPanel`, `HidePanel`) is sandboxed
  to third-party panels: it can no longer target the plugin's built-in panels, so a loaded
  plugin can't hijack or disable `Clock`, the event icons, etc. Custom image colors now pass
  through the same `SafeColor` validation as the config.

## [1.2.1] - 2026-06-08

### Changed
- **Persistence is debounced.** Player preference changes (`hide`/`show`, clock mode/offset,
  time format) now mark the data dirty and flush at most about once a minute (plus on
  `OnServerSave` and unload), instead of writing the whole data file on every command — so
  command spam can no longer drive a disk write per keystroke.
- A light **per-player cooldown** (1s) is applied to the state-changing chat/console
  subcommands; commands sent faster than that are silently ignored.

### Fixed
- **No-op coalescing:** `hide`/`show`/`clock`/`timeformat` only persist and redraw when the
  value actually changes, eliminating redundant full-UI rebuilds from repeated commands.
- The stored-data file is bounded: player entries that match the default behaviour are pruned
  on flush, so it can't grow without limit.

## [1.2.0] - 2026-06-08

### Added
- Event indicators now also recognize **modded subclasses** of the vanilla event
  entities (airdrop plane, patrol helicopter, chinook, cargo ship, bradley) via a
  secondary match, so custom variants still drive their indicator. Vanilla detection
  is unchanged (exact-type fast path).

### Changed
- `mipanel import` is now **confined to the config directory** — a bare filename or a
  path inside `oxide/config/` is accepted; paths outside it are rejected (new
  `ImportOutside` message added to all 8 locales).
- Snapshot-deferred panel registration uses an **expanding backoff** (2s, 5s, 10s, 15s)
  then registers best-effort, instead of retrying every 2s indefinitely.
- `INSTALL.md` documents the InfoPanel import, including the best-effort dock-margin caveat.

### Fixed
- `PanelRegister` now **rejects** third-party panel names that collide with a built-in
  panel id (they would otherwise shadow the built-in).
- Stricter JSON type checks in the InfoPanel importer (booleans and strings are read only
  from matching token types).

## [1.1.1] - 2026-06-08

### Changed
- The InfoPanel import now writes a full report — docks/panels mapped, panels
  skipped, and a coverage note — to the server console and a logfile under
  `oxide/logs/ModernInfoPanel/`, on both first-load auto-import and `mipanel import`.

### Fixed
- Import failures no longer surface raw parser details in chat; players see the
  generic failure message while the full error is logged server-side only.
- Dock layout clamps cumulative panel widths, so a misconfigured dock can no longer
  produce overlapping or out-of-bounds panels.
- Hardened internal panel lookups to use safe lookups instead of direct indexing.

## [1.1.0] - 2026-06-08

### Added
- **InfoPanel config import** for a smooth migration from the classic InfoPanel
  plugin. On first load (when no Modern Info Panel config exists yet), an existing
  `oxide/config/InfoPanel.json` is detected and its docks/panels are folded into the
  generated config; admins, the server console, and RCON can also run
  `mipanel import [path]` at any time. Recognized docks and panels are matched by
  name (via an alias table) and merged onto MIP's defaults; unrecognized panels are
  logged and skipped, and InfoPanel's file is read-only and never modified.
- Localized messages `HelpImport`, `ImportOk`, `ImportNone`, and `ImportFailed`
  across all 8 locales.

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
