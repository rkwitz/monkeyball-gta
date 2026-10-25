"""The Deluxe HUD as a GTA texture dictionary (mbgta_hud.ytd): the sprites the script draws with
DRAW_SPRITE, each digit and letter cut out of its sheet as a texture of its own.

Input is the sprite and font PNGs that extract_level_assets.py writes (sprites/bmp_bmp_nml,
sprites/bmp_bmp_com, fonts/). Glyphs are cropped to their own width, so the script can read each
one's advance back with GET_TEXTURE_RESOLUTION. A few plain shapes for the minimap are drawn here.
"""
import json
import math
import os

from PIL import Image

from .dds import write_dds

NAME = "mbgta_hud"

# Whole sprites: texture name -> (folder, file).
SPRITES = {
    "score": ("bmp_bmp_nml", "011"), "time": ("bmp_bmp_nml", "015"), "plate": ("bmp_bmp_nml", "019"),
    "goal": ("bmp_bmp_nml", "002"), "banana": ("bmp_bmp_nml", "001"), "mph": ("bmp_bmp_nml", "025"),
    "badge": ("bmp_bmp_nml", "004"), "bar": ("bmp_bmp_nml", "005"), "arc": ("bmp_bmp_nml", "020"),
    "face": ("bmp_bmp_com", "016"),
}
# Digit sheets: prefix -> (file, cell width, cell height, top of the first row); 5 digits a row, two rows.
DIGITS = {"sd": ("012", 16, 20, 0), "wd": ("013", 16, 16, 0), "td": ("017", 28, 40, 0), "ts": ("018", 16, 24, 0)}
# Effect textures from init/common.tpl (effect.c): the hit flash's circle_white2 and CRASH_FLASH, and
# a quarter of CROSS_LIGHT (the goal sparkles), which its model mirrors into a whole cross.
EFFECTS = {"fx_circle": 15, "fx_flash": 21}
CROSS_LIGHT = 22
# 24x24 font (Shift-JIS order): index of '0', 'A' and 'a'; '-' sits at 33.
SMALL_FONT = {"0": 62, "A": 72, "a": 98, "-": 33, ":": 5}


def _crop_width(im):
    """Keep the full height but only the columns with ink (plus a pixel of margin)."""
    box = im.getchannel("A").point(lambda a: 255 if a > 8 else 0).getbbox()
    if not box:
        return im
    x0, x1 = max(0, box[0] - 1), min(im.width, box[2] + 1)
    return im.crop((x0, 0, x1, im.height))


def _shapes():
    """A white square, a soft-edged disc, and a triangle pointing up."""
    out = {"white": Image.new("RGBA", (4, 4), (255, 255, 255, 255))}
    n = 128
    disc = Image.new("RGBA", (n, n))
    tri = Image.new("RGBA", (n, n))
    for y in range(n):
        for x in range(n):
            r = math.hypot(x + 0.5 - n / 2, y + 0.5 - n / 2) / (n / 2)
            disc.putpixel((x, y), (255, 255, 255, int(255 * max(0.0, min(1.0, (1.0 - r) * 20)))))
            half = (y + 0.5) / n * 0.5  # apex at the top, base at the bottom
            inside = abs(x + 0.5 - n / 2) / n <= half
            tri.putpixel((x, y), (255, 255, 255, 255 if inside else 0))
    out["disc"], out["tri"] = disc, tri
    return out


def _effects(tpl):
    out = {}
    for name, i in EFFECTS.items():
        t = tpl[i]
        im = Image.frombytes("RGBA", (t.width, t.height), t.rgba)
        # The game adds these onto the picture; a sprite blends, so black has to become see-through.
        if name == "fx_flash":
            im.putalpha(im.convert("RGB").convert("L"))
        out[name] = im
    t = tpl[CROSS_LIGHT]
    q = Image.frombytes("RGBA", (t.width, t.height), t.rgba)
    cross = Image.new("RGBA", (t.width * 2, t.height * 2))
    # The quarter's bright corner (the cross's centre) is its top right.
    cross.paste(q.transpose(Image.FLIP_TOP_BOTTOM), (0, 0))
    cross.paste(q.transpose(Image.FLIP_LEFT_RIGHT).transpose(Image.FLIP_TOP_BOTTOM), (t.width, 0))
    cross.paste(q, (0, t.height))
    cross.paste(q.transpose(Image.FLIP_LEFT_RIGHT), (t.width, t.height))
    out["fx_cross"] = cross
    # A spark: effect_coli_particle_draw draws a line, bright at its head and dim along its tail;
    # this is that line's soft-edged shape, white so the script can tint it.
    w, h = 16, 64
    spark = Image.new("RGBA", (w, h))
    for y in range(h):
        for x in range(w):
            across = 1.0 - abs(x + 0.5 - w / 2) / (w / 2)
            along = 1.0 - (y + 0.5) / h
            spark.putpixel((x, y), (255, 255, 255, int(255 * max(0.0, across) ** 1.5 * (0.35 + 0.65 * along))))
    out["fx_spark"] = spark
    return out


def build(src_dir, out_dir, tpl=None):
    """Write mbgta_hud.ytd.xml and its DDS files into out_dir. tpl: init/common.tpl's textures, for
    the effects."""
    textures = {}
    for name, (folder, f) in SPRITES.items():
        im = Image.open(os.path.join(src_dir, "sprites", folder, f + ".png")).convert("RGBA")
        # The sheets pad each sprite out to a power of two; keep only the picture.
        textures[name] = im.crop(im.getchannel("A").point(lambda a: 255 if a > 8 else 0).getbbox())
    # GOAL's flash (hud.c goal_sprite_draw adds white): the same shape in white, drawn over it.
    white = Image.new("RGBA", textures["goal"].size, (255, 255, 255, 0))
    white.putalpha(textures["goal"].getchannel("A"))
    textures["goal_white"] = white
    for prefix, (f, w, h, top) in DIGITS.items():
        sheet = Image.open(os.path.join(src_dir, "sprites", "bmp_bmp_nml", f + ".png")).convert("RGBA")
        for d in range(10):
            x, y = (d % 5) * w, top + (d // 5) * h
            textures[f"{prefix}{d}"] = sheet.crop((x, y, x + w, y + h))
    # The small timer's point is the lower of the two dots under its digits.
    sheet = Image.open(os.path.join(src_dir, "sprites", "bmp_bmp_nml", "018.png")).convert("RGBA")
    dot = Image.new("RGBA", (16, 24))
    dot.paste(sheet.crop((0, 59, 16, 71)), (0, 12))
    textures["tsdot"] = _crop_width(dot)

    # The banner font (READY, GO, FALL OUT, TIME OVER): 72x64 cells, 16 a row, white.
    font = Image.open(os.path.join(src_dir, "fonts", "fnt_asc_72x64.png")).convert("RGBA")
    chars = json.load(open(os.path.join(src_dir, "fonts", "fnt_asc_72x64.json")))["chars"]
    for ch, i in chars.items():
        if ch.isalnum() or ch in "!-":
            cell = font.crop(((i % 16) * 72, (i // 16) * 64, (i % 16) * 72 + 72, (i // 16) * 64 + 64))
            textures["b_" + _key(ch)] = _crop_width(cell)
    # The small font for the level name.
    small = Image.open(os.path.join(src_dir, "fonts", "fnt_asc_24x24.png")).convert("RGBA")
    glyphs = [(str(d), SMALL_FONT["0"] + d) for d in range(10)] + \
             [(chr(65 + k), SMALL_FONT["A"] + k) for k in range(26)] + \
             [(chr(97 + k), SMALL_FONT["a"] + k) for k in range(26)] + [("-", SMALL_FONT["-"]), (":", SMALL_FONT[":"])]
    for ch, i in glyphs:
        cell = small.crop(((i % 16) * 24, (i // 16) * 24, (i % 16) * 24 + 24, (i // 16) * 24 + 24))
        textures["f_" + _key(ch)] = _crop_width(cell)
    textures.update(_shapes())
    if tpl:
        textures.update(_effects(tpl))

    folder = os.path.join(out_dir, NAME)
    os.makedirs(folder, exist_ok=True)
    items = []
    for name in sorted(textures):
        im = textures[name]
        write_dds(os.path.join(folder, name + ".dds"), im.width, im.height, im.tobytes())
        items.append(f"""  <Item>
    <Name>{name}</Name>
    <Unk32 value="0" />
    <Usage>UNKNOWN</Usage>
    <ExtraFlags value="0" />
    <Width value="{im.width}" />
    <Height value="{im.height}" />
    <MipLevels value="1" />
    <Format>D3DFMT_A8R8G8B8</Format>
    <FileName>{name}.dds</FileName>
  </Item>""")
    path = os.path.join(out_dir, NAME + ".ytd.xml")
    nl = "\n"
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(f'<?xml version="1.0" encoding="UTF-8"?>\n<TextureDictionary>\n{nl.join(items)}\n</TextureDictionary>\n')
    return path, len(textures)


def _key(ch):
    """Texture names are case-blind in GTA, so capitals get a prefix."""
    if ch.isupper():
        return "u" + ch.lower()
    return {"!": "bang", "-": "dash", ":": "colon"}.get(ch, ch)
