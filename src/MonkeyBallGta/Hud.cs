// The Deluxe HUD, drawn with the sprites in mbgta_hud.ytd (gta/hud.py): SCORE top left, the TIME
// plate top centre, bananas and AiAi's face top right, speed and the level bar bottom left, the
// round minimap bottom right, and the READY / GO / GOAL!! / FALL OUT / TIME OVER banners.
//
// Layout is in Deluxe's own 640x448 screen (measured from a screenshot of the game), in square
// pixels, anchored to the left, centre or right of GTA's screen so it fits any aspect ratio.
//
// GTA's minimap can't be moved or reshaped by a script, so the radar is hidden and drawn again
// inside the ring from GTA's own radar map tiles (the minimap_<row>_<col> texture dictionaries the
// radar streams, each covering TileMetres of the world), cut into thin strips so the map fills a
// circle, turned with the camera, with the road route to the goal drawn over it as a line. The
// tiles are about 9 m a pixel, so the ring shows a wide area: zoomed in they only blur.
// (Moving the real radar with an edited frontend.xml put it in the wrong place and changed it
// outside ball mode too, so that was dropped.)
// It also draws the ball's effects (Effects.cs) as sprites standing in the world.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using GTA.Math;
using GTA.Native;
using static MonkeyBallGta.Util;

namespace MonkeyBallGta
{
    class Hud
    {
        const string Dict = "mbgta_hud";
        const float ScreenW = 640f, ScreenH = 448f;
        enum Anchor { Left, Centre, Right }

        // Minimap: where it sits (Deluxe px), its radius, and how many metres that radius shows.
        const float MapX = 585f, MapY = 372f, MapR = 113f, MapMetres = 1600f;
        const float RouteWidth = 2.5f;  // about GTA's own route line (Deluxe px)
        const float RouteStepPx = 3f;  // the route is drawn as segments about this long (Deluxe px)
        const int Strips = 56;
        // GTA's radar tiles: 3 rows of 2, row 0 at the north, each this many metres square, the
        // top-left corner of tile 0_0 at (TileLeft, TileTop).
        const int TileRows = 3, TileCols = 2;
        const float TileMetres = 4500f, TileLeft = -4140f, TileTop = 8400f;
        // DRAW_SPRITE turns clockwise for a positive heading (flip this if the map turns the wrong way).
        const float Turn = 1f;

        readonly Dictionary<string, IntPtr> names = new Dictionary<string, IntPtr>();
        readonly Dictionary<string, float> widths = new Dictionary<string, float>();
        IntPtr dict;
        bool requested, loaded;
        float aspect = 16f / 9f;

        // The radar tiles: [row, col] dictionary (its texture has the same name), and whether it's in.
        readonly IntPtr[,] tiles = new IntPtr[TileRows, TileCols], seaTiles = new IntPtr[TileRows, TileCols];
        readonly bool[,] tileIn = new bool[TileRows, TileCols], seaIn = new bool[TileRows, TileCols];
        int tileWait;
        bool tilesLogged;

        // The camera, for sprites in the world.
        Vector3 eye, look;
        float fov = 60f;

        public List<Vector3> Route;  // the road to the goal (GTA positions), or null

        public void Request()
        {
            if (dict == IntPtr.Zero)
                dict = Utf8(Dict);
            Function.Call(Hash.REQUEST_STREAMED_TEXTURE_DICT, dict, false);
            for (int r = 0; r < TileRows; r++)
                for (int c = 0; c < TileCols; c++)
                {
                    if (tiles[r, c] == IntPtr.Zero)
                    {
                        tiles[r, c] = Utf8($"minimap_{r}_{c}");
                        seaTiles[r, c] = Utf8($"minimap_sea_{r}_{c}");
                    }
                    Function.Call(Hash.REQUEST_STREAMED_TEXTURE_DICT, tiles[r, c], false);
                    Function.Call(Hash.REQUEST_STREAMED_TEXTURE_DICT, seaTiles[r, c], false);
                }
            requested = true;
        }

        public void Reset() => Route = null;

        public void SetView(Vector3 eye, Vector3 look, float fov)
        {
            this.eye = eye;
            this.look = look;
            this.fov = fov;
        }

        public struct State
        {
            public int TimeFrames, Score, Bananas;
            public float SpeedMph;
            public string Level;
            public string Banner;      // READY, GO!, GOAL!, FALL OUT, TIME OVER or null
            public int BannerAge, BannerLength;
            public Vector3 Ball, Goal;
            public bool HasGoal;
            public float CameraHeading;  // GTA degrees
            public Vector3 Velocity;     // the ball's, m/s
            public float MapSize;        // 0..1 of its full size
            public float MapSpin;        // degrees
            public Tally Tally;
            public int TallyAge;         // frames since the goal
        }

        public struct Tally
        {
            public int Clear, Stage;
            public bool TimeBonus, Shown;
        }

        public void Draw(State s)
        {
            if (!requested)
                Request();
            if (!loaded)
            {
                loaded = Function.Call<bool>(Hash.HAS_STREAMED_TEXTURE_DICT_LOADED, dict);
                if (!loaded)
                    return;
            }
            aspect = Function.Call<float>(Hash.GET_ASPECT_RATIO, false);
            if (aspect < 1f)
                aspect = 16f / 9f;
            Function.Call(Hash.DISPLAY_RADAR, false);  // BallMode turns it back on when ball mode ends
            // The vehicle name, area name, vehicle class and street name GTA prints by the radar.
            for (int c = 6; c <= 9; c++)
                Function.Call(Hash.HIDE_HUD_COMPONENT_THIS_FRAME, c);

            // SCORE
            Box(Anchor.Left, 16f, 24f, 58f, 19f);
            Sprite("score", Anchor.Left, 44f, 33f, 52f, 17f);
            Digits("sd", s.Score.ToString("0000000"), Anchor.Left, 89f, 33f, 17f, 21f, 17f);

            // TIME: seconds in three large digits, hundredths in two small ones under them.
            int hundredths = Math.Max(0, s.TimeFrames) * 100 / 60;
            Sprite("plate", Anchor.Centre, 320f, 57f, 106f, 86f);
            Sprite("time", Anchor.Centre, 320f, 25f, 50f, 22f);
            Digits("td", (hundredths / 100 % 1000).ToString("000"), Anchor.Centre, 292f, 60f, 30f, 42f, 28f);
            Sprite("tsdot", Anchor.Centre, 308f, 89f, 8f, 18f);
            Digits("ts", (hundredths % 100).ToString("00"), Anchor.Centre, 319f, 89f, 12f, 18f, 12f);

            // Bananas and AiAi.
            Box(Anchor.Right, 565f, 24f, 56f, 19f);
            Sprite("banana", Anchor.Right, 549f, 33f, 24f, 24f);
            Digits("sd", Math.Min(999, s.Bananas).ToString("000"), Anchor.Right, 576f, 33f, 17f, 21f, 17f);
            Sprite("face", Anchor.Right, 586f, 82f, 44f, 44f * Tall("face"));

            // Speed above the level bar.
            string mph = ((int)Math.Round(s.SpeedMph)).ToString();
            Digits("wd", mph, Anchor.Left, 62f - (mph.Length - 1) * 14f, 391f, 15f, 15f, 14f);
            Sprite("mph", Anchor.Left, 77f, 393f, 24f, 15f);
            Sprite("bar", Anchor.Left, 118f, 419f, 172f, 18f);
            Sprite("badge", Anchor.Left, 30f, 414f, 28f, 28f);
            Text(s.Level, Anchor.Left, 50f, 412f, 17f, 255, 255, 255);

            Minimap(s);
            Banner(s);
            DrawTally(s);
        }

        // hud.c hud_show_goal_score_info: CLEAR SCORE, TIME BONUS and STAGE SCORE (Deluxe's name for
        // FLOOR SCORE) in the 16 px font. Each label slides in from the left from frame 60 of the goal,
        // its number fades in from frame 120, the lines start 0, 20 and 60 frames apart, and all of it
        // fades out over the last 15 of the goal's 360 frames.
        const int TallyFrames = 360;

        void DrawTally(State s)
        {
            if (!s.Tally.Shown || s.TallyAge <= 0)
                return;
            int y = 128;
            TallyLine(s.TallyAge, 0, y, "CLEAR SCORE : ", " " + s.Tally.Clear.ToString().PadLeft(5), 96, 96, 255, 0);
            if (s.Tally.TimeBonus)
            {
                y += 20;
                TallyLine(s.TallyAge, 20, y, " TIME BONUS   ", "   X 2", 255, 255, 0, 0);
            }
            int t = s.TallyAge - 60;
            int flash = (int)Math.Max(Math.Sin(t * 0x280 * Math.PI / 32768.0) * 255.0, 0.0);
            TallyLine(s.TallyAge, 60, y + 24, "STAGE SCORE : ", s.Tally.Stage.ToString().PadLeft(6), 255, 128, 0, flash);
        }

        void TallyLine(int age, int delay, float y, string label, string value, int r, int g, int b, int flash)
        {
            int t = age - delay, counter = TallyFrames - age;
            float end = counter <= 15 ? Math.Max(0f, counter / 15f) : 1f;
            // func_8007F61C: the label.
            if (t < 60)
                return;
            float a = t < 90 ? (t - 60) / 30f : end;
            float x = 214f - 190f + (t < 90 ? (t - 90) * 5f : 0f), cy = y - 10f + 8f;
            // The game's add colour flashes it toward white; the font is white, so lift the tint instead.
            r += (255 - r) * flash / 255;
            g += (255 - g) * flash / 255;
            b += (255 - b) * flash / 255;
            x = Mono(label, x, cy, r, g, b, (int)(255 * a));
            // lbl_8007F754_inline2: the number carries on from where the label ended.
            if (t < 120)
                return;
            a = t < 150 ? (t - 120) / 30f : end;
            Mono(value, x, cy, r, g, b, (int)(255 * a));
        }

        // The 16 px ASCII font: every character takes a 16 px cell.
        float Mono(string text, float x, float cy, int r, int g, int b, int a)
        {
            const float Cell = 16f;
            foreach (char ch in text)
            {
                if (ch != ' ')
                {
                    string n = "f_" + Key(ch);
                    float w = Width(n) * (Cell / 24f);
                    if (w > 0f)
                    {
                        Sprite(n, Anchor.Left, x + Cell / 2f + 1.5f, cy + 1.5f, w, Cell, 0, 0, 0, a * 200 / 255);
                        Sprite(n, Anchor.Left, x + Cell / 2f, cy, w, Cell, r, g, b, a);
                    }
                }
                x += Cell;
            }
            return x;
        }

        void Banner(State s)
        {
            if (s.Banner == null || s.BannerLength <= 0)
                return;
            int age = s.BannerAge, left = s.BannerLength - s.BannerAge;
            float grow = Math.Min(1f, age / 15f), fade = Math.Min(1f, left / 15f);
            int alpha = (int)(255 * Math.Min(grow, fade));
            if (s.Banner == "GOAL!")
            {
                GoalBanner(age, left);
                return;
            }
            // Tints from the Deluxe sequence notes.
            (int r, int g, int b) = s.Banner == "READY" ? (255, 200, 0) : s.Banner == "GO!" ? (0, 128, 255) : (255, 140, 0);
            float h = 64f * (s.Banner == "GO!" ? 1.4f : 1f) * (0.5f + 0.5f * grow);
            BannerText(s.Banner, 320f, 210f, h, r, g, b, alpha);
        }

        // hud.c goal_sprite_draw: four copies fly in from the corners and meet over 30 frames as it fades
        // in, it flashes, and from frame 180 it shrinks to half and slides to the bottom right in 30 frames
        // (where the minimap was), staying there until it fades out over the last 15 frames.
        void GoalBanner(int t, int counter)
        {
            const float W = 380f, H = 88f, Y = 270f;
            float opacity = t < 30 ? t / 60f : counter <= 15 ? counter / 15f : 1f;
            int flash = t >= 30 && t < 45 ? (int)Math.Max(0.0, Math.Sin((t - 30) * 0x888 * Math.PI / 32768.0) * 255.0) : 0;
            // From the centre (centre anchored) to the corner (right anchored), in screen shares.
            float u = t >= 210 ? 1f : t >= 180 ? (t - 180) / 30f : 0f;
            float k = 1f - 0.5f * u;
            float sx = ScreenX(Anchor.Centre, 320f) + (ScreenX(Anchor.Right, 561f) - ScreenX(Anchor.Centre, 320f)) * u;
            float cy = Y + (420f - Y) * u;
            int a = (int)(255 * opacity);
            if (t < 30)
            {
                float spread = (30 - t) / 30f;
                foreach (var (dx, dy) in new[] { (-1, 1), (1, 1), (1, -1), (-1, -1) })
                    SpriteAt("goal", sx + spread * 216f * dx * Unit, cy - spread * 192f * dy, W, H, 255, 255, 255, a);
                return;
            }
            SpriteAt("goal", sx, cy, W * k, H * k, 255, 255, 255, a);
            // The add-colour flash: a whitened copy on top (the tint can't go above the texture's colour).
            if (flash > 0)
                SpriteAt("goal_white", sx, cy, W * k, H * k, 255, 255, 255, flash * a / 255);
        }

        // A sprite at a screen share across (sx) and a Deluxe px down.
        void SpriteAt(string texture, float sx, float cy, float w, float h, int r, int g, int b, int a)
        {
            Function.Call(Hash.DRAW_SPRITE, dict, Name(texture), sx, cy / ScreenH, w * Unit, h / ScreenH, 0f, r, g, b, a, false);
        }

        void Minimap(State s)
        {
            float mk = s.MapSize;
            if (mk <= 0.01f)
                return;
            double h = (s.CameraHeading + s.MapSpin) * Math.PI / 180.0;
            var fwd = new Vector2(-(float)Math.Sin(h), (float)Math.Cos(h));
            var right = new Vector2((float)Math.Cos(h), (float)Math.Sin(h));
            float scale = MapR * mk / MapMetres;  // Deluxe px a metre
            Vector2 ToMap(float x, float y)
            {
                var d = new Vector2(x - s.Ball.X, y - s.Ball.Y);
                return new Vector2(Vector2.Dot(d, right), -Vector2.Dot(d, fwd)) * scale;
            }
            float inner = (MapR - 4f) * mk;
            float metres = inner / scale;

            // The sea, then GTA's map: strips across the circle, square to the world's axes, each cut
            // where it crosses from one tile to the next and turned with the camera.
            Sprite("disc", Anchor.Right, MapX, MapY, inner * 2f, inner * 2f, 64, 96, 128, 255);
            if (TilesReady())
            {
                float heading = Turn * (s.CameraHeading + s.MapSpin);
                for (int pass = 0; pass < 2; pass++)
                {
                    float dy = 2f * metres / Strips;
                    for (int k = 0; k < Strips; k++)
                    {
                        float y0 = -metres + k * dy, y1 = y0 + dy, yc = (y0 + y1) / 2f;
                        float hw = (float)Math.Sqrt(Math.Max(0f, metres * metres - yc * yc));
                        if (hw < 0.5f)
                            continue;
                        Strip(pass == 0 ? seaTiles : tiles, pass == 0 ? seaIn : tileIn, s.Ball.X - hw, s.Ball.X + hw,
                            s.Ball.Y + y0, s.Ball.Y + y1, ToMap, scale, heading);
                    }
                }
            }

            // The road to the goal.
            // A point every few px is plenty at this zoom; every GPS point (one each 8 m) was hundreds of
            // sprites, and the game stops drawing script sprites past its limit, which took AiAi's face
            // and the ring with it.
            if (Route != null && Route.Count > 1)
            {
                Vector2 p = ToMap(Route[0].X, Route[0].Y);
                for (int i = 1; i < Route.Count; i++)
                {
                    Vector2 q = ToMap(Route[i].X, Route[i].Y);
                    if (i < Route.Count - 1 && (q - p).Length() < RouteStepPx)
                        continue;
                    Vector2 a = p, b = q;
                    if (Clip(ref a, ref b, inner))
                        Line(a, b, RouteWidth * mk, 240, 200, 80, 255);  // GTA's own GPS yellow (HUD_COLOUR_YELLOW)
                    p = q;
                }
            }
            if (s.HasGoal)
            {
                Vector2 g = ToMap(s.Goal.X, s.Goal.Y);
                bool off = g.Length() > inner - 4f * mk;
                if (off)
                    g = g / g.Length() * (inner - 4f * mk);
                // The blue of Deluxe's goal gate, ringed in white so it stands off the dark map and the
                // yellow route.
                Sprite("disc", Anchor.Right, MapX + g.X, MapY + g.Y, 14f * mk, 14f * mk, 255, 255, 255, off ? 160 : 255);
                Sprite("disc", Anchor.Right, MapX + g.X, MapY + g.Y, 10f * mk, 10f * mk, 30, 150, 255, off ? 160 : 255);
            }
            // AiAi in the middle.
            Sprite("face", Anchor.Right, MapX, MapY, 24f * mk, 24f * mk * Tall("face"));
            // The gold ring: the top half arc, and the same turned over for the bottom. It's round, so it
            // only grows and shrinks with the map (a turned non-square sprite skews in GTA).
            float r = MapR * mk;
            Sprite("arc", Anchor.Right, MapX, MapY - r * 0.5f, r * 2f, r);
            Sprite("arc", Anchor.Right, MapX, MapY + r * 0.5f, r * 2f, r, 255, 255, 255, 255, 180f);
        }

        // One strip of map, [x0, x1] x [y0, y1] in the world, cut at tile edges.
        void Strip(IntPtr[,] dicts, bool[,] have, float x0, float x1, float y0, float y1, Func<float, float, Vector2> toMap, float scale, float heading)
        {
            float x = x0;
            while (x < x1)
            {
                int col = (int)Math.Floor((x - TileLeft) / TileMetres);
                float xe = Math.Min(x1, TileLeft + (col + 1) * TileMetres);
                float y = y0;
                while (y < y1)
                {
                    // Rows count down from the top: the tile just above y, and where its top cuts the strip.
                    int row = (int)Math.Floor((TileTop - y - 0.01f) / TileMetres);
                    float ye = Math.Min(y1, TileTop - row * TileMetres);
                    float yBottom = y;
                    if (row >= 0 && row < TileRows && col >= 0 && col < TileCols && have[row, col])
                    {
                        float u0 = (x - TileLeft) / TileMetres - col, u1 = (xe - TileLeft) / TileMetres - col;
                        float v0 = (TileTop - ye) / TileMetres - row, v1 = (TileTop - yBottom) / TileMetres - row;
                        Vector2 c = toMap((x + xe) / 2f, (yBottom + ye) / 2f);
                        SpriteUV(dicts[row, col], dicts[row, col], Anchor.Right, MapX + c.X, MapY + c.Y,
                            (xe - x) * scale + 0.6f, (ye - yBottom) * scale + 0.6f, u0, v0, u1, v1, heading);
                    }
                    y = ye > y ? ye : y1;
                }
                x = xe > x ? xe : x1;
            }
        }

        // Are the radar tiles in? Logged once, so a wrong guess about their names shows in the log.
        bool TilesReady()
        {
            bool any = false;
            for (int r = 0; r < TileRows; r++)
                for (int c = 0; c < TileCols; c++)
                {
                    if (!tileIn[r, c])
                        tileIn[r, c] = Function.Call<bool>(Hash.HAS_STREAMED_TEXTURE_DICT_LOADED, tiles[r, c]);
                    if (!seaIn[r, c])
                        seaIn[r, c] = Function.Call<bool>(Hash.HAS_STREAMED_TEXTURE_DICT_LOADED, seaTiles[r, c]);
                    any |= tileIn[r, c];
                }
            if (!tilesLogged && (++tileWait == 600 || AllIn()))
            {
                tilesLogged = true;
                var parts = new List<string>();
                for (int r = 0; r < TileRows; r++)
                    for (int c = 0; c < TileCols; c++)
                    {
                        Vector3 res = tileIn[r, c] ? Function.Call<Vector3>(Hash.GET_TEXTURE_RESOLUTION, tiles[r, c], tiles[r, c]) : Vector3.Zero;
                        parts.Add($"{r}_{c}: {(tileIn[r, c] ? $"{res.X}x{res.Y}" : "missing")}{(seaIn[r, c] ? " +sea" : "")}");
                    }
                Log("minimap tiles: " + string.Join(", ", parts));
            }
            return any;
        }

        bool AllIn()
        {
            foreach (bool b in tileIn)
                if (!b)
                    return false;
            return true;
        }

        // A line on the minimap, between two points relative to its centre.
        void Line(Vector2 p, Vector2 q, float width, int r, int g, int b, int a)
        {
            Vector2 d = q - p;
            float len = d.Length();
            if (len < 0.5f)
                return;
            float ang = (float)(Math.Atan2(d.Y, d.X) * 180.0 / Math.PI);
            Vector2 m = (p + q) / 2f;
            // Square caps half the width past each end, so neighbouring pieces overlap at the bends.
            Sprite("white", Anchor.Right, MapX + m.X, MapY + m.Y, len + width, width, r, g, b, a, ang * Turn);
        }

        // A sprite standing in the world, facing the camera: size in metres across.
        public void Billboard(string texture, Vector3 at, float size, float heading, int r, int g, int b, int a)
        {
            if (!loaded || a <= 0)
                return;
            Vector3 f = look - eye;
            f.Normalize();
            float dist = Vector3.Dot(at - eye, f);
            if (dist < 0.3f || !Screen(at, out float sx, out float sy))
                return;
            float hFrac = size / (2f * dist * (float)Math.Tan(fov * Math.PI / 360.0));
            Function.Call(Hash.DRAW_SPRITE, dict, Name(texture), sx, sy, hFrac / aspect, hFrac, heading, r, g, b, Math.Min(255, a), false);
        }

        // A spark: a bright line from head back to tail, width in metres.
        public void Streak(Vector3 head, Vector3 tail, float width, int r, int g, int b, int a)
        {
            if (!loaded || a <= 0)
                return;
            Vector3 f = look - eye;
            f.Normalize();
            float dist = Vector3.Dot(head - eye, f);
            if (dist < 0.3f || !Screen(head, out float hx, out float hy) || !Screen(tail, out float tx, out float ty))
                return;
            float px = 1f / (2f * dist * (float)Math.Tan(fov * Math.PI / 360.0));  // screen heights a metre
            // In screen heights, so x and y are the same size.
            float dx = (hx - tx) * aspect, dy = hy - ty;
            float len = (float)Math.Sqrt(dx * dx + dy * dy);
            float w = Math.Max(0.002f, width * px);
            if (len < w)
                len = w;
            float ang = (float)(Math.Atan2(dx, -dy) * 180.0 / Math.PI);
            Function.Call(Hash.DRAW_SPRITE, dict, Name("fx_spark"), (hx + tx) / 2f, (hy + ty) / 2f, w / aspect, len, ang * Turn, r, g, b, Math.Min(255, a), false);
        }

        static bool Screen(Vector3 p, out float x, out float y)
        {
            bool on = Function.Call<bool>(Hash.GET_SCREEN_COORD_FROM_WORLD_COORD, p.X, p.Y, p.Z, OutA, OutB);
            x = ReadFloat(OutA);
            y = ReadFloat(OutB);
            return on;
        }

        // A texture's height over its width, so a sprite drawn from a width keeps its shape.
        float Tall(string texture)
        {
            Vector3 res = Function.Call<Vector3>(Hash.GET_TEXTURE_RESOLUTION, dict, Name(texture));
            return res.X > 0f ? res.Y / res.X : 1f;
        }

        // Keep the part of a segment inside a circle about the origin.
        static bool Clip(ref Vector2 p, ref Vector2 q, float r)
        {
            Vector2 d = q - p;
            float a = Vector2.Dot(d, d), b = 2f * Vector2.Dot(p, d), c = Vector2.Dot(p, p) - r * r;
            if (a < 1e-6f)
                return c <= 0f;
            float disc = b * b - 4f * a * c;
            if (disc <= 0f)
                return false;
            float sq = (float)Math.Sqrt(disc);
            float t0 = Math.Max(0f, (-b - sq) / (2f * a)), t1 = Math.Min(1f, (-b + sq) / (2f * a));
            if (t0 >= t1)
                return false;
            Vector2 p0 = p + d * t0, q0 = p + d * t1;
            p = p0;
            q = q0;
            return true;
        }

        void Digits(string prefix, string text, Anchor anchor, float x0, float cy, float w, float h, float step)
        {
            for (int i = 0; i < text.Length; i++)
                Sprite(prefix + text[i], anchor, x0 + i * step, cy, w, h);
        }

        // The small font for the level name, left-aligned at x, with a dark shadow.
        void Text(string text, Anchor anchor, float x, float cy, float size, int r, int g, int b)
        {
            if (string.IsNullOrEmpty(text))
                return;
            float k = size / 24f;
            foreach (char ch in text)
            {
                if (ch == ' ')
                {
                    x += 8f * k;
                    continue;
                }
                string n = "f_" + Key(ch);
                float w = Width(n) * k;
                if (w <= 0f)
                    continue;
                Sprite(n, anchor, x + w / 2f + 1.5f, cy + 1.5f, w, size, 0, 0, 0, 200);
                Sprite(n, anchor, x + w / 2f, cy, w, size, r, g, b, 255);
                x += w - 1f * k;
            }
        }

        void BannerText(string text, float cx, float cy, float size, int r, int g, int b, int a)
        {
            float k = size / 64f, total = 0f;
            foreach (char ch in text)
                total += ch == ' ' ? 30f * k : Width("b_" + Key(ch)) * k - 4f * k;
            float x = cx - total / 2f;
            foreach (char ch in text)
            {
                if (ch == ' ')
                {
                    x += 30f * k;
                    continue;
                }
                string n = "b_" + Key(ch);
                float w = Width(n) * k;
                Sprite(n, Anchor.Centre, x + w / 2f, cy, w, size, r, g, b, a);
                x += w - 4f * k;
            }
        }

        // Texture names are case-blind, so gta/hud.py gives capitals a "u" in front.
        static string Key(char ch)
        {
            if (char.IsUpper(ch))
                return "u" + char.ToLowerInvariant(ch);
            if (ch == '!')
                return "bang";
            if (ch == '-')
                return "dash";
            if (ch == ':')
                return "colon";
            return ch.ToString();
        }

        float Width(string texture)
        {
            if (!widths.TryGetValue(texture, out float w))
            {
                Vector3 res = Function.Call<Vector3>(Hash.GET_TEXTURE_RESOLUTION, dict, Name(texture));
                w = res.X;
                widths[texture] = w;
            }
            return w;
        }

        void Box(Anchor anchor, float x, float y, float w, float h) =>
            Sprite("white", anchor, x + w / 2f, y + h / 2f, w, h, 0, 0, 0, 120);

        // One sprite, its centre and size in Deluxe px.
        void Sprite(string texture, Anchor anchor, float cx, float cy, float w, float h, int r = 255, int g = 255, int b = 255, int a = 255, float heading = 0f)
        {
            float x = ScreenX(anchor, cx);
            // DRAW_SPRITE turns a sprite in screen shares, which skews anything turned off the axes on a
            // wide screen (route pieces became slanted bits that didn't meet). The ARX draw (the one the
            // map tiles use) turns it square.
            if (heading % 180f != 0f)
                Function.Call(Hash.DRAW_SPRITE_ARX_WITH_UV, dict, Name(texture), x, cy / ScreenH, w * Unit, h / ScreenH,
                    0f, 0f, 1f, 1f, heading, r, g, b, a, 0);
            else
                Function.Call(Hash.DRAW_SPRITE, dict, Name(texture), x, cy / ScreenH, w * Unit, h / ScreenH, heading, r, g, b, a, false);
        }

        // Part of a texture from another dictionary: u, v from 0 to 1 across it.
        void SpriteUV(IntPtr d, IntPtr texture, Anchor anchor, float cx, float cy, float w, float h, float u0, float v0, float u1, float v1, float heading)
        {
            Function.Call(Hash.DRAW_SPRITE_ARX_WITH_UV, d, texture, ScreenX(anchor, cx), cy / ScreenH, w * Unit, h / ScreenH,
                u0, v0, u1, v1, heading, 255, 255, 255, 255, 0);
        }

        // A Deluxe px across, as a share of GTA's screen width (Deluxe px are square here).
        float Unit => 1f / ScreenH / aspect;

        float ScreenX(Anchor anchor, float cx) =>
            anchor == Anchor.Left ? cx * Unit : anchor == Anchor.Right ? 1f - (ScreenW - cx) * Unit : 0.5f + (cx - ScreenW / 2f) * Unit;

        IntPtr Name(string texture)
        {
            if (!names.TryGetValue(texture, out IntPtr p))
            {
                p = Utf8(texture);
                names[texture] = p;
            }
            return p;
        }
    }
}
