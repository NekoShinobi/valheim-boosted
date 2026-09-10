"""Exercise release preparation without publishing or calling GitHub."""
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import zipfile

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("prepare_release", ROOT / "scripts/prepare-release.py")
prepare = importlib.util.module_from_spec(spec)
spec.loader.exec_module(prepare)
COMMIT = "a" * 40
OTHER = "b" * 40
TAG = "v0.1.0"


class FakeGitHub:
    def __init__(self):
        self.release = {"id": 42, "tag_name": TAG, "target_commitish": "main", "draft": True,
                        "immutable": False, "prerelease": True, "name": "First pre-alpha",
                        "body": "## Changes\n\n- Literal `code` and $(example).\n", "assets": []}
        self.ref = None
        self.annotated = {}
        self.branch_sha = COMMIT
        self.mutations = []
        self.uploads = []
        self.bad_upload = False

    def api(self, endpoint, data=None, missing=False, paginate=False):
        if data is not None:
            self.mutations.append((endpoint, copy.deepcopy(data)))
        if endpoint == "releases?per_page=100":
            assert paginate
            return [[{"tag_name": "v0.0.1", "draft": False}], [copy.deepcopy(self.release)]]
        if endpoint == "releases/42":
            if data is not None:
                self.release.update(data)
            return copy.deepcopy(self.release)
        if endpoint == f"git/ref/tags/{TAG}":
            assert missing
            return copy.deepcopy(self.ref)
        if endpoint == "git/refs":
            assert self.ref is None and data["ref"] == f"refs/tags/{TAG}"
            self.ref = {"object": {"type": "commit", "sha": data["sha"]}}
            return copy.deepcopy(self.ref)
        if endpoint.startswith("git/tags/"):
            return copy.deepcopy(self.annotated[endpoint.removeprefix("git/tags/")])
        if endpoint == "commits/main":
            return {"sha": self.branch_sha}
        raise AssertionError(f"Unexpected API request: {endpoint}")

    def upload(self, tag, paths):
        assert tag == TAG
        self.uploads.append([p.name for p in paths])
        names = {p.name for p in paths}
        self.release["assets"] = [a for a in self.release["assets"] if a["name"] not in names]
        self.release["assets"] += [
            {"name": p.name, "state": "uploaded", "size": p.stat().st_size,
             "digest": "sha256:" + hashlib.sha256(p.read_bytes()).hexdigest()}
            for p in paths
        ]
        if self.bad_upload:
            self.release["assets"][-1]["digest"] = "sha256:wrong"


class ReleaseChecks(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.assets = Path(temp.name)
        self.github = FakeGitHub()
        self.packages()

    def packages(self, manifest_version="0.1.0", different_dll=False, wrapper=False):
        for plugins in (False, True):
            suffix = "-plugins" if plugins else ""
            path = self.assets / f"valheim-boosted-0.1.0{suffix}.zip"
            prefix = "ValheimBoosted/" if plugins else "BepInEx/plugins/ValheimBoosted/"
            with zipfile.ZipFile(path, "w") as archive:
                if wrapper:
                    archive.writestr("inner.zip", b"wrapped package")
                    continue
                for name in ("ValheimBoosted.dll", "ValheimBoosted.pdb"):
                    archive.writestr(prefix + name, b"different" if plugins and different_dll else name.encode())
                archive.writestr((prefix if plugins else "") + "build-references.json", "{}")
                if not plugins:
                    archive.writestr("manifest.json", json.dumps({"version_number": manifest_version}))
                    for name in ("README.md", "CHANGELOG.md", "icon.png"):
                        archive.writestr(name, b"fixture")

    def attach(self):
        return prepare.attach(self.github, TAG, 42, COMMIT, "main", self.assets)

    def test_resolve_finds_draft_on_later_page_and_freezes_branch_commit(self):
        self.assertEqual(prepare.resolve(self.github, TAG), {
            "sha": COMMIT, "release_id": "42", "target": "main", "version": "0.1.0"})
        self.assertEqual(self.github.mutations, [])

    def test_existing_annotated_tag_takes_precedence_over_branch(self):
        self.github.ref = {"object": {"type": "tag", "sha": OTHER}}
        self.github.annotated[OTHER] = {"object": {"type": "commit", "sha": COMMIT}}
        self.github.branch_sha = OTHER
        self.assertEqual(prepare.resolve(self.github, TAG)["sha"], COMMIT)

    def test_bad_or_non_commit_tag_is_rejected(self):
        for obj in ({"type": "tree", "sha": COMMIT}, {"type": "commit", "sha": "bad"}):
            with self.subTest(obj=obj):
                self.github.ref = {"object": obj}
                with self.assertRaises(ValueError):
                    prepare.resolve(self.github, TAG)

    def test_numeric_tag_required(self):
        for tag in ("0.1.0", "v0.1.0-alpha", "v01.1.0", "v0.1.0\nsha=evil", "$(example)"):
            with self.subTest(tag=tag), self.assertRaises(ValueError):
                prepare.resolve(self.github, tag)

    def test_missing_draft_requires_editor_first(self):
        self.github.release["tag_name"] = "v0.2.0"
        with self.assertRaisesRegex(ValueError, "Save exactly one draft"):
            prepare.resolve(self.github, TAG)

    def test_reject_published_immutable_changed_tag_id_target_or_empty_notes_before_writes(self):
        original = copy.deepcopy(self.github.release)
        for update in ({"draft": False}, {"immutable": True}, {"tag_name": "v0.2.0"},
                       {"id": 43}, {"target_commitish": "other"}, {"body": " \n"}):
            with self.subTest(update=update):
                self.github.release = {**original, **update}
                with self.assertRaises(ValueError):
                    self.attach()
                self.assertEqual(self.github.mutations, [])
                self.assertEqual(self.github.uploads, [])

    def test_newline_in_target_cannot_inject_job_outputs(self):
        self.github.release["target_commitish"] = "main\nversion=evil"
        with self.assertRaisesRegex(ValueError, "invalid target"):
            prepare.resolve(self.github, TAG)

    def test_tag_moved_during_build_stops_before_writes(self):
        self.github.ref = {"object": {"type": "commit", "sha": OTHER}}
        with self.assertRaisesRegex(ValueError, "Tag moved"):
            self.attach()
        self.assertEqual(self.github.mutations, [])
        self.assertEqual(self.github.uploads, [])

    def test_draft_choices_and_notes_survive_branch_movement_and_retry(self):
        for prerelease in (True, False):
            with self.subTest(prerelease=prerelease):
                self.github = FakeGitHub()
                self.github.release["prerelease"] = prerelease
                values = prepare.resolve(self.github, TAG)
                self.github.branch_sha = OTHER
                # Notes can be edited while the source is building.
                self.github.release["body"] += "\nA correction from the editor.\n"
                self.github.release["assets"] = [{"name": "user-notes.txt", "state": "uploaded", "size": 8}]
                original = copy.deepcopy(self.github.release)
                for _ in range(2):
                    result = prepare.attach(self.github, TAG, 42, values["sha"], values["target"], self.assets)
                    for key in ("body", "name", "prerelease", "draft"):
                        self.assertEqual(result[key], original[key])
                    self.assertEqual(result["target_commitish"], COMMIT)
                    self.assertEqual(self.github.ref["object"]["sha"], COMMIT)
                    self.assertIn("user-notes.txt", [a["name"] for a in result["assets"]])
                self.assertEqual(self.github.mutations.count(("git/refs", {"ref": f"refs/tags/{TAG}", "sha": COMMIT})), 1)
                self.assertTrue(all(data == {"target_commitish": COMMIT}
                                    for endpoint, data in self.github.mutations if endpoint == "releases/42"))
                self.assertEqual(self.github.uploads[-1], ["valheim-boosted-0.1.0.zip", "valheim-boosted-0.1.0-plugins.zip", "SHA256SUMS"])
                for line in (self.assets / "SHA256SUMS").read_text().splitlines():
                    digest, name = line.split("  ")
                    self.assertEqual(digest, hashlib.sha256((self.assets / name).read_bytes()).hexdigest())

    def test_incorrect_package_version_build_or_wrapper_never_uploads(self):
        for kwargs in ({"manifest_version": "0.2.0"}, {"different_dll": True}, {"wrapper": True}):
            with self.subTest(kwargs=kwargs):
                self.packages(**kwargs)
                with self.assertRaises(ValueError):
                    self.attach()
                self.assertEqual(self.github.mutations, [])
                self.assertEqual(self.github.uploads, [])

    def test_missing_package_fails_before_tag_creation(self):
        (self.assets / "valheim-boosted-0.1.0-plugins.zip").unlink()
        with self.assertRaisesRegex(ValueError, "Missing"):
            self.attach()
        self.assertEqual(self.github.mutations, [])

    def test_corrupt_upload_is_not_reported_as_ready(self):
        self.github.bad_upload = True
        with self.assertRaisesRegex(ValueError, "digest mismatch"):
            self.attach()
        self.assertTrue(self.github.release["draft"])

    @patch.object(prepare.subprocess, "run")
    def test_api_absence_is_distinct_from_permission_failure(self, run):
        github = prepare.GitHub("owner/repo")
        run.return_value = subprocess.CompletedProcess([], 1, "", "gh: Not Found (HTTP 404)")
        self.assertIsNone(github.api("git/ref/tags/v0.1.0", missing=True))
        run.return_value = subprocess.CompletedProcess([], 1, "", "gh: Forbidden (HTTP 403)")
        with self.assertRaisesRegex(RuntimeError, "403"):
            github.api("git/ref/tags/v0.1.0", missing=True)

    @patch.object(prepare.subprocess, "run")
    def test_upload_passes_paths_as_arguments_without_a_shell(self, run):
        paths = [self.assets / "a file.zip", self.assets / "SHA256SUMS"]
        prepare.GitHub("owner/repo").upload(TAG, paths)
        run.assert_called_once_with(["gh", "release", "upload", TAG, "--repo", "owner/repo",
                                     "--clobber", *map(str, paths)], check=True)
