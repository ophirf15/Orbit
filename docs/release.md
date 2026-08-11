# Orbit release & install

Daily debug stays unpackaged (`WindowsPackageType=None`). Takeaway installs use the **Inno wizard**; updates for those installs go through **GitHub Releases → Orbit-Setup-*.exe**.

## Clean PC install (wizard — recommended takeaway)

1. On a build machine: `.\scripts\pack-installer.ps1` (or download `Orbit-Setup-*.exe` from [GitHub Releases](https://github.com/ophirf15/Orbit/releases)).
2. Copy the setup exe to the target PC if needed.
3. Run the setup wizard (Next → install folder → Finish). Needs admin once.
4. Launch Orbit from Start / Desktop. Data lives under `%LocalAppData%\Orbit\`.
5. Optional Hermes: Settings → **Connect Hermes** (syncs `docs/hermes` skills + cron + MCP into `%LocalAppData%\hermes\`). Native Hermes gateway required separately.

Includes self-contained `Orbit.App.exe` + `Orbit.Core.Host.exe` (x64), **`orbit-mcp/`** (`Orbit.Mcp.exe` Hermes stdio bridge), **`outlook-launcher/`** (Classic Outlook Send to Orbit ribbon), **and** `docs/hermes/` (SOUL, Orbit skills, portable cron/scripts). Windows 10 1809+ / Windows 11.

Installer also stages `%LocalAppData%\Orbit\orbit-mcp\` and `%LocalAppData%\Orbit\OutlookLauncher\`. Connect re-syncs MCP from `{app}\orbit-mcp` on upgrade. Outlook add-in registration is per-user via **Settings → Classic Outlook add-in → Install / Update**.

If Hermes shows `Skill(s) not found … pulse-refresh`, the install is missing `docs/hermes` beside the EXE (old pack) or Connect was never re-run after upgrading — install a current setup and Connect again.

If Hermes MCP test says **Orbit: Connection closed**, the install is missing `orbit-mcp` (old pack) — install a current setup, confirm `%LocalAppData%\Orbit\orbit-mcp\Orbit.Mcp.exe` exists, then Connect again / `/reload-mcp`.

## Updating an installed wizard build (GitHub)

On the **dev** machine, publish a newer release (see [Cutting a release](#cutting-a-release) — preferred path is Actions → **release** with a version).

On the **work** PC (already installed):

1. About or Settings → Updates → **Check now**.
2. If a newer release has `Orbit-Setup-*.exe`, choose **Install update**.
3. Orbit snapshots the DB when a sync folder is set, downloads the setup to `%TEMP%\OrbitUpdates\`, and launches a **silent in-place upgrade** (same AppId — no uninstall). Approve UAC if prompted.
4. `%LocalAppData%\Orbit\` is left alone.

First install on a machine still needs one wizard run (USB or download). After that, in-app updates are enough.

Day-to-day editing: unpackaged `.\build.ps1` / F5 — do not reinstall for every tweak.

## Clean PC install (MSIX / App Installer)

Long-term lane when signing/sideload is ready (ADR 0019). Sideload trust is heavier on a fresh PC than the wizard above:

1. Enable sideloading (Settings → System → For developers → Developer Mode, or your org’s sideload policy).
2. Trust the signing certificate used to sign the `.msix` (double-click `.cer` → Install → Local Machine → Trusted People / Trusted Root as your policy requires). Without a purchased cert this step is manual — see `docs/TODO.md`.
3. Download the release `.msix` (or `.appinstaller`) from GitHub Releases for `ophirf15/Orbit`.
4. Prefer double-clicking `Orbit.appinstaller` so Windows App Installer registers update checks. Direct `.msix` install also works for a one-shot sideload.
5. Launch Orbit from Start. Core Host should start with the app (same as unpackaged). Data lives under `%LocalAppData%\Orbit\`.

## Unpackaged vs packaged host launch

| Mode | How | Host |
|---|---|---|
| Daily debug | `.\build.ps1` then F5 / `dotnet run` on `Orbit.App` with `WindowsPackageType=None` | App launches Core Host via `CoreHostLauncher` |
| Wizard install | Inno Setup (`Orbit-Setup-*.exe`) | Host exe beside App in Program Files |
| MSIX | App Installer / `.msix` sideload | Same launcher; package identity present |

If Host fails to start, check About → Core Host status and Settings → Background host enabled.

## Cutting a release

### Preferred: Actions → release (set version, Run)

1. Push the commit you want to ship (any branch is fine; the release is pinned to that commit).
2. GitHub → **Actions** → **release** → **Run workflow**.
3. Enter **version** without a leading `v` (e.g. `0.1.2`). Optionally mark **draft**.
4. Leave **skip_pack** as `false`.
5. Run. CI builds, tests, packs `Orbit-Setup-<version>.exe` (version baked into the binaries), and creates a GitHub Release at tag `v<version>` with that EXE attached.
6. Confirm under [Releases](https://github.com/ophirf15/Orbit/releases) that `Orbit-Setup-*.exe` is present. Draft releases are **not** visible to in-app `/releases/latest`.

You do **not** need to bump `Orbit.App.csproj` for this path — the workflow passes `-Version` into publish. Keeping the csproj roughly in sync is still good for local F5 / About while developing.

### Alternatives

- **Tag push:** `git tag v0.1.2 && git push origin v0.1.2` — same workflow; version is taken from the tag.
- **Local:** `.\scripts\publish-github-release.ps1 -Version 0.1.2` (needs `gh auth login`).
- **Pack only:** `.\scripts\pack-installer.ps1 -Version 0.1.2`.

Optional secrets (never commit plaintext certs):

- `SIGNING_CERT_BASE64` — base64 `.pfx`
- `SIGNING_CERT_PASSWORD` — PFX password

## In-app updates

About (and Settings → Updates) call the public GitHub Releases API and compare semver against the assembly `<Version>`. Apply prefers **Orbit-Setup-*.exe** (download + silent upgrade), then `.appinstaller` / `.msix`, then the release page. Pre-update OneDrive DB snapshot runs when a sync folder is configured.
