"""Makes the plain background images the built-in themes ship with: a theme's own background color, two soft
glows in its accent colors, a light vignette and a little noise (so a smooth gradient does not band).

    python -I tools/Make-ThemeBackgrounds.py [theme ...]

With no names it remakes every picture; with names, only those (the noise differs each run, so the others stay as they are).

Writes Resources/Themes/Backgrounds/<theme>.jpg, which the csproj embeds. They are placeholders meant to be
replaced: drop a better JPEG over a file and rebuild. Needs Pillow.

The picture is worked out per pixel on a small canvas and scaled up, so every edge in it is smooth.
"""
import math
import pathlib
import sys

from PIL import Image, ImageChops

W, H = 2560, 1440
SMALL_W, SMALL_H = 320, 180
OUT = pathlib.Path(__file__).resolve().parent.parent / "Resources" / "Themes" / "Backgrounds"

# name: (background, glow one, glow two, glow strength, vignette strength, vignette color)
THEMES = {
    "dark":  ("#121214", "#1F4E8C", "#12335E", 0.60, 0.55, "#000000"),
    "light": ("#F3F3F6", "#9CC3EE", "#BBD6F2", 0.60, 0.30, "#9A9AB0"),
    "green": ("#121214", "#1E6B33", "#0F4A3A", 0.60, 0.55, "#000000"),
    "amber": ("#121214", "#8A4A00", "#5A3410", 0.60, 0.55, "#000000"),
    "purple": ("#15111F", "#5B2D9E", "#3A1F66", 0.60, 0.55, "#000000"),
}

# The TT mark, drawn faint in the lower right (the cards cover the middle): theme: (front T, back T, opacity).
# Traced from Assets/app_icon_1024.png, in that picture's 1024-pixel units. A theme not listed has no mark.
# Every theme has it in the same place and size; only the tint follows the theme's accent.
MARKS = {
    "dark": ("#3A9BFF", "#DDE8F8", 0.18),
    "green": ("#3FB950", "#DDF3E2", 0.18),
    "amber": ("#F0A030", "#F8E8D0", 0.18),
    "purple": ("#A67CF5", "#EDE5FA", 0.18),
    "light": ("#1F6FD0", "#45536B", 0.15),
}
MARK_BACK = ((250, 205), (732, 205), (700, 334), (528, 334), (454, 637), (302, 686), (389, 334), (218, 334))
MARK_FRONT = ((425, 410), (906, 410), (874, 540), (703, 540), (627, 843), (476, 890), (563, 540), (393, 540))
MARK_HEIGHT = 0.15  # of the picture's height. The picture is scaled to the window, so a taller window shows it larger: keep it small.
# Built-in themes keep the picture's right edge in view ("position": "right"), so the mark can sit close to it. It is the
# same distance from the right edge and the bottom edge, in the picture's own pixels.
MARK_INSET = 88  # from the right edge: clears the pages' scroll bar (about 20 window pixels), which a game library nearly always has
MARK_INSET_BOTTOM = 40  # from the bottom edge: about as far below the mark as the scroll bar is to its right
MARK_SIDE = "right"  # "left" puts it the same distance in from the left edge instead

# Where each glow sits (fractions of the width and height) and how far it spreads.
GLOWS = ((0.80, 0.90, 0.45, 0.60), (0.10, 0.05, 0.40, 0.55))


def rgb(hex_text: str):
    h = hex_text.lstrip("#")
    return tuple(int(h[i:i + 2], 16) for i in (0, 2, 4))


def lerp(a, b, t):
    return tuple(x + (y - x) * t for x, y in zip(a, b))


def smoothstep(lo, hi, v):
    t = min(1.0, max(0.0, (v - lo) / (hi - lo)))
    return t * t * (3 - 2 * t)


def add_mark(img, front, back, opacity):
    """Draws the TT mark at 2x and shrinks it, so its edges are smooth, then lays it over the picture."""
    from PIL import ImageDraw
    # The mark spans x 218..906 and y 205..890 of the 1024 icon.
    x0, y0, x1, y1 = 218, 205, 906, 890
    scale = (H * MARK_HEIGHT) / (y1 - y0)
    w, h = round((x1 - x0) * scale), round((y1 - y0) * scale)
    layer = Image.new("RGBA", (w * 2, h * 2), (0, 0, 0, 0))
    draw = ImageDraw.Draw(layer)
    for points, color in ((MARK_BACK, back), (MARK_FRONT, front)):
        draw.polygon([((x - x0) * scale * 2, (y - y0) * scale * 2) for x, y in points], fill=rgb(color) + (255,))
    layer = layer.resize((w, h), Image.LANCZOS)
    alpha = layer.getchannel("A").point(lambda a: int(a * opacity))
    layer.putalpha(alpha)
    out = img.convert("RGBA")
    left = W - w - MARK_INSET if MARK_SIDE == "right" else MARK_INSET
    out.alpha_composite(layer, (left, H - h - MARK_INSET_BOTTOM))
    return out.convert("RGB")


def make(name, spec):
    background, one, two, strength, vignette, edge = spec
    base, colors, edge_color = rgb(background), (rgb(one), rgb(two)), rgb(edge)

    small = Image.new("RGB", (SMALL_W, SMALL_H))
    pixels = []
    for y in range(SMALL_H):
        v = (y + 0.5) / SMALL_H
        for x in range(SMALL_W):
            u = (x + 0.5) / SMALL_W
            c = base
            for color, (cx, cy, sx, sy) in zip(colors, GLOWS):
                g = math.exp(-(((u - cx) / sx) ** 2 + ((v - cy) / sy) ** 2)) * strength
                c = lerp(c, color, min(1.0, g))
            d = ((u - 0.5) / 0.5) ** 2 + ((v - 0.5) / 0.5) ** 2
            c = lerp(c, edge_color, vignette * smoothstep(0.35, 2.0, d))
            pixels.append(tuple(int(round(max(0, min(255, k)))) for k in c))
    small.putdata(pixels)

    img = small.resize((W, H), Image.BICUBIC)
    noise = Image.effect_noise((W, H), 3).convert("RGB")
    img = ImageChops.add(img, noise, scale=1, offset=-128)

    if name in MARKS:
        img = add_mark(img, *MARKS[name])

    OUT.mkdir(parents=True, exist_ok=True)
    path = OUT / f"{name}.jpg"
    img.save(path, "JPEG", quality=82, optimize=True, progressive=True)
    return path


if __name__ == "__main__":
    wanted = sys.argv[1:] or list(THEMES)
    for theme in wanted:
        spec = THEMES[theme]
        path = make(theme, spec)
        print(f"{theme:6} {path.stat().st_size // 1024} KB")
    sys.exit(0)
