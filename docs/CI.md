# GitHub Actions

Workflows run in [NekoShinobi/valheim-boosted](https://github.com/NekoShinobi/valheim-boosted) for pushes to main, version tags, pull requests, and manual dispatch.

## Create a GitHub release or pre-release

Use GitHub's **release editor** for the large Markdown description box and preview. The **Prepare GitHub release** workflow uses the draft's tag and description to commit version and changelog updates, build the packages, and attach them to your saved draft; you publish after reviewing the result. GitHub's [workflow inputs](https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/trigger-a-workflow) have no multiline textarea type, while the [release editor](https://docs.github.com/en/repositories/releasing-projects-on-github/managing-releases-in-a-repository) supports writing notes ahead of publication.

1. Commit and push the intended code to `main`. Leave the version files at their current consistent version; preparation updates them automatically. The workflow must be on the default branch before its **Run workflow** button appears.
2. Open [Draft a new release](https://github.com/NekoShinobi/valheim-boosted/releases/new). Enter an unused version tag (`0.1.0` for the initial release, then a higher version such as `0.2.0`) and select the target **branch**, normally `main`. Enter a title and write the description in the large **Describe this release** box. Check **This is a pre-release** for pre-alpha builds. Tags use plain `Major.Minor.Patch`, without a `v` prefix or `-alpha` suffix. Do not push the tag yourself before preparation.
3. Click **Save draft**. You can return to edit the description later. Saving alone does not start a build.
4. Open [Actions → Prepare GitHub release](https://github.com/NekoShinobi/valheim-boosted/actions/workflows/release.yml), select **Run workflow**, use `main` for the workflow branch, and enter the saved draft's tag.
5. Wait for the full workflow to succeed. Its summary links back to Releases. The draft now contains `valheim-boosted-0.1.0.zip`, `valheim-boosted-0.1.0-plugins.zip`, and `SHA256SUMS`. Review them and click **Publish release** in the editor.

Publication makes the assets downloadable without login on the public repository and starts **Build metrics image**, which publishes `ghcr.io/nekoshinobi/valheim-boosted-metrics:0.1.0` after its checks pass. The image build is separate and may finish after the mod release is published. Both regular releases and pre-releases use this flow; neither uploads to Thunderstore automatically.

Preparation reads the selected branch at a full commit SHA and plans these updates:

| File | Automatic change |
| --- | --- |
| `mod/ValheimBoosted.csproj` | Set the assembly/package version |
| `mod/Plugin.cs` | Set `PluginVersion` |
| `package.json` | Set the dashboard package version |
| `thunderstore/manifest.json` | Set the Thunderstore package version |
| `CHANGELOG.md` | Add the new `## vMajor.Minor.Patch` entry using the draft description |
| `README.md`, `thunderstore/README.md` | Update current version labels and installation ZIP filenames |

After validating all candidate metadata, it appends `chore(release): prepare Major.Minor.Patch` to the target branch using GitHub's [commit API](https://docs.github.com/en/graphql/reference/commits#createcommitonbranch). The API requires the branch head to still match the selected commit; a concurrent push fails preparation instead of overwriting it. The existing mod workflow builds the resulting commit. After its checks pass, preparation verifies both ZIP layouts and matching binaries, creates the tag at that commit, pins the draft there, and uploads the original ZIPs and checksums. Pull the target branch locally afterward to get the automated commit.

Older changelog entries remain intact. If the selected version already has a hand-written entry (including the initial `0.1.0`), preparation retains it and adds the draft notes within that entry. Generated notes are marked with HTML comments so retries update them without duplicating headings. Description headings are nested beneath the version heading; fenced code remains literal. The GitHub description itself is preserved. Dependency versions, tool pins, and lockfiles are unchanged; the current Bun lockfile does not store the root package version.

A failed build leaves the version commit on the branch and the draft unpublished, with no new tag. Rerun preparation after fixing the cause; identical version/notes content creates no extra commit. Editing the description during a build stops attachment: rerun so the changelog and package include those edits. Before a successful attachment, leave the tag, target and draft state alone. Release preparations are serialized across the repository, and version decreases are rejected.

Once a tag exists, retries build its existing commit and validate its versions without changing source or moving the tag. A manually created tag with outdated versions therefore fails: use a new unused tag and branch target for automatic version preparation. After attachment, further edits in the GitHub editor affect the release page; changing the packaged changelog requires a new version. Rerunning preparation replaces only its two named ZIPs and `SHA256SUMS`; other attachments are retained. Published releases are rejected. Attaching before publication also supports [immutable releases](https://docs.github.com/en/code-security/concepts/supply-chain-security/immutable-releases), where assets become locked when published.

No personal token is needed for repositories that permit the workflow's branch and release writes. The preparation and attachment jobs use `GITHUB_TOKEN` with `contents: write`; the reusable build has `contents: read`. Branch protection, required PR/check rules and tag rules still apply. If they reject the version commit, preparation stops before building or tagging; it does not bypass those rules. Use code already pushed to `main`: GitHub can also reject tag creation for a branch with workflow changes that the Actions token cannot authorize. Commits and tags created with `GITHUB_TOKEN` do not start extra push workflows, so the mod build is called explicitly. A user publishing through the editor supplies the event that starts the image workflow.

### If preparation cannot find the draft

The **pre-release** checkbox and **Save draft** are separate controls. Clicking **Publish release** publishes even when pre-release is checked; automatic version preparation requires a release saved with **Save draft**. Creating a Git tag alone or leaving the editor open without saving does not create that draft.

If resolution reports no matching draft, open [Draft a new release](https://github.com/NekoShinobi/valheim-boosted/releases/new), enter the exact numeric tag you will pass to the workflow, select `main`, write the description and click **Save draft**. Then start a new **Prepare GitHub release** run with that tag. The failure log and workflow summary show the requested repository/tag and the draft/published tags visible to the token. If your saved draft is missing from that list, verify its repository/tag and the workflow's `contents: write` permission. A tag that already has a published release requires a new unused version for this preparation flow; it is not converted back into a draft automatically.

For fixes to the workflow or its scripts, push the fix and start a **new Run workflow** from `main`. GitHub's [Re-run jobs](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/re-run-workflows-and-jobs) uses the original run's commit, so rerunning an old failure will not pick up a script fix.

Attachment sends both `tag_name` and `target_commitish` when pinning the draft. Older code sent only the target, which could leave that same draft named `untagged-*` after the Git tag had already been created. If this happened, keep the existing draft and verify its release ID and the tag's commit against the failed run. Restore the intended tag on that draft; do not delete or move the Git tag. The fixed workflow can rebuild that tagged commit, or the already successful run's checked ZIPs can be attached to the repaired draft.

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

1. Use the [draft release flow above](#create-a-github-release-or-pre-release) with a higher unused `Major.Minor.Patch` tag and write its release notes. Preparation commits the source versions, changelog and README version updates automatically.
2. Keep the same Thunderstore package name and publishing Team. Keep `website_url` pointed at the project repository.
3. Upload the resulting Thunderstore ZIP under that same Team. Existing published versions cannot be edited, even for README-only changes.

For a fully manual local release, update those same version fields and prepend a `## vMajor.Minor.Patch` changelog section before packaging. Direct numeric tag pushes still trigger CI artifact/image builds, but do not run automatic version preparation. The changelog's heading format is separate from Git tag names.

The package uses `thunderstore/README.md` for a self-contained mod page; the root README includes developer/dashboard links for GitHub. The root changelog is copied unchanged into the ZIP. `thunderstore/icon.svg` is the editable artwork source; `icon.png` is the required 256×256 export. CI uses the checked-in PNG without an image-rendering dependency.

Workflows build artifacts/images; they do not publish to Thunderstore. No Team name or token is required to build a compatible package.

Requirements: [Creating a package](https://wiki.thunderstore.io/mods/creating-a-package), [BepInEx packaging](https://wiki.thunderstore.io/mods/packaging-your-mods), and [Updating a package](https://wiki.thunderstore.io/mods/updating-a-package).

## Dashboard image

`.github/workflows/dashboard.yml` installs the Bun version pinned in package.json, restores bun.lock, runs Svelte/TypeScript checks, backend tests, and a production UI build. A separate job builds the linux/amd64 image, starts it, and checks HTTP health, waiting-state JSON, and the index page.

On successful main/tag/manual runs and `release: published` events (including pre-releases), it publishes to `ghcr.io/<lowercase-owner>/valheim-boosted-metrics`. Pull requests only build and smoke-test. Images receive branch/SHA tags; default-branch builds also receive `latest`, and semver tags produce version tags. `latest` remains the main-branch channel: numeric release/pre-release builds do not overwrite it through automatic semver tagging. GitHub's GITHUB_TOKEN provides registry credentials with packages:write only for the image job. Repository/organization policy must allow that package write. Make package visibility appropriate for where you intend to pull it.

The image contains Bun, the compiled Svelte UI, and the TypeScript API. It does not contain Valheim or game references. Local image builds require Docker.

## Updating actions

Third-party actions are pinned to full commit SHAs, with version comments in [the workflows](../.github/workflows/). Dependabot proposes weekly action updates and also checks npm and Docker dependencies. Review the release notes and workflow results before merging an update.
