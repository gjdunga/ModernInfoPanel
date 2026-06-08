# Building Modern Info Panel

The shipped artifact is the **raw** `oxide/plugins/ModernInfoPanel.cs` — Oxide and
Carbon compile it in-process on the server. This build chain only **type-checks**
the source against the real Oxide/Rust/Unity assemblies so API breaks are caught
before release. The produced DLL is throwaway.

## Prerequisites

- [.NET SDK 8.0+](https://dotnet.microsoft.com/download) (provides the net48
  reference assemblies on all platforms).
- `curl` + `tar` (Linux/macOS) or PowerShell 5+ (Windows) to fetch references.
- Disk: a few dozen MB with `--managed-only`, or ~8 GB for a full server install.

## One-time: fetch reference assemblies

The proprietary game assemblies are **not** committed. Download them once:

```bash
# Linux / macOS
make references-managed            # or: tools/fetch-references.sh --managed-only
```

```powershell
# Windows
tools\fetch-references.ps1 -ManagedOnly
```

This installs the Rust dedicated server via SteamCMD, overlays the latest
Oxide.Rust, and leaves the assemblies under
`references/RustDedicated_Data/Managed` (git-ignored).

Already have a server? Point the build at its Managed folder instead:

```bash
dotnet build build/ModernInfoPanel.csproj -c Release \
  -p:ManagedDir=/path/to/RustDedicated_Data/Managed
```

(or set the `MANAGED_DIR` environment variable.)

## Build (type-check)

```bash
make build         # or: dotnet build build/ModernInfoPanel.csproj -c Release --nologo
```

A successful run reports `0 Error(s)`.

## Conformance

```bash
python3 tools/check-standard.py .   # must report: 0 errors
```

Both checks run in CI (`.github/workflows/compile.yml`, `standards.yml`) on every
push and pull request.

## Release

Bump the version in lockstep across `[Info]`, `manifest.json`, `.umod.yaml`,
`README.md`, and the top `CHANGELOG.md` heading, then tag:

```bash
git tag v1.0.0 && git push --tags
```

`draft-release-on-tag.yml` drafts a GitHub release with the `.cs` and locale files
attached. Publish it and confirm it is marked **Latest**.
