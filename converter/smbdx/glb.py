"""Export GMA models to binary glTF (.glb) for checking in Blender or any glTF viewer.

Debugging aid only: the GTA pipeline reads the parsed models directly.
"""
import io
import json
import struct

from .png import write_png


def _png_bytes(tex):
    buf = io.BytesIO()
    write_png(buf, tex.width, tex.height, tex.rgba)
    return buf.getvalue()


def export_glb(path, models, textures):
    """Write `models` (list of gma.Model) into one .glb, one node per model."""
    gltf = {"asset": {"version": "2.0", "generator": "monkeyball-gta converter"},
            "scene": 0, "scenes": [{"nodes": []}], "nodes": [], "meshes": [],
            "materials": [], "textures": [], "images": [], "samplers": [{"wrapS": 10497, "wrapT": 10497}],
            "accessors": [], "bufferViews": [], "buffers": []}
    blob = bytearray()
    image_for_texture = {}

    def add_view(data, target=None):
        while len(blob) % 4:
            blob.append(0)
        view = {"buffer": 0, "byteOffset": len(blob), "byteLength": len(data)}
        if target:
            view["target"] = target
        blob.extend(data)
        gltf["bufferViews"].append(view)
        return len(gltf["bufferViews"]) - 1

    def add_accessor(values, kind, comps):
        flat = [c for v in values for c in v]
        view = add_view(struct.pack(f"<{len(flat)}f", *flat), 34962)
        acc = {"bufferView": view, "componentType": 5126, "count": len(values), "type": kind}
        if kind == "VEC3" and comps == "pos":
            acc["min"] = [min(v[i] for v in values) for i in range(3)]
            acc["max"] = [max(v[i] for v in values) for i in range(3)]
        gltf["accessors"].append(acc)
        return len(gltf["accessors"]) - 1

    def material_for(model, mesh):
        r, g, b, a = (c / 255 for c in mesh.diffuse)
        mat = {"name": f"{model.name}_{len(gltf['materials'])}", "doubleSided": bool(mesh.render_flags & 2),
               "pbrMetallicRoughness": {"baseColorFactor": [r, g, b, a], "metallicFactor": 0.0,
                                        "roughnessFactor": 0.4}}
        if mesh.layer == 2:
            # Layer 2 is the game's translucent pass (ball shell, goal film). The real blend
            # mode isn't decoded yet, so the preview just makes it half see-through.
            mat["alphaMode"] = "BLEND"
            mat["pbrMetallicRoughness"]["baseColorFactor"][3] = a * 0.5
        # The first non-environment texture is the base color. Environment/highlight maps
        # (the ball's shine) need a reflection shader, so the preview leaves them out.
        for index in mesh.material_indices:
            material = model.materials[index]
            tex = textures[material.texture_index] if material.texture_index < len(textures) else None
            if material.is_env_map or tex is None:
                continue
            if material.texture_index not in image_for_texture:
                view = add_view(_png_bytes(tex))
                gltf["images"].append({"bufferView": view, "mimeType": "image/png",
                                       "name": f"tex{material.texture_index:02d}"})
                gltf["textures"].append({"sampler": 0, "source": len(gltf["images"]) - 1})
                image_for_texture[material.texture_index] = len(gltf["textures"]) - 1
            mat["pbrMetallicRoughness"]["baseColorTexture"] = {"index": image_for_texture[material.texture_index]}
            break
        gltf["materials"].append(mat)
        return len(gltf["materials"]) - 1

    for model in models:
        prims = []
        for mesh in model.meshes:
            tris = list(mesh.triangles())
            if not tris:
                continue
            verts = [v for tri in tris for v in tri]
            attrs = {"POSITION": add_accessor([v.position for v in verts], "VEC3", "pos")}
            if all(v.normal for v in verts):
                attrs["NORMAL"] = add_accessor([_unit(v.normal) for v in verts], "VEC3", "nrm")
            if all(v.uv for v in verts):
                attrs["TEXCOORD_0"] = add_accessor([v.uv[0] for v in verts], "VEC2", "uv")
            if all(v.color for v in verts):
                attrs["COLOR_0"] = add_accessor([tuple(c / 255 for c in v.color) for v in verts], "VEC4", "col")
            prims.append({"attributes": attrs, "material": material_for(model, mesh)})
        if not prims:
            continue
        gltf["meshes"].append({"name": model.name, "primitives": prims})
        gltf["nodes"].append({"name": model.name, "mesh": len(gltf["meshes"]) - 1})
        gltf["scenes"][0]["nodes"].append(len(gltf["nodes"]) - 1)

    for key in ("textures", "images", "materials"):
        if not gltf[key]:
            del gltf[key]
    while len(blob) % 4:
        blob.append(0)
    gltf["buffers"].append({"byteLength": len(blob)})
    js = json.dumps(gltf, separators=(",", ":")).encode()
    js += b" " * (-len(js) % 4)
    with open(path, "wb") as f:
        f.write(struct.pack("<III", 0x46546C67, 2, 12 + 8 + len(js) + 8 + len(blob)))
        f.write(struct.pack("<II", len(js), 0x4E4F534A) + js)
        f.write(struct.pack("<II", len(blob), 0x004E4942) + blob)


def _unit(n):
    length = (n[0] ** 2 + n[1] ** 2 + n[2] ** 2) ** 0.5 or 1.0
    return (n[0] / length, n[1] / length, n[2] / length)
