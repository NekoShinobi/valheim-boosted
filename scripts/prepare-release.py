#!/usr/bin/env python3
"""Build inputs and draft-only asset attachment for the release-editor workflow."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
from urllib.parse import quote
import zipfile

TAG = re.compile(r"(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)")
SHA = re.compile(r"[0-9a-f]{40}")


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


def version(tag):
    match = TAG.fullmatch(tag)
    if not match:
        raise ValueError("Use a numeric Major.Minor.Patch tag without a v prefix; choose pre-release using the release editor's checkbox")
    return match[0]


def draft_only(release, tag, release_id=None):
    if not release or release.get("tag_name") != tag or release_id is not None and release.get("id") != release_id:
        raise ValueError("Draft was removed or its tag changed; save the intended draft and run the workflow again")
    if release.get("draft") is not True or release.get("immutable"):
        raise ValueError("This workflow only attaches to drafts. Published releases are never replaced")
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
    matches = [r for page in pages for r in page if r.get("tag_name") == tag]
    if len(matches) != 1:
        raise ValueError("Save exactly one draft for this tag in Releases → Draft a new release first")
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


def attach(github, tag, release_id, sha, target, directory):
    if not SHA.fullmatch(sha):
        raise ValueError("Expected a full source commit SHA")
    paths = checked_assets(directory, tag)
    endpoint = f"releases/{release_id}"

    def check_draft():
        release = draft_only(github.api(endpoint), tag, release_id)
        if release["target_commitish"] not in (target, sha):
            raise ValueError("Draft target changed during the build; run preparation again")
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
    parser.add_argument("operation", choices=("resolve", "attach"))
    parser.add_argument("--assets", type=Path, default=Path("release-assets"))
    args = parser.parse_args()
    try:
        tag = os.environ["RELEASE_TAG"]
        github = GitHub(os.environ["GITHUB_REPOSITORY"])
        if args.operation == "resolve":
            values = resolve(github, tag)
            with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
                for key, value in values.items():
                    output.write(f"{key}={value}\n")
            summary = f"Building `{tag}` from `{values['sha']}`. Your saved title, Markdown notes and pre-release choice will be preserved.\n"
        else:
            result = attach(github, tag, int(os.environ["RELEASE_ID"]), os.environ["RELEASE_SHA"], os.environ["RELEASE_TARGET"], args.assets)
            kind = "pre-release" if result["prerelease"] else "release"
            summary = f"Both installation ZIPs and `SHA256SUMS` are attached to the **{kind} draft** for `{tag}` at `{os.environ['RELEASE_SHA']}`.\n\nReview the draft and select **Publish release** when ready. Publishing starts the versioned metrics-image workflow.\n"
        summary += f"\n[Open releases and drafts](https://github.com/{github.repository}/releases)\n"
        with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as output:
            output.write(summary)
    except (ValueError, OSError, KeyError, RuntimeError, subprocess.CalledProcessError, zipfile.BadZipFile) as exc:
        parser.exit(1, f"Release preparation failed: {exc}\n")


if __name__ == "__main__":
    main()
