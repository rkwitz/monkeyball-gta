"""Copy the level sounds that extract_level_assets.py pulled from your own copy of Deluxe to where the
ball mode script plays them from:
  <GTA V>/scripts/MonkeyBallGta/sfx/<name>.wav    (sounds/main_game, plus the banana sounds below)
Nothing here is ever committed; these are your files, copied on your own machine. build.py calls
install() with its own output folder; run this file directly to copy straight into a GTA V folder.

Usage: python install_audio.py --assets <level_assets dir> --gta <GTA V folder>"""
import argparse
import glob
import os
import shutil

HERE = os.path.dirname(os.path.abspath(__file__))
WORKSPACE = os.path.normpath(os.path.join(HERE, "..", ".."))
GTA = r"C:\Program Files (x86)\Steam\steamapps\common\Grand Theft Auto V"


# Sounds outside main_game: (file under sounds/, name the script plays). The banana pickups are sound
# 3 (one banana) and 0x39 (a bunch), and AiAi's "bunch get" voice (item_coin.c item_coin_collect).
EXTRA = [("named/se01/SND_SEB_SE_JGL_SEPCM_3.wav", "banana"),
         ("named/se01/SND_SEB_SE_JGL_SE034UP_1.wav", "banana_bunch"),
         ("named/boy/SND_SED_VO1_BOYM_BBGET2.wav", "monkey_banana_bunch")]


def install(assets, root):
    """Copy the sounds into <root>/scripts/MonkeyBallGta/sfx. Returns how many files were copied."""
    out = os.path.join(root, "scripts", "MonkeyBallGta", "sfx")
    os.makedirs(out, exist_ok=True)
    files = sorted(glob.glob(os.path.join(assets, "sounds", "main_game", "*.wav")))
    if not files:
        raise SystemExit(f"no sounds in {os.path.join(assets, 'sounds', 'main_game')}: run extract_level_assets.py first")
    for f in files:
        shutil.copyfile(f, os.path.join(out, os.path.basename(f)))
    count = len(files)
    for src, name in EXTRA:
        path = os.path.join(assets, "sounds", *src.split("/"))
        if os.path.exists(path):
            shutil.copyfile(path, os.path.join(out, name + ".wav"))
            count += 1
        else:
            print(f"missing {path}")
    print(f"{count} sounds -> {out}")
    return count


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--assets", default=os.path.join(WORKSPACE, "reference", "smbdx", "converted", "level_assets"))
    ap.add_argument("--gta", default=GTA)
    args = ap.parse_args()
    install(args.assets, args.gta)


if __name__ == "__main__":
    main()
