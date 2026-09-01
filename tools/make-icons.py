#!/usr/bin/env python3
"""Regenerates the app icons from src/InterviewFlow.App/Assets/logo.png.

    python tools/make-icons.py          # needs Pillow: pip install pillow

Writes:
    src/InterviewFlow.App/Assets/icon.ico    Windows exe + window icon
    tools/macos/InterviewFlow.icns           macOS .app bundle icon

The logo is line art on a white field, so both icons put it on a white
rounded-square plate with transparent corners. Small sizes (<=64 px) use a
tighter crop and a smaller radius — at 16 px the standard padding leaves the
artwork unreadable. The Windows plate is full-bleed; the macOS one is inset to
the 824/1024 Dock grid so it sits the same size as its neighbours.
"""

import struct
from io import BytesIO
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
LOGO = ROOT / "src/InterviewFlow.App/Assets/logo.png"
ICO = ROOT / "src/InterviewFlow.App/Assets/icon.ico"
ICNS = ROOT / "tools/macos/InterviewFlow.icns"

MASTER = 1024
SS = 4  # supersampling factor for the rounded corners
PLATE = (254, 254, 254, 255)  # the logo's own background colour
ICO_SIZES = [16, 24, 32, 48, 64, 128, 256]

# icns entry type -> pixel size (what `iconutil` emits for an .iconset).
ICNS_TYPES = [
    (b"ic11", 32),    # 16pt @2x
    (b"ic12", 64),    # 32pt @2x
    (b"ic07", 128),
    (b"ic13", 256),   # 128pt @2x
    (b"ic08", 256),
    (b"ic14", 512),   # 256pt @2x
    (b"ic09", 512),
    (b"ic10", 1024),  # 512pt @2x
]


def artwork() -> Image.Image:
    """The logo cropped to its ink, background included (it matches PLATE)."""
    logo = Image.open(LOGO).convert("RGB")
    field = Image.new("RGB", logo.size, PLATE[:3])
    ink = ImageChops.difference(logo, field).convert("L").point(lambda v: 255 if v > 5 else 0)
    return logo.crop(ink.getbbox())


def plate(art: Image.Image, inset: float, radius: float, cover: float = 1.0) -> Image.Image:
    """`art` centred on a rounded white square, rendered at MASTER px.

    `cover` is the plate's share of the canvas, `inset` and `radius` are
    fractions of the plate itself.
    """
    big = MASTER * SS
    side = round(big * cover)
    off = (big - side) // 2
    canvas = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    mask = Image.new("L", (big, big), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        (off, off, off + side - 1, off + side - 1), int(radius * side), fill=255)
    canvas.paste(Image.new("RGBA", (big, big), PLATE), mask=mask)

    box = int(side * (1 - 2 * inset))
    scale = min(box / art.width, box / art.height)
    scaled = art.resize((max(1, round(art.width * scale)), max(1, round(art.height * scale))),
                        Image.Resampling.LANCZOS)
    canvas.paste(scaled, ((big - scaled.width) // 2, (big - scaled.height) // 2))
    return canvas.resize((MASTER, MASTER), Image.Resampling.LANCZOS)


def frame(masters: dict[str, Image.Image], size: int) -> Image.Image:
    master = masters["compact" if size <= 64 else "normal"]
    return master.resize((size, size), Image.Resampling.LANCZOS)


def write_icns(frames: dict[int, Image.Image]) -> None:
    entries = []
    for kind, size in ICNS_TYPES:
        buf = BytesIO()
        frames[size].save(buf, "PNG")
        data = buf.getvalue()
        entries.append(kind + struct.pack(">I", len(data) + 8) + data)
    body = b"".join(entries)
    ICNS.write_bytes(b"icns" + struct.pack(">I", len(body) + 8) + body)


def main() -> None:
    art = artwork()
    windows = {
        "normal": plate(art, inset=0.14, radius=0.20),
        "compact": plate(art, inset=0.06, radius=0.16),
    }
    # macOS: 824/1024 plate with the Big Sur corner radius.
    mac = {
        "normal": plate(art, inset=0.14, radius=0.2237, cover=0.824),
        "compact": plate(art, inset=0.06, radius=0.2237, cover=0.824),
    }

    ico_frames = {size: frame(windows, size) for size in ICO_SIZES}
    ico = ico_frames[max(ICO_SIZES)]
    ico.save(ICO, sizes=[(s, s) for s in ICO_SIZES],
             append_images=[ico_frames[s] for s in ICO_SIZES])
    write_icns({size: frame(mac, size) for _, size in ICNS_TYPES})
    print(f"wrote {ICO.relative_to(ROOT)} ({ICO.stat().st_size:,} bytes)")
    print(f"wrote {ICNS.relative_to(ROOT)} ({ICNS.stat().st_size:,} bytes)")


if __name__ == "__main__":
    main()
