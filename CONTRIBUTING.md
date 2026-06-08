# Contributing

Thanks for helping improve this plugin. This repo follows the
[DunganSoft Plugin Standard](https://github.com/gjdunga/rust-plugin-standard);
changes must keep it conformant.

## Before you open a PR

- **Branch** off `main` (`feat/…`, `fix/…`, `perf/…`, `docs/…`, `i18n/…`).
- **Compile** against the real assemblies: `make references-managed` once, then
  `make build` (see [BUILD.md](BUILD.md)). It must report `0 Error(s)`.
- **Conformance:** `python3 tools/check-standard.py .` must report `0 errors`.
- **Version + changelog:** any user-visible change bumps the version (in lockstep
  across `[Info]`, `manifest.json`, `.umod.yaml`, README, and the top
  `CHANGELOG.md` heading) and adds a `CHANGELOG.md` entry.
- **Translations:** if you add or change a message, update **every** locale in
  `oxide/lang/` (8 locales) — same keys, same placeholders.

## Code style

- C# 8+, early returns over nested `if`s, XML-doc the non-obvious (explain *why*).
- No new dependencies; the plugin must compile with stock Oxide references.
- No `System.Reflection` (uMod plugins). No `System.ValueTuple`.

## Reviews & testing

There is no unit-test harness (the plugin exercises Facepunch APIs in-process).
Verify on a private Rust server and note what you checked in the PR.
