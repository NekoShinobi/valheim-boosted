#!/usr/bin/env python3
"""Validate release metadata and build Thunderstore and direct plugin-install ZIPs."""
import argparse
import json
import os
from pathlib import Path
import re
import struct
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parent.parent
VERSION = r"(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)"


def validate(root=ROOT, tag=None):
    manifest = json.loads((root / "thunderstore/manifest.json").read_text(encoding="utf-8"))
    required = {"name", "version_number", "website_url", "description", "dependencies"}
    if set(manifest) != required:
        raise ValueError("Manifest must contain exactly the five Thunderstore fields")
    if not isinstance(manifest["name"], str) or not re.fullmatch(r"[A-Za-z0-9_]{1,128}", manifest["name"]):
        raise ValueError("Thunderstore names allow letters, numbers and underscores only")
    version = manifest["version_number"]
    if not isinstance(version, str) or not re.fullmatch(VERSION, version):
        raise ValueError("Thunderstore versions must be Major.Minor.Patch without suffixes")
    description = manifest["description"]
    if not isinstance(description, str) or not 1 <= len(description) <= 250:
        raise ValueError("Description must contain 1–250 characters")
    website = manifest["website_url"]
    if not isinstance(website, str) or (website and not re.fullmatch(r"https?://[^\s/]+(?:/[^\s]*)?", website)):
        raise ValueError("website_url must be empty or an HTTP(S) URL")
    dependencies = manifest["dependencies"]
    if not isinstance(dependencies, list) or not all(isinstance(d, str) and re.fullmatch(r"[A-Za-z0-9_]+-[A-Za-z0-9_]+-" + VERSION, d) for d in dependencies):
        raise ValueError("Invalid Thunderstore dependency string")
    lock = json.loads((root / "toolchain.lock.json").read_text(encoding="utf-8"))
    expected = [lock["packages"][key]["name"] for key in ("bepinex", "jotunn")]
    if sorted(dependencies) != sorted(expected):
        raise ValueError("Manifest dependencies must match the pinned build libraries")
    project_version = ET.parse(root / "mod/ValheimBoosted.csproj").findtext(".//Version")
    plugin = (root / "mod/Plugin.cs").read_text(encoding="utf-8")
    match = re.search(r'const string PluginVersion = "([^"]+)";', plugin)
    js_version = json.loads((root / "package.json").read_text(encoding="utf-8"))["version"]
    if project_version != version or not match or match[1] != version or js_version != version:
        raise ValueError("Version mismatch between manifest, C# project/plugin and package.json")
    if tag is not None and tag != f"v{version}":
        raise ValueError(f"Release tag must be v{version}, got {tag}")
    changelog = (root / "CHANGELOG.md").read_text(encoding="utf-8")
    headings = re.findall(r"^## v(\S+)\s*$", changelog, re.MULTILINE)
    if not headings or headings[0] != version:
        raise ValueError("CHANGELOG.md must start with a release section for the current version")
    if not all(re.fullmatch(VERSION, v) for v in headings):
        raise ValueError("Changelog release headings must use ## vMajor.Minor.Patch")
    versions = [tuple(map(int, v.split("."))) for v in headings]
    if versions != sorted(set(versions), reverse=True):
        raise ValueError("Changelog releases must be unique and newest first")
    if not (root / "thunderstore/README.md").read_text(encoding="utf-8").strip():
        raise ValueError("Package README must not be empty")
    icon = (root / "thunderstore/icon.png").read_bytes()
    if (len(icon) < 33 or icon[:8] != b"\x89PNG\r\n\x1a\n" or icon[8:16] != b"\x00\x00\x00\rIHDR"
            or struct.unpack(">II", icon[16:24]) != (256, 256)):
        raise ValueError("icon.png must be a 256×256 PNG")
    return manifest


def package(root=ROOT, tag=None, plugins_only=False):
    manifest = validate(root, tag)
    version = manifest["version_number"]
    files = {} if plugins_only else {
        "manifest.json": root / "thunderstore/manifest.json",
        "icon.png": root / "thunderstore/icon.png",
        "README.md": root / "thunderstore/README.md",
        "CHANGELOG.md": root / "CHANGELOG.md",
    }
    plugin_directory = "ValheimBoosted/" if plugins_only else "BepInEx/plugins/ValheimBoosted/"
    for name in ("ValheimBoosted.dll", "ValheimBoosted.pdb"):
        files[plugin_directory + name] = root / "mod/bin/Release/net48" / name
    provenance = root / ".local/references/ci-provenance.json"
    if provenance.exists():
        files[(plugin_directory if plugins_only else "") + "build-references.json"] = provenance
    for source in files.values():
        if not source.is_file():
            raise ValueError(f"Missing package input: {source}. Build Release before packaging.")
    suffix = "-plugins" if plugins_only else ""
    output = root / "artifacts" / f"valheim-boosted-{version}{suffix}.zip"
    output.parent.mkdir(exist_ok=True)
    temporary = output.with_suffix(".zip.tmp")
    try:
        with zipfile.ZipFile(temporary, "w", zipfile.ZIP_DEFLATED) as archive:
            for name, source in files.items():
                archive.write(source, name)
        with zipfile.ZipFile(temporary) as archive:
            if archive.testzip() is not None or set(archive.namelist()) != set(files):
                raise ValueError("ZIP integrity or layout check failed")
        temporary.replace(output)
    finally:
        temporary.unlink(missing_ok=True)
    return output


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="Validate metadata without requiring a build")
    parser.add_argument("--tag", help="Require a matching vMajor.Minor.Patch release tag")
    args = parser.parse_args()
    tag = args.tag
    if tag is None and os.environ.get("GITHUB_REF_TYPE") == "tag":
        tag = os.environ.get("GITHUB_REF_NAME")
    try:
        if args.check:
            manifest = validate(tag=tag)
            print(f"Thunderstore metadata valid: {manifest['name']} {manifest['version_number']}")
        else:
            output = package(tag=tag)
            plugins_output = package(tag=tag, plugins_only=True)
            print(output)
            print(plugins_output)
            if os.environ.get("GITHUB_OUTPUT"):
                with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as stream:
                    stream.write(f"package_path={output.relative_to(ROOT).as_posix()}\n")
                    stream.write(f"plugins_package_path={plugins_output.relative_to(ROOT).as_posix()}\n")
    except (ValueError, OSError, KeyError) as exc:
        parser.exit(1, f"Packaging failed: {exc}\n")
