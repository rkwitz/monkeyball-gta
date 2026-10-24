// Ball mode: the player becomes the ball. It starts once story mode has loaded; F5 rolls a new
// level, Ctrl+F5 leaves ball mode (and F5 brings it back). F6 sets the time to day, F7 to night, and
// F8/F9 lower/raise the max tilt.
//
// The ball runs Monkey Ball's own physics, tilt and follow camera (SmbPhysics.cs) at the game's 60
// steps a second, in Monkey Ball units scaled up so the ball is 3 m across. GTA's world comes in as
// collision planes found by rays fired out from the ball's centre each step.
// The ball weighs 100 t. What it hits takes an impulse as in a collision between two masses: a car
// flies off and costs the ball a couple of percent of its speed, a bus nearer a fifth. Props a car
// would knock over (signs, lamp posts, bins, fences) are torn loose and thrown the same way.
// A swept-sphere test along each frame's path catches thin poles (and small trees and bushes)
// that fall between the rays, and nearby cars are also tested as their model boxes.
// Each level starts and ends on a random road (Level.cs): roll through the goal before the clock runs
// out for a new level; time over or falling in the water tries the same level again.
// The mbgta_ball prop is only the picture: frozen, no collision, moved and spun by the script.
// The player ped rides along invisibly (moved with the ball and turned the way it rolls, so GTA's
// map arrow points along the road). AiAi rides inside it (Monkey.cs).
// A level opens with Deluxe's READY: the camera flies in to behind the start and the ball drops in
// from above. Through the goal the Deluxe goal camera takes over (camera.c camera_func_goal_main).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using GTA;
using GTA.Math;
using GTA.Native;
using static MonkeyBallGta.Util;

namespace MonkeyBallGta
{
    public class BallMode : Script
    {
        const string Version = "v22";  // shown on F5, so a stale install is obvious
        const string BallModel = "mbgta_ball";
        const float Scale = 3.5f;             // metres per Monkey Ball unit; matches build_gta.py SCALE
        const float Radius = SmbBall.Radius * Scale;
        const float Step = 1f / 60f;          // Monkey Ball runs one physics step per 1/60 s
        const int MaxStepsPerFrame = 5;       // after a long hitch, slow down rather than jump
        const float SmashSpeed = 2f;          // m/s; slower than this the ball rests against props
        const float FlingBoost = 1.3f;        // props are shoved at this times the ball's speed
        const float BallMass = 100000f;       // kg
        const float HitRestitution = 0.3f;    // how bouncy a hit is: 0 sticks, 1 bounces right off
        const int HitCooldown = 30;           // frames before the same thing can be hit again
        // The clock: the road distance at this average speed (m/s; the ball tops out near 68 on the
        // flat), plus a few seconds, rounded up to 5. Until the GPS route is in, the straight line
        // times RoadFactor stands in for the road.
        const float ChallengeSpeed = 36f, SpareSeconds = 5f, RoadFactor = 1.3f;
        // Beat lengths in frames, from the Deluxe sequence: READY then GO, and how long each ending runs.
        const int ReadyFrames = 120, GoFrames = 60, GoalFrames = 360, FallOutFrames = 270, TimeOverFrames = 180;
        const int GoalFlyUp = 120;            // the frame of the goal where the monkey jumps and the ball lifts off
        // READY: the camera flies in over the first FlyInFrames (camera_func_ready_main), the ball
        // falls in for the last DropFrames from as high as it falls in that time.
        const int FlyInFrames = 90, DropFrames = 24;
        const float FlyInRadius = 14f;        // units: the fly-in starts this far out (as the stage's 0.8 x radius)
        const float HitSparks = -0.11f;       // a hit harder than this throws sparks (ball.c update_ball_ape_transform)
        const int VoiceGap = 30;              // frames between two of the monkey's bump voices
        const float VoiceVolume = 0.6f;       // the monkey's voice against the other sounds
        const float CamFov = 60f;
        const int InputMoveLR = 30, InputMoveUD = 31, InputPause = 199, InputPauseAlt = 200;

        // Directions the collision rays go out in: the 6 axes, 12 edge diagonals and 8 corners.
        static readonly Vector3[] Probes = MakeProbes();

        enum State { Off, Loading, Placing, On }
        State state = State.Off;
        // Placing a level: finding the route, then waiting for the map around the start to load.
        bool retry, routeFound;
        int placeFrames;
        readonly RouteFinder route = new RouteFinder();
        readonly Goal goal = new Goal(Scale);
        int timeLeft;                          // physics steps
        float levelSeconds;                    // this level's clock
        Hud.Tally tally;                       // the goal's score lines (effect.c u_give_points)
        // The minimap's size (0..320) and spin (s16 angle units), as minimap.c ev_minimap_main runs them:
        // it spins up out of nothing at READY and spins away to nothing when the level ends.
        int mapSize, mapSpin, mapSpinSpeed, mapMode;
        bool timeFromRoute;                    // set by the road distance yet
        // Where the level is: READY (ball held, timer stopped), play, or one of the endings.
        enum Beat { Ready, Play, Goal, FallOut, TimeOver }
        Beat beat;
        int beatFrames, pending;               // pending: 1 = next level, 2 = this level again
        string banner;
        int bannerFrames, bannerLength;
        readonly Hud hud = new Hud();
        float hardestHit;                     // the hardest thing the ball hit by this step, as a speed into it (u/f, negative)
        int lastVoice;
        int goals;
        readonly Audio audio = new Audio();
        readonly Effects effects = new Effects(Scale);
        readonly Bananas bananas = new Bananas(Scale);
        readonly GpsRoute gps = new GpsRoute();
        int score, bananaCount, routeAge, levelScore, levelBananas;  // level*: what this try has collected, taken back on a retry
        Vector3 hardNormal;                   // the surface of hardestHit (Monkey Ball space)
        // Our own camera for READY and the goal (Monkey Ball space): eye, its velocity, and the point it looks at.
        Vector3 camEye, camEyeVel, camLook, stepEye, prevStepEye;
        Vector3 landing;                      // where the ball lands at the start (Monkey Ball space)
        float goalFloor;                      // the ground under the goal, for the sparkles (units)
        bool audioOn;
        readonly Random rng = new Random();
        readonly Dictionary<int, int> lastHit = new Dictionary<int, int>();
        readonly Dictionary<int, float> masses = new Dictionary<int, float>();
        // Car model -> its box, shrunk a little.
        readonly Dictionary<int, (Vector3 lo, Vector3 hi)> boxes = new Dictionary<int, (Vector3, Vector3)>();
        readonly List<int> nearbyCars = new List<int>();
        static readonly IntPtr carBuf = Marshal.AllocHGlobal(8 * 25);
        readonly Dictionary<int, (Vector3 vel, int frame)> shoved = new Dictionary<int, (Vector3, int)>();
        int ball, cam, loadTicks, sweep, readyTicks;
        bool autoStarted;
        Vector3 sweepFrom;
        readonly SmbBall smb = new SmbBall();
        readonly SmbCamera smbCam = new SmbCamera();
        readonly Monkey monkey = new Monkey();
        Quaternion spin = Quaternion.Identity;
        float stepClock;
        // Props we tried to tear loose, and the ones that turned out to be bolted down (map parts).
        readonly Dictionary<int, Smash> smashed = new Dictionary<int, Smash>();
        // A prop the ball went into: where it stood, when, and the face the ball met (GTA space).
        struct Smash { public Vector3 Pos, Point, Normal; public int Frame; public bool Settled; }
        readonly HashSet<int> solid = new HashSet<int>();
        int frame;

        public BallMode()
        {
            LoadTilt();
            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.F5) Guard(() => F5(e.Control));
                else if (e.KeyCode == Keys.F6) Guard(() => SetClock(12));
                else if (e.KeyCode == Keys.F7) Guard(() => SetClock(0));
                else if (e.KeyCode == Keys.F8) Guard(() => ChangeTilt(-1f));
                else if (e.KeyCode == Keys.F9) Guard(() => ChangeTilt(1f));
            };
            Tick += (s, e) => Guard(OnTick);
            Aborted += (s, e) => Guard(() => { Stop(); audio.Dispose(); });
        }

        static Vector3[] MakeProbes()
        {
            var list = new List<Vector3>();
            for (int x = -1; x <= 1; x++)
                for (int y = -1; y <= 1; y++)
                    for (int z = -1; z <= 1; z++)
                        if (x != 0 || y != 0 || z != 0)
                            list.Add(new Vector3(x, y, z).Normalized);
            return list.ToArray();
        }

        // Every prop a level uses besides the ball and AiAi (the game's own among them only if installed).
        static IEnumerable<string> Props => Goal.Models.Concat(Bananas.Models).Concat(new[] { Effects.StarModel })
            .Where(m => Function.Call<bool>(Hash.IS_MODEL_IN_CDIMAGE, Joaat(m)));

        // Monkey Ball space (y up) <-> GTA (z up), the same mapping the converter uses for the models.
        static Vector3 ToGta(Vector3 v) => new Vector3(v.X, -v.Z, v.Y);
        static Vector3 ToSmb(Vector3 v) => new Vector3(v.X, v.Z, -v.Y);



        void Toggle()
        {
            if (state == State.Off)
            {
                uint hash = Joaat(BallModel);
                if (!Function.Call<bool>(Hash.IS_MODEL_IN_CDIMAGE, hash))
                {
                    Notify("~r~mbgta: the ball model isn't installed.~s~ Is dlcpacks:/mbgta/ in dlclist.xml?");
                    return;
                }
                Function.Call(Hash.REQUEST_MODEL, hash);
                foreach (string m in Props)
                    Function.Call(Hash.REQUEST_MODEL, Joaat(m));
                monkey.Request();
                hud.Request();
                loadTicks = 0;
                state = State.Loading;
            }
            else
            {
                Stop();
            }
        }

        // F5 rolls a new level (or starts ball mode if it's off); Ctrl+F5 leaves ball mode.
        void F5(bool control)
        {
            if (control || state == State.Off)
                Toggle();
            else if (state == State.On)
                NewLevel(false);
        }

        // F8/F9 lower and raise the stage's max tilt a degree at a time, kept in scripts\MonkeyBallGta\tilt.txt.
        static readonly string TiltFile = Path.Combine("scripts", "MonkeyBallGta", "tilt.txt");
        const float MinTiltSetting = 10f, MaxTiltSetting = 45f;

        static void LoadTilt()
        {
            try
            {
                if (File.Exists(TiltFile) && float.TryParse(File.ReadAllText(TiltFile).Trim(), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out float t))
                    SmbBall.MaxTilt = Math.Max(MinTiltSetting, Math.Min(MaxTiltSetting, t));
            }
            catch (Exception) { }
        }

        static void ChangeTilt(float by)
        {
            SmbBall.MaxTilt = Math.Max(MinTiltSetting, Math.Min(MaxTiltSetting, SmbBall.MaxTilt + by));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(TiltFile));
                File.WriteAllText(TiltFile, SmbBall.MaxTilt.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception) { }
            Notify($"Max tilt: {SmbBall.MaxTilt:0}\u00b0 (Super Monkey Ball: 23\u00b0, default {SmbBall.DefaultMaxTilt:0}\u00b0)");
        }

        // F6 sets the clock to midday, F7 to midnight.
        static void SetClock(int hour) => Function.Call(Hash.SET_CLOCK_TIME, hour, 0, 0);

        // Ball mode starts by itself once story mode has loaded: the loading screen gone, the player
        // in control and the screen faded in, held for a couple of seconds.
        void AutoStart()
        {
            int player = Function.Call<int>(Hash.PLAYER_ID);
            bool ready = !Function.Call<bool>(Hash.GET_IS_LOADING_SCREEN_ACTIVE)
                && Function.Call<bool>(Hash.IS_PLAYER_PLAYING, player)
                && !Function.Call<bool>(Hash.IS_PLAYER_SWITCH_IN_PROGRESS)
                && Function.Call<bool>(Hash.IS_SCREEN_FADED_IN)
                && !Function.Call<bool>(Hash.IS_ENTITY_DEAD, Function.Call<int>(Hash.PLAYER_PED_ID), false);
            readyTicks = ready ? readyTicks + 1 : 0;
            if (readyTicks < 120)
                return;
            autoStarted = true;
            Toggle();
        }

        void OnTick()
        {
            if (!autoStarted && state == State.Off)
                AutoStart();
            else if (state == State.Loading)
                TryStart();
            else if (state == State.Placing)
                Place();
            else if (state == State.On)
                Update();
        }

        void TryStart()
        {
            uint hash = Joaat(BallModel);
            if (!Function.Call<bool>(Hash.HAS_MODEL_LOADED, hash) || !Props.All(m => Function.Call<bool>(Hash.HAS_MODEL_LOADED, Joaat(m))) || !monkey.Ready)
            {
                if (++loadTicks > 300)
                {
                    Notify("~r~mbgta: the ball model didn't load");
                    state = State.Off;
                }
                return;
            }
            int ped = Function.Call<int>(Hash.PLAYER_PED_ID);
            Vector3 start = Function.Call<Vector3>(Hash.GET_ENTITY_COORDS, ped, true) + new Vector3(0f, 0f, Radius);
            float heading = Function.Call<float>(Hash.GET_ENTITY_HEADING, ped);

            ball = Function.Call<int>(Hash.CREATE_OBJECT, hash, start.X, start.Y, start.Z, false, false, false);
            Function.Call(Hash.SET_MODEL_AS_NO_LONGER_NEEDED, hash);
            if (ball == 0)
            {
                Log("ball mode: CREATE_OBJECT returned 0");
                Notify("~r~mbgta: couldn't create the ball");
                state = State.Off;
                return;
            }
            Function.Call(Hash.FREEZE_ENTITY_POSITION, ball, true);
            Function.Call(Hash.SET_ENTITY_COLLISION, ball, false, false);

            // The ped rides inside, unseen and untouchable, so the game keeps streaming the world around the ball.
            Function.Call(Hash.CLEAR_PED_TASKS_IMMEDIATELY, ped);
            Function.Call(Hash.SET_ENTITY_VISIBLE, ped, false, false);
            Function.Call(Hash.SET_ENTITY_INVINCIBLE, ped, true);
            Function.Call(Hash.SET_ENTITY_COLLISION, ped, false, false);
            Function.Call(Hash.FREEZE_ENTITY_POSITION, ped, true);

            int player = Function.Call<int>(Hash.PLAYER_ID);
            Function.Call(Hash.CLEAR_PLAYER_WANTED_LEVEL, player);
            Function.Call(Hash.SET_MAX_WANTED_LEVEL, 0);

            IntPtr camName = Utf8("DEFAULT_SCRIPTED_CAMERA");
            try { cam = Function.Call<int>(Hash.CREATE_CAM, camName, false); }
            finally { Marshal.FreeHGlobal(camName); }
            Function.Call(Hash.SET_CAM_FOV, cam, CamFov);
            Function.Call(Hash.SET_CAM_ACTIVE, cam, true);
            Function.Call(Hash.RENDER_SCRIPT_CAMS, true, false, 0, true, false, 0);

            smb.Pos = smb.PrevPos = ToSmb(start) / Scale;
            smb.Vel = Vector3.Zero;
            smb.XRot = smb.ZRot = 0;
            smb.Frames = 0;
            // GTA heading h faces (-sin h, cos h); in Monkey Ball space the camera sits behind along +z
            // of its yaw, so a camera yaw of h (in 16-bit angle units) looks the same way as the ped.
            smbCam.Reset(smb, SmbMath.Degrees(heading));
            monkey.Spawn(smb.Pos, smbCam.RotY, Scale);
            spin = Quaternion.Identity;
            stepClock = 0f;
            sweep = 0;
            smashed.Clear();
            solid.Clear();
            if (!audioOn)
                audioOn = audio.Start();
            effects.Spawn();
            goals = score = bananaCount = 0;
            hud.Reset();
            autoStarted = true;  // once on, F5 decides from here
            Log($"ball mode on ({Version}): ball {ball}, cam {cam}");
            Notify($"mbgta ball mode {Version}");
            NewLevel(false);
        }

        // Fade out and set up a level: a new random route, or the same one again.
        void NewLevel(bool again)
        {
            retry = again;
            routeFound = again;
            placeFrames = 0;
            if (!again)
            {
                goal.Clear();
                bananas.Clear();
                gps.Clear();
                hud.Route = null;
                route.Begin();
            }
            // A retry starts back at the start: the route there is read afresh.
            gps.Clear();
            hud.Route = null;
            effects.Clear();
            Function.Call(Hash.DO_SCREEN_FADE_OUT, 400);
            state = State.Placing;
        }

        void Place()
        {
            Function.Call(Hash.DISPLAY_RADAR, false);
            Function.Call(Hash.DISABLE_ALL_CONTROL_ACTIONS, 0);
            Function.Call(Hash.ENABLE_CONTROL_ACTION, 0, InputPause, true);
            Function.Call(Hash.ENABLE_CONTROL_ACTION, 0, InputPauseAlt, true);
            if (!routeFound)
            {
                if (!route.Tick())
                {
                    if (route.Tries > 400)
                    {
                        Log("level: no route found");
                        Notify("~r~mbgta: couldn't find a road route");
                        Stop();
                    }
                    return;
                }
                routeFound = true;
                Log($"level: start {route.Start} goal {route.Goal} ({(route.Goal - route.Start).Length():F0} m, goal traffic {route.GoalDensity}, {route.Tries} tries)");
            }
            // Park the ball over the start so the map streams in around it, then drop it onto the road.
            Vector3 start = route.Start + new Vector3(0f, 0f, Radius + 1f);
            if (placeFrames++ == 0)
            {
                Function.Call(Hash.SET_ENTITY_COORDS_NO_OFFSET, ball, start.X, start.Y, start.Z, false, false, false);
                RidePed(start);
                smb.Pos = smb.PrevPos = ToSmb(start) / Scale;
                monkey.Draw(smb, Scale);
                UpdateCamera();
            }
            Function.Call(Hash.REQUEST_COLLISION_AT_COORD, start.X, start.Y, start.Z);
            bool ground = Raycast.Cast(start + new Vector3(0f, 0f, 5f), start - new Vector3(0f, 0f, 15f), ball, out RayHit hit, 1);
            if (!ground && placeFrames < 300)
                return;
            if (!ground)
            {
                Log("level: the ground at the start never loaded; trying another route");
                NewLevel(false);
                return;
            }
            if (!Function.Call<bool>(Hash.IS_SCREEN_FADED_OUT) && placeFrames < 60)
                return;

            if (!retry && !goal.Spawn(route.Goal, route.GoalHeading, route.Start))
                Log("level: CREATE_OBJECT for the goal returned 0");
            if (retry)
                goal.Reroute();
            // Face along the road, whichever way is nearer the goal.
            float heading = route.StartHeading;
            double h = heading * Math.PI / 180.0;
            Vector3 toGoal = route.Goal - route.Start;
            if (-(float)Math.Sin(h) * toGoal.X + (float)Math.Cos(h) * toGoal.Y < 0f)
                heading += 180f;

            Vector3 pos = hit.Point + new Vector3(0f, 0f, Radius + 0.05f);
            landing = ToSmb(pos) / Scale;
            // The camera settles behind where the ball will land...
            smb.Pos = smb.PrevPos = landing;
            smb.Vel = Vector3.Zero;
            smb.XRot = smb.ZRot = 0;
            smb.Frames = 0;
            smbCam.Reset(smb, SmbMath.Degrees(heading));
            // ...and the ball starts as high as it falls in DropFrames, out of sight until then.
            smb.Pos = smb.PrevPos = landing + new Vector3(0f, 0.5f * SmbBall.Gravity * DropFrames * DropFrames, 0f);
            Vector3 high = ToGta(smb.Pos) * Scale;
            Function.Call(Hash.SET_ENTITY_COORDS_NO_OFFSET, ball, high.X, high.Y, high.Z, false, false, false);
            Function.Call(Hash.SET_ENTITY_VISIBLE, ball, false, false);
            RidePed(pos);
            monkey.Reset(smbCam.RotY);
            monkey.Draw(smb, Scale);
            monkey.Visible(false);
            if (retry)
            {
                // A new try: the bananas come back, and what the last try collected is taken back.
                bananas.Respawn();
                score -= levelScore;
                bananaCount -= levelBananas;
            }
            levelScore = levelBananas = 0;
            tally = default;
            SetBeat(Beat.Ready);
            FlyIn(0);
            UpdateCamera();
            stepClock = 0f;
            if (!retry)
            {
                levelSeconds = TimeFor((route.Goal - route.Start).Length() * RoadFactor);
                timeFromRoute = false;
            }
            timeLeft = (int)(levelSeconds * 60f);
            smb.GravityScale = 1f;
            SetBeat(Beat.Ready);
            pending = 0;
            bannerFrames = 0;
            hardestHit = 0f;
            lastHit.Clear();
            shoved.Clear();
            Function.Call(Hash.DO_SCREEN_FADE_IN, 400);
            state = State.On;
        }

        void Update()
        {
            if (!Function.Call<bool>(Hash.DOES_ENTITY_EXIST, ball))
            {
                Stop();
                return;
            }
            frame++;

            // Read the stick (WASD maps to the same controls) while the game's own use of it is switched off.
            Function.Call(Hash.DISABLE_ALL_CONTROL_ACTIONS, 0);
            Function.Call(Hash.ENABLE_CONTROL_ACTION, 0, InputPause, true);
            Function.Call(Hash.ENABLE_CONTROL_ACTION, 0, InputPauseAlt, true);
            float stickX = Function.Call<float>(Hash.GET_DISABLED_CONTROL_NORMAL, 0, InputMoveLR);
            float stickY = Function.Call<float>(Hash.GET_DISABLED_CONTROL_NORMAL, 0, InputMoveUD);

            Vector3 before = ToGta(smb.Pos) * Scale;
            stepClock += Function.Call<float>(Hash.GET_FRAME_TIME);
            int steps = 0;
            monkey.Look(smb, Scale, ball);
            FindCars();
            while (stepClock >= Step && steps < MaxStepsPerFrame && pending == 0)
            {
                stepClock -= Step;
                steps++;
                StepBeat();
                bool play = beat == Beat.Play;
                bool dropping = beat == Beat.Ready && beatFrames > ReadyFrames - DropFrames;
                if ((beat != Beat.Ready || dropping) && beat != Beat.TimeOver)
                {
                    if (beat == Beat.Goal)
                        smb.Vel *= 0.95f;  // through the goal the ball coasts to a stop in the air
                    smb.Tilt(play ? stickX : 0f, play ? stickY : 0f, smbCam.RotY);
                    hardestHit = 0f;
                    smb.Move(smbCam.RotY);
                    Collide();
                    float hardest = hardestHit;
                    Vector3 normal = hardNormal;
                    if (smb.Hardest < hardest)
                    {
                        hardest = smb.Hardest;
                        normal = smb.HardestNormal;
                    }
                    Bumps(hardest);
                    if (hardest < HitSparks)
                        effects.Hit(smb.Pos, smb.Vel, normal, hardest);
                }
                monkey.Step(smb);
                if (beat == Beat.Ready)
                    FlyIn(beatFrames);
                else if (beat == Beat.Goal)
                    GoalCamera();
                else
                {
                    smbCam.Step(smb);
                    prevStepEye = stepEye;
                    smbCam.Pose(smb, out stepEye, out _, out _);
                }
                if (beat == Beat.Goal)
                    effects.Sparkle(smb.Pos, goalFloor);
                effects.Step();
                if (beat == Beat.Play)
                {
                    var (got, points) = bananas.Step(ToGta(smb.Pos) * Scale, Radius, audio);
                    bananaCount += got;
                    levelBananas += got;
                    score += points;
                    levelScore += points;
                    CheckLevel();
                }
            }
            if (steps == MaxStepsPerFrame)
                stepClock = 0f;

            Vector3 pos = ToGta(smb.Pos) * Scale;
            CheckSweep(before, pos);
            Roll(before, pos);
            Function.Call(Hash.SET_ENTITY_COORDS_NO_OFFSET, ball, pos.X, pos.Y, pos.Z, false, false, false);
            Function.Call(Hash.SET_ENTITY_QUATERNION, ball, spin.X, spin.Y, spin.Z, spin.W);
            RidePed(pos);
            monkey.Draw(smb, Scale);
            UpdateCamera();
            goal.Update(timeLeft, pos, Radius, Function.Call<float>(Hash.GET_FRAME_TIME));
            UpdateRoute(pos);
            bananas.Draw();
            effects.Draw(hud);
            DrawHud(pos);
            audio.Tick(Function.Call<bool>(Hash.IS_PAUSE_MENU_ACTIVE));
            float roll = new Vector2(smb.Vel.X, smb.Vel.Z).Length();
            audio.Roll(smb.Touched && beat != Beat.TimeOver ? Math.Min(1f, roll / 0.4f) * 0.6f : 0f);

            if (pending != 0)
                NewLevel(pending == 2);
        }

        void SetBeat(Beat b)
        {
            beat = b;
            beatFrames = 0;
            if (b == Beat.Ready)
            {
                // func_800846B0(4) then (3): from nothing, spinning one way at a random speed.
                mapSize = 0;
                mapSpinSpeed = (rng.Next(0x400)) + 0x200;
                mapMode = 3;
            }
            else if (b != Beat.Play)
                mapMode = 1;
        }

        void StepMap()
        {
            switch (mapMode)
            {
                case 1:
                    mapSize -= 6;
                    mapSpinSpeed -= 71;
                    if (mapSize < 0)
                    {
                        mapSize = 0;
                        mapMode = 0;
                    }
                    break;
                case 2:
                    // The game lets the spin run down (speed -= speed >> 4), which never quite stops and
                    // left the map drifting off forward; settle it back to forward instead.
                    mapSpinSpeed = 0;
                    mapSpin = Math.Abs(mapSpin) < 64 ? 0 : mapSpin - mapSpin / 8;
                    break;
                case 3:
                    mapSize += 8;
                    mapSpinSpeed += 33;
                    if (mapSize > 320)
                    {
                        mapSize = 320;
                        mapMode = 2;
                    }
                    break;
            }
            mapSpin = (short)(mapSpin + mapSpinSpeed);
        }

        // effect.c u_give_points: the clear score is the time left in hundredths, doubled for a time bonus
        // when more than half the clock is left (a blue goal adds nothing and there are no warps).
        void GiveGoalPoints()
        {
            int clear = Math.Max(0, timeLeft) * 100 / 60;
            bool bonus = timeLeft > (int)(levelSeconds * 60f) / 2;
            int stage = clear * (bonus ? 2 : 1);
            score += stage;
            tally = new Hud.Tally { Clear = clear, Stage = stage, TimeBonus = bonus, Shown = true };
        }

        void Banner(string text, int frames)
        {
            banner = text;
            bannerFrames = bannerLength = frames;
        }

        // Once a step: the sounds and turns of the level's sequence (see the Deluxe level sequence notes).
        void StepBeat()
        {
            beatFrames++;
            StepMap();
            if (bannerFrames > 0)
                bannerFrames--;
            switch (beat)
            {
                case Beat.Ready:
                    if (beatFrames == 1)
                    {
                        audio.Play("narrator_ready");
                        Banner("READY", ReadyFrames);
                    }
                    else if (beatFrames == ReadyFrames - DropFrames)
                    {
                        audio.Play("monkey_start");
                        Function.Call(Hash.SET_ENTITY_VISIBLE, ball, true, false);
                        monkey.Visible(true);
                    }
                    else if (beatFrames >= ReadyFrames)
                    {
                        audio.Play("narrator_go");
                        Banner("GO!", GoFrames);
                        SetBeat(Beat.Play);
                        smb.Frames = 0;
                    }
                    break;
                case Beat.Goal:
                    if (beatFrames == 30)
                        audio.Play("narrator_goal");
                    else if (beatFrames == 60)
                        audio.Play("monkey_goal_" + (1 + rng.Next(3)));
                    else if (beatFrames == GoalFlyUp)
                    {
                        smb.GravityScale = -1f;  // the ball flies up and away
                        audio.Play("goal_fly_up");
                    }
                    else if (beatFrames >= GoalFrames)
                        pending = 1;
                    break;
                case Beat.FallOut:
                    if (beatFrames == 30)
                        audio.Play("narrator_fall_out");
                    else if (beatFrames >= FallOutFrames)
                        pending = 2;
                    break;
                case Beat.TimeOver:
                    if (beatFrames == 60)
                        audio.Play("monkey_time_over");
                    else if (beatFrames >= TimeOverFrames)
                        pending = 2;
                    break;
            }
        }

        // ball.c update_ball_ape_transform and func_8003CDC0: a bump sound by how hard the ball hit, and
        // on a hard hit the monkey cries out by how fast it was going.
        void Bumps(float hardest)
        {
            if (hardest >= -0.055f)
                return;
            audio.Play(hardest < -0.187f ? "ball_bump_2" : hardest < -0.143f ? "ball_bump_9" : hardest < -0.099f ? "ball_bump_4" : "ball_bump_8");
            if (hardest < -0.15f && frame - lastVoice > VoiceGap)
            {
                lastVoice = frame;
                float speed = smb.Vel.Length() * 216f;
                audio.Play(speed >= 35f ? "monkey_bump_big" : speed >= 20f ? "monkey_bump_medium" : "monkey_bump_small", VoiceVolume);
            }
        }

        // Once a step: through the goal, out of time, or into the water.
        void CheckLevel()
        {
            Vector3 old = ToGta(smb.PrevPos) * Scale, pos = ToGta(smb.Pos) * Scale;
            if (goal.Crossed(old, pos))
            {
                EndLevel(Beat.Goal);
                return;
            }
            if (--timeLeft <= 0)
            {
                EndLevel(Beat.TimeOver);
                return;
            }
            // The last ten seconds: HURRY UP, then a tick and the count each second, ticks each half second in the last five.
            if (timeLeft % 30 == 0 && timeLeft <= 600)
            {
                int secs = timeLeft / 60;
                if (timeLeft % 60 == 0)
                {
                    audio.Play("countdown_tick");
                    if (secs == 10)
                        audio.Play("narrator_hurry_up");
                    else
                        audio.Play("narrator_" + secs);
                }
                else if (secs < 5)
                    audio.Play("countdown_tick");
            }
            if (pos.Z < -30f || (Function.Call<bool>(Hash.GET_WATER_HEIGHT, pos.X, pos.Y, pos.Z + Radius, OutA) && pos.Z < ReadFloat(OutA)))
                EndLevel(Beat.FallOut);
        }

        void EndLevel(Beat how)
        {
            SetBeat(how);
            switch (how)
            {
                case Beat.Goal:
                    goal.Burst(ToGta(smb.Vel) * (Scale * 60f));
                    audio.Play("goal_tape_cut");
                    audio.Play("party_ball_open");
                    Banner("GOAL!", GoalFrames);
                    GiveGoalPoints();
                    smb.GravityScale = 0f;
                    monkey.Finish(Monkey.End.Goal);
                    StartGoalCamera();
                    goals++;
                    break;
                case Beat.TimeOver:
                    timeLeft = 0;
                    smb.Vel = Vector3.Zero;
                    audio.Play("narrator_time_over");
                    Banner("TIME OVER", TimeOverFrames);
                    monkey.Finish(Monkey.End.TimeOver);
                    break;
                case Beat.FallOut:
                    audio.Play("monkey_fall");
                    audio.Play("narrator_fall_out_2");
                    Banner("FALL OUT", FallOutFrames);
                    monkey.Finish(Monkey.End.FallOut);
                    break;
            }
            Log($"level: {how}");
        }

        void DrawHud(Vector3 pos)
        {
            hud.Draw(new Hud.State
            {
                TimeFrames = timeLeft,
                SpeedMph = smb.Vel.Length() * Scale * 60f * 2.23694f,
                Level = "San Andreas",
                Banner = bannerFrames > 0 ? banner : null,
                BannerAge = bannerLength - bannerFrames,
                BannerLength = bannerLength,
                Ball = pos,
                Goal = goal.Pos,
                HasGoal = goal.Entity != 0,
                CameraHeading = CameraHeading(),
                Velocity = ToGta(smb.Vel) * (Scale * 60f),
                Score = score,
                MapSize = mapSize / 320f,
                MapSpin = mapSpin * (360f / 65536f),
                Tally = tally,
                TallyAge = beat == Beat.Goal ? beatFrames : 0,
                Bananas = bananaCount,
            });
        }

        // Feed GTA's surfaces around the ball to Monkey Ball's plane collision.
        void Collide()
        {
            CheckSmashes();
            Vector3 old = ToGta(smb.PrevPos) * Scale, pos = ToGta(smb.Pos) * Scale;
            // Moving fast enough to skip through a wall in one step? Collide with it where the path meets it.
            Vector3 path = pos - old;
            if (path.Length() > 0.01f
             && Raycast.Cast(old, pos + path.Normalized * Radius, ball, out RayHit through) && !PassThrough(through.Entity, through.Normal, through.Point))
            {
                Edge(pos, ref through);
                smb.CollidePlane(ToSmb(through.Point) / Scale, ToSmb(through.Normal));
            }
            for (int pass = 0; pass < 2; pass++)
            {
                pos = ToGta(smb.Pos) * Scale;
                foreach (Vector3 dir in Probes)
                {
                    if (!Raycast.Cast(pos, pos + dir * (Radius + 0.05f), ball, out RayHit hit) || PassThrough(hit.Entity, hit.Normal, hit.Point))
                        continue;
                    Edge(pos, ref hit);
                    smb.CollidePlane(ToSmb(hit.Point) / Scale, ToSmb(hit.Normal));
                }
            }
            // The goal's posts (GTA doesn't use the gate's own collision).
            for (int side = -1; side <= 1; side += 2)
            {
                if (goal.Post(side, ToGta(smb.Pos) * Scale, Radius, out Vector3 point, out Vector3 normal))
                    smb.CollidePlane(ToSmb(point) / Scale, ToSmb(normal));
            }
            CollideCars();
        }

        // Monkey Ball collides with a triangle's edges as well as its face (stcoli.c
        // collide_ball_with_tri_edge): against the top edge of a curb or low wall, lower than the
        // ball's centre, the push is from the edge to the centre, tipped up, so at speed the ball
        // bounces up over it, and slowly it rolls up or stops. The rays only see the wall's face, so a
        // wall hit below the centre looks for the wall's top just behind the face, and if that top is
        // below the centre the hit becomes that edge.
        void Edge(Vector3 centre, ref RayHit hit)
        {
            Vector3 n = hit.Normal;
            if (Math.Abs(n.Z) > 0.6f || hit.Point.Z >= centre.Z)
                return;  // not a wall, or hit at or above the middle: the face it is
            var into = new Vector3(-n.X, -n.Y, 0f);
            into.Normalize();
            Vector3 from = new Vector3(hit.Point.X, hit.Point.Y, centre.Z) + into * 0.15f;
            if (!Raycast.Cast(from, new Vector3(from.X, from.Y, hit.Point.Z - 0.05f), ball, out RayHit top, 1 | 16)
             || top.Normal.Z < 0.7f || top.Point.Z >= centre.Z)
                return;  // the wall reaches up past the centre (or no flat top): the face it is
            var edge = new Vector3(hit.Point.X, hit.Point.Y, top.Point.Z);
            Vector3 d = centre - edge;
            float len = d.Length();
            if (len < 1e-4f)
                return;
            hit.Point = edge;
            hit.Normal = d / len;
        }

        // The rays only touch a car where one happens to land on its shell, so at speed the ball could
        // slip in between them. Every nearby car is also tested as its model box (a sphere against a
        // box: the nearest point of the box to the ball's centre), and a touch is a hit like any other.
        void CollideCars()
        {
            Vector3 pos = ToGta(smb.Pos) * Scale;
            foreach (int car in nearbyCars)
            {
                if (!Function.Call<bool>(Hash.DOES_ENTITY_EXIST, car))
                    continue;
                int model = Function.Call<int>(Hash.GET_ENTITY_MODEL, car);
                if (!boxes.TryGetValue(model, out var box))
                {
                    ModelBox(car, out Vector3 lo, out Vector3 hi);
                    Vector3 shrink = (hi - lo) * 0.04f;  // the model box takes in the mirrors
                    box = (lo + shrink, hi - shrink);
                    boxes[model] = box;
                }
                Vector3 local = Function.Call<Vector3>(Hash.GET_OFFSET_FROM_ENTITY_GIVEN_WORLD_COORDS, car, pos.X, pos.Y, pos.Z);
                Vector3 near = new Vector3(Clamp(local.X, box.lo.X, box.hi.X), Clamp(local.Y, box.lo.Y, box.hi.Y), Clamp(local.Z, box.lo.Z, box.hi.Z));
                Vector3 outLocal = local - near;
                float dist = outLocal.Length();
                if (dist >= Radius)
                    continue;
                if (dist < 1e-3f)
                {
                    // The centre is inside the box: out through the nearest face.
                    float[] gaps = { local.X - box.lo.X, box.hi.X - local.X, local.Y - box.lo.Y, box.hi.Y - local.Y, local.Z - box.lo.Z, box.hi.Z - local.Z };
                    int face = Array.IndexOf(gaps, gaps.Min());
                    var dirs = new[] { -Vector3.UnitX, Vector3.UnitX, -Vector3.UnitY, Vector3.UnitY, -Vector3.UnitZ, Vector3.UnitZ };
                    near = local + dirs[face] * gaps[face];
                    outLocal = dirs[face];
                }
                Vector3 point = Function.Call<Vector3>(Hash.GET_OFFSET_FROM_ENTITY_IN_WORLD_COORDS, car, near.X, near.Y, near.Z);
                Vector3 tip = Function.Call<Vector3>(Hash.GET_OFFSET_FROM_ENTITY_IN_WORLD_COORDS, car, near.X + outLocal.X, near.Y + outLocal.Y, near.Z + outLocal.Z);
                Vector3 normal = (tip - point).Normalized;
                PassThrough(car, normal, point);
            }
        }

        static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;

        // The cars near the ball, once a frame (the hidden player's own list of nearby vehicles).
        void FindCars()
        {
            nearbyCars.Clear();
            const int max = 24;
            Marshal.WriteInt64(carBuf, max);
            int n = Function.Call<int>(Hash.GET_PED_NEARBY_VEHICLES, Function.Call<int>(Hash.PLAYER_PED_ID), carBuf);
            Vector3 pos = ToGta(smb.Pos) * Scale;
            for (int i = 0; i < Math.Min(n, max); i++)
            {
                int car = Marshal.ReadInt32(carBuf, 8 + i * 8);
                if (car == 0)
                    continue;
                Vector3 at = Function.Call<Vector3>(Hash.GET_ENTITY_COORDS, car, true);
                if ((at - pos).Length() < Radius + 25f)
                    nearbyCars.Add(car);
            }
        }

        // The sphere swept along last frame's path finds thin things between the rays; knock them over.
        void CheckSweep(Vector3 from, Vector3 to)
        {
            if (sweep != 0)
            {
                int status = Raycast.Poll(sweep, sweepFrom, out RayHit hit);
                if (status == 1)
                    return;  // still running; keep it
                if (status == 2)
                    PassThrough(hit.Entity, hit.Normal, hit.Point);
            }
            sweepFrom = from;
            sweep = Raycast.StartSweep(from, to, Radius * 0.95f, ball, Raycast.World | Raycast.Vegetation);
        }

        // True when the ball goes through what it hit: cars and people get flung, loose props get thrown.
        // normal is the hit surface's, facing the ball. The hit is a collision between two masses: the
        // ball loses (1 + e) m / (M + m) of its speed into the thing, and the thing gains the rest.
        // A car is never passed through: the ball is also pushed back out of it, keeping its speed,
        // so a car that hasn't got going yet (or is pinned) is shoved again rather than entered.
        bool PassThrough(int entity, Vector3 normal, Vector3 point)
        {
            if (entity == 0 || solid.Contains(entity) || !Function.Call<bool>(Hash.DOES_ENTITY_EXIST, entity))
                return false;
            if (entity == Function.Call<int>(Hash.PLAYER_PED_ID))
                return true;  // our own rider
            if (entity == goal.Entity || goal.IsBlocker(entity))
                return false;  // the goal's posts: solid
            Vector3 vel = ToGta(smb.Vel) * (Scale * 60f);  // m/s
            bool isVehicle = Function.Call<bool>(Hash.IS_ENTITY_A_VEHICLE, entity);
            bool isPed = !isVehicle && Function.Call<bool>(Hash.IS_ENTITY_A_PED, entity);
            bool isObject = !isVehicle && !isPed && Function.Call<bool>(Hash.IS_ENTITY_AN_OBJECT, entity);
            if (!isVehicle && !isPed && !isObject)
                return false;
            if (isObject && !TrySmash(entity, vel, point, normal))
                return false;
            Vector3 n = -normal;  // from the ball into what it hit
            if (isVehicle)
                smb.PushOut(ToSmb(point) / Scale, ToSmb(normal));
            else if (lastHit.TryGetValue(entity, out int at) && frame - at < HitCooldown)
                return true;  // just hit it; it's on its way

            Vector3 their = Function.Call<Vector3>(Hash.GET_ENTITY_VELOCITY, entity);
            // The game reports the new velocity we gave a car a frame or so late; don't hit it twice for it.
            if (isVehicle && shoved.TryGetValue(entity, out var gave) && frame - gave.frame < 10
             && Vector3.Dot(gave.vel, n) > Vector3.Dot(their, n))
                their = gave.vel;
            float closing = Vector3.Dot(vel - their, n);
            if (closing < 0.5f)
                return true;  // moving apart, or barely touching
            lastHit[entity] = frame;

            float m = Mass(entity, isVehicle, isPed);
            float impulse = (1f + HitRestitution) * closing / (1f / BallMass + 1f / m);
            smb.Vel -= ToSmb(n * (impulse / BallMass)) / (Scale * 60f);
            if (isPed)
                Function.Call(Hash.SET_PED_TO_RAGDOLL, entity, 4000, 6000, 0, true, true, false);
            Vector3 target = their + n * (impulse / m) + new Vector3(0f, 0f, 1f + closing * 0.12f);
            if (isVehicle)
            {
                Function.Call(Hash.ACTIVATE_PHYSICS, entity);
                shoved[entity] = (target, frame);
            }
            Function.Call(Hash.SET_ENTITY_VELOCITY, entity, target.X, target.Y, target.Z);
            if (isVehicle)
            {
                // Dent the car where the ball hit it, deeper the harder the hit (a 100 t ball crushes it).
                Vector3 at = Function.Call<Vector3>(Hash.GET_OFFSET_FROM_ENTITY_GIVEN_WORLD_COORDS, entity, point.X, point.Y, point.Z);
                Function.Call(Hash.SET_VEHICLE_DAMAGE, entity, at.X, at.Y, at.Z, Math.Min(4000f, closing * closing * 25f), Radius * 1.2f, true);
                if (closing > 12f)
                    Function.Call(Hash.SET_VEHICLE_ENGINE_HEALTH, entity, -1f);
            }
            float into = -closing / (Scale * 60f);
            if (into < hardestHit)
            {
                hardestHit = into;
                hardNormal = ToSmb(normal);
            }
            return true;
        }

        // A guess from the model's size, since the game's own masses can't be read on this build:
        // cars come out near 1.5 t, a bus near 16 t; people are 80 kg.
        float Mass(int entity, bool isVehicle, bool isPed)
        {
            if (isPed)
                return 80f;
            if (masses.TryGetValue(entity, out float m))
                return m;
            Vector3 size = ModelSize(entity);
            float volume = Math.Abs(size.X * size.Y * size.Z);
            m = isVehicle ? Math.Max(500f, Math.Min(40000f, volume * 120f)) : Math.Max(20f, Math.Min(3000f, volume * 100f));
            masses[entity] = m;
            return m;
        }

        // Tear a prop loose so physics takes it, and let the ball through it. A prop that hasn't moved
        // or broken a few frames later is bolted down (an unbreakable fence, part of the map): the
        // ball is put back on the near side of it and bounces off as from a wall (CheckSmashes), and
        // from then on it's solid.
        bool TrySmash(int obj, Vector3 vel, Vector3 point, Vector3 normal)
        {
            if (smashed.ContainsKey(obj))
                return true;
            if (vel.Length() < SmashSpeed)
                return false;
            smashed[obj] = new Smash { Pos = Function.Call<Vector3>(Hash.GET_ENTITY_COORDS, obj, true), Point = point, Normal = normal, Frame = frame };
            Function.Call(Hash.FREEZE_ENTITY_POSITION, obj, false);
            Function.Call(Hash.SET_ENTITY_DYNAMIC, obj, true);
            Function.Call(Hash.ACTIVATE_PHYSICS, obj);
            // Breakable props (lamp posts, traffic lights) are fragments bolted to a base: snap them off,
            // then shove high up so they topple the way a car hit makes them.
            Function.Call(Hash.BREAK_OBJECT_FRAGMENT_CHILD, obj, 0, false);
            Vector3 shove = vel * FlingBoost;
            Function.Call(Hash.APPLY_FORCE_TO_ENTITY, obj, 1, shove.X, shove.Y, shove.Z + 2f, 0f, 0f, 1.5f, 0, false, true, true, false, true);
            return true;
        }

        // Once a step: settle the props the ball went into SettleFrames ago.
        void CheckSmashes()
        {
            const int SettleFrames = 4;
            List<int> done = null;
            foreach (var kv in smashed)
            {
                Smash s = kv.Value;
                if (s.Settled || frame - s.Frame < SettleFrames)
                    continue;
                (done ?? (done = new List<int>())).Add(kv.Key);
                int obj = kv.Key;
                bool exists = Function.Call<bool>(Hash.DOES_ENTITY_EXIST, obj);
                bool loose = !exists
                    || (Function.Call<Vector3>(Hash.GET_ENTITY_COORDS, obj, true) - s.Pos).Length() > 0.05f
                    || Function.Call<Vector3>(Hash.GET_ENTITY_VELOCITY, obj).Length() > 0.3f
                    || Function.Call<bool>(Hash.HAS_OBJECT_BEEN_BROKEN, obj, 0);
                if (loose)
                    continue;
                solid.Add(obj);
                // It held: back out to where the ball met it, and off it like a wall.
                Vector3 p = ToSmb(s.Point) / Scale, n = ToSmb(s.Normal);
                if (Vector3.Dot(smb.Pos - p, n) < SmbBall.Radius)
                {
                    smb.PushOut(p, n);
                    smb.CollidePlane(p, n);
                }
            }
            if (done != null)
                foreach (int obj in done)
                {
                    Smash s = smashed[obj];
                    s.Settled = true;
                    smashed[obj] = s;
                }
        }

        // Spin the ball picture to match how far it rolled.
        void Roll(Vector3 old, Vector3 pos)
        {
            Vector3 up = Vector3.WorldUp;
            Vector3 moved = pos - old;
            moved -= up * Vector3.Dot(moved, up);
            float dist = moved.Length();
            if (dist < 1e-4f)
                return;
            Vector3 axis = Vector3.Cross(up, moved / dist);
            spin = Quaternion.RotationAxis(axis, dist / Radius) * spin;
            spin.Normalize();
        }

        // Place GTA's camera at the Monkey Ball camera's pose. GTA rotation order 2 is yaw, then pitch,
        // then roll about the view direction; the camera looks along its +y with +z up.
        void UpdateCamera()
        {
            Vector3 eyeSmb, fwdSmb, upSmb;
            if (state != State.Off && (beat == Beat.Ready || beat == Beat.Goal))
            {
                eyeSmb = camEye;
                fwdSmb = (camLook - camEye).Normalized;
                Vector3 side = Vector3.Cross(fwdSmb, new Vector3(0f, 1f, 0f));
                upSmb = side.Length() > 1e-4f ? Vector3.Cross(side.Normalized, fwdSmb) : new Vector3(0f, 0f, -1f);
            }
            else
                smbCam.Pose(smb, out eyeSmb, out fwdSmb, out upSmb);
            Vector3 eye = ToGta(eyeSmb) * Scale, f = ToGta(fwdSmb).Normalized, u = ToGta(upSmb).Normalized;
            if (state != State.Off && (beat == Beat.Ready || beat == Beat.Goal))
                eye = AboveGround(eye, ToGta(camLook) * Scale);
            hud.SetView(eye, eye + f, CamFov);
            float pitch = (float)Math.Asin(Math.Max(-1f, Math.Min(1f, f.Z)));
            float yaw = (float)Math.Atan2(-f.X, f.Y);
            var right0 = new Vector3((float)Math.Cos(yaw), (float)Math.Sin(yaw), 0f);
            Vector3 up0 = Vector3.Cross(right0, f);
            float roll = (float)Math.Atan2(Vector3.Dot(u, right0), Vector3.Dot(u, up0));
            const float deg = 180f / (float)Math.PI;
            Function.Call(Hash.SET_CAM_COORD, cam, eye.X, eye.Y, eye.Z);
            Function.Call(Hash.SET_CAM_ROT, cam, pitch * deg, roll * deg, yaw * deg, 2);
        }

        // The fly-in and goal cameras swing freely; keep them out of the ground and walls: in front
        // of anything between the eye and what it looks at, and a little above the ground under it.
        Vector3 AboveGround(Vector3 eye, Vector3 look)
        {
            if (Raycast.Cast(look, eye, ball, out RayHit wall, 1))
                eye = wall.Point + (look - wall.Point).Normalized * 0.5f;
            if (Raycast.Cast(eye + new Vector3(0f, 0f, 3f), eye - new Vector3(0f, 0f, 1.5f), ball, out RayHit ground, 1))
                eye.Z = Math.Max(eye.Z, ground.Point.Z + 1.5f);
            return eye;
        }

        // camera_func_ready_main: from high and out over the start, swing in to behind the ball's
        // landing spot over FlyInFrames, smoothstepped.
        void FlyIn(int frames)
        {
            float t = Math.Max(0f, (FlyInFrames - frames) / (float)FlyInFrames);
            t = t * t * (3f - 2f * t);
            Vector3 end = landing + new Vector3(0f, 0.5f, 0f);
            camLook = end;
            short yaw = (short)(smbCam.RotY - (int)(0x2000 * t));
            float r = FlyInRadius * 0.8f;
            camEye = camLook + SmbMath.RotY(new Vector3(0f, (1f - t) + r * t, (1f - t) * 3f + r * t), yaw);
            camEyeVel = Vector3.Zero;
        }

        void StartGoalCamera()
        {
            // From where the follow camera was, still moving as it was.
            camEye = stepEye;
            camEyeVel = stepEye - prevStepEye;
            camLook = smbCam.LookAt;
            Vector3 p = ToGta(smb.Pos) * Scale;
            goalFloor = Raycast.Cast(p, p - new Vector3(0f, 0f, 60f), ball, out RayHit hit, 1) ? ToSmb(hit.Point).Y / Scale : smb.Pos.Y - SmbBall.Radius;
        }

        // camera_func_goal_main: the eye keeps drifting the way it was going and is pulled round to stay
        // 2 units from the ball; it looks at the ball with a lag, so it turns up after it as it flies off.
        void GoalCamera()
        {
            Vector3 to = smb.Pos - camEye;
            to.Y = 0f;
            float along = Vector3.Dot(to, camEyeVel);
            if (to.Length() > 1e-6f)
                to.Normalize();
            camEyeVel.Y *= 0.97f;
            float f2 = -0.01f * along;
            camEyeVel.X += f2 * to.X;
            camEyeVel.Z += f2 * to.Z;
            if (f2 < 0f)
            {
                f2 *= 0.5f;
                camEyeVel.X += f2 * to.Z;
                camEyeVel.Z += f2 * to.X;
            }
            camEye += camEyeVel;
            camLook += (smb.Pos - camLook) * 0.3f;
            Vector3 d = camEye - camLook;
            d.Y = 0f;
            float len = d.Length();
            if (len > 1.1920929e-7f)
            {
                float k = (len + 0.08f * (2f - len)) / len;
                camEye.X = d.X * k + camLook.X;
                camEye.Z = d.Z * k + camLook.Z;
                if (beatFrames < GoalFlyUp)  // once the ball is flying the eye stays put and looks up after it
                    camEye.Y += 0.01f * (camLook.Y - camEye.Y);
            }
        }

        // The camera's heading in GTA degrees, for the minimap.
        float CameraHeading()
        {
            if (state == State.On && (beat == Beat.Ready || beat == Beat.Goal))
            {
                Vector3 f = ToGta(camLook - camEye);
                return (float)(Math.Atan2(-f.X, f.Y) * 180.0 / Math.PI);
            }
            return smbCam.RotY * 360f / 65536f;
        }

        // The hidden player rides with the ball, facing the way it rolls.
        void RidePed(Vector3 pos)
        {
            int ped = Function.Call<int>(Hash.PLAYER_PED_ID);
            Function.Call(Hash.SET_ENTITY_COORDS_NO_OFFSET, ped, pos.X, pos.Y, pos.Z, false, false, false);
            Vector3 v = ToGta(smb.Vel);
            if (v.X * v.X + v.Y * v.Y > 1e-6f)
                Function.Call(Hash.SET_ENTITY_HEADING, ped, (float)(Math.Atan2(-v.X, v.Y) * 180.0 / Math.PI));
        }

        static float TimeFor(float metres) => Math.Max(30f, (float)Math.Ceiling((metres / ChallengeSpeed + SpareSeconds) / 5f) * 5f);

        // Once a second, read the road route to the goal again; lay the bananas the first time.
        // Every frame, keep the road network loaded all the way to the goal, so the GPS can route
        // the whole distance (path nodes otherwise only stream in near the player).
        void UpdateRoute(Vector3 pos)
        {
            if (goal.Entity == 0)
                return;
            const float pad = 300f;
            Function.Call(Hash.REQUEST_PATH_NODES_IN_AREA_THIS_FRAME, Math.Min(pos.X, goal.Pos.X) - pad, Math.Min(pos.Y, goal.Pos.Y) - pad,
                Math.Max(pos.X, goal.Pos.X) + pad, Math.Max(pos.Y, goal.Pos.Y) + pad);
            if (--routeAge > 0)
                return;
            routeAge = 60;
            if (!gps.Sample(goal.Blip, pos - new Vector3(0f, 0f, Radius), goal.Pos))
                return;
            hud.Route = new List<Vector3>(gps.Points);
            int metres = timeFromRoute ? 0 : Function.Call<int>(Hash.GET_GPS_BLIP_ROUTE_LENGTH, goal.Blip);
            if (metres > 0)
            {
                // The road distance is in: set the clock by it, keeping the time already used.
                timeFromRoute = true;
                float seconds = TimeFor(metres);
                timeLeft += (int)((seconds - levelSeconds) * 60f);
                levelSeconds = seconds;
                Log($"level: {metres} m by road, {seconds:F0} s");
            }
            if (!bananas.Placed && gps.FromGps && beat != Beat.Goal)
                bananas.Place(gps.Points);
        }

        void Stop()
        {
            if (state == State.Off)
                return;
            int ped = Function.Call<int>(Hash.PLAYER_PED_ID);
            goal.Clear();
            Function.Call(Hash.DISPLAY_RADAR, true);
            Function.Call(Hash.DO_SCREEN_FADE_IN, 0);
            if (state == State.On || state == State.Placing)
            {
                Vector3 pos = ToGta(smb.Pos) * Scale;
                Function.Call(Hash.FREEZE_ENTITY_POSITION, ped, false);
                Function.Call(Hash.SET_ENTITY_COLLISION, ped, true, true);
                Function.Call(Hash.SET_ENTITY_VISIBLE, ped, true, false);
                Function.Call(Hash.SET_ENTITY_INVINCIBLE, ped, false);
                Function.Call(Hash.SET_ENTITY_COORDS, ped, pos.X, pos.Y, pos.Z, false, false, false, false);
                DeleteObject(ball);
                monkey.Remove();
                effects.Remove();
                bananas.Clear();
                audio.StopAll();
                Function.Call(Hash.RENDER_SCRIPT_CAMS, false, false, 0, true, false, 0);
                Function.Call(Hash.DESTROY_CAM, cam, false);
                Function.Call(Hash.SET_MAX_WANTED_LEVEL, 5);
                Log("ball mode off");
            }
            ball = cam = 0;
            state = State.Off;
        }
    }
}
