# Security Policy

## Reporting a vulnerability

Please report security issues **privately**, not as a public issue:

- Open a GitHub **security advisory** (Security tab → "Report a vulnerability"), or
- Contact the maintainer via the email on the [gjdunga](https://github.com/gjdunga) profile.

Include the plugin version, Oxide/uMod build, Rust server build, reproduction
steps, and impact. You will receive an acknowledgement within a few days.

## Scope

This plugin runs inside the Rust server process via Oxide/uMod. Relevant concerns
include command-permission bypass, unsanitised player input reaching logs or
config, and any path that lets a non-admin change protected state. Please do not
run denial-of-service tests against servers you do not own.

## Supported versions

The latest released version receives fixes. Older versions are not maintained;
please update before reporting.

## Verifying signatures

Releases, tags, and commits may be signed with the maintainer's OpenPGP key. The
public key is committed at [`keys/gabriel-dungan.asc`](keys/gabriel-dungan.asc):

- **Owner:** Gabriel Dungan `<gjdunga@gmail.com>`
- **Primary key fingerprint:** `EAC0 A2AE 65CC 6C97 62DD 6AF0 6877 8437 61D5 C6E6`
- **Signing subkey:** `C89A 1C77 FC8F 426D 62A2 377F 2036 D16F D755 671B`

Import it and verify, e.g.:

```bash
gpg --import keys/gabriel-dungan.asc
git verify-tag  vX.Y.Z      # a signed release tag
git verify-commit HEAD      # a signed commit
gpg --verify ModernInfoPanel.cs.asc ModernInfoPanel.cs   # a detached signature
```

A good signature from the fingerprint above confirms the artifact is from the
maintainer. This file is the **public** key only; it cannot create signatures.

