# Installing Modern Info Panel

## Install

1. Download `ModernInfoPanel.cs` from the
   [latest release](https://github.com/gjdunga/ModernInfoPanel/releases/latest).
2. Upload it to your server:
   - **Oxide:** `oxide/plugins/ModernInfoPanel.cs`
   - **Carbon:** `carbon/plugins/ModernInfoPanel.cs`
3. The plugin compiles and loads automatically. On first load it writes:
   - **Oxide:** `oxide/config/ModernInfoPanel.json` and `oxide/lang/<locale>/ModernInfoPanel.json`
   - **Carbon:** `carbon/configs/ModernInfoPanel.json` and `carbon/lang/<locale>/ModernInfoPanel.json`

No hard dependencies are required. The `Balance` and `Points` panels light up only
when **Economics** and **ServerRewards** respectively are installed; otherwise they
read `0`.

## Update

1. Replace the `.cs` file with the new release.
2. Oxide/Carbon recompiles it automatically (or run `o.reload ModernInfoPanel` /
   `c.reload ModernInfoPanel`).
3. Review `CHANGELOG.md` for any new config keys; missing sections are filled with
   defaults and saved on load.

## Permissions

```
oxide.grant user <name|steamid> moderninfopanel.admin
```

On Carbon, use `c.grant` instead of `oxide.grant`. By default every panel is shown
to everyone. To restrict an individual panel, set its `Permission suffix` in the
config (e.g. `vip`) — the plugin registers `moderninfopanel.vip` and only holders
see that panel:

```
oxide.grant group vip moderninfopanel.vip
```

## Configure

Edit `ModernInfoPanel.json`, then apply with either:

- in-game (admin): `/mipanel reload`
- server console / RCON: `mipanel reload`
- Oxide/Carbon reload of the whole plugin: `o.reload ModernInfoPanel` / `c.reload ModernInfoPanel`

See the [README](README.md#configuration) for the full schema.

## Migrating from InfoPanel

If you ran the classic **InfoPanel**, Modern Info Panel can adopt its settings:

- **Automatic** — on first load (when no `ModernInfoPanel.json` exists yet) an existing
  `oxide/config/InfoPanel.json` is detected and its docks/panels are folded into the
  config that gets generated.
- **Manual** — an admin or the server console/RCON can run `mipanel import [file]` at any
  time. The path is **confined to the config directory**: pass a bare filename
  (e.g. `mipanel import InfoPanel.backup.json`) or a path inside `oxide/config/`; paths
  outside it are rejected. With no argument it imports `oxide/config/InfoPanel.json`.

Every import writes a report to the **server console** and to a logfile under
`oxide/logs/ModernInfoPanel/` (docks/panels mapped, panels skipped, and a coverage note).

**Caveats — the import is best-effort:**

- **Dock placement is approximate.** InfoPanel positions docks with a four-value CUI
  `Margin` (`left top right bottom`); Modern Info Panel uses a single distance from each
  anchored edge, so imported dock offsets are derived from the matching margin sides and
  may need a small tweak. Review the result and run `mipanel reload` to apply edits.
- **Only what's in `InfoPanel.json` is imported.** Panels that InfoPanel registers at
  runtime via its sub-plugins (or stores under `oxide/data`) are not in the config file
  and cannot be migrated; the import log lists what was skipped.
- Your `InfoPanel.json` is read-only and never modified.

## Uninstall

1. Remove the `.cs` file from the plugins folder.
2. Optionally delete `ModernInfoPanel.json`, the `lang/*/ModernInfoPanel.json`
   files, and the `ModernInfoPanel` data file (per-player clock/visibility prefs).

The panel is removed from every player's screen automatically when the plugin unloads.

## Troubleshooting

- **No panel appears** — confirm the plugin loaded (`o.plugins` / `c.plugins`) and
  that the relevant `Dock` and `Panel` are `Enabled`. A player who ran `/mipanel hide`
  stays hidden until `/mipanel show`.
- **Balance/Points show 0** — install Economics / ServerRewards, or disable those
  panels in the config.
- **Clock looks wrong** — `/mipanel clock game` uses the in-game day/night clock;
  `/mipanel clock server [offset]` uses the host clock with an hour offset. Change the
  default format under `Panels → Clock → Settings → Format`.
- **Icons are missing** — event/icon panels fetch PNGs from external URLs; verify
  the client has internet access or replace the `Url` values with self-hosted images.
- **Text is clipped** — widen the panel's `Width within dock`, widen its dock, or
  lower the `Font size`.
- **Config didn't apply** — run `/mipanel reload`; check the console for a config
  error (a malformed file is replaced with defaults, and Oxide backs up the old one).
