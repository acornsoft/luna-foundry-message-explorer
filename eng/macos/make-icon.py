#!/usr/bin/env python3
# SPDX-License-Identifier: MPL-2.0
# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. If a copy of the MPL was not distributed with this
# file, You can obtain one at https://mozilla.org/MPL/2.0/.
"""Draw the app icon and write it as a macOS .icns (PNG entries). Standard library only.

Our own simple mark: a gold magnifier over three gold message bars on a maroon rounded square.
Usage: make-icon.py <out.icns> [preview.png]
"""
import math
import struct
import sys
import zlib

MAROON = (0x4F, 0x20, 0x01)
GOLD = (0xFF, 0x99, 0x00)

# (OSType, pixel size). PNG payloads are read by macOS 10.7 and later.
ENTRIES = [
    (b"icp4", 16), (b"icp5", 32), (b"icp6", 64), (b"ic07", 128), (b"ic08", 256),
    (b"ic09", 512), (b"ic10", 1024), (b"ic11", 32), (b"ic12", 64), (b"ic13", 256), (b"ic14", 512),
]


def rounded_box(x, y, cx, cy, hw, hh, r):
    qx, qy = abs(x - cx) - hw + r, abs(y - cy) - hh + r
    return math.hypot(max(qx, 0.0), max(qy, 0.0)) + min(max(qx, qy), 0.0) - r


def capsule(x, y, ax, ay, bx, by, r):
    px, py, dx, dy = x - ax, y - ay, bx - ax, by - ay
    t = max(0.0, min(1.0, (px * dx + py * dy) / (dx * dx + dy * dy)))
    return math.hypot(px - dx * t, py - dy * t) - r


def layers(x, y):
    """Signed distances (in 1024 space) for the background and the gold mark."""
    background = rounded_box(x, y, 512, 512, 412, 412, 186)
    ring = abs(math.hypot(x - 455, y - 455) - 215) - 30
    handle = capsule(x, y, 625, 625, 790, 790, 48)
    bars = min(
        capsule(x, y, 350, 385, 560, 385, 22),
        capsule(x, y, 350, 455, 520, 455, 22),
        capsule(x, y, 350, 525, 470, 525, 22),
    )
    return background, min(ring, handle, bars)


def render(size):
    scale = 1024.0 / size
    rows = []
    for j in range(size):
        row = bytearray(b"\x00")
        y = (j + 0.5) * scale
        for i in range(size):
            x = (i + 0.5) * scale
            background, mark = layers(x, y)
            a = max(0.0, min(1.0, 0.5 - background / scale))
            g = max(0.0, min(1.0, 0.5 - mark / scale)) * a
            rgb = [round(m * (1 - g) + o * g) for m, o in zip(MAROON, GOLD)]
            row += bytes(rgb) + bytes([round(a * 255)])
        rows.append(bytes(row))
    return png(size, b"".join(rows))


def png(size, raw):
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)

    header = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b"")


def main():
    if len(sys.argv) not in (2, 3):
        sys.exit(__doc__)
    cache = {}
    body = b""
    for kind, size in ENTRIES:
        if size not in cache:
            cache[size] = render(size)
        body += kind + struct.pack(">I", 8 + len(cache[size])) + cache[size]
    with open(sys.argv[1], "wb") as f:
        f.write(b"icns" + struct.pack(">I", 8 + len(body)) + body)
    if len(sys.argv) == 3:
        with open(sys.argv[2], "wb") as f:
            f.write(cache[512])


if __name__ == "__main__":
    main()
