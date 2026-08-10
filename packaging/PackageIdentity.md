# Package identity placeholders

Stable identity is required for MSIX upgrades to replace an existing install instead of side-by-side.

| Field | Current placeholder | Notes |
|---|---|---|
| Identity `Name` | `749007A4-D624-444E-BA20-676468111479` | From `src/Orbit.App/Package.appxmanifest`. Keep fixed across releases. |
| Identity `Publisher` | `CN=AppPublisher` | Must match the signing certificate subject once real signing lands. |
| Identity `Version` | `1.0.0.0` (scaffold) | Bump on each release; App Installer `Version` must match. |
| DisplayName | `Orbit.App` | Cosmetic; may become `Orbit` later. |

## Signing (not in repo)

- Do **not** commit `.pfx` / passwords.
- CI expects GitHub Actions secrets: `SIGNING_CERT_BASE64`, `SIGNING_CERT_PASSWORD` (optional until purchased).
- Until a trusted cert is available, sideload requires developer mode / self-signed trust — see `docs/TODO.md`.

## App Installer

Template: `packaging/Orbit.appinstaller`. Release automation substitutes URIs + version.
