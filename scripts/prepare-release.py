#!/usr/bin/env python3
"""Build inputs and draft-only asset attachment for the release-editor workflow."""
import argparse
import base64
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import subprocess
from urllib.parse import quote
import zipfile

TAG = re.compile(r"(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)")
SHA = re.compile(r"[0-9a-f]{40}")


def version_tools():
    # Load tooling from the workflow checkout, never execute code from the release target.
    spec = importlib.util.spec_from_file_location("release_version", Path(__file__).with_name("release-version.py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class GitHub:
    def __init__(self, repository):
        if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository):
            raise ValueError("Invalid repository")
        self.repository = repository

    def api(self, endpoint, data=None, missing=False, paginate=False):
        args = ["gh", "api", f"repos/{self.repository}/{endpoint}"]
        if data is not None:
            args += ["--method", "PATCH" if endpoint.startswith("releases/") else "POST", "--input", "-"]
        if paginate:
            args += ["--paginate", "--slurp"]
        result = subprocess.run(args, input=None if data is None else json.dumps(data), text=True, capture_output=True, check=False)
        if result.returncode:
            # Only a real 404 means absence. Authentication/rate-limit failures must stop the run.
            if missing and "(HTTP 404)" in result.stderr:
                return None
            raise RuntimeError(result.stderr.strip() or "GitHub API request failed")
        return json.loads(result.stdout)

    def upload(self, tag, paths):
        # Arguments, never shell code. User-authored notes never enter this command.
        subprocess.run(["gh", "release", "upload", tag, "--repo", self.repository, "--clobber", *map(str, paths)], check=True)

    def commit_files(self, branch, expected_sha, changes, tag):
        # GitHub atomically compares the branch head and appends a signed commit.
        # Protected-branch rules still apply; no force update or rules bypass.
        query = """mutation($input: CreateCommitOnBranchInput!) {
          createCommitOnBranch(input: $input) { commit { oid } }
        }"""
        payload = {"query": query, "variables": {"input": {
            "branch": {"repositoryNameWithOwner": self.repository, "branchName": branch},
            "expectedHeadOid": expected_sha,
            "message": {"headline": f"chore(release): prepare {tag}"},
            "fileChanges": {"additions": [
                {"path": name, "contents": base64.b64encode(data).decode("ascii")}
                for name, data in sorted(changes.items())
            ]},
        }}}
        result = subprocess.run(["gh", "api", "graphql", "--input", "-"],
                                input=json.dumps(payload), text=True, capture_output=True, check=False)
        if result.returncode:
            raise RuntimeError(result.stderr.strip() or "Release version commit failed")
        response = json.loads(result.stdout)
        if response.get("errors"):
            raise RuntimeError("Release version commit failed: " + json.dumps(response["errors"]))
        sha = response["data"]["createCommitOnBranch"]["commit"]["oid"]
        if not SHA.fullmatch(sha):
            raise ValueError("GitHub returned an invalid release commit")
        return sha


def version(tag):
    match = TAG.fullmatch(tag)
    if not match:
        raise ValueError("Use a numeric Major.Minor.Patch tag without a v prefix; choose pre-release using the release editor's checkbox")
    return match[0]


def draft_only(release, tag, release_id=None):
    if not release or release.get("tag_name") != tag or release_id is not None and release.get("id") != release_id:
        raise ValueError("Draft was removed or its tag changed; save the intended draft and run the workflow again")
    if release.get("draft") is not True:
        kind = "pre-release" if release.get("prerelease") else "release"
        raise ValueError(f"Tag {tag!r} already has a published {kind}. A pre-release is not a draft. "
                         "For automatic version preparation, choose a new unused tag and click Save draft, "
                         "then run this workflow. Published releases are never replaced")
    if release.get("immutable"):
        raise ValueError(f"Release {tag!r} is immutable; prepare a new draft with an unused tag")
    if not isinstance(release.get("body"), str) or not release["body"].strip():
        raise ValueError("Write a description in Releases → Edit → Describe this release, then Save draft")
    return release


def tag_commit(github, tag):
    ref = github.api(f"git/ref/tags/{quote(tag, safe='')}", missing=True)
    if ref is None:
        return None
    obj = ref["object"]
    for _ in range(8):
        if not SHA.fullmatch(obj.get("sha", "")):
            raise ValueError("Invalid tag object")
        if obj["type"] == "commit":
            return obj["sha"]
        if obj["type"] != "tag":
            break
        obj = github.api(f"git/tags/{obj['sha']}")["object"]
    raise ValueError("Release tag does not resolve to a commit")


def resolve(github, tag):
    release_version = version(tag)
    # The public by-tag endpoint is not relied on for unpublished drafts.
    pages = github.api("releases?per_page=100", paginate=True)
    releases = [r for page in pages for r in page]
    matches = [r for r in releases if r.get("tag_name") == tag]
    if not matches:
        drafts = [r.get("tag_name") for r in releases if r.get("draft") is True]
        published = [r.get("tag_name") for r in releases if r.get("draft") is False]
        raise ValueError(
            f"No saved release draft with tag {tag!r} is visible in {github.repository}. "
            f"Visible draft tags (up to 10): {json.dumps(drafts[:10])}. "
            f"Published tags (up to 10): {json.dumps(published[:10])}. "
            f"Open https://github.com/{github.repository}/releases/new, select tag {tag!r} and a target branch, "
            "write your description, and click Save draft. Then start a new Prepare GitHub release run with that exact tag. "
            "A Git tag or an unsaved editor page is not a saved draft. If the draft is already saved, "
            "check its repository/tag and that the workflow token has contents: write access to see drafts.")
    if len(matches) > 1:
        raise ValueError(f"Found {len(matches)} release records for tag {tag!r} in {github.repository}; "
                         "choose a tag with exactly one saved draft")
    release = draft_only(matches[0], tag)
    target = release.get("target_commitish", "")
    if not isinstance(target, str) or not target or "\n" in target or "\r" in target:
        raise ValueError("Draft has an invalid target")
    sha = tag_commit(github, tag)
    if sha is None:
        sha = github.api(f"commits/{quote(target, safe='')}")["sha"]
    if not SHA.fullmatch(sha) or not isinstance(release.get("id"), int) or release["id"] <= 0:
        raise ValueError("Invalid draft commit or ID")
    return {"sha": sha, "release_id": str(release["id"]), "target": target, "version": release_version}


def notes_digest(release):
    return hashlib.sha256(release["body"].encode("utf-8")).hexdigest()


def prepare_source(github, tag, release_id, sha, target, root):
    release_version = version(tag)
    if not SHA.fullmatch(sha):
        raise ValueError("Expected a full source commit SHA")
    release = draft_only(github.api(f"releases/{release_id}"), tag, release_id)
    if release["target_commitish"] != target:
        raise ValueError("Draft target changed; run preparation again")
    tools = version_tools()
    existing = tag_commit(github, tag)
    if existing is not None:
        if existing != sha:
            raise ValueError("Tag moved; run preparation again")
        # Retries after attachment, and manually created tags, rebuild committed files.
        # Bumping a version at an existing tag would require moving that tag.
        tools.packager.validate(root, tag=tag)
    else:
        branch = github.api(f"git/ref/heads/{quote(target, safe='')}", missing=True)
        if branch is None:
            raise ValueError("A new release must target a branch, such as main, so its version changes can be committed")
        if branch["object"]["type"] != "commit" or branch["object"]["sha"] != sha:
            raise ValueError("Target branch moved; run preparation again")
        changes = tools.plan(root, tag, release["body"])
        latest = draft_only(github.api(f"releases/{release_id}"), tag, release_id)
        if latest["target_commitish"] != target or notes_digest(latest) != notes_digest(release):
            raise ValueError("Draft changed while preparing versions; run preparation again")
        if changes:
            sha = github.commit_files(target, sha, changes, tag)
    return {"sha": sha, "release_id": str(release_id), "target": target,
            "version": release_version, "notes_digest": notes_digest(release)}


def checked_assets(directory, tag):
    release_version = version(tag)
    paths = [directory / f"valheim-boosted-{release_version}{suffix}.zip" for suffix in ("", "-plugins")]
    payloads = []
    for path, plugins_only in zip(paths, (False, True)):
        if not path.is_file() or path.stat().st_size > 32 * 1024 * 1024:
            raise ValueError(f"Missing or oversized package: {path.name}")
        prefix = "ValheimBoosted/" if plugins_only else "BepInEx/plugins/ValheimBoosted/"
        required = {prefix + name for name in ("ValheimBoosted.dll", "ValheimBoosted.pdb")}
        if not plugins_only:
            required |= {"manifest.json", "README.md", "CHANGELOG.md", "icon.png"}
        optional = {(prefix if plugins_only else "") + "build-references.json"}
        with zipfile.ZipFile(path) as archive:
            names = set(archive.namelist())
            if len(names) != len(archive.infolist()) or not required <= names or names - required - optional:
                raise ValueError(f"Incorrect installation layout: {path.name}")
            if sum(i.file_size for i in archive.infolist()) > 64 * 1024 * 1024 or archive.testzip() is not None:
                raise ValueError(f"Invalid ZIP: {path.name}")
            if not plugins_only and json.loads(archive.read("manifest.json"))["version_number"] != release_version:
                raise ValueError("Built package version differs from the release tag")
            payloads.append({name: archive.read(prefix + name) for name in ("ValheimBoosted.dll", "ValheimBoosted.pdb")})
    if payloads[0] != payloads[1]:
        raise ValueError("The two packages must contain the same mod build")
    return paths


def attach(github, tag, release_id, sha, target, directory, expected_notes_digest=None):
    if not SHA.fullmatch(sha):
        raise ValueError("Expected a full source commit SHA")
    paths = checked_assets(directory, tag)
    endpoint = f"releases/{release_id}"

    def check_draft():
        release = draft_only(github.api(endpoint), tag, release_id)
        if release["target_commitish"] not in (target, sha):
            raise ValueError("Draft target changed during the build; run preparation again")
        if expected_notes_digest is not None and notes_digest(release) != expected_notes_digest:
            raise ValueError("Draft description changed during the build; run preparation again before publishing")
        return release

    check_draft()
    current = tag_commit(github, tag)
    if current is not None and current != sha:
        raise ValueError("Tag moved during the build; refusing to attach mismatched binaries")
    if current is None:
        # Reserve exactly the built commit. Never move an existing tag.
        github.api("git/refs", {"ref": f"refs/tags/{tag}", "sha": sha})
    # Pin the draft too, so branch movement cannot change what is published.
    # Do not send body, name, prerelease, draft or make_latest: editor choices remain intact.
    github.api(endpoint, {"target_commitish": sha})
    check_draft()
    checksum = directory / "SHA256SUMS"
    checksum.write_text("".join(f"{hashlib.sha256(p.read_bytes()).hexdigest()}  {p.name}\n" for p in paths), encoding="utf-8")
    github.upload(tag, [*paths, checksum])
    release = check_draft()
    assets = {a["name"]: a for a in release.get("assets", [])}
    for path in [*paths, checksum]:
        asset = assets.get(path.name)
        if not asset or asset.get("state") != "uploaded" or asset.get("size") != path.stat().st_size:
            raise ValueError(f"Release asset upload incomplete: {path.name}")
        digest = asset.get("digest")
        if digest and digest != "sha256:" + hashlib.sha256(path.read_bytes()).hexdigest():
            raise ValueError(f"Release asset digest mismatch: {path.name}")
    if tag_commit(github, tag) != sha:
        raise ValueError("Tag changed during upload; do not publish this draft")
    return release


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("operation", choices=("resolve", "prepare", "attach"))
    parser.add_argument("--assets", type=Path, default=Path("release-assets"))
    parser.add_argument("--source", type=Path, default=Path("release-source"))
    args = parser.parse_args()
    try:
        tag = os.environ["RELEASE_TAG"]
        github = GitHub(os.environ["GITHUB_REPOSITORY"])
        if args.operation in ("resolve", "prepare"):
            if args.operation == "resolve":
                values = resolve(github, tag)
                summary = f"Selected `{tag}` from `{values['sha']}`. Preparing source versions and changelog next.\n"
            else:
                source_sha = os.environ["RELEASE_SHA"]
                checkout = subprocess.run(["git", "-C", str(args.source), "rev-parse", "HEAD"],
                                          text=True, capture_output=True, check=True).stdout.strip()
                if checkout != source_sha:
                    raise ValueError("Source checkout does not match the selected release commit")
                values = prepare_source(github, tag, int(os.environ["RELEASE_ID"]), source_sha,
                                        os.environ["RELEASE_TARGET"], args.source)
                summary = f"Versions and changelog ready for `{tag}`. Building commit [`{values['sha']}`](https://github.com/{github.repository}/commit/{values['sha']}).\n"
            with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
                for key, value in values.items():
                    output.write(f"{key}={value}\n")
        else:
            result = attach(github, tag, int(os.environ["RELEASE_ID"]), os.environ["RELEASE_SHA"],
                            os.environ["RELEASE_TARGET"], args.assets, os.environ["RELEASE_NOTES_DIGEST"])
            kind = "pre-release" if result["prerelease"] else "release"
            summary = f"Both installation ZIPs and `SHA256SUMS` are attached to the **{kind} draft** for `{tag}` at `{os.environ['RELEASE_SHA']}`.\n\nReview the draft and select **Publish release** when ready. Publishing starts the versioned metrics-image workflow.\n"
        summary += f"\n[Open releases and drafts](https://github.com/{github.repository}/releases)\n"
        with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as output:
            output.write(summary)
    except (ValueError, OSError, KeyError, RuntimeError, subprocess.CalledProcessError, zipfile.BadZipFile) as exc:
        if summary_path := os.environ.get("GITHUB_STEP_SUMMARY"):
            with open(summary_path, "a", encoding="utf-8") as output:
                output.write(f"### Release preparation stopped\n\n{exc}\n")
        parser.exit(1, f"Release preparation failed: {exc}\n")


if __name__ == "__main__":
    main()
