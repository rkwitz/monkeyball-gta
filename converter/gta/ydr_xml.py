"""Turn parsed GMA models into a CodeWalker .ydr.xml drawable with embedded DDS textures.

Monkey Ball is Y-up; GTA is Z-up, so (x, y, z) becomes (x, -z, y). Each GMA mesh becomes
one geometry. Its color (the mesh's diffuse color times its base texture) is baked into
an embedded texture, because GTA's default shaders take color from the texture.
"""
import math
import os
import zlib
from xml.sax.saxutils import escape

from .dds import write_dds

# Shader files (Sollumz's default preset parameters work for both).
OPAQUE_SHADER, OPAQUE_BUCKET = "default.sps", 0
ALPHA_SHADER, ALPHA_BUCKET = "alpha.sps", 1
TRANSLUCENT_ALPHA = 0.6  # stand-in for the game's translucent pass until its blend mode is decoded

SHADER_PARAMS = """          <Item name="matMaterialColorScale" type="Vector" x="1.0" y="0.0" z="0.0" w="1.0" />
          <Item name="HardAlphaBlend" type="Vector" x="1.0" y="0.0" z="0.0" w="0.0" />
          <Item name="useTessellation" type="Vector" x="0.0" y="0.0" z="0.0" w="0.0" />
          <Item name="wetnessMultiplier" type="Vector" x="1.0" y="0.0" z="0.0" w="0.0" />
          <Item name="globalAnimUV1" type="Vector" x="0.0" y="1.0" z="0.0" w="0.0" />
          <Item name="globalAnimUV0" type="Vector" x="1.0" y="0.0" z="0.0" w="0.0" />"""


def to_gta(p, scale):
    return (p[0] * scale, -p[2] * scale, p[1] * scale)


def _facing(tri):
    """'front' (the timer panel's way, -z by winding), 'back' (+z) or 'edge' for a Monkey Ball triangle."""
    a, b, c = (v.position for v in tri)
    n = _cross(_sub(b, a), _sub(c, a))
    length = (n[0] ** 2 + n[1] ** 2 + n[2] ** 2) ** 0.5 or 1.0
    nz = n[2] / length
    return "front" if nz < -0.3 else "back" if nz > 0.3 else "edge"


def _turned(v, turn):
    """v spun half a turn about Monkey Ball's up axis (y), if turn."""
    return (-v[0], v[1], -v[2]) if turn else v


def _unit(n):
    length = math.sqrt(n[0] ** 2 + n[1] ** 2 + n[2] ** 2) or 1.0
    return (n[0] / length, n[1] / length, n[2] / length)


def _tinted(tex, color, alpha_scale):
    """Base texture (or a 4x4 white square) multiplied by an RGBA byte color."""
    if tex is None:
        w = h = 4
        src = bytes([255]) * (w * h * 4)
    else:
        w, h, src = tex.width, tex.height, tex.rgba
    out = bytearray(len(src))
    for c in range(4):
        k = color[c] / 255 * (alpha_scale if c == 3 else 1.0)
        out[c::4] = bytes(min(255, int(v * k + 0.5)) for v in src[c::4])
    return w, h, bytes(out)


def _f(v):
    return f"{v:.7f}"


# Sollumz's "General (Default)" collision preset: what the shape is, and what it collides with.
COLLISION_TYPE_FLAGS = "MAP_WEAPON, MAP_DYNAMIC, MAP_ANIMAL, MAP_COVER, MAP_VEHICLE"
COLLISION_INCLUDE_FLAGS = ("VEHICLE_NOT_BVH, VEHICLE_BVH, PED, RAGDOLL, ANIMAL, ANIMAL_RAGDOLL, OBJECT, PLANT, "
                           "PROJECTILE, EXPLOSION, FORKLIFT_FORKS, TEST_WEAPON, TEST_CAMERA, TEST_AI, TEST_SCRIPT, "
                           "TEST_VEHICLE_WHEEL, GLASS")


def _bound_fields(lo, hi, center, radius, volume, inertia, margin=0.04):
    """The fields every CodeWalker <Bounds> node starts with (material 0 is DEFAULT)."""
    return f"""    <BoxMin x="{_f(lo[0])}" y="{_f(lo[1])}" z="{_f(lo[2])}" />
    <BoxMax x="{_f(hi[0])}" y="{_f(hi[1])}" z="{_f(hi[2])}" />
    <BoxCenter x="{_f((lo[0] + hi[0]) / 2)}" y="{_f((lo[1] + hi[1]) / 2)}" z="{_f((lo[2] + hi[2]) / 2)}" />
    <SphereCenter x="{_f(center[0])}" y="{_f(center[1])}" z="{_f(center[2])}" />
    <SphereRadius value="{_f(radius)}" />
    <Margin value="{margin}" />
    <Volume value="{_f(volume)}" />
    <Inertia x="{_f(inertia[0])}" y="{_f(inertia[1])}" z="{_f(inertia[2])}" />
    <MaterialIndex value="0" />
    <MaterialColourIndex value="0" />
    <ProceduralID value="0" />
    <RoomID value="0" />
    <PedDensity value="0" />
    <UnkFlags value="0" />
    <PolyFlags value="0" />
    <UnkType value="1" />"""


def _box_mesh(lo, hi):
    """A box as (shader, vertices, triangle indices), the shape set_mesh_bounds reads."""
    corners = [((hi[0] if i & 1 else lo[0]), (hi[1] if i & 2 else lo[1]), (hi[2] if i & 4 else lo[2])) for i in range(8)]
    quads = [(0, 2, 3, 1), (4, 5, 7, 6), (0, 1, 5, 4), (2, 6, 7, 3), (0, 4, 6, 2), (1, 3, 7, 5)]
    indices = [i for a, b, c, d in quads for i in (a, b, c, a, c, d)]
    return None, [(p,) for p in corners], indices


def _indent(text, n):
    return "\n".join(" " * n + line for line in text.split("\n"))


class DrawableBuilder:
    def __init__(self, name, scale=1.0):
        self.name = name
        self.scale = scale
        self.shaders = []  # (key, xml)
        self.textures = []  # (name, w, h, rgba)
        self.geometries = []  # (shader index, vertices, indices, bone index or None for skinned)
        self.bounds = None  # (bound type, common fields xml, type-specific xml, wrap in composite)
        self.bones = None  # skinned drawables: [(name, tag, parent index, translation, quaternion xyzw)]

    def _shader_for(self, model, mesh, textures, color, alpha):
        base = None
        for index in mesh.material_indices:
            material = model.materials[index]
            if not material.is_env_map and material.texture_index < len(textures):
                base = textures[material.texture_index]
                break
        translucent = mesh.layer == 2
        diffuse = mesh.diffuse if color is None else tuple(color) + mesh.diffuse[3:]
        alpha = alpha if alpha is not None else TRANSLUCENT_ALPHA if translucent else 1.0
        translucent = translucent or alpha < 1.0
        key = (id(base), diffuse, alpha)
        for i, (k, _) in enumerate(self.shaders):
            if k == key:
                return i
        tex_name = f"{self.name}_{len(self.textures):02d}"
        w, h, rgba = _tinted(base, diffuse, alpha)
        self.textures.append((tex_name, w, h, rgba))
        sps, bucket = (ALPHA_SHADER, ALPHA_BUCKET) if translucent else (OPAQUE_SHADER, OPAQUE_BUCKET)
        xml = f"""      <Item>
        <Name>default</Name>
        <FileName>{sps}</FileName>
        <RenderBucket value="{bucket}" />
        <Parameters>
          <Item name="DiffuseSampler" type="Texture">
            <Name>{tex_name}</Name>
          </Item>
{SHADER_PARAMS}
        </Parameters>
      </Item>"""
        self.shaders.append((key, xml))
        return len(self.shaders) - 1

    def add_model(self, model, textures, color=None, alpha=None, mesh_alpha=None, vertex_fn=None,
                  double_sided=False, offset=None, meshes=None, rigid=False, turn=False, faces=None):
        """color replaces each mesh's RGB (the game tints some models at run time, like the ball), or
        is {mesh index: RGB} for single meshes; alpha overrides the opacity of every mesh, mesh_alpha
        of single meshes ({index: alpha}). meshes keeps only those mesh indices.
        double_sided adds the back faces (GTA culls them; flat things like confetti need both).
        offset moves the model (in Monkey Ball units and axes) before converting; turn then spins it
        half a turn about the up axis, so a part that faces the front faces the back.
        faces keeps only some triangles, by their winding normal in Monkey Ball axes: "front" those
        facing the way the goal's timer panel does, "edge" those side-on to it.
        vertex_fn(vertex) -> (GTA position, GTA normal, (4 weight bytes, 4 bone indices)) places the
        vertices of a skinned drawable (see set_skeleton) instead of the plain scale-and-swap.
        rigid (with vertex_fn) doesn't skin: each triangle goes whole to the bone that weighs most on
        its corners, and moves with that bone alone (the way the game's own moving prop parts work)."""
        for i, mesh in enumerate(model.meshes):
            if meshes is not None and i not in meshes:
                continue
            tris = list(mesh.triangles())
            if faces is not None:
                tris = [t for t in tris if _facing(t) == faces]
            if not tris:
                continue
            if double_sided:
                tris += [(c, b, a) for a, b, c in tris]
            a = (mesh_alpha or {}).get(i, alpha)
            c = color.get(i) if isinstance(color, dict) else color
            shader = self._shader_for(model, mesh, textures, c, a)
            parts = {}  # bone -> (vertices, indices, seen)
            for t, tri in enumerate(tris):
                back = double_sided and t >= len(tris) // 2
                placed = [vertex_fn(v) for v in tri] if vertex_fn else None
                if placed:
                    p = [x[0] for x in placed]
                else:
                    p = [to_gta(_turned(tuple(v.position[k] + (offset[k] if offset else 0.0) for k in range(3)), turn), self.scale) for v in tri]
                face_n = _unit(_cross(_sub(p[1], p[0]), _sub(p[2], p[0])))
                bone = None
                if rigid and placed:
                    weight = {}
                    for x in placed:
                        for w, b in zip(*x[2]):
                            weight[b] = weight.get(b, 0) + w
                    bone = max(weight, key=weight.get)
                verts, indices, seen = parts.setdefault(bone, ([], [], {}))
                for k, (v, pos) in enumerate(zip(tri, p)):
                    if placed:
                        n, blend = _unit(placed[k][1]), (None if rigid else placed[k][2])
                    else:
                        n, blend = (_unit(to_gta(_turned(v.normal, turn), 1.0)) if v.normal and not back else face_n), None
                    uv = v.uv[0] if v.uv else (0.0, 0.0)
                    key = (pos, n, uv, blend)
                    if key not in seen:
                        seen[key] = len(verts)
                        verts.append(key)
                    indices.append(seen[key])
            for bone, (verts, indices, _) in parts.items():
                self.geometries.append((shader, verts, indices, bone))

    def set_sphere_bounds(self, radius, center=(0.0, 0.0, 0.0), composite=True):
        """Collision: one sphere. composite wraps it in a Bound Composite, as game props do."""
        cx, cy, cz = center
        lo, hi = (cx - radius, cy - radius, cz - radius), (cx + radius, cy + radius, cz + radius)
        volume = 4.0 / 3.0 * math.pi * radius ** 3
        inertia = (0.4 * radius ** 2,) * 3
        self.bounds = ("Sphere", _bound_fields(lo, hi, center, radius, volume, inertia), "", composite)

    def set_mesh_bounds(self, composite=True, boxes=None):
        """Collision: a triangle mesh (BVH) made from the drawable's own geometry, or from boxes
        [((x0, y0, z0), (x1, y1, z1))] in GTA metres instead."""
        verts, index, polys = [], {}, []
        for _, gverts, indices, *_ in (self.geometries if boxes is None else [_box_mesh(lo, hi) for lo, hi in boxes]):
            ids = []
            for p, *_ in gverts:
                key = tuple(round(c, 4) for c in p)
                if key not in index:
                    index[key] = len(verts)
                    verts.append(key)
                ids.append(index[key])
            for i in range(0, len(indices), 3):
                a, b, c = (ids[indices[i + k]] for k in range(3))
                if len({a, b, c}) == 3:
                    polys.append((a, b, c))
        lo, hi, center, radius = self.extents()
        size = [hi[i] - lo[i] for i in range(3)]
        volume = max(size[0] * size[1] * size[2], 1e-4)
        inertia = ((size[1] ** 2 + size[2] ** 2) / 12, (size[0] ** 2 + size[2] ** 2) / 12, (size[0] ** 2 + size[1] ** 2) / 12)
        rows = "\n".join(f"      {_f(v[0])}, {_f(v[1])}, {_f(v[2])}" for v in verts)
        tris = "\n".join(f'      <Triangle m="0" v1="{a}" v2="{b}" v3="{c}" f1="0" f2="0" f3="0" />' for a, b, c in polys)
        extra = f"""    <GeometryCenter x="{_f(center[0])}" y="{_f(center[1])}" z="{_f(center[2])}" />
    <UnkFloat1 value="0" />
    <UnkFloat2 value="0" />
    <Materials>
      <Item>
        <Type value="0" />
        <ProceduralID value="0" />
        <RoomID value="0" />
        <PedDensity value="0" />
        <Flags>NONE</Flags>
        <MaterialColourIndex value="0" />
        <Unk value="0" />
      </Item>
    </Materials>
    <Vertices>
{rows}
    </Vertices>
    <Polygons>
{tris}
    </Polygons>"""
        self.bounds = ("GeometryBVH", _bound_fields(lo, hi, center, radius, volume, inertia), "\n" + extra, composite)

    def _bounds_xml(self):
        if self.bounds is None:
            return ""
        kind, fields, extra, composite = self.bounds
        if not composite:
            return f'  <Bounds type="{kind}">\n{fields}{extra}\n  </Bounds>'
        child = _indent(fields + extra, 4)
        # The composite's own box and sphere are recalculated from its children by the packer.
        return f"""  <Bounds type="Composite">
{fields}
    <Children>
      <Item type="{kind}">
{child}
        <CompositeTransform>
          1 0 0 0
          0 1 0 0
          0 0 1 0
          0 0 0 1
        </CompositeTransform>
        <CompositeFlags1>{COLLISION_TYPE_FLAGS}</CompositeFlags1>
        <CompositeFlags2>{COLLISION_INCLUDE_FLAGS}</CompositeFlags2>
      </Item>
    </Children>
  </Bounds>"""

    def set_skeleton(self, bones):
        """bones: [(name, tag, parent index or -1, translation, rotation quaternion xyzw)], parents first.
        Each bone's translation and rotation are relative to its parent, in GTA space."""
        self.bones = bones

    def _skeleton_xml(self):
        if not self.bones:
            return ""
        items = []
        for i, (name, tag, parent, t, q) in enumerate(self.bones):
            sibling = next((j for j in range(i + 1, len(self.bones)) if self.bones[j][2] == parent), -1)
            flags = ["RotX", "RotY", "RotZ", "TransX", "TransY", "TransZ"] if parent >= 0 else []
            if any(b[2] == i for b in self.bones):
                flags.append("Unk0")  # has children
            flags = ", ".join(flags) or "None"
            items.append(f"""      <Item>
        <Name>{escape(name)}</Name>
        <Tag value="{tag}" />
        <Index value="{i}" />
        <ParentIndex value="{parent}" />
        <SiblingIndex value="{sibling}" />
        <Flags>{flags}</Flags>
        <Translation x="{_f(t[0])}" y="{_f(t[1])}" z="{_f(t[2])}" />
        <Rotation x="{_f(q[0])}" y="{_f(q[1])}" z="{_f(q[2])}" w="{_f(q[3])}" />
        <Scale x="1" y="1" z="1" />
        <TransformUnk x="0" y="4" z="-3" w="0" />
      </Item>""")
        nl = "\n"
        # The game keys its cached bone mappings for animation by these skeleton signatures, so they
        # must be non-zero and unique to this skeleton. 0x01000000 is the flag value game prop skeletons carry.
        sig = zlib.crc32("|".join(f"{b[0]}:{b[1]}:{b[2]}" for b in self.bones).encode())
        return f"""  <Skeleton>
    <Unknown1C value="16777216" />
    <Unknown50 value="{sig}" />
    <Unknown54 value="{zlib.crc32(b"nonchiral", sig)}" />
    <Unknown58 value="{zlib.crc32(b"comprehensive", sig)}" />
    <Bones>
{nl.join(items)}
    </Bones>
  </Skeleton>
"""

    def _bone_world(self):
        """Each bone's rest transform in the drawable's space, as (3x3 rows, translation)."""
        world = []
        for name, tag, parent, t, q in self.bones or []:
            m = (_quat_matrix(q), tuple(t))
            if parent >= 0:
                pm, pt = world[parent]
                m = (_mat_mul(pm, m[0]), _add(_mat_vec(pm, m[1]), pt))
            world.append(m)
        return world

    def extents(self):
        pts = [v[0] for _, verts, *_ in self.geometries for v in verts]
        lo = tuple(min(p[i] for p in pts) for i in range(3))
        hi = tuple(max(p[i] for p in pts) for i in range(3))
        center = tuple((lo[i] + hi[i]) / 2 for i in range(3))
        radius = max(math.dist(center, p) for p in pts)
        return lo, hi, center, radius

    def write(self, out_dir):
        """Write <name>.ydr.xml and <name>/<texture>.dds into out_dir."""
        tex_dir = os.path.join(out_dir, self.name)
        os.makedirs(tex_dir, exist_ok=True)
        tex_items = []
        for tex_name, w, h, rgba in self.textures:
            write_dds(os.path.join(tex_dir, tex_name + ".dds"), w, h, rgba)
            tex_items.append(f"""      <Item>
        <Name>{tex_name}</Name>
        <Unk32 value="0" />
        <Usage>UNKNOWN</Usage>
        <ExtraFlags value="0" />
        <Width value="{w}" />
        <Height value="{h}" />
        <MipLevels value="1" />
        <Format>D3DFMT_A8R8G8B8</Format>
        <FileName>{tex_name}.dds</FileName>
      </Item>""")
        nl = "\n"
        models = {}  # bone index or None (skinned) -> geometry xml
        world = self._bone_world()
        for shader, verts, indices, bone in sorted(self.geometries, key=lambda g: g[0]):
            skinned = self.bones is not None and bone is None
            if bone is not None:
                # A rigid part is drawn in its bone's space.
                to_local = _invert(world[bone])
                verts = [(_apply(to_local, p), _unit(_rotate(to_local, n)), uv, b) for p, n, uv, b in verts]
            elif skinned:
                verts = [(p, n, uv, _blend_order(b)) for p, n, uv, b in verts]
            lo = [min(v[0][i] for v in verts) for i in range(3)]
            hi = [max(v[0][i] for v in verts) for i in range(3)]
            # Columns follow the layout's semantic order: position, blend weights, blend indices, normal, ...
            rows = "\n".join(
                f"              {_f(p[0])} {_f(p[1])} {_f(p[2])}   "
                + (f"{' '.join(map(str, b[0]))}   {' '.join(map(str, b[1]))}   " if skinned else "")
                + f"{_f(n[0])} {_f(n[1])} {_f(n[2])}   255 255 255 255   {_f(uv[0])} {_f(uv[1])}" for p, n, uv, b in verts)
            layout = ("              <Position />\n" + ("              <BlendWeights />\n              <BlendIndices />\n" if skinned else "")
                      + "              <Normal />\n              <Colour0 />\n              <TexCoord0 />")
            bone_ids = (f"\n          <BoneIDs>{', '.join(str(i) for i in range(len(self.bones)))}</BoneIDs>" if skinned else "")
            idx = "\n".join("              " + " ".join(map(str, indices[i:i + 24])) for i in range(0, len(indices), 24))
            models.setdefault(bone, []).append(f"""        <Item>
          <ShaderIndex value="{shader}" />
          <BoundingBoxMin x="{_f(lo[0])}" y="{_f(lo[1])}" z="{_f(lo[2])}" />
          <BoundingBoxMax x="{_f(hi[0])}" y="{_f(hi[1])}" z="{_f(hi[2])}" />{bone_ids}
          <VertexBuffer>
            <Flags value="0" />
            <Layout type="GTAV1">
{layout}
            </Layout>
            <Data>
{rows}
            </Data>
          </VertexBuffer>
          <IndexBuffer>
            <Data>
{idx}
            </Data>
          </IndexBuffer>
        </Item>""")
        model_items = []
        for bone, geoms in sorted(models.items(), key=lambda m: -1 if m[0] is None else m[0]):
            skinned = self.bones is not None and bone is None
            # A skinned model carries flag 1 and the skeleton's bone count, as Sollumz writes them.
            model_items.append(f"""    <Item>
      <RenderMask value="255" />
      <Flags value="{int(skinned)}" />
      <HasSkin value="{int(skinned)}" />
      <BoneIndex value="{bone or 0}" />
      <Unknown1 value="{len(self.bones) if skinned else 0}" />
      <Geometries>
{nl.join(geoms)}
      </Geometries>
    </Item>""")
        lo, hi, center, radius = self.extents()
        xml = f"""<?xml version="1.0" encoding="UTF-8"?>
<Drawable>
  <Name>{escape(self.name)}</Name>
  <BoundingSphereCenter x="{_f(center[0])}" y="{_f(center[1])}" z="{_f(center[2])}" />
  <BoundingSphereRadius value="{_f(radius)}" />
  <BoundingBoxMin x="{_f(lo[0])}" y="{_f(lo[1])}" z="{_f(lo[2])}" />
  <BoundingBoxMax x="{_f(hi[0])}" y="{_f(hi[1])}" z="{_f(hi[2])}" />
  <LodDistHigh value="9998" />
  <LodDistMed value="9998" />
  <LodDistLow value="9998" />
  <LodDistVlow value="9998" />
  <FlagsHigh value="{len(model_items)}" />
  <FlagsMed value="0" />
  <FlagsLow value="0" />
  <FlagsVlow value="0" />
  <ShaderGroup>
    <TextureDictionary>
{nl.join(tex_items)}
    </TextureDictionary>
    <Shaders>
{nl.join(xml for _, xml in self.shaders)}
    </Shaders>
  </ShaderGroup>
{self._skeleton_xml()}  <DrawableModelsHigh>
{nl.join(model_items)}
  </DrawableModelsHigh>
{self._bounds_xml()}
</Drawable>
"""
        path = os.path.join(out_dir, self.name + ".ydr.xml")
        with open(path, "w", encoding="utf-8", newline="\n") as f:
            f.write(xml)
        return path, (lo, hi, center, radius)


def _sub(a, b):
    return (a[0] - b[0], a[1] - b[1], a[2] - b[2])


def _cross(a, b):
    return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])


def _add(a, b):
    return (a[0] + b[0], a[1] + b[1], a[2] + b[2])


def _quat_matrix(q):
    x, y, z, w = q
    return ((1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)),
            (2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)),
            (2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)))


def _mat_vec(m, v):
    return tuple(m[r][0] * v[0] + m[r][1] * v[1] + m[r][2] * v[2] for r in range(3))


def _mat_mul(a, b):
    return tuple(tuple(sum(a[r][k] * b[k][c] for k in range(3)) for c in range(3)) for r in range(3))


def _invert(m):
    """A rotation and translation, inverted."""
    rot, t = m
    rt = tuple(tuple(rot[c][r] for c in range(3)) for r in range(3))
    return rt, tuple(-c for c in _mat_vec(rt, t))


def _apply(m, p):
    return _add(_mat_vec(m[0], p), m[1])


def _rotate(m, n):
    return _mat_vec(m[0], n)


def _blend_order(blend):
    """Weights and their bone indices in the slots the game's own files use: sorted lightest first,
    then turned one slot left, so the heaviest sits third (as Sollumz writes them)."""
    pairs = sorted(zip(*blend), key=lambda p: p[0])
    pairs = pairs[1:] + pairs[:1]
    return tuple(p[0] for p in pairs), tuple(p[1] for p in pairs)
