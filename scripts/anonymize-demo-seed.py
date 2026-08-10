"""Rename demo seed identities to generic placeholders across the repo.

Idempotent for already-renamed trees. Prefer running once on a clean working copy.
"""
from __future__ import annotations

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SKIP_DIR_NAMES = {".git", "bin", "obj", "artifacts", ".vs", "node_modules"}
TEXT_SUFFIXES = {
    ".cs",
    ".md",
    ".json",
    ".xaml",
    ".csproj",
    ".ps1",
    ".yml",
    ".yaml",
    ".ics",
    ".txt",
    ".xml",
    ".iss",
}

# Longest / most-specific first.
REPLACEMENTS: list[tuple[str, str]] = [
    ("ComcastServesColtonRelId", "MetroFiberServesHarborRelId"),
    ("ComcastServesPropertyBRelId", "MetroFiberServesRiverviewRelId"),
    ("ColtonInternetWorkstreamId", "HarborInternetWorkstreamId"),
    ("PropertyBInternetWorkstreamId", "RiverviewInternetWorkstreamId"),
    ("ColtonExtractionId", "HarborExtractionId"),
    ("PropertyBExtractionId", "RiverviewExtractionId"),
    ("ColtonProjectId", "HarborProjectId"),
    ("PropertyBProjectId", "RiverviewProjectId"),
    ("ColtonTaskId", "HarborTaskId"),
    ("PropertyBTaskId", "RiverviewTaskId"),
    ("ComcastOrgId", "MetroFiberOrgId"),
    ("propertyb-mailbox", "riverview-mailbox"),
    ("colton-mailbox", "harbor-mailbox"),
    ("Property B", "Riverview"),
    ("PROP-B", "RIVER"),
    ("PropertyB", "Riverview"),
    ("propertyb", "riverview"),
    ("COLTON", "HARBOR"),
    ("Colton", "Harbor Court"),
    ("colton", "harbor-court"),
    ("Comcast", "MetroFiber"),
    ("comcast", "metrofiber"),
]


def should_skip(path: Path) -> bool:
    rel = path.relative_to(ROOT).as_posix()
    if set(rel.split("/")) & SKIP_DIR_NAMES:
        return True
    if rel.startswith(("docs/plans/", "docs/product/", "Orbit_Cursor_Build_Pack/")):
        return True
    if path.suffix.lower() not in TEXT_SUFFIXES:
        return True
    return False


def main() -> None:
    changed = 0
    for path in ROOT.rglob("*"):
        if not path.is_file() or should_skip(path):
            continue
        if path.resolve() == Path(__file__).resolve():
            continue
        try:
            text = path.read_text(encoding="utf-8")
        except (UnicodeDecodeError, OSError):
            continue
        original = text
        for old, new in REPLACEMENTS:
            text = text.replace(old, new)
        if text != original:
            path.write_text(text, encoding="utf-8", newline="\n")
            changed += 1
            print(f"updated {path.relative_to(ROOT)}")

    renames = [
        (
            ROOT / "tests/Orbit.Tests/Calendar/Fixtures/colton-mailbox.ics",
            ROOT / "tests/Orbit.Tests/Calendar/Fixtures/harbor-mailbox.ics",
        ),
        (
            ROOT / "tests/Orbit.Tests/Calendar/Fixtures/propertyb-mailbox.ics",
            ROOT / "tests/Orbit.Tests/Calendar/Fixtures/riverview-mailbox.ics",
        ),
        (
            ROOT / "docs/hermes/skills/comcast-setup.md",
            ROOT / "docs/hermes/skills/metrofiber-setup.md",
        ),
    ]
    for src, dest in renames:
        if src.exists():
            dest.parent.mkdir(parents=True, exist_ok=True)
            if dest.exists():
                dest.unlink()
            src.rename(dest)
            print(f"renamed {src.relative_to(ROOT)} -> {dest.relative_to(ROOT)}")

    print(f"done; files content-updated={changed}")


if __name__ == "__main__":
    main()
