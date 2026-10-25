"""AiAi as a GTA prop: the Deluxe body, head, hair, ears, eyes and hands in one skinned drawable, and
every motion from aiai_main.mta as a clip.

GTA gets a flat skeleton: a root plus one bone per thing that moves (the 14 body skin matrices, the
head and the two hands), each a direct child of the root. Our rig (smbdx.ape) solves Deluxe's IK
offline, so each clip simply stores every bone's model-space position and rotation per frame.
"""
import os
import numpy as np

from smbdx import ape
from smbdx.gma import read_gma
from smbdx.tpl import read_tpl
from .ydr_xml import DrawableBuilder
from .ycd_xml import write_ycd

NAME = "mbgta_aiai"
FPS = 60.0
# Body skin matrix -> aiai.skl bone: chest, L upper/fore arm, R upper/fore arm, hips, R thigh/shin/
# foot/toe, L thigh/shin/foot/toe.
BODY_BONES = [1, 5, 6, 9, 10, 13, 15, 16, 18, 19, 20, 21, 23, 24]
HEAD, HAND_L, HAND_R = 4, 8, 12  # l_kubi (neck end), k_te_l, k_te_r
# Parts drawn on a bone: (model, bone, offset in the bone's space). The ear offsets are by eye.
PARTS = [("AIAI_FACE_SKL", HEAD, (0, 0, 0)), ("AIAI_HAIR_SKL", HEAD, (0, 0, 0)), ("AIAI_EYE", HEAD, (0, 0, 0)),
         ("AIAI_EARL", HEAD, (0, 0.12, 0.17)), ("AIAI_EARR", HEAD, (0, -0.12, 0.17)),
         ("AIAI_HANDL_SKL", HAND_L, (0, 0, 0)), ("AIAI_HANDR_SKL", HAND_R, (0, 0, 0))]
GTA_BONES = BODY_BONES + [HEAD, HAND_L, HAND_R]  # GTA bone i + 1 follows aiai.skl bone GTA_BONES[i]

# Monkey Ball (y up) -> GTA (z up): (x, y, z) -> (x, -z, y).
C = np.array([[1.0, 0, 0], [0, 0, -1.0], [0, 1.0, 0]])


def _gta_pose(world, scale):
    """aiai.skl bone matrices -> (GTA translation, quaternion) for each GTA bone after the root."""
    out = []
    for bone in GTA_BONES:
        m = world[bone]
        out.append((tuple(C @ m[:3, 3] * scale), tuple(ape.matrix_quat(C @ m[:3, :3] @ C.T))))
    return out


def build(ape_dir, out_dir, scale):
    """Write mbgta_aiai.ydr.xml (+ textures) and mbgta_aiai.ycd.xml into out_dir; returns
    [(name, ydr path, extents)]."""
    read = lambda f: open(os.path.join(ape_dir, f), "rb").read()
    models = read_gma(read("aiai_h.gma"))
    textures = read_tpl(read("aiai_all.tpl"))
    rig = ape.Rig(ape.read_skl(read("aiai.skl")))
    rest = rig.pose()

    pose = _gta_pose(rest, scale)
    skeleton = ([(NAME + "_root", 0, -1, (0, 0, 0), (0, 0, 0, 1))] +
                         [(rig.bones[b]["name"], i + 1, 0, t, q) for i, (b, (t, q)) in enumerate(zip(GTA_BONES, pose))])

    body = models["AIAI_BODY_SKL"]
    bind = [np.array(m).reshape(3, 4) for m in body.matrices]

    def body_vertex(v):
        p, n = np.zeros(3), np.zeros(3)
        weights, indices = [], []
        for local, k, w in v.influences:
            m = rest[BODY_BONES[k]]
            p += w * (m[:3, :3] @ local + m[:3, 3])
            n += w * (m[:3, :3] @ (bind[k][:, :3].T @ np.array(v.normal)))
            weights.append(w)
            indices.append(k + 1)
        b = [int(round(w * 255)) for w in weights]
        b[0] += 255 - sum(b)
        return tuple(C @ p * scale), tuple(C @ n), (tuple(b + [0] * (4 - len(b))), tuple(indices + [0] * (4 - len(indices))))

    builder = DrawableBuilder(NAME, scale)
    builder.set_skeleton(skeleton)
    builder.add_model(body, textures, vertex_fn=body_vertex)
    for name, bone, offset in PARTS:
        m = rest[bone]
        index = GTA_BONES.index(bone) + 1

        def part_vertex(v, m=m, index=index, offset=np.array(offset)):
            p = m[:3, :3] @ (np.array(v.position) + offset) + m[:3, 3]
            n = m[:3, :3] @ np.array(v.normal or (0, 0, 1))
            return tuple(C @ p * scale), tuple(C @ n), ((255, 0, 0, 0), (index, 0, 0, 0))
        builder.add_model(models[name], textures, vertex_fn=part_vertex)
    path, extents = builder.write(out_dir)
    built = [(NAME, path, extents)]

    mta = read("aiai_main.mta")
    clips = []
    for name, anim in zip(ape.mta_names(mta), ape.read_mta(mta)):
        frames = [_gta_pose(rig.pose(anim["bones"], f), scale) for f in range(anim["frames"] + 1)]
        tracks = {i + 1: ([fr[i][0] for fr in frames], [fr[i][1] for fr in frames]) for i in range(len(GTA_BONES))}
        clips.append((name.lower(), anim["frames"] / FPS, tracks))
    write_ycd(os.path.join(out_dir, NAME + ".ycd.xml"), clips)
    return built
