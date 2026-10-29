"""Draws the Medal Sync icon and writes tools/medal-sync/MedalSync.Launch/medal-sync.ico.

Run with Python 3.11+ and Pillow: python scripts/generate-icon.py
Each size is drawn separately at 8x and scaled down, so small tray sizes stay sharp.
Optional: --preview DIR also writes one PNG per size for review.
"""

from __future__ import annotations

import argparse
import math
from pathlib import Path

from PIL import Image, ImageDraw

SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)
SCALE = 8
TOP = (124, 58, 237)  # violet
BOTTOM = (67, 56, 202)  # indigo
WHITE = (255, 255, 255, 255)
OUTPUT = Path(__file__).resolve().parent.parent / "tools" / "medal-sync" / "MedalSync.Launch" / "medal-sync.ico"


def background(size: int) -> Image.Image:
    """A rounded square with a vertical violet-to-indigo gradient."""
    gradient = Image.new("RGBA", (size, size))
    pixels = gradient.load()
    for y in range(size):
        t = y / max(1, size - 1)
        color = tuple(round(a + (b - a) * t) for a, b in zip(TOP, BOTTOM)) + (255,)
        for x in range(size):
            pixels[x, y] = color
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, size - 1, size - 1), radius=round(size * 0.22), fill=255)
    gradient.putalpha(mask)
    return gradient


def arrow_arc(draw: ImageDraw.ImageDraw, center: float, radius: float, width: float, start: float, end: float) -> None:
    """A clockwise arc from start to end degrees (0 = right, 90 = down) with an arrowhead at the end."""
    box = (center - radius, center - radius, center + radius, center + radius)
    draw.arc(box, start, end, fill=WHITE, width=round(width))
    angle = math.radians(end)
    tip_x, tip_y = center + radius * math.cos(angle), center + radius * math.sin(angle)
    # Tangent pointing in the direction of travel (clockwise in screen coordinates).
    tx, ty = -math.sin(angle), math.cos(angle)
    nx, ny = math.cos(angle), math.sin(angle)
    head = width * 1.45
    front = (tip_x + tx * head, tip_y + ty * head)
    outer = (tip_x + nx * head - tx * head * 0.15, tip_y + ny * head - ty * head * 0.15)
    inner = (tip_x - nx * head - tx * head * 0.15, tip_y - ny * head - ty * head * 0.15)
    draw.polygon([front, outer, inner], fill=WHITE)


def render(size: int) -> Image.Image:
    big = size * SCALE
    image = background(big)
    draw = ImageDraw.Draw(image)
    c = big / 2
    if size >= 32:
        # Two arrows chasing each other around a play button: recordings kept in sync.
        radius = big * 0.335
        width = big * (0.07 if size >= 48 else 0.085)
        arrow_arc(draw, c, radius, width, 205, 315)
        arrow_arc(draw, c, radius, width, 25, 135)
        play = big * 0.14
    else:
        # Arrowheads blur at tray sizes; two plain arcs keep the same outline.
        radius = big * 0.34
        width = big * 0.105
        box = (c - radius, c - radius, c + radius, c + radius)
        draw.arc(box, 200, 340, fill=WHITE, width=round(width))
        draw.arc(box, 20, 160, fill=WHITE, width=round(width))
        play = big * 0.15
    # A play triangle, optically centered (shifted right by a sixth of its width).
    shift = play * 0.18
    draw.polygon([
        (c - play * 0.75 + shift, c - play),
        (c - play * 0.75 + shift, c + play),
        (c + play * 0.95 + shift, c),
    ], fill=WHITE)
    return image.resize((size, size), Image.Resampling.LANCZOS)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--preview", type=Path, help="also write one PNG per size to this folder")
    args = parser.parse_args()
    images = [render(size) for size in SIZES]
    largest = images[-1]
    largest.save(OUTPUT, format="ICO", sizes=[(s, s) for s in SIZES], append_images=images[:-1])
    if args.preview:
        args.preview.mkdir(parents=True, exist_ok=True)
        for size, image in zip(SIZES, images):
            image.save(args.preview / f"medal-sync-{size}.png")
    print(f"Wrote {OUTPUT} ({', '.join(map(str, SIZES))} px)")


if __name__ == "__main__":
    main()
