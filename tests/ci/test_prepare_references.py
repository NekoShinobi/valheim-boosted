import importlib.util
import io
from pathlib import Path
import tempfile
import unittest
import zipfile

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("prepare_ci", ROOT / "scripts/prepare-ci.py")
prepare = importlib.util.module_from_spec(spec)
spec.loader.exec_module(prepare)


class ReferencePreparationChecks(unittest.TestCase):
    def archive(self, missing=None):
        data = io.BytesIO()
        with zipfile.ZipFile(data, "w") as archive:
            for name in prepare.REQUIRED_LIBRARIES["bepinex"]:
                if name != missing:
                    archive.writestr("BepInExPack_Valheim/BepInEx/core/" + name, name.encode())
            # A same-named file elsewhere must not satisfy the core dependency.
            archive.writestr("unrelated/MonoMod.RuntimeDetour.dll", b"wrong copy")
        return data.getvalue()

    def test_clean_directory_gets_harmony_runtime_dependencies(self):
        with tempfile.TemporaryDirectory() as directory:
            target = Path(directory)
            prepare.extract_libraries(self.archive(), "bepinex", target)
            self.assertEqual({p.name for p in target.iterdir()}, {
                "0Harmony.dll", "BepInEx.dll", "MonoMod.RuntimeDetour.dll", "MonoMod.Utils.dll", "Mono.Cecil.dll",
            })
            self.assertEqual((target / "MonoMod.RuntimeDetour.dll").read_bytes(), b"MonoMod.RuntimeDetour.dll")

    def test_incomplete_archive_fails_before_extracting(self):
        with tempfile.TemporaryDirectory() as directory:
            target = Path(directory)
            with self.assertRaisesRegex(RuntimeError, "MonoMod.RuntimeDetour.dll"):
                prepare.extract_libraries(self.archive("MonoMod.RuntimeDetour.dll"), "bepinex", target)
            self.assertEqual(list(target.iterdir()), [])
