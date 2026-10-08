"""Generates src/Equator.App/Assets/Equator.ico (an integral sign on an accent tile) without third-party packages."""
import math
import struct
import zlib
from pathlib import Path

ACCENT = (47, 111, 219)
WHITE = (255, 255, 255)


def integral_curve(n=160):
    """Points of a stylised integral sign in unit coordinates (0..1)."""
    pts = []
    for i in range(n + 1):
        t = i / n  # 0 = bottom, 1 = top
        y = 0.86 - 0.72 * t
        # Slanted spine with hooks at both ends.
        x = 0.5 + 0.10 * (t - 0.5) * 2
        if t < 0.18:
            u = (0.18 - t) / 0.18
            x -= 0.13 * math.sin(u * math.pi / 2) ** 1.5
            y -= 0.05 * u * u
        if t > 0.82:
            u = (t - 0.82) / 0.18
            x += 0.13 * math.sin(u * math.pi / 2) ** 1.5
            y += 0.05 * u * u
        pts.append((x, y))
    return pts


def dist_to_segment(px, py, ax, ay, bx, by):
    dx, dy = bx - ax, by - ay
    l2 = dx * dx + dy * dy
    t = 0 if l2 == 0 else max(0.0, min(1.0, ((px - ax) * dx + (py - ay) * dy) / l2))
    cx, cy = ax + t * dx, ay + t * dy
    return math.hypot(px - cx, py - cy)


def render(size):
    pts = [(x * size, y * size) for x, y in integral_curve()]
    stroke = size * 0.075
    radius = size * 0.22
    ss = 3  # supersampling
    rows = []
    for py in range(size):
        row = bytearray()
        for px in range(size):
            cover_tile = 0
            cover_glyph = 0
            for sy in range(ss):
                for sx in range(ss):
                    x = px + (sx + 0.5) / ss
                    y = py + (sy + 0.5) / ss
                    # Rounded square tile.
                    qx = max(abs(x - size / 2) - (size / 2 - radius), 0)
                    qy = max(abs(y - size / 2) - (size / 2 - radius), 0)
                    if math.hypot(qx, qy) <= radius:
                        cover_tile += 1
                        # Integral glyph (thicker in the middle, tapering at the ends).
                        best = 1e9
                        for i in range(len(pts) - 1):
                            ax, ay = pts[i]
                            bx, by = pts[i + 1]
                            if abs(ay - y) > stroke * 2 and abs(by - y) > stroke * 2:
                                continue
                            d = dist_to_segment(x, y, ax, ay, bx, by)
                            if d < best:
                                best = d
                        rel = (y / size - 0.14) / 0.72
                        width = stroke * (0.55 + 0.45 * math.sin(max(0.0, min(1.0, rel)) * math.pi))
                        if best <= width / 2:
                            cover_glyph += 1
            n = ss * ss
            a = cover_tile / n
            g = cover_glyph / n
            r = ACCENT[0] * (1 - g / max(a, 1e-9)) + WHITE[0] * (g / max(a, 1e-9)) if a > 0 else 0
            gg = ACCENT[1] * (1 - g / max(a, 1e-9)) + WHITE[1] * (g / max(a, 1e-9)) if a > 0 else 0
            b = ACCENT[2] * (1 - g / max(a, 1e-9)) + WHITE[2] * (g / max(a, 1e-9)) if a > 0 else 0
            row += bytes((int(r), int(gg), int(b), int(round(a * 255))))
        rows.append(bytes(row))
    return rows


def png(size, rows):
    raw = b"".join(b"\x00" + r for r in rows)

    def chunk(tag, data):
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def main():
    sizes = [16, 24, 32, 48, 64, 128, 256]
    images = [png(s, render(s)) for s in sizes]
    header = struct.pack("<HHH", 0, 1, len(sizes))
    offset = 6 + 16 * len(sizes)
    entries = b""
    for s, data in zip(sizes, images):
        entries += struct.pack("<BBBBHHII", s % 256, s % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    out = Path(__file__).resolve().parent.parent / "src" / "Equator.App" / "Assets" / "Equator.ico"
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_bytes(header + entries + b"".join(images))
    print("wrote", out)


if __name__ == "__main__":
    main()
