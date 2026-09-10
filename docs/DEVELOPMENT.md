# Local mod development

Linux development workspace for the native Steam build of Valheim. Open with `./scripts/open-vscode` for the project-local VS Code profile and C# extension. The .NET SDK and NuGet caches are under `.tools/`; no global SDK or shell changes are required.

On a fresh checkout, copy `.vscode/settings.example.json` to `.vscode/settings.json`. The latter is gitignored so machine-specific editor settings stay local. If the C# extension needs an explicit SDK location, set `dotnetAcquisitionExtension.existingDotnetPath` in that local file to your absolute `.tools/dotnet/dotnet` path.

## Layout

- `mod/`: .NET Framework 4.8 BepInEx/Jötunn plugin, including its NuGet dependency lock.
- `decomp/`: searchable C# generated from your installed game. Excluded from the mod build.
- `.local/references/`: snapshot of the game's Managed DLLs and modding libraries.
- `.local/profile/`: separate BepInEx/Jötunn development profile and deployed ValheimBoosted plugin.
- `.tools/`: pinned .NET SDK, ILSpy, downloaded packages, caches, and isolated editor profile.
- `toolchain.lock.json`: SDK version and archive checksums; BepInEx/Jötunn package versions and checksums.

## Everyday commands

Run from this directory (the Python script itself also works from other directories):

```sh
python3 scripts/dev.py doctor
python3 scripts/dev.py build
python3 scripts/dev.py deploy
python3 scripts/dev.py logs
```

`Ctrl+Shift+B` builds in VS Code. Run Task -> Build and deploy also stages the DLL and PDB under `.local/profile/BepInEx/plugins/ValheimBoosted/`. Deploy refuses while a local Valheim process is running. It never copies game or framework DLLs into the plugin output. C# assembly changes require restarting the game.

The mod provides an F8 diagnostics HUD, server telemetry and configurable networking improvements. See [OBSERVABILITY.md](OBSERVABILITY.md) for metrics and [SERVER-IMPROVEMENTS.md](SERVER-IMPROVEMENTS.md) for feature settings. The display name is `valheim-boosted`, assembly/namespace `ValheimBoosted`, and plugin GUID `valheim.boosted`. CI is described in [CI.md](CI.md).

## Run the development profile

In Steam -> Valheim -> Properties -> Launch Options, enter:

```text
"/absolute/path/to/valheim-boosted/.local/profile/start_game_bepinex.sh" %command%
```

Replace the example path with your checkout path, or run `python3 scripts/dev.py doctor` to print the exact launch option. Then launch the native Linux game through Steam. This uses the workspace profile without installing BepInEx into the Steam game directory. Remove the launch option to return to ordinary Steam launching.

Look for `valheim-boosted <version> loaded (build ...)` in `.local/profile/BepInEx/LogOutput.log`. Use a development character/world. Launching uses Valheim's normal save locations unless you configure a separate save directory. Breakpoint debugging and automatic code hot reload are not configured; portable symbols are retained for tooling.

After applying the Steam launch option, confirm the plugin loaded in the profile log.

## Inspect and refresh game code

```sh
python3 scripts/dev.py refresh
python3 scripts/dev.py decompile
```

The refresh command locates Steam libraries (or uses `VALHEIM_INSTALL`), copies the installed assemblies, and records their hashes and Steam build ID. Build refuses stale references after a game update. Decompilation exports all `assembly_*.dll` plus `Assembly-CSharp.dll` into separate folders. Generated project files are removed so VS Code loads only the actual mod solution.

Search `decomp/assembly_valheim/Player.cs`, `Character.cs`, `Inventory.cs`, or `ZNet.cs` to start investigating gameplay. Decompiled code is a reconstruction and may contain imperfect output; prefab values require asset inspection. Treat these folders as generated, private reference material. Do not compile or distribute them as your mod.

Game references are unmodified copies: private-member publicizing is not enabled. Use Jötunn APIs, public game members, and Harmony patches as needed. Add further reference assemblies to `mod/ValheimBoosted.csproj` when a feature requires them.

## Recreate tooling

Python 3.12+ and network access are required for bootstrap. This pinned SDK archive targets Linux x64.

```sh
python3 scripts/dev.py setup
```

Setup downloads and verifies pinned archives, restores ILSpy and locked framework reference packages, stages the separate runtime profile, and refreshes game references. It preserves existing BepInEx configuration. SDK changes require updating both `global.json` and `toolchain.lock.json`.

The optional local editor profile can be recreated with:

```sh
code --user-data-dir "$PWD/.tools/vscode-user-data" \
  --extensions-dir "$PWD/.tools/vscode-extensions" \
  --install-extension ms-dotnettools.csharp
```

For VS Code Remote SSH, open this folder remotely and install the recommended C# extension on the SSH host. The integrated terminal and tasks use the local SDK. The dedicated `open-vscode` launcher is for running the installed Linux VS Code desktop, not for opening a window on a different SSH client machine. No VS Code debug attach configuration is provided.

## References

- Jötunn tutorials: https://valheim-modding.github.io/Jotunn/tutorials/overview.html
- Jötunn example: https://github.com/Valheim-Modding/JotunnModExample
- ILSpy CLI: https://www.nuget.org/packages/ilspycmd
- Valheim BepInEx pack: https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/
