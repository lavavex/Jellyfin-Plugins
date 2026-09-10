#!/usr/bin/env python3
"""Compose plugin catalogue logos.

Matches the official Jellyfin plugin images: 1920x1080, near-black ground with a
soft radial lift, rounded corners, and the service's own mark centred. No added
wordmark — each project's logo speaks for itself.

Source marks are vendored in src/ so the build is reproducible offline:
  suwayomi-mark.png   the Suwayomi project logo (suwayomi.com)
  mangabaka-mark.png  the MangaBaka mascot, hand-cut from a source frame
"""
import math
import os

from PIL import Image, ImageDraw

W, H = 1920, 1080
CORNER = 22
OUT = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(OUT, "src")


def background():
    """Near-black with a soft radial lift behind the mark."""
    im = Image.new("RGB", (W, H), (26, 26, 26))
    px = im.load()
    cx, cy = W / 2, H / 2
    far = math.hypot(cx, cy)
    for y in range(H):
        for x in range(0, W, 2):
            t = 1.0 - min(1.0, math.hypot(x - cx, y - cy) / far)
            v = int(23 + (t ** 1.8) * 27)     # 23 at the edges, ~50 at the core
            px[x, y] = (v, v, v)
            if x + 1 < W:
                px[x + 1, y] = (v, v, v)
    return im


def round_corners(im, radius=CORNER):
    mask = Image.new("L", (W, H), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, W - 1, H - 1), radius=radius, fill=255)
    out = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    out.paste(im, (0, 0), mask)
    return out


def trim(im):
    """Drop fully transparent margins so marks are sized by their real extent."""
    if im.mode != "RGBA":
        return im
    box = im.getchannel("A").getbbox()
    return im.crop(box) if box else im


def compose(mark_file, out_file, target_h=560, corner=None):
    mark = trim(Image.open(os.path.join(SRC, mark_file)).convert("RGBA"))
    scale = target_h / mark.height
    mark = mark.resize((max(1, round(mark.width * scale)), target_h), Image.LANCZOS)

    # Kept for marks that arrive as a square tile with a baked-in backdrop.
    if corner:
        m = Image.new("L", mark.size, 0)
        ImageDraw.Draw(m).rounded_rectangle((0, 0, mark.size[0] - 1, mark.size[1] - 1),
                                            radius=corner, fill=255)
        mark.putalpha(m)

    im = background()
    im_rgba = im.convert("RGBA")
    im_rgba.alpha_composite(mark, ((W - mark.width) // 2, (H - mark.height) // 2))
    round_corners(im_rgba.convert("RGB")).save(os.path.join(OUT, out_file), optimize=True)
    return out_file


if __name__ == "__main__":
    made = [
        compose("suwayomi-mark.png", "suwayomi.png", target_h=580),
        compose("mangabaka-mark.png", "mangabaka.png", target_h=620),
    ]
    for f in made:
        p = os.path.join(OUT, f)
        im = Image.open(p)
        print(f"  {f}  {im.size[0]}x{im.size[1]}  {os.path.getsize(p) // 1024} KB")
