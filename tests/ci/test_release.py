"""Exercise release preparation without publishing or calling GitHub."""
import copy
import base64
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import subprocess
import shutil
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
TAG = "0.1.0"


class FakeGitHub:
    def __init__(self, tag=TAG):
        self.repository = "owner/repo"
        self.tag = tag
        self.release = {"id": 42, "tag_name": tag, "target_commitish": "main", "draft": True,
                        "immutable": False, "prerelease": True, "name": "First pre-alpha",
                        "body": "## Changes\n\n- Literal `code` and $(example).\n", "assets": []}
        self.ref = None
        self.annotated = {}
        self.branch_sha = COMMIT
        self.mutations = []
        self.uploads = []
        self.bad_upload = False
        self.commits = []
        self.commit_race = False

    def api(self, endpoint, data=None, missing=False, paginate=False):
        if data is not None:
            self.mutations.append((endpoint, copy.deepcopy(data)))
        if endpoint == "releases?per_page=100":
            assert paginate
            return [[{"tag_name": "0.0.1", "draft": False}], [copy.deepcopy(self.release)]]
        if endpoint == "releases/42":
            if data is not None:
                self.release.update(data)
            return copy.deepcopy(self.release)
        if endpoint == f"git/ref/tags/{self.tag}":
            assert missing
            return copy.deepcopy(self.ref)
        if endpoint == "git/refs":
            assert self.ref is None and data["ref"] == f"refs/tags/{self.tag}"
            self.ref = {"object": {"type": "commit", "sha": data["sha"]}}
            return copy.deepcopy(self.ref)
        if endpoint.startswith("git/tags/"):
            return copy.deepcopy(self.annotated[endpoint.removeprefix("git/tags/")])
        if endpoint == "commits/main":
            return {"sha": self.branch_sha}
        if endpoint == "git/ref/heads/main":
            return {"object": {"type": "commit", "sha": self.branch_sha}}
        raise AssertionError(f"Unexpected API request: {endpoint}")

    def upload(self, tag, paths):
        assert tag == self.tag
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

    def commit_files(self, branch, expected_sha, changes, tag):
        assert branch == "main" and tag == self.tag
        if self.commit_race or self.branch_sha != expected_sha:
            raise RuntimeError("Expected branch head no longer matches")
        self.commits.append(copy.deepcopy(changes))
        self.branch_sha = OTHER
        return OTHER


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
        for tag in ("v0.1.0", "0.1.0-alpha", "01.1.0", "0.1", "0.1.0\nsha=evil", "$(example)"):
            with self.subTest(tag=tag), self.assertRaises(ValueError):
                prepare.resolve(self.github, tag)

    def test_missing_draft_requires_editor_first(self):
        self.github.release["tag_name"] = "0.2.0"
        with self.assertRaisesRegex(ValueError, "No saved release draft") as error:
            prepare.resolve(self.github, TAG)
        self.assertIn("tag '0.1.0'", str(error.exception))
        self.assertIn('Visible draft tags (up to 10): ["0.2.0"]', str(error.exception))
        self.assertIn("https://github.com/owner/repo/releases/new", str(error.exception))
        self.assertIn("click Save draft", str(error.exception))
        self.assertEqual(self.github.mutations, [])

    def test_missing_draft_lists_published_tags_separately(self):
        self.github.release.update(tag_name="0.2.0", draft=False, prerelease=True)
        with self.assertRaises(ValueError) as error:
            prepare.resolve(self.github, TAG)
        self.assertIn("Visible draft tags (up to 10): []", str(error.exception))
        self.assertIn('Published tags (up to 10): ["0.0.1", "0.2.0"]', str(error.exception))
        self.assertNotIn("already has a published", str(error.exception))

    def test_matching_published_release_explains_draft_vs_prerelease(self):
        for prerelease in (True, False):
            with self.subTest(prerelease=prerelease):
                self.github.release.update(draft=False, prerelease=prerelease)
                with self.assertRaisesRegex(ValueError, "already has a published") as error:
                    prepare.resolve(self.github, TAG)
                self.assertIn("A pre-release is not a draft", str(error.exception))
                self.assertIn("new unused tag", str(error.exception))
                self.assertEqual(self.github.mutations, [])

    def test_duplicate_matching_records_are_not_reported_as_missing(self):
        with patch.object(self.github, "api", return_value=[[self.github.release, self.github.release]]):
            with self.assertRaisesRegex(ValueError, "Found 2 release records"):
                prepare.resolve(self.github, TAG)

    def test_unavailable_drafts_are_not_assumed_to_be_absent_without_permission_hint(self):
        with patch.object(self.github, "api", return_value=[[]]):
            with self.assertRaises(ValueError) as error:
                prepare.resolve(self.github, TAG)
        self.assertIn("is visible in owner/repo", str(error.exception))
        self.assertIn("contents: write", str(error.exception))

    def test_resolve_failure_adds_recovery_details_to_workflow_summary(self):
        summary = self.assets / "summary.md"
        self.github.release["tag_name"] = "0.2.0"
        with patch.dict(prepare.os.environ, {"RELEASE_TAG": TAG, "GITHUB_REPOSITORY": "owner/repo",
                                            "GITHUB_STEP_SUMMARY": str(summary)}), \
                patch.object(prepare, "GitHub", return_value=self.github), \
                patch("sys.argv", ["prepare-release.py", "resolve"]), patch("sys.stderr", new_callable=io.StringIO):
            with self.assertRaises(SystemExit) as error:
                prepare.main()
        self.assertEqual(error.exception.code, 1)
        self.assertIn("Release preparation stopped", summary.read_text())
        self.assertIn("tag '0.1.0'", summary.read_text())
        self.assertIn("click Save draft", summary.read_text())

    def test_reject_published_immutable_changed_tag_id_target_or_empty_notes_before_writes(self):
        original = copy.deepcopy(self.github.release)
        for update in ({"draft": False}, {"immutable": True}, {"tag_name": "0.2.0"},
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

    def test_notes_changed_during_build_prevents_tag_and_asset_creation(self):
        digest = prepare.notes_digest(self.github.release)
        self.github.release["body"] += "\nNew notes to put in the changelog.\n"
        with self.assertRaisesRegex(ValueError, "description changed"):
            prepare.attach(self.github, TAG, 42, COMMIT, "main", self.assets, digest)
        self.assertEqual(self.github.mutations, [])
        self.assertEqual(self.github.uploads, [])

    @patch.object(prepare.subprocess, "run")
    def test_api_absence_is_distinct_from_permission_failure(self, run):
        github = prepare.GitHub("owner/repo")
        run.return_value = subprocess.CompletedProcess([], 1, "", "gh: Not Found (HTTP 404)")
        self.assertIsNone(github.api("git/ref/tags/0.1.0", missing=True))
        run.return_value = subprocess.CompletedProcess([], 1, "", "gh: Forbidden (HTTP 403)")
        with self.assertRaisesRegex(RuntimeError, "403"):
            github.api("git/ref/tags/0.1.0", missing=True)

    @patch.object(prepare.subprocess, "run")
    def test_upload_passes_paths_as_arguments_without_a_shell(self, run):
        paths = [self.assets / "a file.zip", self.assets / "SHA256SUMS"]
        prepare.GitHub("owner/repo").upload(TAG, paths)
        run.assert_called_once_with(["gh", "release", "upload", TAG, "--repo", "owner/repo",
                                     "--clobber", *map(str, paths)], check=True)


class VersionPreparationChecks(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.root = Path(temp.name)
        self.versions = prepare.version_tools()
        for name in self.versions.VALIDATION_FILES:
            target = self.root / name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(ROOT / name, target)
        self.current = self.versions.packager.validate(self.root)["version_number"]
        # Keep fixtures independent of the actual release description on future tags.
        (self.root / "CHANGELOG.md").write_text(
            f"# Changelog\n\n## v{self.current}\n\nHand-written details.\n\n## v0.0.0\n\nOlder release.\n")
        major, minor, patch_version = map(int, self.current.split("."))
        self.next = f"{major}.{minor}.{patch_version + 1}"
        self.github = FakeGitHub(self.next)

    def apply_commit_to_checkout(self):
        # Model checking out the newly committed source on a retry.
        for name, data in self.github.commits[-1].items():
            (self.root / name).write_bytes(data)

    def prepare_version(self, sha=COMMIT):
        return prepare.prepare_source(self.github, self.github.tag, 42, sha, "main", self.root)

    def test_updates_all_versions_and_readme_examples_preserving_old_history_and_dependencies(self):
        original = {name: (self.root / name).read_bytes() for name in self.versions.VALIDATION_FILES}
        notes = "# Highlights\n\n## Changes\n\n- Faster sync – measured.\n\n```md\n## v99.0.0\n$(example)\n```"
        changes = self.versions.plan(self.root, self.next, notes)
        self.assertEqual(set(changes), set(self.versions.EDITABLE_FILES))
        self.assertEqual(original, {name: (self.root / name).read_bytes() for name in original})
        for name, data in changes.items():
            (self.root / name).write_bytes(data)
        self.assertEqual(self.versions.packager.validate(self.root, tag=self.next)["version_number"], self.next)
        text = changes["CHANGELOG.md"].decode()
        self.assertIn("### Highlights", text)
        self.assertIn("#### Changes", text)
        self.assertIn("```md\n## v99.0.0\n$(example)\n```", text)
        old_text = original["CHANGELOG.md"].decode()
        self.assertTrue(text.endswith(old_text[old_text.index(f"## v{self.current}"):]))
        readme = changes["README.md"].decode()
        self.assertIn(f"<br>{self.next} ·", readme)
        self.assertNotIn(f"valheim-boosted-{self.current}-plugins.zip", readme)
        self.assertIn(f"valheim-boosted-{self.next}-plugins.zip", readme)
        self.assertEqual(json.loads(changes["package.json"])["dependencies"],
                         json.loads(original["package.json"])["dependencies"])
        self.assertEqual(json.loads(changes["thunderstore/manifest.json"])["dependencies"],
                         json.loads(original["thunderstore/manifest.json"])["dependencies"])

    def test_initial_version_keeps_hand_written_notes_and_same_notes_are_idempotent(self):
        original = (self.root / "CHANGELOG.md").read_text()
        notes = self.github.release["body"]
        changes = self.versions.plan(self.root, self.current, notes)
        self.assertEqual(set(changes), {"CHANGELOG.md"})
        (self.root / "CHANGELOG.md").write_bytes(changes["CHANGELOG.md"])
        self.assertTrue(changes["CHANGELOG.md"].decode().endswith(original.split(f"## v{self.current}\n", 1)[1].lstrip("\n")))
        self.assertEqual(self.versions.plan(self.root, self.current, notes), {})
        updated = self.versions.plan(self.root, self.current, "Revised release description.")
        self.assertNotIn(notes.strip(), updated["CHANGELOG.md"].decode())
        self.assertIn("Revised release description.", updated["CHANGELOG.md"].decode())
        self.assertTrue(updated["CHANGELOG.md"].decode().endswith(original.split(f"## v{self.current}\n", 1)[1].lstrip("\n")))

    def test_invalid_version_or_notes_never_changes_source(self):
        original = (self.root / "CHANGELOG.md").read_bytes()
        for tag, notes in (("0.0.0", "Older release"), ("v1.0.0", "Invalid prefix"),
                           (self.next, "```\nunclosed code"), (self.next, self.versions.NOTES_START)):
            with self.subTest(tag=tag, notes=notes), self.assertRaises(ValueError):
                self.versions.plan(self.root, tag, notes)
        self.assertEqual((self.root / "CHANGELOG.md").read_bytes(), original)

    def test_commit_before_build_and_retry_reuses_the_committed_version(self):
        result = self.prepare_version()
        self.assertEqual(result["sha"], OTHER)
        self.assertEqual(result["notes_digest"], prepare.notes_digest(self.github.release))
        self.assertEqual(len(self.github.commits), 1)
        self.assertIsNone(self.github.ref)  # Tag is created only after the build.
        self.assertTrue(self.github.release["draft"])
        self.apply_commit_to_checkout()
        self.assertEqual(self.prepare_version(OTHER), result)
        self.assertEqual(len(self.github.commits), 1)

    def test_rerun_updates_generated_notes_without_duplicate_changelog_versions(self):
        self.prepare_version()
        self.apply_commit_to_checkout()
        self.github.release["body"] = "Updated notes after a failed build."
        self.prepare_version(OTHER)
        self.assertEqual(set(self.github.commits[-1]), {"CHANGELOG.md"})
        self.apply_commit_to_checkout()
        changelog = (self.root / "CHANGELOG.md").read_text()
        self.assertEqual([s["version"] for s in self.versions.packager.release_sections(changelog)][:2], [self.next, self.current])
        self.assertIn(self.github.release["body"], changelog)

    def test_bumped_commit_packages_and_attaches_under_the_selected_tag(self):
        result = self.prepare_version()
        self.apply_commit_to_checkout()
        build = self.root / "mod/bin/Release/net48"
        build.mkdir(parents=True)
        for name in ("ValheimBoosted.dll", "ValheimBoosted.pdb"):
            (build / name).write_bytes(f"Fixture build of {result['sha']}: {name}".encode())
        for plugins in (False, True):
            self.versions.packager.package(self.root, tag=self.next, plugins_only=plugins)
        release = prepare.attach(self.github, self.next, 42, result["sha"], "main",
                                 self.root / "artifacts", result["notes_digest"])
        self.assertEqual(self.github.ref["object"]["sha"], result["sha"])
        self.assertEqual(release["target_commitish"], result["sha"])
        self.assertTrue(release["draft"])
        with zipfile.ZipFile(self.root / f"artifacts/valheim-boosted-{self.next}.zip") as package:
            self.assertEqual(json.loads(package.read("manifest.json"))["version_number"], self.next)
            self.assertIn(self.github.release["body"].strip(), package.read("CHANGELOG.md").decode())

    def test_existing_tag_only_builds_matching_committed_version(self):
        self.github.ref = {"object": {"type": "commit", "sha": COMMIT}}
        with self.assertRaisesRegex(ValueError, "Release tag"):
            self.prepare_version()
        self.github.tag = self.current
        self.github.release["tag_name"] = self.current
        result = self.prepare_version()
        self.assertEqual(result["sha"], COMMIT)
        self.assertEqual(self.github.commits, [])

    def test_branch_movement_before_or_during_commit_cannot_overwrite_changes(self):
        self.github.branch_sha = OTHER
        with self.assertRaisesRegex(ValueError, "branch moved"):
            self.prepare_version()
        self.github.branch_sha = COMMIT
        self.github.commit_race = True
        with self.assertRaisesRegex(RuntimeError, "branch head"):
            self.prepare_version()
        self.assertEqual(self.github.commits, [])
        self.assertIsNone(self.github.ref)

    def test_draft_edited_during_planning_does_not_commit_stale_notes(self):
        original_plan = self.versions.plan

        def edit_while_planning(*args):
            changes = original_plan(*args)
            self.github.release["body"] += "\nMore notes."
            return changes

        with patch.object(prepare, "version_tools", return_value=self.versions), \
                patch.object(self.versions, "plan", side_effect=edit_while_planning):
            with self.assertRaisesRegex(ValueError, "Draft changed"):
                self.prepare_version()
        self.assertEqual(self.github.commits, [])

    @patch.object(prepare.subprocess, "run")
    def test_graphql_commit_passes_exact_head_and_base64_files_as_structured_data(self, run):
        changes = self.versions.plan(self.root, self.next, self.github.release["body"])
        run.return_value = subprocess.CompletedProcess([], 0, json.dumps({
            "data": {"createCommitOnBranch": {"commit": {"oid": OTHER}}}}), "")
        sha = prepare.GitHub("owner/repo").commit_files("main", COMMIT, changes, self.next)
        self.assertEqual(sha, OTHER)
        self.assertEqual(run.call_args.args[0], ["gh", "api", "graphql", "--input", "-"])
        request = json.loads(run.call_args.kwargs["input"])["variables"]["input"]
        self.assertEqual(request["expectedHeadOid"], COMMIT)
        # CommittableBranch fields verified with GitHub's live __type query:
        # https://docs.github.com/en/graphql/reference/git#committablebranch
        self.assertEqual(request["branch"], {"repositoryNameWithOwner": "owner/repo", "branchName": "main"})
        self.assertEqual({f["path"]: base64.b64decode(f["contents"]) for f in request["fileChanges"]["additions"]}, changes)
        self.assertNotIn("force", request)

    @patch.object(prepare.subprocess, "run")
    def test_graphql_error_cannot_be_reported_as_a_successful_commit(self, run):
        run.return_value = subprocess.CompletedProcess([], 0, '{"data": null, "errors": [{"message": "branch protected"}]}', "")
        with self.assertRaisesRegex(RuntimeError, "branch protected"):
            prepare.GitHub("owner/repo").commit_files("main", COMMIT, {"CHANGELOG.md": b"notes"}, self.next)
