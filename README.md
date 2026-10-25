# monkeyball-gta

Play GTA V's Los Santos as Super Monkey Ball. AiAi rolls inside his ball with Super Monkey Ball
Deluxe's own physics, tilt and camera. Each level starts somewhere random on the road and puts a goal
somewhere else on the map, with a clock to beat. Cars, people and props get flattened, the minimap
shows your route, and a new level starts each time you finish, forever.

> **This repository contains no game files.** No Sega or Rockstar models, textures, sounds or code
> are here. The build reads **your own** disc image of *Super Monkey Ball Deluxe* (original Xbox) and
> makes the ball, goal, monkey, HUD and sounds on your PC. What it makes is yours alone: please don't
> upload or share it. Story mode only; never take a modded game into GTA Online.

## What you need

- **Windows 10 or 11.**
- **GTA V Legacy** (the original PC version, from Steam, Epic or Rockstar), updated and launched once.
  *GTA V Enhanced isn't supported.*
- **A disc image (`.iso`) of Super Monkey Ball Deluxe for the original Xbox**, made from your own disc.
  The build checks that it's the right game.
- Free tools, installed in step 1: Script Hook V, ScriptHookVDotNet, OpenIV, Python, the .NET 8 SDK and Git.

## 1. Install the tools (once)

The examples use the default Steam folder,
`C:\Program Files (x86)\Steam\steamapps\common\Grand Theft Auto V`. If your game is somewhere else, use
your own folder wherever you see that path. It's the folder that has `GTA5.exe` in it.

1. **Script Hook V**: download it from <http://www.dev-c.com/gtav/scripthookv/>. From the zip's `bin`
   folder, copy `ScriptHookV.dll` and `dinput8.dll` into your GTA V folder. A game update can break
   it until a new Script Hook V comes out. If GTA shows a Script Hook V error at start, check that
   page for a new version.
2. **ScriptHookVDotNet v3** (3.6.0 or newer): download the latest release zip from
   <https://github.com/scripthookvdotnet/scripthookvdotnet/releases> and copy everything in it into
   your GTA V folder. Make a folder called `scripts` there if there isn't one:
   `C:\Program Files (x86)\Steam\steamapps\common\Grand Theft Auto V\scripts`.
3. **OpenIV**: install it from <https://openiv.com/>, open it and pick *GTA V* > *Windows*. Then go to
   *Tools* > *ASI Manager* and install **ASI Loader** (if it isn't shown as installed) and **OpenIV.ASI**. This makes GTA read add-ons from its `mods`
   folder, so your original game files are never touched.
4. **Python 3.9 or newer**: download it from <https://www.python.org/downloads/>. In the installer,
   tick **"Add python.exe to PATH"**.
5. **.NET 8 SDK**: download it from <https://dotnet.microsoft.com/download/dotnet/8.0>. Pick the
   **SDK** for Windows x64, not just the runtime.
6. **Git**: download it from <https://git-scm.com/downloads>. The default options are fine.

Open a **new** Command Prompt or PowerShell window after installing, so it can find the new tools.

## 2. Get the code

```powershell
cd %USERPROFILE%\Documents
git clone https://github.com/rkwitz/monkeyball-gta
cd monkeyball-gta
python -m pip install -r requirements.txt
```

In PowerShell, use `cd $HOME\Documents` for the first line.

## 3. Build and install

Run this from the `monkeyball-gta` folder. Put your own `.iso` path in `--iso`, and your GTA V
folder in `--gta`. **Close GTA V first.**

```powershell
python build.py --iso "C:\Users\you\Downloads\Super Monkey Ball Deluxe.iso" --gta "C:\Program Files (x86)\Steam\steamapps\common\Grand Theft Auto V"
```

The first run takes a few minutes. It unpacks the disc, downloads the
[CodeWalker](https://github.com/dexyfex/CodeWalker) source (a pinned version, used to write GTA's file
formats), converts everything, builds the script and copies the result into your GTA V folder:

| File | What it is |
| --- | --- |
| `scripts\MonkeyBallGta.dll` | the mod |
| `scripts\MonkeyBallGta\sfx\*.wav` | the game's sounds, from your disc |
| `mods\update\x64\dlcpacks\mbgta\dlc.rpf` | the ball, goal, AiAi and HUD art, from your disc |

If you leave out `--gta`, nothing is copied. Copy the folders inside `monkeyball-gta\build\dist` into
your GTA V folder yourself.

If Windows says you don't have permission to write to `Program Files`, run the command from a
Command Prompt opened with **Run as administrator**.

## 4. Tell GTA about the pack (once)

GTA only loads add-on packs listed in its `dlclist.xml`. OpenIV edits a copy of that file in the
`mods` folder.

1. Open OpenIV and turn on **Edit mode** (top bar).
2. If there's no `mods\update\update.rpf` yet: browse to `update\update.rpf` in the game folder,
   right-click it and choose **Copy to "mods" folder**.
3. Browse to `mods\update\update.rpf\common\data\` and open **`dlclist.xml`** (right-click > Edit).
4. Add this line just before `</Paths>` near the end, then save:

   ```xml
   <Item>dlcpacks:/mbgta/</Item>
   ```

A big GTA update can replace `update.rpf`. If the ball stops loading after an update, do this step again.

## 5. Play

Start GTA V **story mode**. Once you're in control, ball mode starts by itself.

| Control | Does |
| --- | --- |
| Left stick / WASD | tilt the world (the ball rolls downhill) |
| F5 | new level (also turns ball mode back on) |
| Ctrl+F5 | leave ball mode (normal GTA) |
| F6 / F7 | set the time to day / night |
| F8 / F9 | lower / raise the max tilt, 1° a press, remembered (Super Monkey Ball uses 23°; the default here is 26°) |

Roll through the goal before the clock runs out. Falling in the water or running out of time
restarts the level. You get points for bananas and for time left at the goal, and the points double
if more than half the clock is left, the same as in Deluxe.

## Updating

```powershell
cd %USERPROFILE%\Documents\monkeyball-gta
git pull
python build.py --gta "C:\Program Files (x86)\Steam\steamapps\common\Grand Theft Auto V"
```

The unpacked disc is kept in `build\`, so you don't need `--iso` again.

## Troubleshooting

- **"mbgta: the ball model isn't installed"** in game: the pack isn't loading. Check the `dlclist.xml`
  line from step 4. Check that the file is at
  `<GTA V>\mods\update\x64\dlcpacks\mbgta\dlc.rpf`, and that OpenIV.ASI is installed (step 1.3).
- **Nothing happens in story mode**: check `<GTA V>\ScriptHookVDotNet.log` and
  `<GTA V>\scripts\MonkeyBallGta.log`. Press F4 to open the ScriptHookVDotNet console.
- **GTA closes at start with a Script Hook V message**: your game updated. Get the newest Script Hook V.
- **No sound**: check that `<GTA V>\scripts\MonkeyBallGta\sfx\` has `.wav` files in it, and run
  step 3 again.
- **`python` or `dotnet` "is not recognized"**: open a new terminal window after installing. For
  Python, re-run its installer and tick "Add python.exe to PATH".
- **The build stops with an error**: run it again; finished steps are skipped. If it fails at the same
  place, open an issue with the last 30 lines it printed. Don't attach anything from `build\`.

## Uninstall

Delete `scripts\MonkeyBallGta.dll`, the `scripts\MonkeyBallGta\` folder and
`mods\update\x64\dlcpacks\mbgta\`, and remove the `dlclist.xml` line. Script Hook V and
ScriptHookVDotNet can stay or go; deleting `dinput8.dll` turns all script mods off.

## How it works

See [docs/HOW_IT_WORKS.md](docs/HOW_IT_WORKS.md).

## License

The code in this repository is under the [MIT License](LICENSE). That covers this project's own
scripts and tools only, not Super Monkey Ball, GTA V, or anything the build makes from your disc.

## Credits

- Super Monkey Ball is © SEGA. Grand Theft Auto V is © Rockstar Games. This is a fan project with no
  affiliation with either.
- Physics, camera, HUD and sequence timings follow the
  [Super Monkey Ball decompilation](https://github.com/camthesaxman/smb-decomp) project's research.
- GTA files are written with [CodeWalker](https://github.com/dexyfex/CodeWalker) by dexyfex.
- Scripts run on [Script Hook V](http://www.dev-c.com/gtav/scripthookv/) by Alexander Blade and
  [ScriptHookVDotNet](https://github.com/scripthookvdotnet/scripthookvdotnet).
