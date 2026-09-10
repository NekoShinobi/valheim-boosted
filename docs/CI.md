# GitHub Actions

Workflows run in [NekoShinobi/valheim-boosted](https://github.com/NekoShinobi/valheim-boosted) for pushes to main, version tags, pull requests, and manual dispatch.

## Create a GitHub release or pre-release

Use GitHub's **release editor** for the large Markdown description box and preview. The **Prepare GitHub release** workflow builds the packages and attaches them to your saved draft; you publish after reviewing the result. GitHub's [workflow inputs](https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/trigger-a-workflow) have no multiline textarea type, while the [release editor](https://docs.github.com/en/repositories/releasing-projects-on-github/managing-releases-in-a-repository) supports writing notes ahead of publication.

1. Commit and push the intended code, matching version numbers and changelog to `main`. The workflow must be on the default branch before its **Run workflow** button appears. For the first release, the existing numeric version is `0.1.0`.
2. Open [Draft a new release](https://github.com/NekoShinobi/valheim-boosted/releases/new). Choose or create the matching tag, such as `0.1.0`, and select its target branch. Enter a title and write the description in the large **Describe this release** box. Check **This is a pre-release** for pre-alpha builds. Tags use plain `Major.Minor.Patch`, without a `v` prefix or `-alpha` suffix.
3. Click **Save draft**. You can return to edit the description later. Saving alone does not start a build.
4. Open [Actions → Prepare GitHub release](https://github.com/NekoShinobi/valheim-boosted/actions/workflows/release.yml), select **Run workflow**, use `main` for the workflow branch, and enter the saved draft's tag.
5. Wait for the full workflow to succeed. Its summary links back to Releases. The draft now contains `valheim-boosted-0.1.0.zip`, `valheim-boosted-0.1.0-plugins.zip`, and `SHA256SUMS`. Review them and click **Publish release** in the editor.

Publication makes the assets downloadable without login on the public repository and starts **Build metrics image**, which publishes `ghcr.io/nekoshinobi/valheim-boosted-metrics:0.1.0` after its checks pass. The image build is separate and may finish after the mod release is published. Both regular releases and pre-releases use this flow; neither uploads to Thunderstore automatically.

Preparation resolves the draft's target to a full commit SHA, runs the existing mod checks/build for that commit, verifies both ZIP layouts and their matching binaries, then attaches the original files with checksums. If the tag already exists, its commit wins over the draft's branch selection. If it does not, preparation creates it at the successfully built commit and pins the draft there. A moving branch therefore cannot make the published source differ from the mod build. The workflow does not bump source versions or replace the packaged `CHANGELOG.md` with your release description; write the GitHub notes as freely as you like.

Your title, description, and pre-release checkbox remain editable throughout preparation. Leave the tag, target and draft state alone until the workflow finishes. A build failure leaves the draft unpublished. Rerunning preparation replaces only its two named ZIPs and `SHA256SUMS`; other attachments are retained. Published releases are rejected. Attaching before publication also supports [immutable releases](https://docs.github.com/en/code-security/concepts/supply-chain-security/immutable-releases), where assets become locked when published.

No personal token is needed for this flow. The draft lookup and attachment jobs use the repository's `GITHUB_TOKEN` with `contents: write`; the reusable build has `contents: read`. Repository rules must permit tag creation and draft asset updates. Use code already pushed to `main`: GitHub can reject tag creation for a branch with workflow changes that the Actions token cannot authorize. A user publishing through the editor supplies the event that starts the image workflow; creating the tag with `GITHUB_TOKEN` does not trigger additional push workflows.

## Mod build

`.github/workflows/mod.yml` runs for main, version tags, pull requests, manual dispatch, and as the reusable build called by release preparation. It sets up the SDK from global.json, runs managed telemetry checks, downloads the current public dedicated server (Steam app 896660) anonymously with SteamCMD, and obtains pinned BepInEx/Jötunn references with checksum verification. It validates reviewed game-method fingerprints, runs fair-scheduler policy checks plus Harmony lifecycle/fallback and game-contract checks under Mono, builds Release, and uploads separate Thunderstore and direct plugin-install ZIP artifacts. See [compatibility checks](COMPATIBILITY.md).

The Thunderstore ZIP contains root-level manifest.json, icon.png (256×256), README.md, and CHANGELOG.md, plus our DLL/PDB under BepInEx/plugins/ValheimBoosted and optional CI reference hashes. Game/framework DLLs and decompiled sources are excluded. BepInEx/Jötunn are runtime prerequisites, not bundled copies. Steam's public branch can change; reference hashes in each CI artifact record what that build used. The local build uses the installed client snapshot, while CI exercises dedicated-server compilation. Actual dedicated-server runtime behavior still needs an in-game check.

The Mono test executables require Harmony's runtime dependencies as well as `0Harmony.dll`: `MonoMod.RuntimeDetour.dll`, `MonoMod.Utils.dll`, and `Mono.Cecil.dll`. Reference preparation extracts these from the same checksum-verified BepInEx archive. Both test projects import `tests/HarmonyRuntime.props`, which checks for the files and copies them into fresh test outputs. These dependencies remain private test/build inputs and are not added to the mod ZIP.

The first run needs access to Steam CDN/SteamCMD and Thunderstore. No Steam account secrets or private game-assembly uploads are required. SteamCMD completes a separate self-update first, then the Linux server installation is retried up to three times with ten-second delays. Each install attempt has a seven-minute timeout. A successful exit and a nonempty dedicated-server assembly are both required before compilation. Persistent Steam/CDN failures still fail the build; retries do not silently accept missing references.

## Thunderstore releases

The first package is **0.1.0 (pre-alpha)**. Thunderstore requires numeric versions without suffixes, so pre-alpha is a documentation label. The immutable package identifier is `valheim_boosted` because hyphens are not allowed; the project and plugin display name remain `valheim-boosted`.

Build locally:

```sh
python3 scripts/package-mod.py --check
./scripts/dotnet build mod/ValheimBoosted.csproj -c Release -p:RestoreLockedMode=true
python3 scripts/package-mod.py
```

The command produces two packages:

| Package | Install destination |
| --- | --- |
| `artifacts/valheim-boosted-0.1.0.zip` | Import into a mod manager or upload to Thunderstore. |
| `artifacts/valheim-boosted-0.1.0-plugins.zip` | Extract directly into `BepInEx/plugins`, or the Docker host's mounted `config/bepinex/plugins` directory. |

The direct-install ZIP contains the same DLL/PDB under `ValheimBoosted/`, with any CI reference hashes kept in that folder. It contains no Thunderstore metadata and is intended for manual installation. Both packages require BepInEx/Jötunn to be installed separately.

The workflow uses two `actions/upload-artifact` steps with `archive: false`, each uploading one ZIP unchanged; each filename becomes its artifact name. Download the individual artifact for the package you need. Older workflow downloads named `valheim-boosted-mod.zip` contain an extra wrapper: extract those once to obtain the package. Workflow downloads still require a GitHub login; attach the package to a public GitHub Release for anonymous downloads.

For subsequent releases:

1. Increase the same numeric version in `thunderstore/manifest.json`, `mod/ValheimBoosted.csproj`, `mod/Plugin.cs`, and `package.json`.
2. Prepend a `## vMajor.Minor.Patch` section to the root `CHANGELOG.md`; keep older entries below it. Update the README version labels and installation examples.
3. Keep the same Thunderstore package name and publishing Team. Keep `website_url` pointed at the project repository.
4. Use the [draft release flow above](#create-a-github-release-or-pre-release) with the matching `Major.Minor.Patch` tag, without a `v` prefix. Preparation checks the tag against the source versions before building. Direct numeric tag pushes remain supported for CI artifact/image builds. The changelog's `## vMajor.Minor.Patch` heading format is separate from Git tag names.
5. Upload the new ZIP under that same Team. Existing published versions cannot be edited, even for README-only changes.

The package uses `thunderstore/README.md` for a self-contained mod page; the root README includes developer/dashboard links for GitHub. The root changelog is copied unchanged into the ZIP. `thunderstore/icon.svg` is the editable artwork source; `icon.png` is the required 256×256 export. CI uses the checked-in PNG without an image-rendering dependency.

Workflows build artifacts/images; they do not publish to Thunderstore. No Team name or token is required to build a compatible package.

Requirements: [Creating a package](https://wiki.thunderstore.io/mods/creating-a-package), [BepInEx packaging](https://wiki.thunderstore.io/mods/packaging-your-mods), and [Updating a package](https://wiki.thunderstore.io/mods/updating-a-package).

## Dashboard image

`.github/workflows/dashboard.yml` installs the Bun version pinned in package.json, restores bun.lock, runs Svelte/TypeScript checks, backend tests, and a production UI build. A separate job builds the linux/amd64 image, starts it, and checks HTTP health, waiting-state JSON, and the index page.

On successful main/tag/manual runs and `release: published` events (including pre-releases), it publishes to `ghcr.io/<lowercase-owner>/valheim-boosted-metrics`. Pull requests only build and smoke-test. Images receive branch/SHA tags; default-branch builds also receive `latest`, and semver tags produce version tags. `latest` remains the main-branch channel: numeric release/pre-release builds do not overwrite it through automatic semver tagging. GitHub's GITHUB_TOKEN provides registry credentials with packages:write only for the image job. Repository/organization policy must allow that package write. Make package visibility appropriate for where you intend to pull it.

The image contains Bun, the compiled Svelte UI, and the small TypeScript API. It does not contain Valheim or game references. Docker is not installed on this development host, so the container build/smoke checks await a workflow runner or Docker-enabled host; the same Bun server/frontend were exercised locally.

## Action versions

Latest stable releases were checked on 2026-09-09 against each action's GitHub latest-release page, and tag commits verified with git ls-remote. Workflows pin those immutable commit IDs, with version comments. Dependabot proposes subsequent action updates weekly; it also checks npm and Docker dependencies. Update this audit table when applying those PRs.

`actions/download-artifact` was added on 2026-09-10 using the latest stable v8.0.1 and its [release commit](https://github.com/actions/download-artifact/commit/3e5f45b2cfb9172054b4087a40e8e0b5a5461e7c). Release preparation uses `skip-decompress: true` for the existing `archive: false` uploads and fails on digest mismatch.

| Action | Verified release |
| --- | --- |
| actions/checkout | [v7.0.1](https://github.com/actions/checkout/releases/tag/v7.0.1) |
| actions/setup-dotnet | [v6.0.0](https://github.com/actions/setup-dotnet/releases/tag/v6.0.0) |
| actions/upload-artifact | [v7.0.1](https://github.com/actions/upload-artifact/releases/tag/v7.0.1) |
| actions/download-artifact | [v8.0.1](https://github.com/actions/download-artifact/releases/tag/v8.0.1) |
| oven-sh/setup-bun | [v2.2.0](https://github.com/oven-sh/setup-bun/releases/tag/v2.2.0) |
| docker/setup-buildx-action | [v4.3.0](https://github.com/docker/setup-buildx-action/releases/tag/v4.3.0) |
| docker/build-push-action | [v7.3.0](https://github.com/docker/build-push-action/releases/tag/v7.3.0) |
| docker/login-action | [v4.6.0](https://github.com/docker/login-action/releases/tag/v4.6.0) |
| docker/metadata-action | [v6.2.0](https://github.com/docker/metadata-action/releases/tag/v6.2.0) |
