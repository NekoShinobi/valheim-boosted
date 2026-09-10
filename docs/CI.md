# GitHub Actions

Workflows run in [NekoShinobi/valheim-boosted](https://github.com/NekoShinobi/valheim-boosted) for pushes to main, version tags, pull requests, and manual dispatch.

## Mod build

`.github/workflows/mod.yml` runs for main, version tags, pull requests, and manual dispatch. It sets up the SDK from global.json, runs managed telemetry checks, downloads the current public dedicated server (Steam app 896660) anonymously with SteamCMD, and obtains pinned BepInEx/Jötunn references with checksum verification. It builds Release and uploads a ZIP artifact.

The Thunderstore ZIP contains root-level manifest.json, icon.png (256×256), README.md, and CHANGELOG.md, plus our DLL/PDB under BepInEx/plugins/ValheimBoosted and optional CI reference hashes. Game/framework DLLs and decompiled sources are excluded. BepInEx/Jötunn are runtime prerequisites, not bundled copies. Steam's public branch can change; reference hashes in each CI artifact record what that build used. The local build uses the installed client snapshot, while CI exercises dedicated-server compilation. Actual dedicated-server runtime behavior still needs an in-game check.

The first run needs access to Steam CDN/SteamCMD and Thunderstore. No Steam account secrets or private game-assembly uploads are required. Network outages can fail that download step.

## Thunderstore releases

The first package is **0.1.0 (pre-alpha)**. Thunderstore requires numeric versions without suffixes, so pre-alpha is a documentation label. The immutable package identifier is `valheim_boosted` because hyphens are not allowed; the project and plugin display name remain `valheim-boosted`.

Build locally:

```sh
python3 scripts/package-mod.py --check
./scripts/dotnet build mod/ValheimBoosted.csproj -c Release -p:RestoreLockedMode=true
python3 scripts/package-mod.py
```

The result is `artifacts/valheim-boosted-0.1.0.zip`. Import that ZIP into a mod manager for testing, or upload it to Thunderstore when ready. When downloading a GitHub workflow artifact, extract the outer artifact archive first and upload the contained mod ZIP.

For subsequent releases:

1. Increase the same numeric version in `thunderstore/manifest.json`, `mod/ValheimBoosted.csproj`, `mod/Plugin.cs`, and `package.json`.
2. Prepend a `## vMajor.Minor.Patch` section to the root `CHANGELOG.md`; keep older entries below it. Update the README version labels and installation examples.
3. Keep the same Thunderstore package name and publishing Team. Keep `website_url` pointed at the project repository.
4. Build and test, then create the matching `vMajor.Minor.Patch` Git tag. Both workflows reject a mismatched tag; the mod workflow validates packaging and uploads only the ZIP it just built.
5. Upload the new ZIP under that same Team. Existing published versions cannot be edited, even for README-only changes.

The package uses `thunderstore/README.md` for a self-contained mod page; the root README includes developer/dashboard links for GitHub. The root changelog is copied unchanged into the ZIP. `thunderstore/icon.svg` is the editable artwork source; `icon.png` is the required 256×256 export. CI uses the checked-in PNG without an image-rendering dependency.

Workflows build artifacts/images; they do not publish to Thunderstore. No Team name or token is required to build a compatible package.

Requirements: [Creating a package](https://wiki.thunderstore.io/mods/creating-a-package), [BepInEx packaging](https://wiki.thunderstore.io/mods/packaging-your-mods), and [Updating a package](https://wiki.thunderstore.io/mods/updating-a-package).

## Dashboard image

`.github/workflows/dashboard.yml` installs the Bun version pinned in package.json, restores bun.lock, runs Svelte/TypeScript checks, backend tests, and a production UI build. A separate job builds the linux/amd64 image, starts it, and checks HTTP health, waiting-state JSON, and the index page.

On successful main/tag/manual runs, it publishes to `ghcr.io/<lowercase-owner>/valheim-boosted-metrics`. Pull requests only build and smoke-test. Images receive branch/SHA tags; default-branch builds also receive latest, and semver tags produce version tags. GitHub's GITHUB_TOKEN provides registry credentials with packages:write only for the image job. Repository/organization policy must allow that package write. Make package visibility appropriate for where you intend to pull it.

The image contains Bun, the compiled Svelte UI, and the small TypeScript API. It does not contain Valheim or game references. Docker is not installed on this development host, so the container build/smoke checks await a workflow runner or Docker-enabled host; the same Bun server/frontend were exercised locally.

## Action versions

Latest stable releases were checked on 2026-09-09 against each action's GitHub latest-release page, and tag commits verified with git ls-remote. Workflows pin those immutable commit IDs, with version comments. Dependabot proposes subsequent action updates weekly; it also checks npm and Docker dependencies. Update this audit table when applying those PRs.

| Action | Verified release |
| --- | --- |
| actions/checkout | [v7.0.1](https://github.com/actions/checkout/releases/tag/v7.0.1) |
| actions/setup-dotnet | [v6.0.0](https://github.com/actions/setup-dotnet/releases/tag/v6.0.0) |
| actions/upload-artifact | [v7.0.1](https://github.com/actions/upload-artifact/releases/tag/v7.0.1) |
| oven-sh/setup-bun | [v2.2.0](https://github.com/oven-sh/setup-bun/releases/tag/v2.2.0) |
| docker/setup-buildx-action | [v4.3.0](https://github.com/docker/setup-buildx-action/releases/tag/v4.3.0) |
| docker/build-push-action | [v7.3.0](https://github.com/docker/build-push-action/releases/tag/v7.3.0) |
| docker/login-action | [v4.6.0](https://github.com/docker/login-action/releases/tag/v4.6.0) |
| docker/metadata-action | [v6.2.0](https://github.com/docker/metadata-action/releases/tag/v6.2.0) |
