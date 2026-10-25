"""Deluxe bitmap fonts (media/font/fnt_asc_WxH.bin): fixed-size glyphs, IA4 pixels (high nibble
alpha, low nibble intensity) laid out in GameCube-style 8x4 tiles, glyphs stored back to back."""

# Glyph order of fnt_asc_72x64.bin, the big banner font (READY, GO, FLOOR 1, FALL OUT, TIME OVER).
# After 'Z' come hiragana then katakana. There is no space glyph.
BANNER_CHARS = ",.:;?!---=<>&@0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ"


def read_font(data, width, height):
    """Return a list of glyphs, each width*height*4 RGBA bytes (white, alpha from the file)."""
    size = width * height
    glyphs = []
    for g in range(len(data) // size):
        raw = data[g * size:(g + 1) * size]
        rgba = bytearray(size * 4)
        i = 0
        for ty in range(0, height, 4):
            for tx in range(0, width, 8):
                for y in range(ty, ty + 4):
                    for x in range(tx, tx + 8):
                        b = raw[i]
                        i += 1
                        o = (y * width + x) * 4
                        v = (b & 15) * 17
                        rgba[o:o + 4] = bytes((v, v, v, (b >> 4) * 17))
        glyphs.append(bytes(rgba))
    return glyphs


def atlas(glyphs, width, height, columns=16):
    """Pack glyphs into one RGBA sheet. Returns (sheet_w, sheet_h, rgba)."""
    rows = (len(glyphs) + columns - 1) // columns
    sw, sh = columns * width, rows * height
    out = bytearray(sw * sh * 4)
    for g, px in enumerate(glyphs):
        gx, gy = (g % columns) * width, (g // columns) * height
        for y in range(height):
            o = ((gy + y) * sw + gx) * 4
            out[o:o + width * 4] = px[y * width * 4:(y + 1) * width * 4]
    return sw, sh, bytes(out)
