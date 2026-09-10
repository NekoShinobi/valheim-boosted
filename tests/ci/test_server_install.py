"""Exercise SteamCMD retry control flow without downloading game files."""
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
FAKE_STEAMCMD = r'''
import os
from pathlib import Path
import sys
args = sys.argv[1:]
state = Path(os.environ["FAKE_STEAM_STATE"])
with (state / "calls").open("a") as log:
    log.write(" ".join(args) + "\n")
mode = os.environ["FAKE_STEAM_MODE"]
if args == ["+quit"]:
    sys.exit(1 if mode == "bootstrap_failure" else 0)
counter = state / "attempts"
attempt = int(counter.read_text()) + 1 if counter.exists() else 1
counter.write_text(str(attempt))
if mode == "failure" or (mode == "transient" and attempt == 1):
    print("ERROR! Failed to install app '896660' (Missing configuration)")
    sys.exit(8)
if mode != "missing_assembly":
    install = Path(args[args.index("+force_install_dir") + 1])
    dll = install / "valheim_server_Data/Managed/assembly_valheim.dll"
    dll.parent.mkdir(parents=True, exist_ok=True)
    dll.write_bytes(b"test fixture, not a real assembly")
'''


class ServerInstallChecks(unittest.TestCase):
    def run_install(self, mode, stale=False):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            steamcmd = root / "steamcmd.sh"
            steamcmd.write_text(f"#!{sys.executable}\n" + FAKE_STEAMCMD)
            steamcmd.chmod(0o755)
            # Skip backoff delays in tests; production still uses real sleep/timeout.
            sleep = root / "sleep"
            sleep.write_text("#!/bin/sh\nexit 0\n")
            sleep.chmod(0o755)
            install = root / "server with spaces"
            if stale:
                dll = install / "valheim_server_Data/Managed/assembly_valheim.dll"
                dll.parent.mkdir(parents=True)
                dll.write_bytes(b"stale fixture")
            env = {**os.environ, "PATH": str(root) + os.pathsep + os.environ["PATH"],
                   "FAKE_STEAM_STATE": str(root), "FAKE_STEAM_MODE": mode}
            result = subprocess.run(["bash", str(ROOT / "scripts/install-server-references.sh"),
                                     str(steamcmd), str(install)], env=env, capture_output=True, text=True, timeout=10)
            calls = (root / "calls").read_text().splitlines()
            self.assertEqual(calls[0], "+quit")
            for call in calls[1:]:
                self.assertIn("+@sSteamCmdForcePlatformType linux", call)
                self.assertIn("+login anonymous +app_update 896660 validate +quit", call)
            return result, len(calls) - 1

    def test_first_attempt_success(self):
        result, attempts = self.run_install("success")
        self.assertEqual((result.returncode, attempts), (0, 1), result.stderr)

    def test_missing_configuration_then_recovery(self):
        result, attempts = self.run_install("transient")
        self.assertEqual((result.returncode, attempts), (0, 2), result.stderr)

    def test_persistent_failure_rejects_stale_files(self):
        result, attempts = self.run_install("failure", stale=True)
        self.assertEqual((result.returncode, attempts), (1, 3), result.stderr)

    def test_success_exit_without_assembly_fails(self):
        result, attempts = self.run_install("missing_assembly")
        self.assertEqual((result.returncode, attempts), (1, 3), result.stderr)

    def test_bootstrap_failure_stops_before_install(self):
        result, attempts = self.run_install("bootstrap_failure")
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(attempts, 0)
