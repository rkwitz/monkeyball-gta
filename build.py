"""Build monkeyball-gta from your own copy of Super Monkey Ball Deluxe (Xbox), in one go.

  python build.py --iso "D:\\Games\\Super Monkey Ball Deluxe.iso"
  python build.py --iso "D:\\Games\\Super Monkey Ball Deluxe.iso" --gta "C:\\Program Files (x86)\\Steam\\steamapps\\common\\Grand Theft Auto V"

Steps (each is skipped when its output is already there, so a re-run is quick):
  1. unpack your disc image          -> build/extracted/
  2. pull out the HUD art and sounds -> build/level_assets/
  3. download CodeWalker's source    -> build/CodeWalker/   (a pinned version, used to write GTA files)
  4. build the packer, then turn the ball, goal, monkey and HUD into a GTA add-on pack
                                     -> build/converted/
  5. build the script                -> src/MonkeyBallGta/bin/Release/net48/MonkeyBallGta.dll
  6. lay out everything GTA needs    -> build/dist/  (same folders as your GTA V folder)
  7. with --gta: copy build/dist/ into your GTA V folder

Everything under build/ comes from your own game files: keep it to yourself, never upload it.
The one step this can't do for you is adding the pack to GTA's dlclist.xml with OpenIV; see README.md.
"""
import argparse
import os
import shutil
import subprocess
import sys

ROOT = os.path.dirname(os.path.abspath(__file__))
CONVERTER = os.path.join(ROOT, "converter")
BUILD = os.path.join(ROOT, "build")
# CodeWalker isn't part of this repo. This is the version the packer is built and tested against.
CODEWALKER_URL = "https://github.com/dexyfex/CodeWalker"
CODEWALKER_COMMIT = "485d56bec00262ed7fa472261cce7bbc6202b96e"
PACK = "mbgta"


def step(text):
    print(f"\n== {text}", flush=True)


def run(cmd, cwd=ROOT):
    print("> " + " ".join(f'"{c}"' if " " in c else c for c in cmd), flush=True)
    code = subprocess.call(cmd, cwd=cwd)
    if code != 0:
        sys.exit(f"\nFailed (exit code {code}): {' '.join(cmd)}")


def need(tool, hint):
    if shutil.which(tool) is None:
        sys.exit(f"{tool} not found. {hint}")


def check_tools():
    if sys.version_info < (3, 9):
        sys.exit("Python 3.9 or newer is needed.")
    missing = []
    for module, package in (("numpy", "numpy"), ("PIL", "Pillow")):
        try:
            __import__(module)
        except ImportError:
            missing.append(package)
    if missing:
        sys.exit(f"Missing Python packages: run  {os.path.basename(sys.executable)} -m pip install {' '.join(missing)}")
    need("git", "Install Git from https://git-scm.com/downloads")
    need("dotnet", "Install the .NET 8 SDK from https://dotnet.microsoft.com/download/dotnet/8.0")
    sdks = subprocess.run(["dotnet", "--list-sdks"], capture_output=True, text=True).stdout.split()
    if not any(s[0].isdigit() and int(s.split(".")[0]) >= 8 for s in sdks if "." in s):
        sys.exit("The .NET 8 SDK (or newer) is needed: https://dotnet.microsoft.com/download/dotnet/8.0")


def extract(iso, extracted):
    xbe = os.path.join(extracted, "Default.xbe")
    if os.path.exists(xbe) and os.path.exists(os.path.join(extracted, "media", "init", "common.gma")):
        print(f"already unpacked: {extracted}")
        return
    if not iso:
        sys.exit("Give your disc image with --iso (or an unpacked copy with --extracted).")
    if not os.path.isfile(iso):
        sys.exit(f"Disc image not found: {iso}")
    run([sys.executable, os.path.join(CONVERTER, "extract_xiso.py"), iso, extracted], cwd=CONVERTER)
    if not os.path.exists(os.path.join(extracted, "media", "init", "common.gma")):
        sys.exit("That image doesn't look like Super Monkey Ball Deluxe for the original Xbox (no media/init/common.gma).")


def codewalker():
    cw = os.path.join(BUILD, "CodeWalker")
    if not os.path.exists(os.path.join(cw, "CodeWalker.Core", "CodeWalker.Core.csproj")):
        run(["git", "clone", "--quiet", CODEWALKER_URL, cw])
    head = subprocess.run(["git", "-C", cw, "rev-parse", "HEAD"], capture_output=True, text=True).stdout.strip()
    if head != CODEWALKER_COMMIT:
        run(["git", "-C", cw, "fetch", "--quiet", "origin", CODEWALKER_COMMIT])
        run(["git", "-C", cw, "checkout", "--quiet", CODEWALKER_COMMIT])
    return cw


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--iso", help="your Super Monkey Ball Deluxe (Xbox) disc image")
    ap.add_argument("--extracted", default=os.path.join(BUILD, "extracted"),
                    help="an already unpacked copy of the disc (default: build/extracted)")
    ap.add_argument("--gta", help="your GTA V folder (the one with GTA5.exe); copies the result there")
    args = ap.parse_args()

    check_tools()
    if args.gta and not os.path.isfile(os.path.join(args.gta, "GTA5.exe")):
        sys.exit(f"GTA5.exe isn't in {args.gta}. Give the GTA V Legacy folder (Enhanced isn't supported).")
    os.makedirs(BUILD, exist_ok=True)
    extracted = os.path.abspath(args.extracted)
    assets = os.path.join(BUILD, "level_assets")
    converted = os.path.join(BUILD, "converted")
    dist = os.path.join(BUILD, "dist")

    step("1/7 Unpack the disc")
    extract(args.iso, extracted)

    step("2/7 HUD art and sounds")
    run([sys.executable, os.path.join(CONVERTER, "extract_level_assets.py"), "--extracted", extracted,
         "--out", assets, "--only", "sprites,fonts,sounds"], cwd=CONVERTER)

    step("3/7 CodeWalker source")
    cw = codewalker()

    step("4/7 Packer and add-on pack")
    run(["dotnet", "build", os.path.join(ROOT, "packer", "MbgtaPacker.csproj"), "-c", "Release", "-nologo",
         "-v", "quiet", f"-p:CodeWalkerDir={cw}"])
    packer = os.path.join(ROOT, "packer", "bin", "Release", "net8.0", "MbgtaPacker.dll")
    rpf = os.path.join(converted, "dlc", PACK, "dlc.rpf")
    if os.path.exists(rpf):
        os.remove(rpf)
    run([sys.executable, os.path.join(CONVERTER, "build_gta.py"), "--extracted", extracted, "--assets", assets,
         "--out", converted, "--packer", packer], cwd=CONVERTER)
    if not os.path.isfile(rpf):
        sys.exit(f"The pack wasn't made: {rpf}")

    step("5/7 Script")
    run(["dotnet", "build", os.path.join(ROOT, "src", "MonkeyBallGta", "MonkeyBallGta.csproj"), "-c", "Release",
         "-nologo", "-v", "quiet"])
    dll = os.path.join(ROOT, "src", "MonkeyBallGta", "bin", "Release", "net48", "MonkeyBallGta.dll")

    step("6/7 Lay out build/dist")
    if os.path.exists(dist):
        shutil.rmtree(dist)
    os.makedirs(os.path.join(dist, "scripts"))
    shutil.copyfile(dll, os.path.join(dist, "scripts", "MonkeyBallGta.dll"))
    pack_dir = os.path.join(dist, "mods", "update", "x64", "dlcpacks", PACK)
    os.makedirs(pack_dir)
    shutil.copyfile(rpf, os.path.join(pack_dir, "dlc.rpf"))
    sys.path.insert(0, CONVERTER)
    import install_audio
    install_audio.install(assets, dist)
    for dirpath, _, files in os.walk(dist):
        for f in sorted(files):
            if not f.endswith(".wav"):
                print("  " + os.path.relpath(os.path.join(dirpath, f), dist))

    if args.gta:
        step("7/7 Copy into GTA V")
        for dirpath, _, files in os.walk(dist):
            target = os.path.join(args.gta, os.path.relpath(dirpath, dist))
            os.makedirs(target, exist_ok=True)
            for f in files:
                shutil.copyfile(os.path.join(dirpath, f), os.path.join(target, f))
        print(f"copied build/dist into {args.gta}")
        for f in ("ScriptHookV.dll", "dinput8.dll", "ScriptHookVDotNet.asi", "ScriptHookVDotNet3.dll", "OpenIV.asi"):
            if not os.path.exists(os.path.join(args.gta, f)):
                print(f"  warning: {f} isn't in your GTA V folder yet (see README.md, step 1)")
    else:
        step("7/7 Copy into GTA V (skipped: no --gta)")
        print(f"Copy everything inside {dist} into your GTA V folder, keeping the folders.")
    print("\nDone. If you haven't yet, add the pack to dlclist.xml with OpenIV (README.md, step 4).")


if __name__ == "__main__":
    main()
