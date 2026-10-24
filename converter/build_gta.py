"""Convert the ball and goal from your Deluxe dump into GTA V add-on prop files.

Usage: python build_gta.py [--scale N] [--packer PATH]

Writes CodeWalker XML to reference/smbdx/converted/xml/, then runs the C# packer
(../packer) to make binary .ydr/.ytyp files in reference/smbdx/converted/gta/.
"""
import argparse
import os
import subprocess
import sys

from gta import aiai, hud
from gta.ydr_xml import DrawableBuilder
from gta.ytyp_xml import DYNAMIC, HAS_ANIM, write_ytyp
from smbdx.gma import Mesh, Model, Vertex, read_gma
from smbdx.tpl import read_tpl

HERE = os.path.dirname(os.path.abspath(__file__))
WORKSPACE = os.path.normpath(os.path.join(HERE, "..", ".."))
YTYP_NAME = "mbgta_props"

# Ball and goal size in GTA: Monkey Ball units to metres. The ball is 1 unit across, so 3.5 makes it
# a 3.5 m ball, about the length of a small car. Must match BallMode.Scale.
SCALE = 3.5

# One model part: (model name, colour override or None, opacity override or None, {mesh index: opacity}
# or None[, add_model options]). The colour override is an RGB, or {mesh index: RGB}.
# The Deluxe ball is two half-spheres drawn together: BALL_COLOR (the game can tint it at run time) and
# BALL_CLEAR. Both keep the model's own colour. GOAL_SIGN is not part of the goal gate, so it is left out.
BALL = [("BALL_COLOR", None, 0.6, None), ("BALL_CLEAR", None, 0.3, None)]
# The goal is tinted at run time by its type (stage.c func_8000E338: blue for a normal goal): the frame
# and posts are a white gradient made blue, the inner trim is a shine layer, the timer panel is dark.
# The game only shows the timer from the front, and the trim around the panel has a notch for the small
# digits that is on the other side seen from behind. So the back is the front turned half a turn: the
# front-facing triangles of every part twice (as they are, then turned to face the back), plus the
# side-on ones once. The clock then reads, and fits its box, from both sides.
GOAL_COLOURS = {0: (60, 120, 255), 1: (190, 225, 255), 2: (14, 14, 18)}
GOAL = [("GOAL", GOAL_COLOURS, None, {3: 0.35}, {"faces": "front"}),
        ("GOAL", GOAL_COLOURS, None, {3: 0.35}, {"faces": "front", "turn": True}),
        ("GOAL", GOAL_COLOURS, None, None, {"faces": "edge"})]
# The goal's timer digits light up on the panel (their material colour is black and the game lights them
# by the texture alone), so draw them at full colour, just in front of the panel.
LCD = {"offset": (0.0, 0.0, 0.02)}
FLAT = {"double_sided": True}
PIECES = ["DEEPGREEN", "GREEN", "ORANGE", "RED", "YELLOW"]

# The goal's collision is just its two posts (x 0.87..1.36 either side, 0.49 deep, the arch closing at
# 5.4 up), so cars stop against them; the gap between them stays open for the ball.
POSTS = [(side, 0.87, 1.36, 0.245, 5.4) for side in (-1, 1)]


def post_boxes(scale):
    boxes = []
    for side, inner, outer, depth, height in POSTS:
        x0, x1 = sorted((side * inner * scale, side * outer * scale))
        boxes.append(((x0, -depth * scale, 0.0), (x1, depth * scale, height * scale)))
    return boxes


# (prop name, archive, parts, lod distance, collision, archetype flags[, size multiplier])
# collision: "sphere" or "mesh" inside a Bound Composite; "bare_sphere" without one; "posts" for the
# goal's posts; None for none. The size multiplier scales the model on top of --scale.
PROPS = [
    ("mbgta_ball", "media/init/common", BALL, 300.0, "sphere", DYNAMIC),
    ("mbgta_goal", "media/init/common", GOAL, 500.0, "posts", DYNAMIC),
    # The party ball under the goal's timer: whole, then its two halves swinging open.
    ("mbgta_bag", "media/init/common", [("NEW_SCENT_BAG_WHOLE", None, None, None)], 300.0, None, DYNAMIC),
    ("mbgta_bag_a", "media/init/common", [("NEW_SCENT_BAG_A", None, None, None, FLAT)], 300.0, None, DYNAMIC),
    ("mbgta_bag_b", "media/init/common", [("NEW_SCENT_BAG_B", None, None, None, FLAT)], 300.0, None, DYNAMIC),
] + [(f"mbgta_lcd_{size.lower()}_{d}", "media/init/common", [(f"{size}_LCD_{d}", (255, 255, 255), None, None, LCD)],
      300.0, None, DYNAMIC) for size in "LS" for d in range(10)] \
  + [(f"mbgta_paper_{c.lower()}", "media/init/common", [(f"PAPER_PIECE_{c}", None, None, None, FLAT)], 150.0, None, DYNAMIC)
     for c in PIECES]
# Bananas (item_coin.c draws them at 0.5 / bound radius * 1.5, a bunch at 0.75), and the gold stars
# a hard hit throws (effect.c effect_colistar_particle: CRASH_STAR at 0.015).
PROPS += [
    ("mbgta_banana", "media/init/common", [("OBJ_BANANA_01_LOD0", None, None, None)], 200.0, None, DYNAMIC, "banana"),
    ("mbgta_bunch", "media/init/common", [("OBJ_BANANA_02_LOD0", None, None, None)], 200.0, None, DYNAMIC, "bunch"),
    ("mbgta_star", "media/init/common", [("CRASH_STAR", None, None, None)], 100.0, None, DYNAMIC, 0.015),
]
ITEM_RADIUS = {"banana": 0.5 * 1.5, "bunch": 0.75 * 1.5}  # Monkey Ball units the drawn model reaches out to


def tape_segment(length=0.27, height=0.25):
    """One piece of the goal tape. The game draws the tape in code as a strip of 8 points that the ball
    breaks (stobj_goal.c func_8006DDA0: 0.25 tall, facing through the gate); the script moves 7 of these
    pieces between those points, each a little longer than its link so the bends don't open gaps."""
    l, h = length / 2, height / 2
    n = (0.0, 0.0, 1.0)
    strip = [Vertex((-l, -h, 0.0), n), Vertex((-l, h, 0.0), n), Vertex((l, -h, 0.0), n), Vertex((l, h, 0.0), n)]
    mesh = Mesh(layer=1, render_flags=0, material_indices=[], vertex_flags=0, diffuse=(250, 250, 240, 255),
                strips=[(strip, False)])
    return Model("GOAL_TAPE", 0, (0.0, 0.0, 0.0), l, [], [mesh])


PROPS.append(("mbgta_tape", "media/init/common", [(tape_segment(), None, None, None, FLAT)], 300.0, None, DYNAMIC))

# --test-variants: copies of the ball that differ in one setting, to find what the game needs to spawn a prop.
# (Result 2026-10-22: all four spawned, so composite collision + DYNAMIC is kept for the real props.)
TEST_PROPS = [
    ("mbgta_t_old", "media/init/common", BALL, 300.0, "bare_sphere", 0),
    ("mbgta_t_bare_dyn", "media/init/common", BALL, 300.0, "bare_sphere", DYNAMIC),
    ("mbgta_t_comp_static", "media/init/common", BALL, 300.0, "sphere", 0),
    ("mbgta_t_nocol_dyn", "media/init/common", BALL, 300.0, None, DYNAMIC),
]
BALL_RADIUS = 0.5  # Monkey Ball units


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--extracted", default=os.path.join(WORKSPACE, "reference", "smbdx", "extracted"))
    ap.add_argument("--out", default=os.path.join(WORKSPACE, "reference", "smbdx", "converted"))
    ap.add_argument("--scale", type=float, default=SCALE, help="Monkey Ball units to metres (the ball is 1 unit across)")
    ap.add_argument("--test-variants", action="store_true", help="also build the TEST_PROPS spawn-test copies")
    ap.add_argument("--assets", default=os.path.join(WORKSPACE, "reference", "smbdx", "converted", "level_assets"),
                    help="extract_level_assets.py output, for the HUD sprites and fonts")
    ap.add_argument("--packer", help="path to MbgtaPacker(.exe/.dll); skip packing if not given and not built")
    args = ap.parse_args()

    xml_dir = os.path.join(args.out, "xml")
    gta_dir = os.path.join(args.out, "gta")
    os.makedirs(xml_dir, exist_ok=True)
    cache = {}
    archetypes = []
    for prop, archive, parts, lod, collision, flags, *size in PROPS + (TEST_PROPS if args.test_variants else []):
        if archive not in cache:
            with open(os.path.join(args.extracted, archive + ".gma"), "rb") as f:
                models = read_gma(f.read())
            with open(os.path.join(args.extracted, archive + ".tpl"), "rb") as f:
                textures = read_tpl(f.read())
            cache[archive] = (models, textures)
        models, textures = cache[archive]
        mult = size[0] if size else 1.0
        if isinstance(mult, str):  # an item: sized so its model reaches ITEM_RADIUS from its centre
            mult = ITEM_RADIUS[mult] / models[parts[0][0]].radius
        builder = DrawableBuilder(prop, args.scale * mult)
        for name, color, alpha, mesh_alpha, *options in parts:
            model = models[name] if isinstance(name, str) else name
            builder.add_model(model, textures, color, alpha, mesh_alpha, **(options[0] if options else {}))
        if collision in ("sphere", "bare_sphere"):
            builder.set_sphere_bounds(BALL_RADIUS * args.scale, composite=collision == "sphere")
        elif collision == "mesh":
            builder.set_mesh_bounds()
        elif collision == "posts":
            builder.set_mesh_bounds(boxes=post_boxes(args.scale))
        path, extents = builder.write(xml_dir)
        archetypes.append((prop, extents, lod, flags))
        print(f"wrote {path}")
    # AiAi: a skinned drawable plus a clip dictionary of the same name with every Deluxe motion.
    path, count = hud.build(args.assets, xml_dir, cache["media/init/common"][1])
    print(f"wrote {path} ({count} textures)")
    for name, path, extents in aiai.build(os.path.join(args.extracted, "media", "ape"), xml_dir, args.scale):
        archetypes.append((name, extents, 300.0, DYNAMIC | HAS_ANIM, aiai.NAME))
        print(f"wrote {path}")
    print(f"wrote {aiai.NAME}.ycd.xml")
    write_ytyp(os.path.join(xml_dir, YTYP_NAME + ".ytyp.xml"), YTYP_NAME, archetypes)

    packer = args.packer or _find_packer()
    if packer is None:
        print("packer not built yet; run: dotnet build ../packer -c Release")
        return 0
    run = [packer] if packer.endswith(".exe") else ["dotnet", packer]
    rc = subprocess.call(run + [xml_dir, gta_dir, "--check"])
    if rc != 0:
        return rc
    # dlc/mbgta/dlc.rpf goes to <GTA V>/mods/update/x64/dlcpacks/mbgta/ (see docs/SETUP.md)
    return subprocess.call(run + ["dlc", gta_dir, os.path.join(args.out, "dlc")])


def _find_packer():
    base = os.path.join(HERE, "..", "packer", "bin", "Release", "net8.0")
    for name in ("MbgtaPacker.exe", "MbgtaPacker.dll"):
        p = os.path.join(base, name)
        if os.path.exists(p):
            return p
    return None


if __name__ == "__main__":
    sys.exit(main())
