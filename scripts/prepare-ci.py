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
    required = {"bepinex": {"BepInEx.dll", "0Harmony.dll"}, "jotunn": {"Jotunn.dll"}}
    for key, names in required.items():
        package = lock["packages"][key]
        with urllib.request.urlopen(package["url"], timeout=120) as response:
            data = response.read()
        if hashlib.sha256(data).hexdigest() != package["sha256"]:
            raise RuntimeError("Mod-library checksum mismatch: " + key)
        with zipfile.ZipFile(io.BytesIO(data)) as archive:
            found = set()
            for entry in archive.infolist():
                name = Path(entry.filename).name
                if name in names:
                    (references / "ModLibraries" / name).write_bytes(archive.read(entry))
                    found.add(name)
            if found != names:
                raise RuntimeError("Missing references in " + key)
    (references / "ci-provenance.json").write_text(json.dumps({"source": "SteamCMD app 896660 public branch", "assemblies": hashes}, indent=2) + "\n")
    print("Prepared", len(hashes), "game references and pinned BepInEx/Jotunn libraries.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("game", type=Path)
    prepare(parser.parse_args().game)
