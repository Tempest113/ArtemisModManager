"""Builds the app logo files from the flat blue-on-white source image.

    python manager/tools/make_logo.py <source image> [<out dir>]

Writes logo.png (transparent, square, 256 px) and app.ico (16-256 px) into the app's Assets folder. The source is a flat one-colour
shape on white, so its transparency is recovered exactly: how far a pixel is from white, relative to how far the shape's colour
is, is its opacity. That keeps the anti-aliased edges clean on a dark background (no white fringe).
"""
import sys
from pathlib import Path

from PIL import Image

src = Path(sys.argv[1])
out = Path(sys.argv[2]) if len(sys.argv) > 2 else Path(__file__).resolve().parents[1] / "src" / "Artemis.App" / "Assets"
out.mkdir(parents=True, exist_ok=True)

rgb = Image.open(src).convert("RGB")
red = rgb.getchannel("R")
# Shape colour: the median of the clearly-solid pixels.
px = [p for p in rgb.get_flattened_data() if p[0] < 120]
fg = tuple(sorted(p[i] for p in px)[len(px) // 2] for i in range(3))

span = 255 - fg[0]
alpha = red.point(lambda v: 0 if v > 253 else min(255, round((255 - v) / span * 255)))
logo = Image.new("RGBA", rgb.size, fg + (0,))
logo.putalpha(alpha)

# Crop to the shape, then centre it on a square canvas with a little air around it.
logo = logo.crop(logo.getchannel("A").point(lambda a: 255 if a > 8 else 0).getbbox())
side = round(max(logo.size) * 1.08)
square = Image.new("RGBA", (side, side), fg + (0,))
square.paste(logo, ((side - logo.width) // 2, (side - logo.height) // 2))
square = square.resize((256, 256), Image.LANCZOS)

square.save(out / "logo.png", optimize=True)
square.save(out / "app.ico", sizes=[(s, s) for s in (16, 20, 24, 32, 40, 48, 64, 128, 256)])
print("shape colour", "#%02X%02X%02X" % fg, "->", out)
