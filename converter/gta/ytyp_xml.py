"""Write a .ytyp.xml (archetype definitions) so GTA knows the add-on props by name."""
from xml.sax.saxutils import escape


def _v(tag, v):
    return f'      <{tag} x="{v[0]:.7f}" y="{v[1]:.7f}" z="{v[2]:.7f}" />'


# Archetype flag bits (Sollumz's names). DYNAMIC lets the game move the prop with physics.
DYNAMIC = 1 << 17
HAS_ANIM = 1 << 9  # the prop has clips (a .ycd), so the game gives it an animation player


def write_ytyp(path, ytyp_name, archetypes):
    """archetypes: list of (name, (bbmin, bbmax, centre, radius), lod_dist, flags[, clip dictionary])."""
    items = []
    for name, (lo, hi, centre, radius), lod, flags, *clips in archetypes:
        n = escape(name)
        clip = f"<clipDictionary>{escape(clips[0])}</clipDictionary>" if clips else "<clipDictionary />"
        items.append(f"""    <Item type="CBaseArchetypeDef">
      <lodDist value="{lod:.1f}" />
      <flags value="{flags}" />
      <specialAttribute value="0" />
{_v("bbMin", lo)}
{_v("bbMax", hi)}
{_v("bsCentre", centre)}
      <bsRadius value="{radius:.7f}" />
      <hdTextureDist value="{min(lod, 100.0):.1f}" />
      <name>{n}</name>
      <textureDictionary />
      {clip}
      <drawableDictionary />
      <physicsDictionary />
      <assetType>ASSET_TYPE_DRAWABLE</assetType>
      <assetName>{n}</assetName>
      <extensions />
    </Item>""")
    body = "\n".join(items)
    xml = f"""<?xml version="1.0" encoding="UTF-8"?>
<CMapTypes>
  <extensions />
  <archetypes>
{body}
  </archetypes>
  <name>{escape(ytyp_name)}</name>
  <dependencies />
  <compositeEntityTypes />
</CMapTypes>
"""
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(xml)
