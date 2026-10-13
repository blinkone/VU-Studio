#!/usr/bin/env python3
"""Generates vustudio.ico: an amber VU arc and a needle on a dark rounded square.

Pure standard library (no Pillow): shapes are rendered as signed-distance fields with one pixel of
anti-aliasing. Sizes up to 64 px are stored as 32-bit BMP (DIB) entries, 256 px as PNG, which is what
Windows expects. Colors follow web/public/favicon.svg and the web theme.

    python3 src/VuStudio.Tray/Assets/generate-icon.py
"""

import math
import struct
import zlib
from pathlib import Path

SIZES = [16, 20, 24, 32, 40, 48, 64, 256]

BACKGROUND = (0x16, 0x18, 0x1D)
AMBER = (0xFF, 0xB5, 0x47)
NEEDLE = (0xF4, 0xF4, 0xF5)

# Geometry in a 64-unit design space (same as favicon.svg).
PIVOT = (32.0, 46.0)
ARC_RADIUS = 20.0
ARC_HALF_ANGLE = math.radians(90)  # semicircle, as in the favicon
NEEDLE_TIP = (44.0, 24.0)


def seg_distance(px, py, ax, ay, bx, by):
    vx, vy = bx - ax, by - ay
    wx, wy = px - ax, py - ay
    t = max(0.0, min(1.0, (wx * vx + wy * vy) / (vx * vx + vy * vy)))
    dx, dy = px - (ax + t * vx), py - (ay + t * vy)
    return math.hypot(dx, dy)


def rounded_rect_distance(px, py, size, radius):
    # Distance to a rounded square [0,size]^2 (negative inside).
    cx = cy = size / 2
    hx = hy = size / 2 - radius
    qx, qy = abs(px - cx) - hx, abs(py - cy) - hy
    outside = math.hypot(max(qx, 0.0), max(qy, 0.0))
    inside = min(max(qx, qy), 0.0)
    return outside + inside - radius


def arc_distance(px, py):
    # Distance to the upper semicircular arc around PIVOT (round caps).
    dx, dy = px - PIVOT[0], py - PIVOT[1]
    angle = math.atan2(dx, -dy)  # 0 = straight up
    if abs(angle) <= ARC_HALF_ANGLE:
        return abs(math.hypot(dx, dy) - ARC_RADIUS)
    ends = [(PIVOT[0] + s * ARC_RADIUS * math.sin(ARC_HALF_ANGLE), PIVOT[1] - ARC_RADIUS * math.cos(ARC_HALF_ANGLE)) for s in (-1, 1)]
    return min(math.hypot(px - ex, py - ey) for ex, ey in ends)


def coverage(distance, half_width, pixel):
    # distance and half_width in design units; one pixel of anti-aliasing.
    return max(0.0, min(1.0, 0.5 - (distance - half_width) / pixel))


def blend(dst, src, alpha):
    return tuple(d + (s - d) * alpha for d, s in zip(dst, src))


def render(size):
    unit = 64.0 / size  # design units per pixel
    # Thicken strokes on tiny icons so they stay visible.
    stroke = max(4.0, 1.6 * unit) / 2
    needle = max(4.0, 1.4 * unit) / 2
    hub = max(4.0, 1.8 * unit)
    radius = 14.0 if size >= 32 else 10.0
    pixels = []
    for y in range(size):
        row = []
        for x in range(size):
            px, py = (x + 0.5) * unit, (y + 0.5) * unit
            bg_alpha = coverage(rounded_rect_distance(px, py, 64.0, radius), 0.0, unit)
            color = BACKGROUND
            color = blend(color, AMBER, coverage(arc_distance(px, py), stroke, unit))
            color = blend(color, NEEDLE, coverage(seg_distance(px, py, *PIVOT, *NEEDLE_TIP), needle, unit))
            color = blend(color, NEEDLE, coverage(math.hypot(px - PIVOT[0], py - PIVOT[1]), hub, unit))
            row.append((*(int(round(c)) for c in color), int(round(bg_alpha * 255))))
        pixels.append(row)
    return pixels


def dib(size, pixels):
    header = struct.pack("<IiiHHIIiiII", 40, size, size * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    body = bytearray()
    for row in reversed(pixels):  # bottom-up
        for r, g, b, a in row:
            body += bytes((b, g, r, a))
    mask_stride = ((size + 31) // 32) * 4
    mask = bytes(mask_stride * size)  # alpha channel is authoritative
    return header + bytes(body) + mask


def png(size, pixels):
    raw = bytearray()
    for row in pixels:
        raw.append(0)
        for r, g, b, a in row:
            raw += bytes((r, g, b, a))

    def chunk(tag, data):
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(bytes(raw), 9)) + chunk(b"IEND", b""))


def main():
    images = []
    for size in SIZES:
        pixels = render(size)
        images.append((size, png(size, pixels) if size >= 256 else dib(size, pixels)))

    out = bytearray(struct.pack("<HHH", 0, 1, len(images)))
    offset = 6 + 16 * len(images)
    for size, data in images:
        dim = 0 if size >= 256 else size
        out += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    for _, data in images:
        out += data

    target = Path(__file__).with_name("vustudio.ico")
    target.write_bytes(bytes(out))
    print(f"wrote {target} ({len(out)} bytes, sizes {SIZES})")


if __name__ == "__main__":
    main()
