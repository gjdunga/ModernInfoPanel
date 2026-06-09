# Theming Modern Info Panel

MIP is fully themeable **without touching code** — everything visual lives in
`oxide/config/ModernInfoPanel.json` (or the in-game **`/mipanel admin`** editor) and in the
image `Url`s panels point at. This guide covers colors, the Status-glow palette, swapping icon
art, three ready-to-paste presets, and the editable template under `assets/theme/`.

After any config edit, run **`mipanel reload`** (or `o.reload ModernInfoPanel`) to apply.

---

## 1. How color works

Every color in the config is an **`"R G B A"`** string with each channel in `0..1`
(not `0..255`). Alpha `0` is fully transparent, `1` fully opaque.

| `"R G B A"` | Result |
| --- | --- |
| `"0 0 0 0"` | invisible (docks default to this — only the panels are tinted) |
| `"0 0 0 0.45"` | 45 %-opaque black (the default panel backdrop) |
| `"1 1 1 1"` | solid white (default text/icon tint) |
| `"0 1 0 1"` | solid green |

To convert an 8-bit hex color like `#3C78C8` → divide each pair by 255:
`60/255 ≈ 0.235`, `120/255 ≈ 0.470`, `200/255 ≈ 0.784` → `"0.235 0.470 0.784 1"`.

### Where colors live

- **Docks** → `Docks.<DockName>.Background color (R G B A)` — the strip behind a group of panels
  (transparent by default).
- **Panels** → `Panels.<Name>.Background color (R G B A)` — the per-panel backdrop.
- **Text** → `Panels.<Name>.Text.Color (R G B A)`.
- **Icons** → `Panels.<Name>.Image.Color (R G B A)` — tints the PNG; keep `"1 1 1 1"` to show art
  unchanged, or tint it (e.g. `"1 1 1 0.15"` for the dim "inactive" event look).

---

## 2. Status-glow palette

The optional **`Status`** panel tints one indicator icon to the viewer's own live state. The
map and thresholds live in `Panels.Status.Settings` (defaults shown):

| Setting | Default | Meaning |
| --- | --- | --- |
| `SafeColor` | `"0 1 0 1"` (green) | inside a safe zone |
| `SafeHostileColor` | `"1 0 0 1"` (red) | hostile inside a safe zone — turrets will fire |
| `RaidColor` | `"1 1 0 1"` (yellow) | your building took explosive damage (held `RaidWindowSeconds`) |
| `AfkColor` | `"0.3 0.5 1 1"` (blue) | idle for `AfkSeconds` |
| `BuildingPrivColor` | `"1 1 1 1"` (white) | building-authorized |
| `InactiveColor` | `"1 1 1 0.15"` | none of the above |
| `Priority` | `"SafeHostile,Raid,SafeZone,AFK,BuildingPriv"` | evaluation order; first match wins |

Event icons (`AirdropEvent`, `HelicopterEvent`, …) use the same idea with their own
`ActiveColor` / `InactiveColor` in each panel's `Settings`.

---

## 3. Swapping icon art

Event/icon panels load PNGs from external `imgur` URLs by default (the original InfoPanel
artwork). To use your own:

1. Make a square PNG with transparency (a 256×256 source exports crisp at HUD size — see the
   template below).
2. Host it somewhere clients can reach over HTTP(S) — your own web space, a CDN, or an image host.
3. Point the panel at it: `Panels.<Name>.Image.Url = "https://…/my-icon.png"`.
4. Leave `Image.Color` at `"1 1 1 1"` to show the art as-is, or tint it.

> Clients fetch these URLs directly. Self-hosting avoids depending on a third party and lets you
> ship a coherent icon set. CUI can't load SVG/PSD at runtime — always export to **PNG** and
> reference that.

---

## 4. Copy-paste presets

Each preset is just a set of `Background color` / `Text.Color` values. Apply them per panel, or
swatch every panel from the in-game editor. The editor's swatch strip offers, in order:
`"0 0 0 0.45"`, `"0 0 0 0.70"`, `"0.10 0.10 0.12 0.80"`, `"0.15 0.30 0.50 0.80"`,
`"0.30 0.15 0.15 0.80"`, `"0.15 0.30 0.15 0.80"`, and `"0 0 0 0"` (clear).

### Dark (default-ish, high legibility)

```
Panel  Background color : "0 0 0 0.70"
Panel  Text.Color       : "1 1 1 1"
Dock   Background color  : "0 0 0 0"
```

### Neon (cool slate panels, accent text)

```
Panel  Background color : "0.10 0.10 0.12 0.80"
Panel  Text.Color       : "0.30 0.85 1 1"
Dock   Background color  : "0 0 0 0"
```

### Minimal (near-invisible chrome, white text)

```
Panel  Background color : "0 0 0 0.25"
Panel  Text.Color       : "1 1 1 0.90"
Dock   Background color  : "0 0 0 0"
```

---

## 5. The editable template

[`assets/theme/panel-template.svg`](assets/theme/panel-template.svg) is a layered, editable SVG
with three named groups so you can build a matching icon set:

- **`background`** — the panel backdrop / pill shape.
- **`icon`** — your glyph (replace this group with your art).
- **`accent`** — an optional highlight stroke or status dot.

Workflow: open it in Inkscape / Illustrator / Figma (or any editor that reads SVG groups), edit
the groups, **export each icon to a 256×256 PNG**, self-host, and point the relevant panel's
`Image → Url` at the result. Keep the canvas square and the art centered so it lines up with the
other HUD icons.

---

See the main [README](README.md#configuration) for the full config reference and the
[`/mipanel admin`](README.md#admin-menu) editor.
