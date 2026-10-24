"""Minimal PNG writer (RGBA, no dependencies)."""
import struct
import zlib


def write_png(target, width, height, rgba):
    """Write an RGBA image to a path or a binary file object."""
    def chunk(tag, body):
        return struct.pack(">I", len(body)) + tag + body + struct.pack(">I", zlib.crc32(tag + body) & 0xFFFFFFFF)
    rows = b"".join(b"\0" + rgba[y * width * 4:(y + 1) * width * 4] for y in range(height))
    data = (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(rows, 9)) + chunk(b"IEND", b""))
    if hasattr(target, "write"):
        target.write(data)
    else:
        with open(target, "wb") as f:
            f.write(data)
