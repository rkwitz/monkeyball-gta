"""Write uncompressed 32-bit DDS textures (D3DFMT_A8R8G8B8), the simplest format CodeWalker imports."""
import struct

DDSD_CAPS, DDSD_HEIGHT, DDSD_WIDTH, DDSD_PITCH, DDSD_PIXELFORMAT = 0x1, 0x2, 0x4, 0x8, 0x1000
DDPF_ALPHAPIXELS, DDPF_RGB = 0x1, 0x40
DDSCAPS_TEXTURE = 0x1000


def write_dds(path, width, height, rgba):
    """rgba: width * height * 4 bytes, top row first."""
    header = struct.pack(
        "<4s7I44x8I4I",
        b"DDS ", 124, DDSD_CAPS | DDSD_HEIGHT | DDSD_WIDTH | DDSD_PITCH | DDSD_PIXELFORMAT,
        height, width, width * 4, 0, 1,
        32, DDPF_RGB | DDPF_ALPHAPIXELS, 0, 32, 0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000,
        DDSCAPS_TEXTURE, 0, 0, 0)
    bgra = bytearray(rgba)
    bgra[0::4], bgra[2::4] = rgba[2::4], rgba[0::4]
    with open(path, "wb") as f:
        f.write(header + struct.pack("<I", 0) + bytes(bgra))
