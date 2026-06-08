# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres
to [Semantic Versioning](https://semver.org/spec/v2.0.0.html). Dates are UTC.

## [1.0.0] - 2026-06-08

### Added
- Initial release of **Modern Info Panel**.
- Configurable CUI info panel with four block types: `clock`, `players`,
  `rotator` (rotating announcements), and `text`.
- Per-block configuration: anchors, background/text color, font size, and alignment.
- Clock supports `realtime` and `gametime` modes with a custom .NET format string.
- Live `online / max` player counter.
- Rotating announcements on a configurable interval.
- Per-player `/infopanel [on|off|toggle]` command and `/infopanel reload` (admin).
- `moderninfopanel.reload` console command (admin).
- Permissions `moderninfopanel.use` and `moderninfopanel.admin`.
- Localization in 8 locales: `en, es, ru, la, zh-CN, de, fr, pt`.
- Oxide and Carbon compatibility; no Reflection.
