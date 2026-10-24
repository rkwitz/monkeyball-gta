"""Classic LZSS (4 KB window, 18-byte matches) as used by Super Monkey Ball's .lz files and textures."""

N = 4096
F = 18
THRESHOLD = 3


def decompress(data, out_size=None, fill=0):
    ring = bytearray([fill]) * N
    r = N - F
    out = bytearray()
    pos = 0
    flags = 0
    while pos < len(data) and (out_size is None or len(out) < out_size):
        flags >>= 1
        if not flags & 0x100:
            flags = data[pos] | 0xFF00
            pos += 1
            if pos >= len(data):
                break
        if flags & 1:
            c = data[pos]
            pos += 1
            out.append(c)
            ring[r] = c
            r = (r + 1) % N
        else:
            if pos + 1 >= len(data):
                break
            lo, hi = data[pos], data[pos + 1]
            pos += 2
            index = ((hi & 0xF0) << 4) | lo
            for i in range((hi & 0x0F) + THRESHOLD):
                c = ring[(index + i) % N]
                out.append(c)
                ring[r] = c
                r = (r + 1) % N
    return bytes(out)


def decompress_texture(data, out_size):
    """Decompress an Xbox Deluxe texture stream.

    A variant of LZSS with no ring buffer: flag bits are read most-significant first,
    1 means a back-reference, 0 a literal byte. A reference is two bytes: the low
    nibble of the first is length - 3, the rest is a 12-bit distance - 1.
    """
    out = bytearray()
    flags = data[0]
    pos = 1
    bits = 8
    while len(out) < out_size:
        is_ref = flags & 0x80
        flags = (flags << 1) & 0xFF
        bits -= 1
        if bits == 0:
            flags = data[pos]
            pos += 1
            bits = 8
        if is_ref:
            b0, b1 = data[pos], data[pos + 1]
            pos += 2
            start = len(out) - ((b0 >> 4) | (b1 << 4)) - 1
            for i in range((b0 & 0x0F) + 3):
                out.append(out[start + i])
        else:
            out.append(data[pos])
            pos += 1
    return bytes(out[:out_size])
