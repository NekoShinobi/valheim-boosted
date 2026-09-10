"""Plan and validate version/changelog edits without modifying the source checkout."""
import importlib.util
from pathlib import Path
import re
import tempfile

spec = importlib.util.spec_from_file_location("release_packager", Path(__file__).with_name("package-mod.py"))
packager = importlib.util.module_from_spec(spec)
spec.loader.exec_module(packager)

VERSION_FILES = ("mod/ValheimBoosted.csproj", "mod/Plugin.cs", "package.json", "thunderstore/manifest.json")
EDITABLE_FILES = (*VERSION_FILES, "CHANGELOG.md", "README.md", "thunderstore/README.md")
VALIDATION_FILES = (*EDITABLE_FILES, "toolchain.lock.json", "thunderstore/icon.png")
NOTES_START = "<!-- valheim-boosted:release-notes:start -->"
NOTES_END = "<!-- valheim-boosted:release-notes:end -->"


def changelog_notes(notes):
    if not isinstance(notes, str) or not notes.strip():
        raise ValueError("Write a release description before preparing the version")
    if NOTES_START in notes or NOTES_END in notes:
        raise ValueError("Release description contains reserved changelog markers")
    # Nest the editor's headings beneath the version heading; keep code literal.
    lines = list(packager.markdown_lines(notes.replace("\r\n", "\n").strip() + "\n\n"))
    if lines[-1][2]:
        raise ValueError("Close the Markdown code fence in the release description")
    rendered = []
    for _, line, code in lines:
        if not code:
            line = re.sub(r"^( {0,3})(#{1,6})([ \t]+.*)$",
                          lambda m: m[1] + "#" * min(len(m[2]) + 2, 6) + m[3], line)
        rendered.append(line)
    return NOTES_START + "\n" + "".join(rendered).rstrip() + "\n" + NOTES_END + "\n"


def update_changelog(text, tag, notes):
    sections = packager.release_sections(text)
    if not sections:
        raise ValueError("Changelog has no version sections")
    first = sections[0]
    block = changelog_notes(notes)
    if first["version"] != tag:
        return text[:first["start"]] + f"## v{tag}\n\n{block}\n" + text[first["start"]:]
    end = sections[1]["start"] if len(sections) > 1 else len(text)
    body = text[first["end"]:end]
    if NOTES_START in body or NOTES_END in body:
        if body.count(NOTES_START) != 1 or body.count(NOTES_END) != 1:
            raise ValueError("Ambiguous generated changelog notes")
        start = body.index(NOTES_START)
        finish = body.index(NOTES_END)
        if finish < start:
            raise ValueError("Invalid generated changelog notes")
        body = body[:start] + block.rstrip("\n") + body[finish + len(NOTES_END):]
    else:
        # Retain an already hand-written entry, including the initial pre-alpha notes.
        body = "\n" + block + "\n" + body.lstrip("\n")
    return text[:first["end"]] + body + text[end:]


def plan(root, tag, notes):
    if not re.fullmatch(packager.VERSION, tag):
        raise ValueError("Release version must use Major.Minor.Patch without a v prefix")
    root = root.resolve()
    originals = {}
    for name in VALIDATION_FILES:
        path = root / name
        if path.is_symlink() or not path.resolve().is_relative_to(root):
            raise ValueError(f"Release metadata must be a regular file inside the checkout: {name}")
        originals[name] = path.read_bytes()
    current = packager.validate(root)["version_number"]
    if tuple(map(int, tag.split("."))) < tuple(map(int, current.split("."))):
        raise ValueError(f"Cannot decrease version from {current} to {tag}")
    changes = {}
    patterns = {
        "mod/ValheimBoosted.csproj": r"(<Version>)[^<]+(</Version>)",
        "mod/Plugin.cs": r'(const string PluginVersion = ")[^"]+(";)',
        "package.json": r'(^  "version": ")[^"]+(")',
        "thunderstore/manifest.json": r'(^  "version_number": ")[^"]+(")',
    }
    for name, pattern in patterns.items():
        changed, count = re.subn(pattern, lambda m: m[1] + tag + m[2],
                                 originals[name].decode("utf-8"), flags=re.MULTILINE)
        if count != 1:
            raise ValueError(f"Expected one version field in {name}")
        changes[name] = changed.encode("utf-8")
    changes["CHANGELOG.md"] = update_changelog(originals["CHANGELOG.md"].decode("utf-8"), tag, notes).encode("utf-8")
    for name in ("README.md", "thunderstore/README.md"):
        text = originals[name].decode("utf-8")
        text = text.replace(f"valheim-boosted-{current}.zip", f"valheim-boosted-{tag}.zip")
        text = text.replace(f"valheim-boosted-{current}-plugins.zip", f"valheim-boosted-{tag}-plugins.zip")
        text = text.replace(f"<br>{current} ·", f"<br>{tag} ·")
        text = text.replace(f"`{current}` ·", f"`{tag}` ·")
        changes[name] = text.encode("utf-8")
    # Validate the complete candidate before making any repository mutations.
    with tempfile.TemporaryDirectory(prefix="release-metadata-") as directory:
        candidate = Path(directory)
        for name, original in originals.items():
            path = candidate / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(changes.get(name, original))
        packager.validate(candidate, tag=tag)
    return {name: data for name, data in changes.items() if data != originals[name]}
