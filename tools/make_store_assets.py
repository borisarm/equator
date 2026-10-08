"""Generates the MSIX/Store logo assets in src/Equator.App/Assets/Package from the same artwork as the app icon.

Square logos are the accent tile with the integral sign; wide images (wide tile, splash screen) centre the tile
on a transparent background so the tile colour from the manifest shows around it.
"""
from pathlib import Path

from make_icon import png, render

OUT = Path(__file__).resolve().parent.parent / "src" / "Equator.App" / "Assets" / "Package"

_cache = {}


def tile(size):
    if size not in _cache:
        _cache[size] = render(size)
    return _cache[size]


def padded(size, icon_fraction):
    """A square image with the tile scaled to icon_fraction of the side, centred, transparent around it."""
    inner = max(8, round(size * icon_fraction))
    return compose(size, size, inner)


def compose(width, height, inner):
    rows = tile(inner)
    out = []
    left, top = (width - inner) // 2, (height - inner) // 2
    for y in range(height):
        if top <= y < top + inner:
            row = bytes(4 * left) + rows[y - top] + bytes(4 * (width - inner - left))
        else:
            row = bytes(4 * width)
        out.append(row)
    return out


def png_rect(width, height, rows):
    import struct
    import zlib
    raw = b"".join(b"\x00" + r for r in rows)

    def chunk(tag, data):
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def write(name, data):
    OUT.mkdir(parents=True, exist_ok=True)
    (OUT / name).write_bytes(data)


def main():
    scales = {100: 1.0, 125: 1.25, 150: 1.5, 200: 2.0, 400: 4.0}
    # App list / taskbar icon (Square44x44Logo): scaled versions and exact target sizes, plated and unplated.
    for s, f in scales.items():
        n = round(44 * f)
        write(f"Square44x44Logo.scale-{s}.png", png_rect(n, n, padded(n, 0.92)))
    for t in (16, 24, 32, 48, 256):
        data = png(t, tile(t))
        write(f"Square44x44Logo.targetsize-{t}.png", data)
        write(f"Square44x44Logo.targetsize-{t}_altform-unplated.png", data)
        write(f"Square44x44Logo.altform-lightunplated_targetsize-{t}.png", data)
    # Tiles.
    for s, f in {100: 1.0, 200: 2.0}.items():
        n = round(150 * f)
        write(f"Square150x150Logo.scale-{s}.png", png_rect(n, n, padded(n, 0.56)))
        n = round(71 * f)
        write(f"SmallTile.scale-{s}.png", png_rect(n, n, padded(n, 0.7)))
        n = round(310 * f)
        write(f"LargeTile.scale-{s}.png", png_rect(n, n, padded(n, 0.46)))
        w, h = round(310 * f), round(150 * f)
        write(f"Wide310x150Logo.scale-{s}.png", png_rect(w, h, compose(w, h, round(h * 0.56))))
        w, h = round(620 * f), round(300 * f)
        write(f"SplashScreen.scale-{s}.png", png_rect(w, h, compose(w, h, round(h * 0.5))))
    # Store logo (shown in the Store and installer).
    for s, f in {100: 1.0, 125: 1.25, 150: 1.5, 200: 2.0, 400: 4.0}.items():
        n = round(50 * f)
        write(f"StoreLogo.scale-{s}.png", png(n, tile(n)))
    print("wrote", len(list(OUT.glob("*.png"))), "files to", OUT)


if __name__ == "__main__":
    main()
