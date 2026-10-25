# Notes for coding agents

Read `README.md`, then `docs/HOW_IT_WORKS.md`.

- GTA V Legacy, story mode only. Script Hook V + ScriptHookVDotNet v3, C#. Never run modded clients online.
- Never commit game files, Sega assets, or anything under `build/` (all of it comes from the player's disc).
- `python build.py --iso <disc> [--gta <GTA V folder>]` is the whole pipeline. Keep it working from a fresh
  clone, and keep the pack byte-identical across machines (LF in generated text, no timestamps).
- CodeWalker is cloned by `build.py` at a pinned commit (`CODEWALKER_COMMIT`); don't vendor it.
- Physics and timings follow the SMB1 decompilation (github.com/camthesaxman/smb-decomp). Read it for
  behaviour and constants; comments cite the function a piece of code follows.
