"""Read GMA model archives (GCMF models) from Super Monkey Ball Deluxe for Xbox.

Deluxe uses the GameCube GMA layout, stored little-endian. Each archive holds named
models; each model holds materials (pointing into the matching TPL) and meshes made
of triangle strips.
"""
import struct
from dataclasses import dataclass, field

MAGIC = b"FMCG"  # "GCMF" stored as a little-endian u32

# Model section flags
FLAG_16BIT = 0x01
FLAG_STITCHING = 0x04
FLAG_SKIN = 0x08
FLAG_EFFECTIVE = 0x10

# Vertex attribute flags
VTX_MATRIX_REF = 0x00000001
VTX_POSITION = 0x00000200
VTX_NORMAL = 0x00000400
VTX_COLOR = 0x00000800
VTX_UV0 = 0x00002000
VTX_UV1 = 0x00004000
VTX_UV2 = 0x00008000
VTX_NBT = 0x02000000

STRIP_FLOAT = 0x98
STRIP_INT16 = 0x99
INT16_SCALE = 1.0 / 8191.0


@dataclass
class Vertex:
    position: tuple
    normal: tuple = None
    color: tuple = None  # (r, g, b, a) bytes
    uv: list = field(default_factory=list)  # up to three (u, v) pairs
    matrix_ref: int = 0
    # Skin models only: [(position in the matrix's space, matrix index, weight)]. The vertex's
    # position is sum(weight * model.matrices[index] @ position) (verified exact on AiAi).
    influences: list = None


MAT_ENV_MAP = 0x8000  # texture is a sphere/highlight map layered on top, not the base color


@dataclass
class Material:
    flags: int
    texture_index: int

    @property
    def is_env_map(self):
        return bool(self.flags & MAT_ENV_MAP)


@dataclass
class Mesh:
    layer: int  # 1 = opaque, 2 = translucent
    render_flags: int
    material_indices: list
    vertex_flags: int
    diffuse: tuple = (255, 255, 255, 255)  # RGBA bytes
    ambient: tuple = (255, 255, 255, 255)
    specular: tuple = (0, 0, 0, 255)
    strips: list = field(default_factory=list)  # list of (vertices, clockwise)

    def triangles(self):
        """Yield triangles (a, b, c) as vertex objects, wound counter-clockwise."""
        for verts, clockwise in self.strips:
            for i in range(len(verts) - 2):
                a, b, c = verts[i], verts[i + 1], verts[i + 2]
                if (i % 2 == 1) != clockwise:
                    a, b = b, a
                yield a, b, c


@dataclass
class Model:
    name: str
    flags: int
    center: tuple
    radius: float
    materials: list
    meshes: list
    matrices: list = field(default_factory=list)


class _Reader:
    def __init__(self, data):
        self.data = data
        self.pos = 0

    def take(self, fmt):
        values = struct.unpack_from("<" + fmt, self.data, self.pos)
        self.pos += struct.calcsize("<" + fmt)
        return values if len(values) > 1 else values[0]


def _read_vertex(r, flags, int16):
    num = (lambda n: tuple(x * INT16_SCALE for x in r.take(f"{n}h"))) if int16 else (lambda n: r.take(f"{n}f"))
    v = Vertex(position=None)
    if flags & VTX_MATRIX_REF:
        v.matrix_ref = r.take("B")
    v.position = num(3)
    if flags & VTX_NORMAL:
        v.normal = num(3)
    if flags & VTX_NBT:
        r.take("9f")
    if flags & VTX_COLOR:
        raw = r.take("I")
        v.color = ((raw >> 24) & 255, (raw >> 16) & 255, (raw >> 8) & 255, raw & 255)
    for bit in (VTX_UV0, VTX_UV1, VTX_UV2):
        if flags & bit:
            v.uv.append(num(2))
    return v


def _read_strip_group(r, size, vertex_flags, int16, clockwise, strips):
    end = r.pos + size
    r.pos += 1  # leading zero byte
    while r.pos < end:
        kind = r.data[r.pos]
        if kind == 0:
            break
        if kind not in (STRIP_FLOAT, STRIP_INT16):
            raise ValueError(f"bad strip type 0x{kind:02x} at 0x{r.pos:x}")
        r.pos += 1
        count = struct.unpack_from("<H", r.data, r.pos)[0]
        r.pos += 2
        strips.append(([_read_vertex(r, vertex_flags, int16) for _ in range(count)], clockwise))
    r.pos = end


def _read_mesh_header(r, layer):
    start = r.pos
    render_flags = r.take("I")
    diffuse, ambient, specular = (tuple(r.data[r.pos + i:r.pos + i + 4]) for i in (0, 4, 8))
    r.pos = start + 0x13
    section_flags = r.take("B")
    r.pos = start + 0x16
    mats = [m for m in r.take("3H") if m != 0xFFFF]
    vertex_flags = r.take("I")
    r.pos = start + 0x28
    chunk1, chunk2 = r.take("2i")
    r.pos = start + 0x60
    mesh = Mesh(layer, render_flags, mats, vertex_flags, diffuse, ambient, specular)
    return mesh, section_flags, chunk1, chunk2


def _read_model(data, offset, name):
    r = _Reader(data)
    r.pos = offset
    if data[offset:offset + 4] != MAGIC:
        raise ValueError(f"{name}: missing GCMF magic")
    r.pos += 4
    flags = r.take("I")
    center = r.take("3f")
    radius = r.take("f")
    n_mats, n_layer1, n_layer2, n_matrices = r.take("3HB")
    r.pos += 1
    header_size = r.take("i")
    r.pos = offset + 0x40
    materials = []
    for _ in range(n_mats):
        mflags, tex = r.take("IH")
        r.pos += 0x20 - 6
        materials.append(Material(mflags, tex))
    matrices = []
    if flags & (FLAG_STITCHING | FLAG_SKIN):
        matrices = [r.take("12f") for _ in range(n_matrices)]
    r.pos = offset + header_size
    model = Model(name, flags, center, radius, materials, [], matrices)
    int16 = bool(flags & FLAG_16BIT)
    total = n_layer1 + n_layer2

    if flags & (FLAG_SKIN | FLAG_EFFECTIVE):
        base = r.pos
        n_verts, off_unk1, off_pool, off_mesh, off_unk2 = r.take("5i")
        r.pos = base + 0x20
        headers = [_read_mesh_header(r, 1 if i < n_layer1 else 2) for i in range(total)]
        r.pos = base + off_pool
        pool = []
        record = base + off_unk1  # skin influence records, 0x20 bytes each, consecutive per vertex
        for _ in range(n_verts):
            vals = struct.unpack_from("<12fI3I", data, r.pos)
            r.pos += 0x40
            raw = vals[12]
            v = Vertex(position=vals[0:3], normal=vals[3:6],
                       uv=[vals[6:8], vals[8:10], vals[10:12]],
                       color=((raw >> 24) & 255, (raw >> 16) & 255, (raw >> 8) & 255, raw & 255))
            if flags & FLAG_SKIN:
                v.influences = []
                for _ in range(vals[13] & 0xFF):  # low byte: how many matrices move this vertex
                    x, y, z, _nx, _ny, _nz, index, _pad, weight = struct.unpack_from("<6fHHf", data, record)
                    v.influences.append(((x, y, z), index, weight))
                    record += 0x20
            pool.append(v)
        r.pos = base + off_mesh
        for mesh, section_flags, chunk1, chunk2 in headers:
            for size, cw, bit in ((chunk1, False, 1), (chunk2, True, 2)):
                if not section_flags & bit:
                    continue
                end = r.pos + size * 4
                while r.pos < end:
                    n = r.take("i")
                    idx = r.take(f"{n}i") if n > 1 else (r.take("i"),)
                    mesh.strips.append(([pool[i // 0x40] for i in idx], cw))
            model.meshes.append(mesh)
        return model

    for i in range(total):
        mesh, section_flags, chunk1, chunk2 = _read_mesh_header(r, 1 if i < n_layer1 else 2)
        if section_flags & 1:
            _read_strip_group(r, chunk1, mesh.vertex_flags, int16, False, mesh.strips)
        if section_flags & 2:
            _read_strip_group(r, chunk2, mesh.vertex_flags, int16, True, mesh.strips)
        if section_flags & 0xFF == 0x0F:
            r.pos += 8
            chunk3, chunk4 = r.take("2i")
            r.pos += 0x10
            _read_strip_group(r, chunk3, mesh.vertex_flags, int16, False, mesh.strips)
            _read_strip_group(r, chunk4, mesh.vertex_flags, int16, True, mesh.strips)
        model.meshes.append(mesh)
    return model


def read_gma(data):
    """Return {model name: Model} for a GMA file's bytes. Empty slots are skipped."""
    count, model_base = struct.unpack_from("<2i", data, 0)
    entries = [struct.unpack_from("<2i", data, 8 + i * 8) for i in range(count)]
    name_base = 8 + count * 8
    models = {}
    for model_off, name_off in entries:
        if model_off == -1:
            continue
        end = data.index(b"\0", name_base + name_off)
        name = data[name_base + name_off:end].decode("ascii")
        models[name] = _read_model(data, model_base + model_off, name)
    return models
