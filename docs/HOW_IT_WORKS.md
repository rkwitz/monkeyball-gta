# How it works

There are three parts. `build.py` runs them in order.

## converter/ (Python): your disc to GTA files

- `extract_xiso.py` unpacks the Xbox disc image (plain XISO or a full disc image).
- `smbdx/` reads Deluxe's formats. That covers GMA models, TPL textures, LZ archives, the fonts,
  ADX/ACX sound banks, the `Default.xbe` sound table, and AiAi's skeleton and animations (`ape.py`:
  `.skl`, `.mta`, `.mal`, with the game's IK rig).
- `extract_level_assets.py` writes the HUD sprites, fonts and level sounds as PNG and WAV files.
  `build.py` asks it only for what the mod uses: `--only sprites,fonts,sounds`.
- `gta/` writes CodeWalker XML: drawables (`ydr_xml.py`), AiAi's skinned drawable and clip dictionary
  (`aiai.py`, `ycd_xml.py`), the HUD texture dictionary (`hud.py`) and the archetypes (`ytyp_xml.py`).
- `build_gta.py` decides what goes into the pack: the ball halves, the goal (with a second timer panel
  on the back), the LCD digits, the party ball, the confetti, bananas, the hit stars, the goal tape,
  AiAi and the HUD. It writes the XML and then runs the packer.
- `install_audio.py` lays out the sounds where the script plays them from.

## packer/ (C#, .NET 8): XML to binary

`MbgtaPacker` uses CodeWalker.Core (cloned by `build.py`, never committed) to turn the XML into
`.ydr`/`.ycd`/`.ytd`/`.ytyp`. `--check` reloads each file to prove it. `dlc` wraps them in a
story-mode add-on pack (`dlc.rpf` with `content.xml`, `setup2.xml` and `x64/mbgta_props.rpf`).
The output is meant to be byte-for-byte the same on every machine, given the same disc.

## src/MonkeyBallGta (C#, ScriptHookVDotNet v3, .NET Framework 4.8): the mod

| File | Does |
| --- | --- |
| `BallMode.cs` | The main loop: level sequence (READY, GO!, play, GOAL / FALL OUT / TIME OVER), input, GTA collision (ray probes, car boxes, smashing props), timer, score, keys |
| `SmbPhysics.cs` | Monkey Ball's ball physics, stage tilt and follow camera, ported from the decompilation at 60 steps a second |
| `Level.cs` | Random start and goal on the road network, the GPS route, and the goal gate with its posts |
| `Monkey.cs` | AiAi inside the ball and which animation plays |
| `GoalShow.cs` | The goal's timer digits, tape, party ball and confetti |
| `Hud.cs` | The Deluxe HUD, the minimap (GTA's own radar tiles and route, in Deluxe's ring) and the goal score tally |
| `Bananas.cs`, `Effects.cs` | Bananas along the route; hit stars and sparkles |
| `Audio.cs` | A small WAV mixer for the game's sounds (GTA can only play its own sound banks) |
| `Raycast.cs`, `Util.cs` | Shape tests and helpers |

Sizes are Monkey Ball units scaled to 3.5 m, so the ball is about the size of a small car.
