"""Rebuild Assets/AppIcon.ico with taskbar-friendly sizes from Brand PNGs.

Pillow's ICO saver often collapses to a single 16x16 entry; this writes the
ICO container explicitly so 16/24/32/48/64/128/256 all ship in one file.
"""
from __future__ import annotations

import struct
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
BRAND = ROOT / "src" / "Orbit.App" / "Assets" / "Brand"
OUT = ROOT / "src" / "Orbit.App" / "Assets" / "AppIcon.ico"

SOURCES = {
    256: BRAND / "orbit-256.png",
    128: BRAND / "orbit-128.png",
    64: BRAND / "orbit-64.png",
    48: BRAND / "orbit-48.png",
    32: BRAND / "orbit-32.png",
    24: BRAND / "orbit-tray-24.png",
}
SIZES = (16, 24, 32, 48, 64, 128, 256)


def load_rgba(size: int, base: Image.Image) -> Image.Image:
    src = SOURCES.get(size)
    if src is not None and src.exists():
        img = Image.open(src).convert("RGBA")
        if img.size != (size, size):
            img = img.resize((size, size), Image.Resampling.LANCZOS)
        return img
    return base.resize((size, size), Image.Resampling.LANCZOS)


def png_bytes(img: Image.Image) -> bytes:
    from io import BytesIO

    buf = BytesIO()
    img.save(buf, format="PNG")
    return buf.getvalue()


def write_ico(path: Path, images: list[tuple[int, bytes]]) -> None:
    # ICONDIR + ICONDIRENTRY[] + PNG payloads (Vista+ style).
    count = len(images)
    offset = 6 + (16 * count)
    entries = bytearray()
    payloads = bytearray()
    for size, data in images:
        w = 0 if size >= 256 else size
        h = 0 if size >= 256 else size
        entries += struct.pack("<BBBBHHII", w, h, 0, 0, 1, 32, len(data), offset)
        payloads += data
        offset += len(data)

    path.write_bytes(struct.pack("<HHH", 0, 1, count) + entries + payloads)


def main() -> None:
    base = Image.open(SOURCES[256]).convert("RGBA")
    images = [(size, png_bytes(load_rgba(size, base))) for size in SIZES]
    write_ico(OUT, images)

    raw = OUT.read_bytes()
    count = struct.unpack_from("<H", raw, 4)[0]
    print(f"wrote {OUT} ({OUT.stat().st_size} bytes, {count} entries)")
    for i in range(count):
        o = 6 + (i * 16)
        w, h = raw[o], raw[o + 1]
        print(f"  {256 if w == 0 else w}x{256 if h == 0 else h}")


if __name__ == "__main__":
    main()
