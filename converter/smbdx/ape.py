"""Deluxe monkey skeletons (.skl), animations (.mta) and motion lists (.mal), and the IK rig that
turns an animation frame into bone matrices.

Formats (little-endian):
  .skl  u32 bone count; per bone: s16 parent (-1 = root), u16 flags, quat q1, quat q2 (xyzw),
        vec3 translation, name (NUL-terminated, padded to 4). The file stores a solved rest pose:
        world = parent @ T(t) @ R(q1) @ R(q2).
  .mta  u32 animation count, u32 size, u32 offsets[count]; the names follow at size. Per
        animation: u16 frame count, u16 bone count, then per bone a u32 channel mask and, per
        set bit, u32 key count and keys of (u16 0, u16 frame, f32 value, f32 tangent in, f32 tangent out). Bits 3, 4, 5 rotate about
        x, y, z (degrees, applied as Rz @ Ry @ Rx); bits 6, 7, 8 translate.
  .mal  f32, u8 motion count, ..., u32 offsets at 8 (records at 0x28, string table at [5]); each
        0x24-byte record names a motion (e.g. APE_MOVE_A) and a label (move1), with a speed
        threshold and playback rate. Motions are found in the .mta by name (see mta_names).

The rig is Softimage-style: c_ bones (flags 2) are chain roots, l_ bones (flags 1) are effectors
whose keyed positions are in model space, ude/sune (flags 16, +8 for legs) are solved elbows and
knees, k_ bones are plain rotations. It follows SMB1's u_joint_tree_calc_some_matrix
(motload_3.c): an effector sits its bone's length along the aim, and one with no keys (l_kosi)
keeps its rest place under its chain root. The solver reproduces the .skl's rest positions.
"""
import struct
import numpy as np


def _rx(a):
    a = np.radians(a); c, s = np.cos(a), np.sin(a)
    return np.array([[1, 0, 0], [0, c, -s], [0, s, c]])


def _ry(a):
    a = np.radians(a); c, s = np.cos(a), np.sin(a)
    return np.array([[c, 0, s], [0, 1, 0], [-s, 0, c]])


def _rz(a):
    a = np.radians(a); c, s = np.cos(a), np.sin(a)
    return np.array([[c, -s, 0], [s, c, 0], [0, 0, 1]])


def quat_matrix(q):
    x, y, z, w = q
    return np.array([[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
                     [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
                     [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]])


def matrix_quat(m):
    """Rotation matrix -> quaternion (x, y, z, w)."""
    t = m[0, 0] + m[1, 1] + m[2, 2]
    if t > 0:
        s = np.sqrt(t + 1.0) * 2
        q = ((m[2, 1] - m[1, 2]) / s, (m[0, 2] - m[2, 0]) / s, (m[1, 0] - m[0, 1]) / s, 0.25 * s)
    elif m[0, 0] > m[1, 1] and m[0, 0] > m[2, 2]:
        s = np.sqrt(1.0 + m[0, 0] - m[1, 1] - m[2, 2]) * 2
        q = (0.25 * s, (m[0, 1] + m[1, 0]) / s, (m[0, 2] + m[2, 0]) / s, (m[2, 1] - m[1, 2]) / s)
    elif m[1, 1] > m[2, 2]:
        s = np.sqrt(1.0 + m[1, 1] - m[0, 0] - m[2, 2]) * 2
        q = ((m[0, 1] + m[1, 0]) / s, 0.25 * s, (m[1, 2] + m[2, 1]) / s, (m[0, 2] - m[2, 0]) / s)
    else:
        s = np.sqrt(1.0 + m[2, 2] - m[0, 0] - m[1, 1]) * 2
        q = ((m[0, 2] + m[2, 0]) / s, (m[1, 2] + m[2, 1]) / s, 0.25 * s, (m[1, 0] - m[0, 1]) / s)
    q = np.array(q)
    return q / np.linalg.norm(q)


def read_skl(data):
    count = struct.unpack_from("<I", data, 0)[0]
    pos, bones = 4, []
    for _ in range(count):
        parent, flags = struct.unpack_from("<hH", data, pos)
        q1 = struct.unpack_from("<4f", data, pos + 4)
        q2 = struct.unpack_from("<4f", data, pos + 20)
        t = struct.unpack_from("<3f", data, pos + 36)
        end = data.index(b"\0", pos + 48)
        bones.append(dict(name=data[pos + 48:end].decode("ascii"), parent=parent, flags=flags, q1=q1, q2=q2, t=t))
        pos = (end + 4) & ~3
    return bones


def read_mta(data):
    """[{'frames': n, 'bones': [{bit: [(frame, value, tangent in, tangent out)]}]}]"""
    count = struct.unpack_from("<I", data, 0)[0]
    offsets = struct.unpack_from(f"<{count}I", data, 8)
    anims = []
    for off in offsets:
        frames, n_bones = struct.unpack_from("<2H", data, off)
        pos, bones = off + 4, []
        for _ in range(n_bones):
            mask = struct.unpack_from("<I", data, pos)[0]
            pos += 4
            channels = {}
            for bit in range(32):
                if mask >> bit & 1:
                    n = struct.unpack_from("<I", data, pos)[0]
                    pos += 4
                    keys = []
                    for _ in range(n):
                        _flags, frame, value, t_in, t_out = struct.unpack_from("<2H3f", data, pos)
                        pos += 16
                        keys.append((frame, value, t_in, t_out))
                    channels[bit] = keys
            bones.append(channels)
        anims.append(dict(frames=frames, bones=bones))
    return anims


def read_mal(data):
    """[(motion name, label, speed threshold, playback rate)]"""
    count = data[4]
    strings = struct.unpack_from("<7I", data, 8)[5]

    def text(off):
        if off == 0xFFFFFFFF:
            return None
        end = data.index(b"\0", strings + off)
        return data[strings + off:end].decode("latin1")
    out = []
    for i in range(count):
        rec = struct.unpack_from("<I4B2HIffiII", data, 0x28 + i * 0x24)
        out.append((text(rec[11]), text(rec[12]), rec[8], rec[9]))
    return out


def mta_names(data):
    """Animation names in .mta order. The file ends (at the size in its header) with one name per
    animation, NUL-terminated; they're in alphabetical order, and so are the animations."""
    count, size = struct.unpack_from("<2I", data, 0)
    return [n.decode("ascii") for n in data[size:].split(b"\0")[:count]]


def hermite(keys, t):
    """SMB's keyframe curve (mot_joint.c): Hermite, tangents per 30 frames."""
    if t <= keys[0][0]:
        return keys[0][1]
    for a, b in zip(keys, keys[1:]):
        if t <= b[0]:
            if b[0] == a[0]:
                return b[1]
            u = (t - a[0]) / (b[0] - a[0])
            dt = (b[0] - a[0]) / 30.0
            u2, u3 = u * u, u * u * u
            return a[1] + (2 * u3 - 3 * u2) * (a[1] - b[1]) + dt * ((u3 - u2) * (a[3] + b[2]) - a[3] * (u2 - u))
    return keys[-1][1]


def _m(r=np.eye(3), t=(0, 0, 0)):
    m = np.eye(4)
    m[:3, :3] = r
    m[:3, 3] = t
    return m


def _aim(v):
    """Turn +x toward v: about z, then y (motload_3.c func_80035DEC)."""
    return _rz(np.degrees(np.arctan2(v[1], v[0]))) @ _ry(np.degrees(np.arctan2(-v[2], np.hypot(v[0], v[1]))))


class Rig:
    def __init__(self, bones):
        self.bones = bones
        self.children = [[j for j, c in enumerate(bones) if c["parent"] == i] for i in range(len(bones))]
        self.rest = []
        for b in bones:
            local = _m(quat_matrix(b["q1"]) @ quat_matrix(b["q2"]), b["t"])
            self.rest.append(local if b["parent"] < 0 else self.rest[b["parent"]] @ local)

    def pose(self, channels=None, t=0.0):
        """Model-space 4x4 matrix per bone at frame t (channels from read_mta, or None for rest)."""
        b = self.bones
        world = [None] * len(b)

        def value(i, bit, default):
            if channels and bit in channels[i]:
                return hermite(channels[i][bit], t)
            return default

        def rotation(i):
            if channels and 3 in channels[i]:
                return _rz(value(i, 5, 0)) @ _ry(value(i, 4, 0)) @ _rx(value(i, 3, 0))
            return quat_matrix(b[i]["q2"])

        def target(e, frame0):
            if not (channels and 6 in channels[e]):
                # Unkeyed (l_kosi): the effector keeps its rest place in the chain's frame, so it
                # follows the body like any child.
                i = b[e]["parent"] if b[b[e]["parent"]]["flags"] == 2 else b[b[e]["parent"]]["parent"]
                f0 = self.rest[b[i]["parent"]] @ _m(quat_matrix(b[i]["q2"]), b[i]["t"])
                return (np.linalg.inv(f0) @ self.rest[e][:, 3])[:3]
            rest = self.rest[e][:3, 3]
            p = np.array([value(e, 6, rest[0]), value(e, 7, rest[1]), value(e, 8, rest[2]), 1.0])
            return (np.linalg.inv(frame0) @ p)[:3]

        def visit(i, parent):
            bone = b[i]
            if bone["flags"] != 2:
                t_local = [value(i, 6 + k, bone["t"][k]) for k in range(3)] if bone["parent"] < 0 else bone["t"]
                world[i] = parent @ _m(rotation(i), t_local)
                for k in self.children[i]:
                    visit(k, world[i])
                return
            frame0 = parent @ _m(rotation(i), bone["t"])
            kids = self.children[i]
            mids = [k for k in kids if b[k]["flags"] & 16]
            if mids:  # two-bone chain: law of cosines, bending about the chain's z
                m = mids[0]
                e = next(k for k in self.children[m] if b[k]["flags"] == 1)
                goal = target(e, frame0)
                l1, l2 = np.linalg.norm(b[m]["t"]), np.linalg.norm(b[e]["t"])
                d = min(max(np.linalg.norm(goal), 1e-6), (l1 + l2) * 0.99999)
                cos1 = np.clip((l1 * l1 + d * d - l2 * l2) / (2 * l1 * d), -1, 1)
                sign = 1 if b[m]["flags"] & 8 else -1
                r1 = _aim(goal) @ _rz(sign * np.degrees(np.arccos(cos1)))
                world[i] = frame0 @ _m(r1)
                elbow = r1 @ np.array([l1, 0, 0])
                rel = r1.T @ (goal - elbow)
                world[m] = frame0 @ _m(r1 @ _rz(np.degrees(np.arctan2(rel[1], rel[0]))), elbow)
                world[e] = world[m] @ _m(np.eye(3), (l2, 0, 0))
                rest_kids = [k for k in kids if k != m]
            else:  # one-bone chain: aim at the effector
                e = next(k for k in kids if b[k]["flags"] == 1)
                goal = target(e, frame0)
                aim = _aim(goal)
                world[i] = frame0 @ _m(aim)
                # The effector sits the bone's length along the aim, not on the target itself
                # (motload_3.c case 0x800: translate by the joint's own offset).
                world[e] = world[i] @ _m(np.eye(3), (np.linalg.norm(b[e]["t"]), 0, 0))
                rest_kids = [k for k in kids if k != e]
            for k in self.children[e]:
                visit(k, world[e])
            for k in rest_kids:
                visit(k, world[i])

        for i, bone in enumerate(b):
            if bone["parent"] < 0:
                visit(i, np.eye(4))
        return world
