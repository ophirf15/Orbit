"""Crop empty padding from Orbit brand marks and regenerate icon sizes."""

from __future__ import annotations

from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
BRAND = ROOT / "src" / "Orbit.App" / "Assets" / "Brand"
ASSETS = ROOT / "src" / "Orbit.App" / "Assets"


def is_content(px: tuple[int, int, int, int]) -> bool:
    r, g, b, a = px
    if a < 20:
        return False
    return (r + g + b) > 45


def crop_to_mark(src: Image.Image) -> Image.Image:
    arr = list(src.getdata())
    w, h = src.size
    xs: list[int] = []
    ys: list[int] = []
    for y in range(h):
        row = y * w
        for x in range(w):
            if is_content(arr[row + x]):
                xs.append(x)
                ys.append(y)
    if not xs:
        raise SystemExit("no content found in logo")

    minx, maxx, miny, maxy = min(xs), max(xs), min(ys), max(ys)
    cw, ch = maxx - minx + 1, maxy - miny + 1
    side = max(cw, ch)
    pad = max(1, int(side * 0.04))
    side = side + pad * 2
    cx = (minx + maxx) / 2
    cy = (miny + maxy) / 2
    left = int(round(cx - side / 2))
    top = int(round(cy - side / 2))
    left = max(0, min(left, w - side))
    top = max(0, min(top, h - side))

    if left + side > w or top + side > h or side > w or side > h:
        left = max(0, minx - pad)
        top = max(0, miny - pad)
        right = min(w, maxx + 1 + pad)
        bottom = min(h, maxy + 1 + pad)
        cropped = src.crop((left, top, right, bottom))
        side = max(cropped.size)
        sq = Image.new("RGBA", (side, side), (0, 0, 0, 0))
        sq.paste(cropped, ((side - cropped.size[0]) // 2, (side - cropped.size[1]) // 2))
        fill = f"{cw / w:.0%}x{ch / h:.0%}"
        print(f"content bbox=({minx},{miny})-({maxx},{maxy}) cropped={sq.size} fill~{fill}")
        return sq

    sq = src.crop((left, top, left + side, top + side))
    fill = f"{cw / w:.0%}x{ch / h:.0%}"
    print(f"content bbox=({minx},{miny})-({maxx},{maxy}) cropped={sq.size} fill~{fill}")
    return sq


def save_size(img: Image.Image, path: Path, size: int) -> None:
    out = img.resize((size, size), Image.Resampling.LANCZOS)
    out.save(path, "PNG")
    print("wrote", path.name, size)


def centered(mark: Image.Image, size_wh: tuple[int, int], mark_ratio: float = 0.72) -> Image.Image:
    width, height = size_wh
    canvas = Image.new("RGBA", (width, height), (0, 0, 0, 255))
    m = int(min(width, height) * mark_ratio)
    resized = mark.resize((m, m), Image.Resampling.LANCZOS)
    canvas.paste(resized, ((width - m) // 2, (height - m) // 2), resized)
    return canvas


def main() -> None:
    src = Image.open(BRAND / "orbit-256.png").convert("RGBA")
    sq = crop_to_mark(src)

    for size, name in [
        (32, "orbit-32.png"),
        (48, "orbit-48.png"),
        (64, "orbit-64.png"),
        (128, "orbit-128.png"),
        (256, "orbit-256.png"),
        (24, "orbit-tray-24.png"),
    ]:
        save_size(sq, BRAND / name, size)

    save_size(sq, ASSETS / "Square44x44Logo.targetsize-44.png", 44)
    save_size(sq, ASSETS / "Square44x44Logo.targetsize-24_altform-unplated.png", 24)
    save_size(sq, ASSETS / "Square44x44Logo.scale-200.png", 88)
    save_size(sq, ASSETS / "Square44x44Logo.targetsize-48_altform-lightunplated.png", 48)
    save_size(sq, ASSETS / "Square150x150Logo.png", 150)
    save_size(sq, ASSETS / "Square150x150Logo.scale-200.png", 300)
    save_size(sq, ASSETS / "StoreLogo.png", 50)
    save_size(sq, ASSETS / "LockScreenLogo.scale-200.png", 48)

    centered(sq, (620, 300), 0.55).save(ASSETS / "Wide310x150Logo.scale-200.png")
    centered(sq, (1240, 600), 0.42).save(ASSETS / "SplashScreen.scale-200.png")

    ico_sizes = [16, 24, 32, 48, 64, 128, 256]
    frames = [sq.resize((s, s), Image.Resampling.LANCZOS) for s in ico_sizes]
    frames[0].save(
        ASSETS / "AppIcon.ico",
        format="ICO",
        sizes=[(s, s) for s in ico_sizes],
        append_images=frames[1:],
    )
    print("wrote AppIcon.ico")
    print("done")


if __name__ == "__main__":
    main()
