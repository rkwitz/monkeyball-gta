"""Read TPL texture archives from Super Monkey Ball Deluxe for Xbox.

The file starts with an "XTPL" header and a GameCube-style table of entries. Each
entry's data begins with a 0x20-byte Xbox header (D3D format, size, mip count,
compression) followed by the pixel data, usually compressed (see lzss.py).
Only the top mip level is decoded.
"""
import struct
from dataclasses import dataclass

from .lzss import decompress_texture

# Xbox D3D formats seen in Deluxe, with bits per pixel
FORMATS = {
    0x00: ("L8", 8), 0x01: ("AL8", 8), 0x02: ("A1R5G5B5", 16), 0x04: ("A4R4G4B4", 16),
    0x05: ("R5G6B5", 16), 0x06: ("A8R8G8B8", 32), 0x0C: ("DXT1", 4), 0x0E: ("DXT3", 8),
    0x0F: ("DXT5", 8), 0x19: ("A8", 8), 0x1A: ("A8L8", 16),
}


@dataclass
class Texture:
    index: int
    width: int
    height: int
    format: str
    rgba: bytes  # width * height * 4, top row first


def _morton_table(width, height):
    """Map each pixel (row-major) to its index in an Xbox swizzled (Morton order) texture."""
    table = []
    for y in range(height):
        for x in range(width):
            off, ob = 0, 0
            for b in range(16):
                if (1 << b) < width:
                    off |= ((x >> b) & 1) << ob
                    ob += 1
                if (1 << b) < height:
                    off |= ((y >> b) & 1) << ob
                    ob += 1
            table.append(off)
    return table


def _rgb565(c):
    r, g, b = (c >> 11) & 31, (c >> 5) & 63, c & 31
    return (r << 3 | r >> 2, g << 2 | g >> 4, b << 3 | b >> 2)


def _dxt_colors(c0, c1, allow_alpha):
    a, b = _rgb565(c0), _rgb565(c1)
    if c0 > c1 or not allow_alpha:
        return [a + (255,), b + (255,),
                tuple((2 * a[i] + b[i]) // 3 for i in range(3)) + (255,),
                tuple((a[i] + 2 * b[i]) // 3 for i in range(3)) + (255,)]
    return [a + (255,), b + (255,), tuple((a[i] + b[i]) // 2 for i in range(3)) + (255,), (0, 0, 0, 0)]


def _decode_dxt(data, w, h, kind):
    out = bytearray(w * h * 4)
    block = 8 if kind == "DXT1" else 16
    pos = 0
    for by in range(0, h, 4):
        for bx in range(0, w, 4):
            alpha = None
            if kind == "DXT3":
                bits = int.from_bytes(data[pos:pos + 8], "little")
                alpha = [((bits >> (4 * i)) & 15) * 17 for i in range(16)]
            elif kind == "DXT5":
                a0, a1 = data[pos], data[pos + 1]
                bits = int.from_bytes(data[pos + 2:pos + 8], "little")
                pal = [a0, a1] + ([((6 - i) * a0 + (i + 1) * a1) // 7 for i in range(6)] if a0 > a1
                                  else [((4 - i) * a0 + (i + 1) * a1) // 5 for i in range(4)] + [0, 255])
                alpha = [pal[(bits >> (3 * i)) & 7] for i in range(16)]
            cpos = pos + (block - 8)
            c0, c1, idx = struct.unpack_from("<HHI", data, cpos)
            colors = _dxt_colors(c0, c1, kind == "DXT1")
            for i in range(16):
                x, y = bx + (i & 3), by + (i >> 2)
                if x < w and y < h:
                    px = colors[(idx >> (2 * i)) & 3]
                    a = px[3] if alpha is None else alpha[i]
                    out[(y * w + x) * 4:(y * w + x) * 4 + 4] = bytes(px[:3] + (a,))
            pos += block
    return bytes(out)


def _decode_linear(data, w, h, kind):
    bpp = FORMATS_BY_NAME[kind] // 8
    table = _morton_table(w, h)
    out = bytearray(w * h * 4)
    for i, s in enumerate(table):
        o = s * bpp
        if kind == "A8R8G8B8":
            b, g, r, a = data[o:o + 4]
        elif kind == "R5G6B5":
            r, g, b = _rgb565(data[o] | data[o + 1] << 8)
            a = 255
        elif kind == "A1R5G5B5":
            c = data[o] | data[o + 1] << 8
            r, g, b = (((c >> s2) & 31) * 255 // 31 for s2 in (10, 5, 0))
            a = 255 if c & 0x8000 else 0
        elif kind == "A4R4G4B4":
            c = data[o] | data[o + 1] << 8
            a, r, g, b = (((c >> s2) & 15) * 17 for s2 in (12, 8, 4, 0))
        elif kind == "A8L8":
            r = g = b = data[o]
            a = data[o + 1]
        elif kind in ("L8", "AL8"):
            r = g = b = data[o]
            a = 255 if kind == "L8" else data[o]
        elif kind == "A8":
            r = g = b = 255
            a = data[o]
        out[i * 4:i * 4 + 4] = bytes((r, g, b, a))
    return bytes(out)


FORMATS_BY_NAME = {name: bpp for name, bpp in FORMATS.values()}


def read_tpl(data):
    """Return a list of Texture (None for empty slots) from a TPL file's bytes."""
    if data[:4] != b"XTPL":
        raise ValueError("not an Xbox TPL file")
    count = struct.unpack_from("<I", data, 4)[0]
    textures = []
    for i in range(count):
        _gc_format, offset, width, height, levels, _check = struct.unpack_from("<IIHHHH", data, 8 + i * 16)
        if offset == 0 or width == 0 or levels == 0:
            textures.append(None)
            continue
        fmt, w, h, _levels, compressed, raw_size, packed_size, _ = struct.unpack_from("<8I", data, offset)
        if fmt not in FORMATS:
            raise ValueError(f"texture {i}: unknown Xbox format 0x{fmt:02x}")
        kind, bpp = FORMATS[fmt]
        body = data[offset + 0x20:]
        pixels = decompress_texture(body, raw_size) if compressed else body[:raw_size]
        top = pixels[:max(w * h * bpp // 8, 8)]
        rgba = _decode_dxt(top, w, h, kind) if kind.startswith("DXT") else _decode_linear(top, w, h, kind)
        textures.append(Texture(i, w, h, kind, rgba))
    return textures
