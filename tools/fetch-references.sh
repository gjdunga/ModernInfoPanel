#!/usr/bin/env bash
#
# fetch-references.sh — download the reference assemblies needed to compile
# ModernInfoPanel out-of-server (Linux/macOS).
#
# It installs the (free, dedicated-server) Rust server via SteamCMD, overlays the
# latest Oxide.Rust build, and leaves the managed assemblies under
#   references/RustDedicated_Data/Managed
# which build/ModernInfoPanel.csproj references by default.
#
# The game files are proprietary and intentionally git-ignored — never commit them.
#
# Usage:
#   tools/fetch-references.sh [--managed-only] [--dir DIR]
#
#   --managed-only   After install, keep only RustDedicated_Data/Managed
#                    (drops install from ~8 GB to a few dozen MB).
#   --dir DIR        Reference root (default: ./references).
set -euo pipefail

REF_DIR="references"
MANAGED_ONLY=0
RUST_APPID=258550

while [[ $# -gt 0 ]]; do
  case "$1" in
    --managed-only) MANAGED_ONLY=1; shift ;;
    --dir) REF_DIR="$2"; shift 2 ;;
    -h|--help) sed -n '2,30p' "$0"; exit 0 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

for bin in curl tar; do
  command -v "$bin" >/dev/null 2>&1 || { echo "error: '$bin' is required" >&2; exit 1; }
done

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
REF_DIR="$(mkdir -p "$ROOT/$REF_DIR" && cd "$ROOT/$REF_DIR" && pwd)"
STEAM_DIR="$REF_DIR/steamcmd"
SERVER_DIR="$REF_DIR"

echo "==> Reference root: $REF_DIR"

# --- SteamCMD -------------------------------------------------------------
if [[ ! -x "$STEAM_DIR/steamcmd.sh" ]]; then
  echo "==> Downloading SteamCMD"
  mkdir -p "$STEAM_DIR"
  curl -sSL https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz \
    | tar -xz -C "$STEAM_DIR"
fi

# --- Rust dedicated server ------------------------------------------------
echo "==> Installing/updating Rust dedicated server (appid $RUST_APPID)"
"$STEAM_DIR/steamcmd.sh" \
  +force_install_dir "$SERVER_DIR" \
  +login anonymous \
  +app_update "$RUST_APPID" validate \
  +quit

# --- Oxide.Rust overlay ---------------------------------------------------
echo "==> Overlaying latest Oxide.Rust"
OXIDE_URL="https://umod.org/games/rust/download?tag=public"
OXIDE_ZIP="$REF_DIR/Oxide.Rust-linux.zip"
if curl -sSL -o "$OXIDE_ZIP" "$OXIDE_URL"; then
  if command -v unzip >/dev/null 2>&1; then
    unzip -o -q "$OXIDE_ZIP" -d "$SERVER_DIR"
  else
    echo "warning: 'unzip' not found; skipping Oxide overlay (Carbon users can skip this)" >&2
  fi
  rm -f "$OXIDE_ZIP"
else
  echo "warning: could not download Oxide.Rust; the game's own assemblies will be used" >&2
fi

MANAGED="$SERVER_DIR/RustDedicated_Data/Managed"
[[ -d "$MANAGED" ]] || { echo "error: Managed/ not found at $MANAGED" >&2; exit 1; }

# --- Trim to Managed only -------------------------------------------------
if [[ "$MANAGED_ONLY" -eq 1 ]]; then
  echo "==> Trimming to RustDedicated_Data/Managed only"
  TMP="$REF_DIR/.managed.tmp"
  rm -rf "$TMP"; mkdir -p "$TMP/RustDedicated_Data"
  mv "$MANAGED" "$TMP/RustDedicated_Data/Managed"
  find "$REF_DIR" -mindepth 1 -maxdepth 1 ! -name "$(basename "$TMP")" -exec rm -rf {} +
  mv "$TMP/RustDedicated_Data" "$REF_DIR/RustDedicated_Data"
  rm -rf "$TMP"
fi

echo "==> Done. Managed assemblies: $REF_DIR/RustDedicated_Data/Managed"
echo "    Build with:  make build   (or: dotnet build build/ModernInfoPanel.csproj -c Release)"
