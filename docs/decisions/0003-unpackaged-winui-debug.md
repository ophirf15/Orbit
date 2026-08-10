# ADR 0003 — Unpackaged WinUI debug loop

## Status

Accepted (Phase 1)

## Context

Daily development must not require installer/MSIX friction. Windows App SDK templates may default toward packaged identity via WinApp CLI.

## Decision

Set `WindowsPackageType=None` on `Orbit.App` for unpackaged debug/run. Target `net9.0-windows10.0.26100.0` with Windows App SDK packages as resolved by the WinUI templates at scaffold time (currently 2.3.x). Packaging and update distribution are Phase 17.

## Consequences

Faster inner loop. CI and machines need the Windows App Runtime / SDK workloads. Production packaging and updates use MSIX + App Installer (ADR 0019); this unpackaged debug setting remains the daily default.
