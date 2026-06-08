# Installing Modern Info Panel

## Install

1. Download `ModernInfoPanel.cs` from the
   [latest release](https://github.com/gjdunga/ModernInfoPanel/releases).
2. Upload it to your server:
   - **Oxide:** `oxide/plugins/ModernInfoPanel.cs`
   - **Carbon:** `carbon/plugins/ModernInfoPanel.cs`
3. The plugin compiles and loads automatically. On first load it writes:
   - **Oxide:** `oxide/config/ModernInfoPanel.json` and `oxide/lang/<locale>/ModernInfoPanel.json`
   - **Carbon:** `carbon/configs/ModernInfoPanel.json` and `carbon/lang/<locale>/ModernInfoPanel.json`

No dependencies are required.

## Update

1. Replace the `.cs` file with the new release.
2. Oxide/Carbon recompiles it automatically (or run `o.reload ModernInfoPanel` /
   `c.reload ModernInfoPanel`).
3. Review `CHANGELOG.md` for any new config keys; existing keys are preserved.

## Permissions

```
oxide.grant group default moderninfopanel.use     # only needed if not shown to everyone
oxide.grant user <name|steamid> moderninfopanel.admin
```

On Carbon, use `c.grant` instead of `oxide.grant`. By default the panel is shown to
everyone and `moderninfopanel.use` is not required.

## Configure

Edit `ModernInfoPanel.json`, then apply with either:

- in-game (admin): `/infopanel reload`
- console: `moderninfopanel.reload` or `o.reload ModernInfoPanel`

See the [README](README.md#configuration) for the full schema.

## Uninstall

1. Remove the `.cs` file from the plugins folder.
2. Optionally delete `ModernInfoPanel.json` and the `lang/*/ModernInfoPanel.json` files.

The panel is removed from every player's screen automatically when the plugin unloads.

## Troubleshooting

- **No panel appears** — confirm the plugin loaded (`o.plugins` / `c.plugins`). If
  "Show panel to everyone" is `false`, grant `moderninfopanel.use`.
- **A player hid it** — `/infopanel on` re-shows it (the toggle is per-player and
  resets on disconnect).
- **Clock looks wrong** — `realtime` uses the host machine's clock; switch to
  `gametime` for the in-game day/night clock, or change the `Clock format`.
- **Text is clipped** — widen the block via its `Anchor min`/`Anchor max`, or lower
  the `Font size`.
- **Config didn't apply** — run `/infopanel reload`; check the console for a config
  error (a malformed file is replaced with defaults).
