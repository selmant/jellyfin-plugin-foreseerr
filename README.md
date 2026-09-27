<p align="center">
<img src="Foreseerr.Jellyfin/thumb.png" alt="Foreseerr for Jellyfin" width="480">
</p>

# Foreseerr for Jellyfin

> [!WARNING]
> **Alpha, in active development.** Anything can change between releases: settings, data, URLs, and behavior. There is **no backward compatibility** between plugin versions, and no upgrade path is promised. If an update misbehaves, uninstall the plugin, delete `plugins/configurations/Foreseerr` in Jellyfin's data folder, and install again.

A third-party Jellyfin plugin that runs [Foreseerr](https://github.com/selmant/foreseerr) (requests, discovery, and a release calendar, built on Seerr) inside Jellyfin. It starts a bundled Foreseerr server on `127.0.0.1`, serves it at `<Jellyfin>/Foreseerr/`, and signs users in with their Jellyfin account.

## Install

1. Jellyfin → Dashboard → Plugins → Repositories → add
   `https://selmant.github.io/jellyfin-plugin-foreseerr/manifest.json`
2. Install **Foreseerr** from the catalog and restart Jellyfin. Jellyfin 10.11 and 12 each get their own build.
3. Open Foreseerr as a Jellyfin administrator first; that account becomes the Foreseerr owner.
4. Optional: install [File Transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation) for a Foreseerr button in the Jellyfin Web header.

Manual install: extract `foreseerr-jellyfin-10.11.zip` or `foreseerr-jellyfin-12.zip` from a [release](https://github.com/selmant/jellyfin-plugin-foreseerr/releases) into Jellyfin's `plugins/Foreseerr/` folder and restart Jellyfin.

Requirements: Jellyfin 10.11 or newer on Linux x64, Linux arm64, or Windows x64, used through Jellyfin Web in a browser. The official mobile and TV apps cannot open Foreseerr. Behind a reverse proxy, forward `/Foreseerr` and `/ForeseerrPlugin`, and add the proxy to Jellyfin's Known Proxies. The [Foreseerr plugin guide](https://selmant.github.io/foreseerr/using-seerr/jellyfin-plugin) covers the user side in more detail.

Report problems with the plugin here, and problems with Foreseerr itself in the [Foreseerr repository](https://github.com/selmant/foreseerr/issues).

## Versions

The plugin has its own version, `ReleaseVersion` in `Build.props` (currently `0.1.0-alpha.1`). Each release bundles the Foreseerr version in `foreseerr.version` (currently `v0.11.0`), compiled with its `/Foreseerr` base path.

Each release ships one build per Jellyfin ABI:

| Jellyfin | Archive                        | Framework | `X.Y.Z` release | `X.Y.Z-alpha.N` release |
| -------- | ------------------------------ | --------- | --------------- | ----------------------- |
| 10.11.x  | `foreseerr-jellyfin-10.11.zip` | net9.0    | `X.Y.Z.9000`    | `X.Y.Z.1N0`             |
| 12.x     | `foreseerr-jellyfin-12.zip`    | net10.0   | `X.Y.Z.9001`    | `X.Y.Z.1N1`             |

Jellyfin versions are four numbers, so the revision carries the prerelease and the ABI: `alpha.N` is `1000 + 10N`, `beta.N` is `2000 + 10N`, `rc.N` is `3000 + 10N`, and a final release is `9000`, plus `0` for 10.11 or `1` for 12. For example, `0.1.0-alpha.2` for Jellyfin 12 is `0.1.0.1021`. Testers on an alpha therefore auto-update to later alphas, betas, and the final release. Other prerelease names, or `N` of 100 or more, fail the build.

Jellyfin treats `targetAbi` as a minimum, so a 12 server also accepts the 10.11 build. The higher revision makes installs and auto-updates pick the matching build. `Build.props` owns this mapping; `scripts/merge-manifests.mjs` refuses to publish two builds with the same version. The repository manifest lists every release, so a server keeps getting the newest build its Jellyfin version accepts after an ABI is dropped.

## Build

```bash
mise install                 # Bun and the .NET 10 SDK (also builds net9.0)
scripts/sidecar.sh           # clone Foreseerr at foreseerr.version into .foreseerr/ and compile the sidecars
scripts/build.sh 10.11       # -> dist/jellyfin-10.11/
scripts/build.sh 12          # -> dist/jellyfin-12/
bun scripts/merge-manifests.mjs  # -> dist/release/ (zips + repository manifest)
```

For a quicker local build, limit the sidecar targets: `scripts/sidecar.sh linux-x64` then `scripts/build.sh 12 linux-x64`.

To build against a local Foreseerr checkout instead (for changes on both sides), set `FORESEERR_DIR` for both scripts: `FORESEERR_DIR=../foreseerr scripts/sidecar.sh linux-x64 && FORESEERR_DIR=../foreseerr scripts/build.sh 12 linux-x64`. `FORESEERR_REF=develop` builds another Foreseerr tag or branch.

## Test

```bash
dotnet test Foreseerr.Jellyfin.Tests/Foreseerr.Jellyfin.Tests.csproj -p:JellyfinTarget=10.11
dotnet test Foreseerr.Jellyfin.Tests/Foreseerr.Jellyfin.Tests.csproj -p:JellyfinTarget=12
bun scripts/prove.mjs 10.11
bun scripts/prove.mjs 12
```

The 10.11 tests need the .NET 9 runtime (or `DOTNET_ROLL_FORWARD=Major`). The proof script uses Docker: it installs the built plugin into a disposable Jellyfin served under `/jellyfin`, then checks sidecar readiness, the subpath SPA and assets, SSO, CSRF, mint isolation, logout, and Jellyfin token revocation. Set `FORESEERR_TEST_FT_ARCHIVE` to a [File Transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation) release zip to also check the header-button injection.

CI runs both ABIs on every push and pull request, and weekly against Foreseerr `develop`. Foreseerr's own pull requests also run these proofs against this repository's `main`.

## Local test environment

`bun run dev` runs a persistent Jellyfin in Docker with the current plugin build and [File Transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation) installed:

```bash
bun run dev up 12                      # Jellyfin 12.1 on http://127.0.0.1:8096/web/
bun run dev up 10.11 --base /jellyfin  # Jellyfin 10.11.11 on http://127.0.0.1:8097/jellyfin/web/
bun run dev up 12 --build              # rebuild sidecar + plugin, then reinstall
bun run dev logs 12                    # Jellyfin and sidecar logs
bun run dev down 12                    # stop, keep data
bun run dev reset 12                   # stop and delete data
```

The first `up` completes the Jellyfin setup wizard and creates `admin` / `foreseerr` (administrator), `viewer` / `viewer` (regular user), and empty Movies and Shows libraries. Sign in to Jellyfin Web, then use the header button or open `<base>/Foreseerr/` directly. Drop media into `.dev/jellyfin-<abi>/media/{movies,shows}`. Every `up` reinstalls the plugin from `dist/` and keeps the Jellyfin and Foreseerr data in `.dev/`. Jellyfin listens on `127.0.0.1` unless you pass `--bind 0.0.0.0`. Other options: `--port`, `--image jellyfin/jellyfin:<tag>`, `--no-file-transformation`. `--build` honors `FORESEERR_DIR`.

## Foreseerr in plugin mode

The sidecar reports `pluginMode` in `/api/v1/settings/public`, and the web app adapts:

- **Sign-in.** Foreseerr has no sign-in form. The user menu's **Sign Out** becomes **Back to Jellyfin**, because the Jellyfin login is the session. If the Jellyfin session ends, `/login` offers **Open Jellyfin** and **Try Again**.
- **Settings → Jellyfin.** The address, port, API key, and URL base fields are replaced by a note that the plugin manages them, with a link to the plugin page. Libraries and scans stay.
- **Settings → Users.** The login method toggles are hidden. **Enable New Jellyfin Sign-In** still decides whether Jellyfin users who were not imported can open Foreseerr.
- **Settings → Network.** **Enable Proxy Support** and **Enable CSRF Protection** are hidden; the plugin sets both.
- **Users.** **Create Local User** is hidden, since local users cannot sign in.
- **Profile settings.** The **Password** tab and **Web Push** are hidden. The admin **Web Push** agent tab is hidden too.
- **About** says Foreseerr runs inside Jellyfin and is updated from the Jellyfin dashboard, and the sidebar version badge reads **Foreseerr for Jellyfin**.

A Jellyfin user that Foreseerr refuses (the first user is not an administrator, or new sign-ins are off) sees Foreseerr's reason in the header button alert, the sign-in page, and the plugin page instead of a generic outage message.

This part lives in Foreseerr (`FORESEERR_PLUGIN=1`); this repository holds the Jellyfin side.

## Settings

The dashboard page shows the sidecar status, the direct link, and one setting: **Public Jellyfin URL**, the address used for links in Foreseerr notifications. The origin (`https://jellyfin.example.com`) is enough; the plugin appends Jellyfin's base URL. When it is empty, the plugin uses an `all=` or `external=` entry from Jellyfin's Published Server URIs. With neither, Foreseerr keeps the Application URL and Jellyfin external URL set in its own settings, and "Play on Jellyfin" links fall back to same-origin paths. Clearing the plugin field removes only values the plugin set.

Everything else comes from Jellyfin on each sidecar start: server name, loopback address, base URL, API key, libraries, locale, and the first administrator. If Better Trakt is loaded and Foreseerr has no direct Trakt app, Trakt actions default to Better Trakt. Radarr, Sonarr, notifications, and other integrations are configured in Foreseerr.

## Security model

- The sidecar binds loopback only and rejects every request without the per-install shared secret, including health checks. The secret and the Jellyfin API key are stored in the plugin XML but are not sent to the dashboard page.
- `POST /Foreseerr/sso` (Jellyfin-authenticated) mints a Foreseerr session over a loopback call carrying that secret. The browser only gets an opaque `HttpOnly`, `SameSite=Strict` ticket cookie scoped to `<base>/Foreseerr`; Jellyfin tokens and the sidecar session cookie never reach JavaScript.
- Each Jellyfin login holds one ticket, and signing in again rotates it. A user can hold tickets for 10 logins at once; older ones are dropped, and other users are never affected. Tickets expire after 8 hours.
- Pages and API requests revalidate the bound Jellyfin token every time, so Jellyfin logout, token revocation, or disabling the user ends the Foreseerr session. Static files and images reuse a validation for up to 30 seconds.
- A page request without a ticket gets a sign-in page. It reads this server's entry from Jellyfin Web's `jellyfin_credentials`, calls `/Foreseerr/sso`, and reloads the requested path, so notification links and bookmarks work. It runs under a nonce-based CSP.
- Foreseerr's own CSRF middleware is off in plugin mode. The proxy instead requires unsafe methods to be same-origin (`Sec-Fetch-Site`, falling back to `Origin`), strips browser credential and forwarding headers, and never exposes the mint endpoint.
- The first plugin login must be a Jellyfin administrator. On every sign-in, Jellyfin administrators get Foreseerr's ADMIN permission. If Jellyfin later removes administrator, the plugin takes back only an ADMIN it granted (the user gets back their other permissions, or the defaults); ADMIN granted in Foreseerr stays. Settings record which grants and URLs came from the plugin.

## Limits

- Jellyfin Web (desktop) is the supported UI. Android TV and the official mobile apps do not load this SPA.
- Without [File Transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation), there is no header button; users open `<base>/Foreseerr/` directly (the dashboard page shows the link).
- Web push notifications are off under the plugin's base path.
- Sidecar binaries exist for linux-x64, linux-arm64, and windows-x64 only.
- Reverse proxies must forward `/Foreseerr` and `/ForeseerrPlugin`. Configure Jellyfin's Known Proxies so it sees HTTPS; otherwise the ticket cookie is not marked `Secure`.
- WebSocket upgrades are not proxied. Foreseerr does not use them.

## Known issues (alpha)

- The first start downloads the anime mapping packs and writes them in one database transaction. Foreseerr can stop answering for a minute or two while that runs, and again when a pack changes upstream. This affects every Foreseerr install, not only the plugin.

## Releasing

1. Set `ReleaseVersion` in `Build.props` and add `release-notes/<version>.md`. Keep the alpha warning while the plugin is in alpha. Point `foreseerr.version` at a released Foreseerr tag.
2. Tag the commit `v<version>` and push the tag.
3. `.github/workflows/release.yml` checks that the tag matches `ReleaseVersion`, runs the tests, builds and proves both ABIs, and publishes the GitHub release (a prerelease for `-alpha`, `-beta`, and `-rc` versions) titled `Foreseerr for Jellyfin <version> (Foreseerr <version>)`.
4. It then runs `.github/workflows/pages.yml`, which rebuilds the repository manifest from every release, and waits until `https://selmant.github.io/jellyfin-plugin-foreseerr/manifest.json` lists the new version.
5. On a test Jellyfin, install from that repository and check the header button, sign-in as an administrator and as a regular user, and the plugin page.

## Remaining work

- [ ] Exercise a real TLS-terminating reverse proxy.
- [ ] Automate the browser flow (header button, click-through, dashboard page, plugin-mode settings). It was checked in headless Chromium on 10.11.11 (under `/jellyfin`) and 12.1; the proofs are HTTP-only.
- [ ] Separate stable and unstable repository manifests once there is a stable release.

Out of scope: Android TV / official apps, the official Jellyfin catalog, password replay.

## License

MIT. Foreseerr itself is MIT-licensed and developed at [selmant/foreseerr](https://github.com/selmant/foreseerr).
