"""Pull everything a Deluxe main-game level shows or plays out of your extracted dump.

Writes to ../../reference/smbdx/converted/level_assets/ (never committed):
  sprites/<sheet>/NNN.png    2D HUD and effect textures (GOAL!!, timer digits, TIME, banana icon, ...)
  fonts/<font>.png + .json   banner and small fonts as atlases with their glyph order
  models/<archive>/<M>.glb   goal, party ball, confetti, LCD digits, sparks, splash, ... one .glb per model
  sounds/<bank>/NNN.wav      every sound in the main-game banks, by index in the bank
  sounds/named/<group>/*.wav the same sounds under the game's own names (from your Default.xbe)
  sounds/main_game/*.wav     the ones a level uses, by what they are (see SOUNDS); sounds/index.csv maps all
  music/<name>.wav           main-game stage music
  monkey/                    list of AiAi's skeleton and animation files (decoded in a later step)

Safe to re-run: files that already exist are skipped, so it can be run in short chunks.
Usage: python extract_level_assets.py [--extracted DIR] [--out DIR] [--only sprites,fonts,models,sounds,music,monkey]
"""
import argparse
import csv
import json
import os
import re
import shutil

from smbdx.adx import decode_adx, split_acx, write_wav
from smbdx.font import BANNER_CHARS, atlas, read_font
from smbdx.glb import export_glb
from smbdx.gma import read_gma
from smbdx.png import write_png
from smbdx.tpl import read_tpl
from smbdx.xbe import Xbe, sound_table

HERE = os.path.dirname(os.path.abspath(__file__))
WORKSPACE = os.path.normpath(os.path.join(HERE, "..", ".."))

SPRITE_SHEETS = ["bmp/bmp_nml", "bmp/bmp_com", "init/efcmdl", "init/common", "asp/bt_effect", "common/common"]
MODEL_ARCHIVES = ["init/common", "init/efcmdl", "asp/bt_effect", "common/common"]
FONTS = [("fnt_asc_72x64", 72, 64, BANNER_CHARS), ("fnt_asc_24x24", 24, 24, None)]
# Sound groups in the game's sound table (read from your Default.xbe) and the bank each lives in.
SOUND_GROUPS = {"GRPse01": "allse", "GRPse02": "allse", "GRPse03": "allse", "GRPse04": "allse",
                "GRPnar": "allse", "GRPboy": "allse", "GRPgirl": "allse", "GRPbaby": "allse",
                "GRPgoli": "allse", "GRPcomn_set": "comn", "GRPcomn2_set": "comn2"}
VOICE_GROUP = "GRPboy"  # AiAi; per-monkey voice rows exist for boy (AiAi), girl, baby, goli

# Sound ids the main game asks for (the same ids as the GameCube original) and what they are.
SOUNDS = {
    0: "banana_get", 1: "banana_bunch_get", 2: "stage_start", 53: "ball_roll",
    4: "narrator_ready", 5: "narrator_go", 6: "countdown_tick", 7: "narrator_hurry_up",
    8: "narrator_goal", 11: "narrator_time_over", 12: "narrator_fall_out", 21: "narrator_fall_out_2",
    18: "ball_bump_8", 19: "ball_bump_4", 20: "ball_bump_9", 47: "ball_bump_2", 105: "ball_bump_1",
    22: "party_ball_open", 294: "goal_fly_up", 295: "goal_tape_cut",
    30: "monkey_start", 23: "monkey_bump_small", 24: "monkey_bump_medium", 26: "monkey_bump_big",
    27: "monkey_goal_1", 88: "monkey_goal_2", 89: "monkey_goal_3",
    28: "monkey_time_over", 29: "monkey_fall", 81: "monkey_fall_last_life",
}
SOUNDS.update({35 + n: f"narrator_{n}" for n in range(10)})

# Stage music used by the main game: SMB1 worlds, then the SMB2 worlds, then jingles.
MUSIC = ["smb1_st1", "smb1_st2", "smb1_st3", "smb1_st4", "smb1_st5", "smb1_st6", "smb1_st7", "smb1_st8",
         "smb1_stb", "smb1_stm", "jungle", "water", "cave", "colony", "clocktower", "kujira", "nabe", "sf",
         "yuenchi", "bonus", "continue", "over", "entry", "point", "sel_clear"]


def _out(path):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    return path


def _read(src, rel):
    with open(os.path.join(src, "media", rel), "rb") as f:
        return f.read()


def do_sprites(src, dst):
    for sheet in SPRITE_SHEETS:
        name = sheet.replace("/", "_")
        for tex in read_tpl(_read(src, sheet + ".tpl")):
            target = os.path.join(dst, "sprites", name, f"{tex.index:03d}.png") if tex else None
            if tex and not os.path.exists(target):
                write_png(_out(target), tex.width, tex.height, tex.rgba)
        print(f"sprites: {name}")


def do_fonts(src, dst):
    for name, w, h, chars in FONTS:
        target = os.path.join(dst, "fonts", name + ".png")
        if os.path.exists(target):
            continue
        glyphs = read_font(_read(src, f"font/{name}.bin"), w, h)
        sw, sh, px = atlas(glyphs, w, h)
        write_png(_out(target), sw, sh, px)
        meta = {"cell": [w, h], "columns": 16, "count": len(glyphs),
                "pixels": "white; tint with a multiply colour like the game does"}
        if chars:
            meta["chars"] = {c: i for i, c in reversed(list(enumerate(chars)))}
        with open(os.path.join(dst, "fonts", name + ".json"), "w") as f:
            json.dump(meta, f, indent=1)
        print(f"fonts: {name} ({len(glyphs)} glyphs)")


def do_models(src, dst):
    for archive in MODEL_ARCHIVES:
        models = read_gma(_read(src, archive + ".gma"))
        textures = read_tpl(_read(src, archive + ".tpl"))
        folder = os.path.join(dst, "models", archive.replace("/", "_"))
        for name, model in models.items():
            target = os.path.join(folder, name + ".glb")
            if not os.path.exists(target):
                export_glb(_out(target), [model], textures)
        print(f"models: {archive} ({len(models)})")


def _scp(path):
    with open(path, encoding="latin-1") as f:
        text = f.read()
    base = int(re.search(r"unit\s+\d+\s+(\d+)", text).group(1))
    # mxm <slot> <1-based index in the .acx, -1 = none> <flag>
    return base, {int(a): int(b) - 1 for a, b, _ in re.findall(r"mxm\s+(\d+)\s+(-?\d+)\s+(-?\d+)", text)}


def do_sounds(src, dst):
    banks = {}
    for bank in sorted(set(SOUND_GROUPS.values())):
        blobs = split_acx(_read(src, f"snd/mkb/{bank}.acx"))
        for i, blob in enumerate(blobs):
            target = os.path.join(dst, "sounds", bank, f"{i:03d}.wav")
            if not os.path.exists(target):
                write_wav(_out(target), *decode_adx(blob))
        banks[bank] = _scp(os.path.join(src, "media", "snd", "mkb", bank + ".scp"))
        print(f"sounds: {bank} ({len(blobs)})")

    with open(os.path.join(src, "Default.xbe"), "rb") as f:
        table = sound_table(Xbe(f.read()))
    rows = []
    for row in table:
        bank = SOUND_GROUPS.get(row["group"])
        if bank is None:
            continue
        base, slots = banks[bank]
        index = slots.get(row["macro"] - base, -1)  # < 0: no file in this bank
        rows.append((row["sound_id"], row["group"][3:], row["name"], bank, index))
        if index < 0 or row["name"] == "DMY_CODE":
            continue
        named = os.path.join(dst, "sounds", "named", row["group"][3:], row["name"] + ".wav")
        if not os.path.exists(named):
            shutil.copyfile(os.path.join(dst, "sounds", bank, f"{index:03d}.wav"), _out(named))
    with open(os.path.join(dst, "sounds", "index.csv"), "w", newline="") as f:
        w = csv.writer(f)
        w.writerow(["sound_id", "group", "name", "bank", "bank_index"])
        w.writerows(rows)

    for sound_id, label in SOUNDS.items():
        found = [r for r in rows if r[0] == sound_id and r[4] >= 0]
        voice = [r for r in found if r[1] == VOICE_GROUP[3:]]
        pick = (voice or found or [None])[0]
        named = os.path.join(dst, "sounds", "main_game", label + ".wav")
        if pick is None:
            print(f"  no file for sound {sound_id} ({label})")
        elif not os.path.exists(named):
            shutil.copyfile(os.path.join(dst, "sounds", pick[3], f"{pick[4]:03d}.wav"), _out(named))


def do_music(src, dst):
    for name in MUSIC:
        target = os.path.join(dst, "music", name + ".wav")
        if not os.path.exists(target):
            write_wav(_out(target), *decode_adx(_read(src, f"snd/stream/{name}.adx")))
            print(f"music: {name}")


def do_monkey(src, dst):
    ape = os.path.join(src, "media", "ape")
    files = sorted(f for f in os.listdir(ape) if f.startswith("aiai") and not re.search(r"scene|ending|lastscene", f))
    with open(_out(os.path.join(dst, "monkey", "files.txt")), "w") as f:
        f.write("\n".join(files) + "\n")
    print(f"monkey: {len(files)} files listed")


STEPS = {"sprites": do_sprites, "fonts": do_fonts, "models": do_models,
         "sounds": do_sounds, "music": do_music, "monkey": do_monkey}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--extracted", default=os.path.join(WORKSPACE, "reference", "smbdx", "extracted"))
    ap.add_argument("--out", default=os.path.join(WORKSPACE, "reference", "smbdx", "converted", "level_assets"))
    ap.add_argument("--only", default=",".join(STEPS))
    args = ap.parse_args()
    for step in args.only.split(","):
        STEPS[step](args.extracted, args.out)


if __name__ == "__main__":
    main()
