"""Fix PascalCase identifiers that accidentally contain 'Harbor Court '."""
from __future__ import annotations

import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
FIXES = [
    ("SeedHarbor CourtProject", "SeedHarborCourtProject"),
    ("DemoHarbor CourtMeeting", "DemoHarborCourtMeeting"),
    ("IncludesDemoHarbor Court", "IncludesDemoHarborCourt"),
    ("Harbor CourtStatusEvidence", "HarborCourtStatusEvidence"),
    ("Harbor CourtMeeting_", "HarborCourtMeeting_"),
    ("Harbor CourtBundle_", "HarborCourtBundle_"),
    ("Harbor CourtProject", "HarborCourtProject"),
    ("public void Harbor Court", "public void HarborCourt"),
    ("public async Task Harbor Court", "public async Task HarborCourt"),
]


def main() -> None:
    changed = 0
    paths = list((ROOT / "src").rglob("*.cs")) + list((ROOT / "tests").rglob("*.cs"))
    for path in paths:
        if {"bin", "obj"} & set(path.relative_to(ROOT).parts):
            continue
        text = path.read_text(encoding="utf-8")
        original = text
        for old, new in FIXES:
            text = text.replace(old, new)
        text = re.sub(
            r"(?<![\"'])Harbor Court([A-Z][A-Za-z0-9_]*)",
            r"HarborCourt\1",
            text,
        )
        if text != original:
            path.write_text(text, encoding="utf-8", newline="\n")
            changed += 1
            print(path.relative_to(ROOT))
    print(f"fixed {changed} files")


if __name__ == "__main__":
    main()
