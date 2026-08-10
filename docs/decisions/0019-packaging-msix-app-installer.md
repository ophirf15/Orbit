# ADR 0019 — Packaging and update lane (MSIX + App Installer)

## Status

Accepted (Phase 17)

## Context

Orbit.App already enables MSIX tooling (`EnableMsixTooling`) while keeping `WindowsPackageType=None` for the daily unpackaged loop (ADR 0003). Phase 17 needs one production install/update path. Foundation’s `deploy.github-updater@0.2.0` is a PHP shared-host ZIP flow — concepts only (GitHub Releases feed, host allowlist, version display), not desktop code.

Alternatives considered:

1. **MSIX + App Installer** — native Windows update checks via `.appinstaller`, GitHub Releases as artifact host.
2. **Velopack** (or similar unpackaged updater) — strong when corporate policy blocks sideloading; adds a second packaging story.

## Decision

**Primary long-term lane: MSIX + App Installer** (when signing + sideload trust are ready).

**Operational takeaway lane (now): Inno `Orbit-Setup-*.exe` on GitHub Releases.**

- Stable package identity documented in `packaging/PackageIdentity.md` (MSIX).
- Release workflow builds/tests, packs the wizard installer (best-effort), best-effort MSIX, checksums, GitHub Release on `v*` tags.
- In-app Check uses the public GitHub Releases API. For wizard installs, **Install update** downloads `Orbit-Setup-*.exe` from the release and launches a silent in-place upgrade (same AppId). MSIX / App Installer URLs remain fallbacks when present.
- Optional pre-update `SnapshotService` when a OneDrive sync folder is set.
- **Velopack is fallback-only** if both MSIX and the wizard lane are impractical. Do not ship two production updaters forever — graduate to MSIX when certs allow.

## Consequences

- Signing cert + corporate sideload trust remain environment work (`docs/TODO.md`); CI runs without secrets.
- Unpackaged debug loop stays the default for developers.
- App Installer background updates only apply to installs that used an `.appinstaller` descriptor.
- Wizard-installed PCs update via Settings/About → Check now without uninstalling; `%LocalAppData%\Orbit\` persists across silent upgrades.
