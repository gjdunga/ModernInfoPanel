#requires -version 5
<#
.SYNOPSIS
  Download the reference assemblies needed to compile ModernInfoPanel
  out-of-server (Windows).

.DESCRIPTION
  Installs the (free, dedicated-server) Rust server via SteamCMD, overlays the
  latest Oxide.Rust build, and leaves the managed assemblies under
  references\RustDedicated_Data\Managed which build\ModernInfoPanel.csproj
  references by default.

  The game files are proprietary and intentionally git-ignored — never commit them.

.PARAMETER ManagedOnly
  After install, keep only RustDedicated_Data\Managed (much smaller).

.PARAMETER Dir
  Reference root (default: .\references).

.EXAMPLE
  tools\fetch-references.ps1 -ManagedOnly
#>
[CmdletBinding()]
param(
  [switch] $ManagedOnly,
  [string] $Dir = "references"
)

$ErrorActionPreference = "Stop"
$RustAppId = 258550

$root      = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$refDir    = Join-Path $root $Dir
$steamDir  = Join-Path $refDir "steamcmd"
$serverDir = $refDir
New-Item -ItemType Directory -Force -Path $refDir | Out-Null

Write-Host "==> Reference root: $refDir"

# --- SteamCMD -------------------------------------------------------------
$steamExe = Join-Path $steamDir "steamcmd.exe"
if (-not (Test-Path $steamExe)) {
  Write-Host "==> Downloading SteamCMD"
  New-Item -ItemType Directory -Force -Path $steamDir | Out-Null
  $zip = Join-Path $steamDir "steamcmd.zip"
  Invoke-WebRequest -Uri "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip" -OutFile $zip
  Expand-Archive -Path $zip -DestinationPath $steamDir -Force
  Remove-Item $zip -Force
}

# --- Rust dedicated server ------------------------------------------------
Write-Host "==> Installing/updating Rust dedicated server (appid $RustAppId)"
& $steamExe +force_install_dir $serverDir +login anonymous +app_update $RustAppId validate +quit

# --- Oxide.Rust overlay ---------------------------------------------------
Write-Host "==> Overlaying latest Oxide.Rust"
$oxideZip = Join-Path $refDir "Oxide.Rust-win.zip"
try {
  Invoke-WebRequest -Uri "https://umod.org/games/rust/download?tag=public" -OutFile $oxideZip
  Expand-Archive -Path $oxideZip -DestinationPath $serverDir -Force
  Remove-Item $oxideZip -Force
} catch {
  Write-Warning "Could not download/extract Oxide.Rust; the game's own assemblies will be used."
}

$managed = Join-Path $serverDir "RustDedicated_Data\Managed"
if (-not (Test-Path $managed)) { throw "Managed\ not found at $managed" }

# --- Trim to Managed only -------------------------------------------------
if ($ManagedOnly) {
  Write-Host "==> Trimming to RustDedicated_Data\Managed only"
  $tmp = Join-Path $refDir ".managed.tmp"
  if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
  New-Item -ItemType Directory -Force -Path (Join-Path $tmp "RustDedicated_Data") | Out-Null
  Move-Item $managed (Join-Path $tmp "RustDedicated_Data\Managed")
  Get-ChildItem -Path $refDir -Force | Where-Object { $_.Name -ne ".managed.tmp" } |
    Remove-Item -Recurse -Force
  Move-Item (Join-Path $tmp "RustDedicated_Data") (Join-Path $refDir "RustDedicated_Data")
  Remove-Item $tmp -Recurse -Force
}

Write-Host "==> Done. Managed assemblies: $refDir\RustDedicated_Data\Managed"
Write-Host "    Build with:  dotnet build build\ModernInfoPanel.csproj -c Release"
