#!/usr/bin/env python3
"""Prepare compilation references from a SteamCMD installation; no game DLLs are published."""
import argparse
import hashlib
import io
import json
from pathlib import Path
import shutil
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parent.parent

# Runtime closure of the pinned Harmony build, required by the Mono test runners.
# These stay in private build references; the mod ZIP does not bundle them.
REQUIRED_LIBRARIES = {
    "bepinex": {"BepInEx.dll", "0Harmony.dll", "MonoMod.RuntimeDetour.dll", "MonoMod.Utils.dll", "Mono.Cecil.dll"},
    "jotunn": {"Jotunn.dll"},
}


def extract_libraries(data, key, destination):
    names = REQUIRED_LIBRARIES[key]
    prefix = "BepInExPack_Valheim/BepInEx/core/" if key == "bepinex" else "plugins/"
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        missing = sorted(name for name in names if prefix + name not in archive.namelist())
        if missing:
            raise RuntimeError("Missing references in " + key + ": " + ", ".join(missing))
        for name in sorted(names):
            (destination / name).write_bytes(archive.read(prefix + name))


def prepare(game):
    managed = next((p for p in [game / "valheim_server_Data/Managed", game / "valheim_Data/Managed"] if (p / "assembly_valheim.dll").exists()), None)
    if managed is None:
        raise RuntimeError("SteamCMD installation has no assembly_valheim.dll in a recognized Managed directory")
    references = ROOT / ".local/references"
    (references / "Managed").mkdir(parents=True, exist_ok=True)
    (references / "ModLibraries").mkdir(parents=True, exist_ok=True)
    hashes = {}
    for source in managed.glob("*.dll"):
        shutil.copy2(source, references / "Managed" / source.name)
        hashes[source.name] = hashlib.sha256(source.read_bytes()).hexdigest()
    lock = json.loads((ROOT / "toolchain.lock.json").read_text())
    for key in REQUIRED_LIBRARIES:
        package = lock["packages"][key]
        with urllib.request.urlopen(package["url"], timeout=120) as response:
            data = response.read()
        if hashlib.sha256(data).hexdigest() != package["sha256"]:
            raise RuntimeError("Mod-library checksum mismatch: " + key)
        extract_libraries(data, key, references / "ModLibraries")
    (references / "ci-provenance.json").write_text(json.dumps({"source": "SteamCMD app 896660 public branch", "assemblies": hashes}, indent=2) + "\n")
    print("Prepared", len(hashes), "game references and pinned BepInEx/Jotunn libraries.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("game", type=Path)
    prepare(parser.parse_args().game)
