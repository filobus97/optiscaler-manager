#!/usr/bin/env python3
"""Renders the Upscaler Manager app icon.

No image libraries are assumed to be available, so this rasterises and encodes
PNG/ICO directly. Run it to regenerate assets/icons/ after changing the design.

The mark: a rounded square in the app's accent purple with a cartoon cat's head.

Detail is chosen per size. Eyes, nose and inner ears are drawn from 48px up; at
32px and below they collapse into a smudge and read worse than nothing, so those
sizes get the plain silhouette instead. Shapes are painted in order, later ones
over earlier ones.
"""
import struct, zlib, os

OUT = os.path.join(os.path.dirname(__file__), "..", "assets", "icons")
SIZES = [16, 24, 32, 48, 64, 128, 256]
SS = 4  # supersampling factor, for antialiasing

# Below this the face turns to mush; those sizes get the plain silhouette.
DETAIL_FROM = 48

TOP = (0x9D, 0x88, 0xFF)   # AccentPrimaryHover
BOT = (0x75, 0x60, 0xE0)   # AccentPrimaryPressed
FUR = (0xFF, 0xFF, 0xFF)
EYE = (0x2A, 0x24, 0x40)   # BgBase — reads as near-black on the white face
NOSE = (0xF2, 0x8F, 0xA8)
INNER_EAR = (0xF2, 0x8F, 0xA8)


def rounded_rect(x, y, w, h, r):
    """Coverage test for a rounded rectangle occupying [0,w)x[0,h)."""
    cx = min(max(x, r), w - r)
    cy = min(max(y, r), h - r)
    dx, dy = x - cx, y - cy
    return dx * dx + dy * dy <= r * r


def in_poly(x, y, pts):
    inside = False
    n = len(pts)
    for i in range(n):
        x1, y1 = pts[i]
        x2, y2 = pts[(i + 1) % n]
        if (y1 > y) != (y2 > y):
            xin = (x2 - x1) * (y - y1) / (y2 - y1) + x1
            if x < xin:
                inside = not inside
    return inside


def in_ellipse(x, y, cx, cy, rx, ry):
    dx, dy = (x - cx) / rx, (y - cy) / ry
    return dx * dx + dy * dy <= 1.0


def poly_ellipse(cx, cy, rx, ry, n=48, squash_top=None):
    """An ellipse as a polygon, so it can be composed like any other shape."""
    import math
    pts = []
    for i in range(n):
        a = 2.0 * math.pi * i / n
        pts.append((cx + rx * math.cos(a), cy + ry * math.sin(a)))
    return pts


def cat_shapes(w, detailed):
    """
    The cat's head as (colour, shape) pairs, painted in order.

    `detailed` adds the face; without it the silhouette is left plain, which is
    what small sizes need.
    """
    u = w / 100.0
    shapes = []

    # Ears. The notch between them must sit above the top of the face, or the
    # face fills the V and the whole mark reads as a featureless blob.
    left_ear = [(21.0 * u, 58.0 * u), (23.0 * u, 6.0 * u), (52.0 * u, 36.0 * u)]
    right_ear = [(79.0 * u, 58.0 * u), (77.0 * u, 6.0 * u), (48.0 * u, 36.0 * u)]
    shapes.append((FUR, ("poly", left_ear)))
    shapes.append((FUR, ("poly", right_ear)))

    # Face: wider than tall, which is what makes it read as a cat and not a bear.
    shapes.append((FUR, ("ellipse", 50.0 * u, 64.0 * u, 33.0 * u, 27.0 * u)))

    if not detailed:
        return shapes

    # Inner ears, well inset so a clear rim of fur stays around them — otherwise the
    # ear reads as a pink triangle with a white outline rather than as an ear.
    shapes.append((INNER_EAR, ("poly",
        [(30.0 * u, 48.0 * u), (31.0 * u, 22.0 * u), (45.0 * u, 39.0 * u)])))
    shapes.append((INNER_EAR, ("poly",
        [(70.0 * u, 48.0 * u), (69.0 * u, 22.0 * u), (55.0 * u, 39.0 * u)])))

    # Eyes: big and round, set wide. Cartoon proportions, not anatomical ones.
    shapes.append((EYE, ("ellipse", 38.0 * u, 60.0 * u, 6.5 * u, 8.0 * u)))
    shapes.append((EYE, ("ellipse", 62.0 * u, 60.0 * u, 6.5 * u, 8.0 * u)))
    # A catchlight in each turns two dots into eyes.
    shapes.append((FUR, ("ellipse", 40.3 * u, 57.0 * u, 2.2 * u, 2.6 * u)))
    shapes.append((FUR, ("ellipse", 64.3 * u, 57.0 * u, 2.2 * u, 2.6 * u)))

    # Nose: a small rounded triangle.
    shapes.append((NOSE, ("poly",
        [(44.5 * u, 73.0 * u), (55.5 * u, 73.0 * u), (50.0 * u, 79.5 * u)])))

    return shapes


def colour_at(shapes, x, y):
    """Topmost shape covering this point, or None for background."""
    found = None
    for colour, shape in shapes:
        if shape[0] == "poly":
            if in_poly(x, y, shape[1]):
                found = colour
        else:
            _, cx, cy, rx, ry = shape
            if in_ellipse(x, y, cx, cy, rx, ry):
                found = colour
    return found




def render(size):
    w = size * SS
    radius = w * 0.22
    shapes = cat_shapes(w, detailed=size >= DETAIL_FROM)
    px = bytearray()
    for py_ in range(size):
        row = bytearray()
        for px_ in range(size):
            r = g = b = a = 0
            for sy in range(SS):
                for sx in range(SS):
                    x = px_ * SS + sx + 0.5
                    y = py_ * SS + sy + 0.5
                    if not rounded_rect(x, y, w, w, radius):
                        continue
                    hit = colour_at(shapes, x, y)
                    if hit is not None:
                        cr, cg, cb = hit
                    else:
                        t = y / w
                        cr = int(TOP[0] + (BOT[0] - TOP[0]) * t)
                        cg = int(TOP[1] + (BOT[1] - TOP[1]) * t)
                        cb = int(TOP[2] + (BOT[2] - TOP[2]) * t)
                    r += cr; g += cg; b += cb; a += 255
            n = SS * SS
            if a == 0:
                row += bytes(4)
            else:
                # Un-premultiply so edges stay the right hue as they fade out.
                cov = a / (255.0 * n)
                row += bytes((int(r / (a / 255.0)), int(g / (a / 255.0)),
                              int(b / (a / 255.0)), int(round(cov * 255))))
        px += b"\x00" + row
    return bytes(px)


def png(size, raw):
    def chunk(tag, data):
        c = struct.pack(">I", len(data)) + tag + data
        return c + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)
    ihdr = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr)
            + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def main():
    os.makedirs(OUT, exist_ok=True)
    pngs = {}
    for s in SIZES:
        data = png(s, render(s))
        pngs[s] = data
        with open(os.path.join(OUT, f"icon-{s}.png"), "wb") as f:
            f.write(data)
        print(f"  icon-{s}.png  {len(data)} bytes")

    # ICO with PNG-compressed entries (supported since Vista).
    entries = [s for s in SIZES if s <= 256]
    header = struct.pack("<HHH", 0, 1, len(entries))
    offset = 6 + 16 * len(entries)
    dirs, blobs = b"", b""
    for s in entries:
        d = pngs[s]
        dirs += struct.pack("<BBBBHHII", s if s < 256 else 0, s if s < 256 else 0,
                            0, 0, 1, 32, len(d), offset)
        blobs += d
        offset += len(d)
    with open(os.path.join(OUT, "icon.ico"), "wb") as f:
        f.write(header + dirs + blobs)
    print(f"  icon.ico     {len(header + dirs + blobs)} bytes")


if __name__ == "__main__":
    main()
