"""Release failures should be caught before producing or uploading a ZIP."""
import importlib.util
import json
from pathlib import Path
import shutil
import tempfile
import unittest
import zipfile

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("package_mod", ROOT / "scripts/package-mod.py")
packager = importlib.util.module_from_spec(spec)
spec.loader.exec_module(packager)


class PackagingChecks(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        for name in ("thunderstore/manifest.json", "thunderstore/README.md", "thunderstore/icon.png",
                     "CHANGELOG.md", "mod/ValheimBoosted.csproj", "mod/Plugin.cs", "package.json", "toolchain.lock.json"):
            dest = self.root / name
            dest.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(ROOT / name, dest)
        self.version = json.loads((self.root / "thunderstore/manifest.json").read_text())["version_number"]
        major, minor, patch = map(int, self.version.split("."))
        self.next_version = f"{major}.{minor}.{patch + 1}"

    def mutate_manifest(self, key, value):
        path = self.root / "thunderstore/manifest.json"
        manifest = json.loads(path.read_text())
        manifest[key] = value
        path.write_text(json.dumps(manifest))

    def test_package_layout_and_excluded_binaries(self):
        build = self.root / "mod/bin/Release/net48"
        build.mkdir(parents=True)
        for name in ("ValheimBoosted.dll", "ValheimBoosted.pdb", "assembly_valheim.dll", "Jotunn.dll"):
            (build / name).write_bytes(b"fixture")
        output = packager.package(self.root, tag=f"v{self.version}")
        with zipfile.ZipFile(output) as archive:
            self.assertEqual(set(archive.namelist()), {
                "manifest.json", "icon.png", "README.md", "CHANGELOG.md",
                "BepInEx/plugins/ValheimBoosted/ValheimBoosted.dll",
                "BepInEx/plugins/ValheimBoosted/ValheimBoosted.pdb",
            })
            self.assertEqual(json.loads(archive.read("manifest.json"))["version_number"], self.version)
            self.assertEqual(archive.read("CHANGELOG.md"), (self.root / "CHANGELOG.md").read_bytes())
            self.assertIsNone(archive.testzip())

    def test_plugins_package_installs_directly_and_matches_thunderstore(self):
        build = self.root / "mod/bin/Release/net48"
        build.mkdir(parents=True)
        for name in ("ValheimBoosted.dll", "ValheimBoosted.pdb", "assembly_valheim.dll", "Jotunn.dll"):
            (build / name).write_bytes(name.encode())
        for include_provenance in (False, True):
            with self.subTest(provenance=include_provenance):
                if include_provenance:
                    provenance = self.root / ".local/references/ci-provenance.json"
                    provenance.parent.mkdir(parents=True)
                    provenance.write_text('{"build": "fixture"}')
                thunderstore = packager.package(self.root, tag=f"v{self.version}")
                output = packager.package(self.root, tag=f"v{self.version}", plugins_only=True)
                self.assertEqual(output.name, f"valheim-boosted-{self.version}-plugins.zip")
                expected = {"ValheimBoosted/ValheimBoosted.dll", "ValheimBoosted/ValheimBoosted.pdb"}
                if include_provenance:
                    expected.add("ValheimBoosted/build-references.json")
                with zipfile.ZipFile(output) as archive, zipfile.ZipFile(thunderstore) as original:
                    self.assertEqual(set(archive.namelist()), expected)
                    self.assertIsNone(archive.testzip())
                    plugins = self.root / "server profile/BepInEx/plugins"
                    archive.extractall(plugins)
                    for name in ("ValheimBoosted.dll", "ValheimBoosted.pdb"):
                        data = original.read("BepInEx/plugins/ValheimBoosted/" + name)
                        self.assertEqual((plugins / "ValheimBoosted" / name).read_bytes(), data)
                    self.assertFalse((plugins / "BepInEx").exists())
                    if include_provenance:
                        self.assertEqual(archive.read("ValheimBoosted/build-references.json"), original.read("build-references.json"))

    def test_invalid_name_and_prerelease_suffix_rejected(self):
        self.mutate_manifest("name", "valheim-boosted")
        with self.assertRaisesRegex(ValueError, "names"):
            packager.validate(self.root)
        self.mutate_manifest("name", "valheim_boosted")
        self.mutate_manifest("version_number", "0.1.0-pre-alpha")
        with self.assertRaisesRegex(ValueError, "without suffixes"):
            packager.validate(self.root)

    def test_version_and_tag_mismatch_rejected(self):
        with self.assertRaisesRegex(ValueError, "Release tag"):
            packager.validate(self.root, tag=f"v{self.next_version}")
        self.mutate_manifest("version_number", self.next_version)
        with self.assertRaisesRegex(ValueError, "Version mismatch"):
            packager.validate(self.root)

    def test_missing_changelog_entry_rejected(self):
        (self.root / "CHANGELOG.md").write_text("# Changelog\n\n## v0.0.1\n\nOld release.\n")
        with self.assertRaisesRegex(ValueError, "current version"):
            packager.validate(self.root)

    def test_dependencies_must_match_build(self):
        self.mutate_manifest("dependencies", ["ValheimModding-Jotunn-2.0.0"])
        with self.assertRaisesRegex(ValueError, "pinned build libraries"):
            packager.validate(self.root)

    def test_bad_icon_and_missing_build_rejected(self):
        with self.assertRaisesRegex(ValueError, "Build Release"):
            packager.package(self.root)
        self.assertFalse((self.root / "artifacts").exists())
        icon = self.root / "thunderstore/icon.png"
        data = bytearray(icon.read_bytes())
        data[16:20] = (128).to_bytes(4, "big")
        icon.write_bytes(data)
        with self.assertRaisesRegex(ValueError, "256×256"):
            packager.validate(self.root)
