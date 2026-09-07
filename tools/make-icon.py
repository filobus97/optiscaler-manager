#!/usr/bin/env python3
"""Renders the OptiScaler Manager app icon.

No image libraries are assumed to be available, so this rasterises and encodes
PNG/ICO directly. Run it to regenerate assets/icons/ after changing the design.

The mark: a rounded square in the app's accent purple with a white diagonal
double-headed arrow — "scaling" — kept geometric so it still reads at 16px.
"""
import struct, zlib, os

OUT = os.path.join(os.path.dirname(__file__), "..", "assets", "icons")
SIZES = [16, 24, 32, 48, 64, 128, 256]
SS = 4  # supersampling factor, for antialiasing

TOP = (0x9D, 0x88, 0xFF)   # AccentPrimaryHover
BOT = (0x75, 0x60, 0xE0)   # AccentPrimaryPressed
GLYPH = (0xFF, 0xFF, 0xFF)


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


def arrow_polys(w):
    """Diagonal double-headed arrow across the square, as polygons in [0,w] space."""
    u = w / 100.0
    # Shaft: a thick bar from lower-left to upper-right.
    t = 8.0 * u                      # half-thickness
    ax, ay = 30.0 * u, 70.0 * u      # lower-left
    bx, by = 70.0 * u, 30.0 * u      # upper-right
    # Perpendicular offset for the shaft rectangle (direction is 45 degrees).
    px, py = t * 0.7071, t * 0.7071
    shaft = [(ax - px, ay - py), (bx - px, by - py), (bx + px, by + py), (ax + px, ay + py)]

    head = 20.0 * u
    # Upper-right head
    h1 = [(78.0 * u, 22.0 * u), (78.0 * u, 22.0 * u + head), (78.0 * u - head, 22.0 * u)]
    # Lower-left head
    h2 = [(22.0 * u, 78.0 * u), (22.0 * u, 78.0 * u - head), (22.0 * u + head, 78.0 * u)]
    return [shaft, h1, h2]


def render(size):
    w = size * SS
    radius = w * 0.22
    polys = arrow_polys(w)
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
                    if any(in_poly(x, y, p) for p in polys):
                        cr, cg, cb = GLYPH
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
