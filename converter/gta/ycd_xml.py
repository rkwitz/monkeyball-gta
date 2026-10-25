"""Write a CodeWalker .ycd.xml clip dictionary: one clip per animation, every bone's position and
rotation sampled once per frame.

Two things the game insists on that the XML doesn't check: the tracks must be listed track-major
(every bone's position, then every bone's rotation; the game looks them up by track << 16 | bone),
and moving values are QuantizeFloat channels (16 bits over the value's range), the kind the game's
own clips use. Unknown1C is the animation's signature, which only has to be unique."""
import zlib
from xml.sax.saxutils import escape

TRACK_POSITION, TRACK_ROTATION = 0, 1
FORMAT_VECTOR3, FORMAT_QUATERNION = 0, 1


def _channels(columns):
    out = []
    for values in columns:
        if max(values) - min(values) < 1e-6:
            out.append(f"""            <Item>
             <Type value="StaticFloat" />
             <Value value="{values[0]:.7f}" />
            </Item>""")
        else:
            lo = min(values)
            quantum = (max(values) - lo) / 65535
            body = " ".join(f"{v:.7f}" for v in values)
            out.append(f"""            <Item>
             <Type value="QuantizeFloat" />
             <Quantum value="{quantum:.9g}" />
             <Offset value="{lo:.7f}" />
             <Values>{body}</Values>
            </Item>""")
    return "\n".join(out)


def write_ycd(path, animations):
    """animations: [(name, duration in seconds, {bone tag: (positions [(x, y, z)], rotations [(x, y, z, w)])})],
    every list one entry per frame."""
    clips, anims = [], []
    for name, duration, tracks in animations:
        n = escape(name)
        frames = len(next(iter(tracks.values()))[0])
        clips.append(f"""  <Item>
   <Hash>{n}</Hash>
   <Name>pack:/{n}.clip</Name>
   <Type value="Animation" />
   <Unknown30 value="0" />
   <Tags />
   <Properties />
   <AnimationHash>{n}</AnimationHash>
   <StartTime value="0" />
   <EndTime value="{duration:.7f}" />
   <Rate value="1" />
  </Item>""")
        entries = []
        for tag in tracks:
            positions, rotations = tracks[tag]
            # Neighbouring quaternions on the same side, or the game blends the long way round.
            fixed = [tuple(rotations[0])]
            for q in rotations[1:]:
                if sum(a * b for a, b in zip(fixed[-1], q)) < 0:
                    q = tuple(-c for c in q)
                fixed.append(tuple(q))
            entries.append((TRACK_POSITION, tag, FORMAT_VECTOR3, positions, 3))
            entries.append((TRACK_ROTATION, tag, FORMAT_QUATERNION, fixed, 4))
        entries.sort(key=lambda e: e[0] << 16 | e[1])
        bone_ids, data = [], []
        for track, tag, fmt, values, width in entries:
            bone_ids.append(f"""     <Item>
      <BoneId value="{tag}" />
      <Track value="{track}" />
      <Unk0 value="{fmt}" />
     </Item>""")
            data.append(f"""        <Item>
         <Channels>
{_channels([[v[k] for v in values] for k in range(width)])}
         </Channels>
        </Item>""")
        nl = "\n"
        anims.append(f"""  <Item>
   <Hash>{n}</Hash>
   <Unknown10 value="0" />
   <FrameCount value="{frames}" />
   <SequenceFrameLimit value="{frames + 30}" />
   <Duration value="{duration:.7f}" />
   <Unknown1C>hash_{zlib.crc32(name.encode()):08X}</Unknown1C>
   <BoneIds>
{nl.join(bone_ids)}
   </BoneIds>
   <Sequences>
    <Item>
     <Hash>hash_00000000</Hash>
     <FrameCount value="{frames}" />
     <SequenceData>
{nl.join(data)}
     </SequenceData>
    </Item>
   </Sequences>
  </Item>""")
    nl = "\n"
    xml = f"""<?xml version="1.0" encoding="UTF-8"?>
<ClipDictionary>
 <Clips>
{nl.join(clips)}
 </Clips>
 <Animations>
{nl.join(anims)}
 </Animations>
</ClipDictionary>
"""
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(xml)
