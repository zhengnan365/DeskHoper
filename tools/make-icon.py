"""Generate DeskHoper's icon assets from one set of geometry constants.

One source of truth for shape: the same primitives render the SVG (vector, used
by the website) and the PNG / ICO (embedded in the exe). No SVG rasterizer is
installed here, so the bitmaps are drawn directly rather than converted.

  python tools/make-icon.py --sheet C D     -> bin/icon-sheet.png for review
  python tools/make-icon.py --emit C        -> UI/Assets/DeskHoper.{svg,png,ico}
"""
from __future__ import annotations

import argparse
import sys
from pathlib import Path

from PIL import Image, ImageDraw

REPO = Path(__file__).resolve().parent.parent
ASSETS = REPO / "UI" / "Assets"

CANVAS = 256
BRAND_TOP = (76, 141, 255)
BRAND_BOTTOM = (27, 79, 208)
WHITE = (255, 255, 255)

# primitives: ("grad", corner) | ("rrect", x, y, w, h, r, color, alpha)
#             ("stroke", [(x, y), ...], width, color, alpha)
SWITCH_BAR = 18


def switch_glyph(y_top, y_bottom, x_left, x_right, head, color, alpha=255, width=SWITCH_BAR):
    """Two opposing bars with arrowheads: the switch glyph."""
    return [
        ("stroke", [(x_left, y_top), (x_right - head, y_top)], width, color, alpha),
        ("stroke", [(x_right - head, y_top - head), (x_right, y_top), (x_right - head, y_top + head)], width, color, alpha),
        ("stroke", [(x_right, y_bottom), (x_left + head, y_bottom)], width, color, alpha),
        ("stroke", [(x_left + head, y_bottom - head), (x_left, y_bottom), (x_left + head, y_bottom + head)], width, color, alpha),
    ]


def _base():
    return [("grad", 58)]


DESIGNS = {
    "A": _base() + [
        ("rrect", 40, 52, 80, 60, 12, WHITE, 255),
        ("rrect", 136, 52, 80, 60, 12, WHITE, 199),
    ] + switch_glyph(152, 206, 74, 182, 20, WHITE, width=16),
    "C": _base() + [("rrect", 58, 40, 140, 90, 18, WHITE, 255)] + switch_glyph(164, 204, 76, 180, 26, WHITE),
    "D": _base() + [
        ("rrect", 44, 46, 168, 116, 20, WHITE, 255),
        ("rrect", 112, 162, 32, 16, 0, WHITE, 217),
        ("rrect", 80, 178, 96, 18, 9, WHITE, 217),
    ] + switch_glyph(88, 124, 84, 172, 22, BRAND_BOTTOM),
}


def _gradient_tile(size, radius, top, bottom):
    """Diagonal gradient, built small then scaled: it is smooth by definition."""
    n = 64
    small = Image.new("RGB", (n, n))
    px = small.load()
    for y in range(n):
        for x in range(n):
            t = (x + y) / (2 * (n - 1))
            px[x, y] = tuple(round(a + (b - a) * t) for a, b in zip(top, bottom))
    full = small.resize((size, size), Image.BILINEAR)
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, size - 1, size - 1], radius=radius, fill=255)
    out = full.convert("RGBA")
    out.putalpha(mask)
    return out


def _round_cap(d, x, y, r, fill):
    d.ellipse([x - r, y - r, x + r, y + r], fill=fill)


def _poly(d, pts, width, fill):
    r = width / 2
    for i in range(len(pts) - 1):
        d.line([pts[i], pts[i + 1]], fill=fill, width=max(1, int(round(width))))
    for p in pts:
        _round_cap(d, p[0], p[1], r, fill)


def render(prims, px, supersample=4):
    size = px * supersample
    s = size / CANVAS
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    for p in prims:
        kind = p[0]
        if kind == "grad":
            img.alpha_composite(_gradient_tile(size, p[1] * s, BRAND_TOP, BRAND_BOTTOM))
        elif kind == "rrect":
            _, x, y, w, h, r, color, alpha = p
            d.rounded_rectangle(
                [x * s, y * s, (x + w) * s, (y + h) * s], radius=r * s, fill=color + (alpha,)
            )
        elif kind == "stroke":
            _, pts, width, color, alpha = p
            _poly(d, [(x * s, y * s) for x, y in pts], width * s, color + (alpha,))
    if supersample != 1:
        img = img.resize((px, px), Image.LANCZOS)
    return img


def to_svg(prims):
    out = [
        '<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256">',
        '  <defs><linearGradient id="bg" x1="0" y1="0" x2="0.7" y2="1">',
        '    <stop offset="0" stop-color="#{0:02X}{1:02X}{2:02X}"/>'.format(*BRAND_TOP),
        '    <stop offset="1" stop-color="#{0:02X}{1:02X}{2:02X}"/>'.format(*BRAND_BOTTOM),
        "  </linearGradient></defs>",
    ]
    for p in prims:
        kind = p[0]
        if kind == "grad":
            out.append('  <rect width="256" height="256" rx="{0}" fill="url(#bg)"/>'.format(p[1]))
        elif kind == "rrect":
            _, x, y, w, h, r, color, alpha = p
            extra = ' fill-opacity="{0:.2f}"'.format(alpha / 255) if alpha != 255 else ""
            out.append('  <rect x="{0}" y="{1}" width="{2}" height="{3}" rx="{4}" fill="#{5:02X}{6:02X}{7:02X}"{8}/>'.format(
                x, y, w, h, r, color[0], color[1], color[2], extra))
        elif kind == "stroke":
            _, pts, width, color, alpha = p
            d = "M" + "L".join("{0} {1}".format(a, b) for a, b in pts)
            extra = ' stroke-opacity="{0:.2f}"'.format(alpha / 255) if alpha != 255 else ""
            out.append('  <path d="{0}" fill="none" stroke="#{1:02X}{2:02X}{3:02X}" stroke-width="{4}" stroke-linecap="round" stroke-linejoin="round"{5}/>'.format(
                d, color[0], color[1], color[2], width, extra))
    out.append("</svg>")
    return "\n".join(out) + "\n"


def contact_sheet(names, out_path):
    sizes = [256, 48, 32, 24, 16]
    pad, tile, row_h = 18, 168, 214
    width = pad + (tile + pad) * len(sizes) + pad
    sheet = Image.new("RGBA", (width, pad + row_h * len(names) + pad), (238, 241, 245, 255))
    d = ImageDraw.Draw(sheet)
    for r, name in enumerate(names):
        y = pad + r * row_h
        d.text((pad + 2, y), "方案 " + name, fill=(23, 32, 44, 255))
        for c, px in enumerate(sizes):
            x = pad + c * (tile + pad)
            dark = c % 2 == 1
            bg = (27, 31, 39, 255) if dark else (255, 255, 255, 255)
            top = y + 22
            d.rounded_rectangle([x, top, x + tile, top + tile], radius=10, fill=bg)
            icon = render(DESIGNS[name], px)
            zoom = 3 if px <= 48 else 1
            shown = icon.resize((px * zoom, px * zoom), Image.NEAREST) if zoom > 1 else icon
            fit = tile - 24
            if shown.width > fit:
                shown = icon.resize((fit, fit), Image.LANCZOS)
            sheet.alpha_composite(shown, (x + (tile - shown.width) // 2, top + (tile - shown.height) // 2))
            d.text((x + 8, top + tile + 6), "{0}{1}".format(px, " px ×3" if zoom > 1 else " px"), fill=(90, 102, 120, 255))
    sheet.convert("RGB").save(out_path)
    return out_path


def emit(name):
    prims = DESIGNS[name]
    ASSETS.mkdir(parents=True, exist_ok=True)
    (ASSETS / "DeskHoper.svg").write_text(to_svg(prims), encoding="utf-8", newline="\n")
    render(prims, 256).save(ASSETS / "DeskHoper.png")
    # BMP entries for every size: Icon.ExtractAssociatedIcon (used by the tray)
    # only reads what the resource compiler accepted, and PNG-in-ICO is not it.
    render(prims, 256).save(
        ASSETS / "DeskHoper.ico", format="ICO",
        sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (256, 256)], bitmap_format="bmp",
    )
    return [ASSETS / "DeskHoper.svg", ASSETS / "DeskHoper.png", ASSETS / "DeskHoper.ico"]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--sheet", nargs="+", metavar="NAME")
    ap.add_argument("--emit", metavar="NAME")
    args = ap.parse_args()

    requested = list(args.sheet or []) + ([args.emit] if args.emit else [])
    unknown = [n for n in requested if n not in DESIGNS]
    if unknown:
        print("unknown design(s): " + ", ".join(unknown), file=sys.stderr)
        return 1

    if args.sheet:
        print("sheet:", contact_sheet(args.sheet, REPO / "bin" / "icon-sheet.png"))
        return 0
    if args.emit:
        for path in emit(args.emit):
            print("wrote", path)
        return 0
    print("nothing to do", file=sys.stderr)
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
