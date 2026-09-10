#!/usr/bin/env python3
"""Local Valheim development commands. Run from any directory."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tarfile
import tempfile
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parent.parent
LOCAL = ROOT / ".local"
TOOLS = ROOT / ".tools"
MANAGED = LOCAL / "references/Managed"
LIBS = LOCAL / "references/ModLibraries"
PROFILE = LOCAL / "profile"
DOTNET = ROOT / "scripts/dotnet"
LOCK = json.loads((ROOT / "toolchain.lock.json").read_text())


def run(*args):
    subprocess.run([str(a) for a in args], cwd=ROOT, check=True)


def digest(path, algorithm="sha256"):
    with path.open("rb") as f:
        return hashlib.file_digest(f, algorithm).hexdigest()


def fetch(url, dest, expected, algorithm="sha256"):
    dest.parent.mkdir(parents=True, exist_ok=True)
    if not dest.exists() or digest(dest, algorithm) != expected.lower():
        temp = dest.with_suffix(dest.suffix + ".partial")
        print("Downloading", url, flush=True)
        urllib.request.urlretrieve(url, temp)
        if digest(temp, algorithm) != expected.lower():
            temp.unlink()
            raise RuntimeError("Download checksum mismatch: " + url)
        temp.replace(dest)
    return dest


def game_path():
    override = os.environ.get("VALHEIM_INSTALL")
    cfg = LOCAL / "environment.json"
    if not override and cfg.exists():
        override = json.loads(cfg.read_text()).get("valheim_install")
    if override:
        path = Path(override).expanduser()
        if not (path / "valheim_Data/Managed/assembly_valheim.dll").exists():
            raise RuntimeError("VALHEIM_INSTALL does not contain valheim_Data/Managed/assembly_valheim.dll")
        return path.resolve()
    steam = Path.home() / ".local/share/Steam"
    libraries = [steam, Path.home() / ".steam/steam"]
    vdf = steam / "steamapps/libraryfolders.vdf"
    if vdf.exists():
        libraries.extend(Path(p) for p in re.findall(r'"path"\s+"([^"]+)"', vdf.read_text()))
    for library in libraries:
        path = library / "steamapps/common/Valheim"
        if (path / "valheim_Data/Managed/assembly_valheim.dll").exists():
            return path.resolve()
    raise RuntimeError("Valheim not found. Set VALHEIM_INSTALL to your Steam Valheim directory.")


def source_manifest(game):
    source = game / "valheim_Data/Managed"
    manifest = game.parent.parent / "appmanifest_892970.acf"
    match = re.search(r'"buildid"\s+"([^"]+)"', manifest.read_text()) if manifest.exists() else None
    return {"game_path": str(game), "steam_build_id": match.group(1) if match else None,
            "assemblies": {p.name: digest(p) for p in sorted(source.glob("*.dll"))}}


def refresh():
    game = game_path()
    current = source_manifest(game)
    MANAGED.mkdir(parents=True, exist_ok=True)
    for path in (game / "valheim_Data/Managed").glob("*.dll"):
        shutil.copy2(path, MANAGED / path.name)
    for path in MANAGED.glob("*.dll"):
        if path.name not in current["assemblies"]:
            path.unlink()
    (LOCAL / "references/manifest.json").write_text(json.dumps(current, indent=2) + "\n")
    (LOCAL / "environment.json").write_text(json.dumps({"valheim_install": str(game)}, indent=2) + "\n")
    print(f"Copied {len(current['assemblies'])} assemblies; Steam build {current['steam_build_id']}")


def check_references():
    p = LOCAL / "references/manifest.json"
    if not p.exists():
        raise RuntimeError("Run python3 scripts/dev.py setup first.")
    saved = json.loads(p.read_text())
    current = source_manifest(game_path())
    if saved != current:
        raise RuntimeError("Valheim changed. Run python3 scripts/dev.py refresh, then decompile.")
    for name, expected in saved["assemblies"].items():
        if not (MANAGED / name).exists() or digest(MANAGED / name) != expected:
            raise RuntimeError("Reference snapshot changed. Run python3 scripts/dev.py refresh.")
    return saved


def setup():
    if not (TOOLS / "dotnet/dotnet").exists():
        sdk = LOCK["sdk"]
        archive = fetch(sdk["url"], TOOLS / "downloads/sdk.tar.gz", sdk["hash"], "sha512")
        with tarfile.open(archive) as t:
            t.extractall(TOOLS / "dotnet", filter="data")
    for key, package in LOCK["packages"].items():
        archive = fetch(package["url"], TOOLS / "downloads" / (package["name"] + ".zip"), package["sha256"])
        with zipfile.ZipFile(archive) as z:
            z.extractall(TOOLS / "packages" / key)
    # Populate a separate runtime profile; preserve user configuration on later setup runs.
    pack = TOOLS / "packages/bepinex/BepInExPack_Valheim"
    for source in pack.rglob("*"):
        if source.is_file():
            dest = PROFILE / source.relative_to(pack)
            dest.parent.mkdir(parents=True, exist_ok=True)
            if "config" not in source.relative_to(pack).parts or not dest.exists():
                shutil.copy2(source, dest)
    (PROFILE / "start_game_bepinex.sh").chmod(0o755)
    plugin_dir = PROFILE / "BepInEx/plugins/Jotunn"
    plugin_dir.mkdir(parents=True, exist_ok=True)
    for source in (TOOLS / "packages/jotunn/plugins").iterdir():
        shutil.copy2(source, plugin_dir / source.name)
    LIBS.mkdir(parents=True, exist_ok=True)
    for directory in [pack / "BepInEx/core", TOOLS / "packages/jotunn/plugins"]:
        for source in directory.iterdir():
            if source.suffix in (".dll", ".xml"):
                shutil.copy2(source, LIBS / source.name)
    refresh()
    run(DOTNET, "tool", "restore")
    run(DOTNET, "restore", "ValheimBoosted.sln", "--locked-mode")
    print("Local toolchain and development profile ready.")


def build():
    check_references()
    run(DOTNET, "build", "ValheimBoosted.sln", "-c", "Debug", "-p:RestoreLockedMode=true")


def decompile():
    manifest = check_references()
    assemblies = sorted(MANAGED.glob("assembly_*.dll")) + [MANAGED / "Assembly-CSharp.dll"]
    base = ROOT / "decomp"
    base.mkdir(exist_ok=True)
    for assembly in assemblies:
        print("Decompiling", assembly.name, flush=True)
        with tempfile.TemporaryDirectory(prefix="decomp-", dir=TOOLS) as temp:
            output = Path(temp)
            run(DOTNET, "tool", "run", "ilspycmd", "--disable-updatecheck", "-p", "-r", MANAGED, "-o", output, assembly)
            # Keep this as source reference, not another project for the language server to load.
            for project in output.glob("*.csproj"):
                project.unlink()
            dest = base / assembly.stem
            if dest.exists():
                shutil.rmtree(dest)
            shutil.copytree(output, dest)
    (base / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
    print("Decompiled", len(assemblies), "assemblies into", base)


def deploy():
    # Refuse to replace a plugin while the local game is running.
    for comm in Path("/proc").glob("[0-9]*/comm"):
        try:
            if comm.read_text().strip().startswith("valheim"):
                raise RuntimeError("Close Valheim before deploying a new plugin.")
        except (FileNotFoundError, PermissionError, ProcessLookupError):
            pass
    build()
    dest = PROFILE / "BepInEx/plugins/ValheimBoosted"
    dest.mkdir(parents=True, exist_ok=True)
    for name in ("ValheimBoosted.dll", "ValheimBoosted.pdb"):
        source = ROOT / "mod/bin/Debug/net48" / name
        tmp = dest / (name + ".tmp")
        shutil.copy2(source, tmp)
        tmp.replace(dest / name)
    # Preserve the old build outside BepInEx/plugins so both identities cannot load.
    legacy = PROFILE / "BepInEx/plugins/UrfMode"
    if legacy.exists():
        archive = LOCAL / "migrations/urfmode"
        archive.mkdir(parents=True, exist_ok=True)
        for name in ("UrfMode.dll", "UrfMode.pdb"):
            source = legacy / name
            if source.exists():
                shutil.move(str(source), str(archive / name))
        if not any(legacy.iterdir()):
            legacy.rmdir()
    old_config = PROFILE / "BepInEx/config/local.urfmode.cfg"
    new_config = PROFILE / "BepInEx/config/valheim.boosted.cfg"
    if old_config.exists() and not new_config.exists():
        text = old_config.read_text().replace("urfmode-telemetry", "valheim-boosted-telemetry")
        new_config.write_text(text)
    print("Deployed DLL and symbols to", dest)


def doctor():
    print("Workspace:", ROOT)
    print("Game:", game_path())
    run(DOTNET, "--version")
    manifest = check_references()
    print("References match Steam build", manifest["steam_build_id"])
    print("BepInEx:", LOCK["packages"]["bepinex"]["name"])
    print("Jotunn:", LOCK["packages"]["jotunn"]["name"])
    run(DOTNET, "tool", "list")
    dec = ROOT / "decomp/manifest.json"
    print("Decompilation:", "current" if dec.exists() and json.loads(dec.read_text()) == manifest else "run decompile")
    print("Development Steam launch option:")
    print(f'"{PROFILE / "start_game_bepinex.sh"}" %command%')


def logs():
    run("tail", "-n", "100", "-F", PROFILE / "BepInEx/LogOutput.log")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=["setup", "refresh", "build", "decompile", "deploy", "doctor", "logs"])
    args = parser.parse_args()
    try:
        globals()[args.command]()
    except (RuntimeError, subprocess.CalledProcessError, OSError) as exc:
        parser.exit(1, f"Error: {exc}\n")
