// One level: a random start and goal on GTA's road network, the goal gate, its map blip and route,
// and the clock.
//
// Path nodes only stream in near the player, so RouteFinder asks for the area it wants and waits a
// few frames for it before reading the nearest road node.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using GTA.Math;
using GTA.Native;
using static MonkeyBallGta.Util;

namespace MonkeyBallGta
{
    // The goal is found first, then a start a long way off. Goals lean toward busy streets: most
    // are looked for inside Los Santos, and each is kept with a chance that grows with the traffic
    // density GTA gives its road node (GET_VEHICLE_NODE_PROPERTIES, 0 to 15), so a quiet lane
    // rarely wins. Starts are anywhere on the map.
    class RouteFinder
    {
        // Roughly the land of San Andreas; points out at sea find no road close enough and are retried.
        const float MinX = -3200f, MaxX = 3800f, MinY = -3400f, MaxY = 7600f;
        // Los Santos, from Vespucci to the docks and up to Vinewood.
        const float CityMinX = -2000f, CityMaxX = 1400f, CityMinY = -2800f, CityMaxY = 700f, CityShare = 0.75f;
        const float Snap = 250f;                         // a node must be this close to the point we asked about
        const float StartMin = 1500f, StartMax = 4000f;  // how far from the goal the start is
        const int LoadFrames = 90;

        readonly Random rng = new Random();
        bool findingStart;
        Vector3 target;
        int waited;
        public int Tries;
        public Vector3 Start, Goal;
        public float StartHeading, GoalHeading;  // GTA headings, degrees
        public int GoalDensity;

        public void Begin()
        {
            findingStart = false;
            Tries = 0;
            NewTarget();
        }

        // Call once a frame; true once both ends are found.
        public bool Tick()
        {
            float x1 = target.X - Snap, y1 = target.Y - Snap, x2 = target.X + Snap, y2 = target.Y + Snap;
            Function.Call(Hash.REQUEST_PATH_NODES_IN_AREA_THIS_FRAME, x1, y1, x2, y2);
            if (!Function.Call<bool>(Hash.ARE_NODES_LOADED_FOR_AREA, x1, y1, x2, y2) && ++waited < LoadFrames)
                return false;

            // Flags 0: roads open to traffic, no boat lanes. A z weight of 0 ignores height, since we don't know it.
            bool found = Function.Call<bool>(Hash.GET_CLOSEST_VEHICLE_NODE_WITH_HEADING,
                target.X, target.Y, 0f, OutA, OutB, 0, 0f, 0);
            Vector3 node = ReadVector(OutA);
            float heading = ReadFloat(OutB);
            float off = new Vector2(node.X - target.X, node.Y - target.Y).Length();
            if (found && off < Snap)
            {
                if (!findingStart)
                {
                    int density = Density(node);
                    if (rng.NextDouble() < 0.1 + 0.9 * Math.Min(1.0, density / 10.0))
                    {
                        Goal = node;
                        GoalHeading = heading;
                        GoalDensity = density;
                        findingStart = true;
                    }
                }
                else
                {
                    float d = new Vector2(node.X - Goal.X, node.Y - Goal.Y).Length();
                    if (d >= StartMin * 0.9f && d <= StartMax * 1.1f)
                    {
                        Start = node;
                        StartHeading = heading;
                        return true;
                    }
                }
            }
            // A start that won't turn up for this goal after a while: pick another goal.
            if (++Tries % 12 == 0)
                findingStart = false;
            NewTarget();
            return false;
        }

        // The traffic density of the road node at p (0 to 15), or 0 if there's none.
        static int Density(Vector3 p)
        {
            Marshal.WriteInt64(OutA, 0);
            Marshal.WriteInt64(OutB, 0);
            if (!Function.Call<bool>(Hash.GET_VEHICLE_NODE_PROPERTIES, p.X, p.Y, p.Z, OutA, OutB))
                return 0;
            return Marshal.ReadInt32(OutA);
        }

        void NewTarget()
        {
            waited = 0;
            if (!findingStart)
            {
                target = rng.NextDouble() < CityShare
                    ? new Vector3(Lerp(CityMinX, CityMaxX), Lerp(CityMinY, CityMaxY), 0f)
                    : new Vector3(Lerp(MinX, MaxX), Lerp(MinY, MaxY), 0f);
                return;
            }
            double a = rng.NextDouble() * Math.PI * 2.0;
            float d = Lerp(StartMin, StartMax);
            target = Goal + new Vector3((float)Math.Cos(a) * d, (float)Math.Sin(a) * d, 0f);
        }

        float Lerp(float a, float b) => a + (float)rng.NextDouble() * (b - a);
    }

    // The road route from the ball to the goal, read from the GPS route the goal's blip sets up
    // (GET_POS_ALONG_GPS_TYPE_ROUTE, a point every few metres), for the minimap and the bananas.
    // Which kind of GPS route answers isn't documented, so the first that does is kept.
    //
    // The GPS reroutes from the player, and sometimes picks the road above or below the ball (a
    // bridge, an underpass), and may hold only part of a long route. So the route is kept for the
    // whole level: a fresh GPS read replaces it only when it starts on the ball's own road level and
    // gets at least as close to the goal, a route that stops short is finished with the straight line
    // to the goal snapped to roads, and what's drawn is the kept route from the point nearest the ball.
    class GpsRoute
    {
        const float StepMetres = 8f, MaxMetres = 9000f;
        const float StartNear = 40f, StartLevel = 6f;  // a fresh read must start this close to the ball (m), and this close in height
        const float DoneNear = 60f;                    // a route ending this close to the goal reached it
        const float LostMetres = 80f;                  // the ball this far from the kept route has left it
        public readonly List<Vector3> Points = new List<Vector3>();  // what to draw: ball to goal
        public bool FromGps => keptGap < float.MaxValue;               // false while only the straight stand-in is in
        readonly List<Vector3> kept = new List<Vector3>(), fresh = new List<Vector3>();
        int kind = -1;
        bool fromStart = true;
        int fails, offReads, reads;
        float keptGap = float.MaxValue;  // how far from the goal the kept GPS route stops
        const int TrustAfter = 5;        // with nothing kept, a read that starts elsewhere is taken after this many

        public void Clear()
        {
            Points.Clear();
            kept.Clear();
            keptGap = float.MaxValue;
            offReads = reads = 0;
        }

        // Read the GPS route again; true once there is a route to draw. ground is the road under the ball.
        public bool Sample(int blip, Vector3 ground, Vector3 goal)
        {
            if (kind < 0 && !Probe())
            {
                if (++fails == 10)
                    Log("route: no GPS route answered GET_POS_ALONG_GPS_TYPE_ROUTE; using the straight line");
                if (kept.Count == 0)
                    Straight(ground, goal, kept);  // a stand-in: any GPS read replaces it (keptGap stays at its max)
            }
            else
            {
                Read(blip);
                if (fresh.Count > 1)
                {
                    Vector3 first = fresh[0];
                    float gap = Flat(fresh[fresh.Count - 1] - goal);
                    bool onOurLevel = Flat(first - ground) < StartNear && Math.Abs(first.Z - ground.Z) < StartLevel;
                    // Just after a jump to the start the GPS may still hold the old route: wait for one
                    // from here, for a few reads.
                    bool firstRead = (kept.Count == 0 || keptGap == float.MaxValue) && (onOurLevel || ++offReads >= TrustAfter);
                    // Off the kept route altogether (another way round): the GPS's new route it is.
                    bool lost = kept.Count > 0 && OffBy(ground) > LostMetres;
                    if (firstRead || lost || (onOurLevel && gap <= Math.Max(keptGap, DoneNear)))
                    {
                        kept.Clear();
                        kept.AddRange(fresh);
                        keptGap = gap;
                        if (gap > DoneNear)
                            Straight(fresh[fresh.Count - 1], goal, kept);
                        if (firstRead)
                            Dump(goal);
                    }
                    if (reads++ < 1)
                        Log($"route: GPS kind {kind} ({(fromStart ? "from the start" : "from the player")}): {fresh.Count} points from {Flat(first - ground):F0} m away ({first.Z - ground.Z:F1} m up), ending {gap:F0} m from the goal; kept {kept.Count}");
                }
            }
            Trim(ground);
            return Points.Count > 1;
        }

        // The kept route from the point nearest the ball (height counted three times, so a road
        // overhead isn't nearest).
        void Trim(Vector3 ground)
        {
            Points.Clear();
            int best = 0;
            float bestD = float.MaxValue;
            for (int i = 0; i < kept.Count; i++)
            {
                Vector3 d = kept[i] - ground;
                float dist = d.X * d.X + d.Y * d.Y + 9f * d.Z * d.Z;
                if (dist < bestD)
                {
                    bestD = dist;
                    best = i;
                }
            }
            for (int i = best; i < kept.Count; i++)
                Points.Add(kept[i]);
        }

        void Read(int blip)
        {
            fresh.Clear();
            float length = blip != 0 ? Function.Call<int>(Hash.GET_GPS_BLIP_ROUTE_LENGTH, blip) : 0f;
            float end = length > 0f ? Math.Min(length + 50f, MaxMetres) : MaxMetres;
            Vector3 last = Vector3.Zero;
            for (float d = 0f; d <= end; d += StepMetres)
            {
                if (!Function.Call<bool>(Hash.GET_POS_ALONG_GPS_TYPE_ROUTE, OutA, fromStart, d, kind))
                    break;
                Vector3 p = ReadVector(OutA);
                if (p == Vector3.Zero || (fresh.Count > 0 && (p - last).Length() < 0.5f && d > StepMetres * 4))
                    break;  // past the end of what the GPS holds
                fresh.Add(p);
                last = p;
            }
        }

        static float Flat(Vector3 v) => new Vector2(v.X, v.Y).Length();

        float OffBy(Vector3 ground)
        {
            float best = float.MaxValue;
            foreach (Vector3 p in kept)
                best = Math.Min(best, Flat(p - ground));
            return best;
        }

        // The level's first route, to check against the game's road network offline.
        void Dump(Vector3 goal)
        {
            try
            {
                var lines = new List<string> { $"goal {goal.X:F1} {goal.Y:F1} {goal.Z:F1}", $"gps {fresh.Count}" };
                lines.AddRange(kept.Select(p => $"{p.X:F1} {p.Y:F1} {p.Z:F1}"));
                System.IO.File.WriteAllLines(System.IO.Path.Combine("scripts", "MonkeyBallGta", "route.txt"), lines);
            }
            catch (Exception ex) { Log("route dump: " + ex.Message); }
        }

        // The straight line on to the goal, each point moved onto the nearest road where one is loaded.
        static void Straight(Vector3 from, Vector3 to, List<Vector3> into)
        {
            float len = (to - from).Length();
            for (float d = 25f; d < len; d += 25f)
            {
                Vector3 p = from + (to - from) * (d / len);
                if (Function.Call<bool>(Hash.GET_CLOSEST_VEHICLE_NODE, p.X, p.Y, p.Z, OutA, 0, 3f, 0f))
                {
                    Vector3 n = ReadVector(OutA);
                    if ((n - p).Length() < 60f)
                        p = n;
                }
                into.Add(p);
            }
            into.Add(to);
        }

        bool Probe()
        {
            for (int k = 0; k <= 3; k++)
            {
                foreach (bool start in new[] { true, false })
                {
                    if (!Function.Call<bool>(Hash.GET_POS_ALONG_GPS_TYPE_ROUTE, OutA, start, 0f, k))
                        continue;
                    Vector3 a = ReadVector(OutA);
                    if (!Function.Call<bool>(Hash.GET_POS_ALONG_GPS_TYPE_ROUTE, OutA, start, 60f, k))
                        continue;
                    Vector3 b = ReadVector(OutA);
                    if (a == Vector3.Zero || (a - b).Length() < 10f)
                        continue;
                    kind = k;
                    fromStart = start;
                    return true;
                }
            }
            return false;
        }
    }

    // The goal gate in the world: its picture, the posts the ball bumps into, and the line it crosses.
    class Goal
    {
        public const string Model = "mbgta_goal";
        // From the GOAL model, in Monkey Ball units: the posts stand at x 0.87..1.36 either side, the arch
        // closes at 5.42 up.
        const float PostX = 1.115f, PostRadius = 0.245f, InnerX = 0.87f, Height = 5.4f;

        // Cars drive straight through the gate's own collision, so each post hides a ring of the game's
        // bollards (invisible, frozen, unbreakable): the first of these models that's installed.
        static readonly string[] BlockerModels = { "prop_bollard_02a", "prop_bollard_04", "prop_bollard_01a" };
        const int BlockersPerPost = 7;
        const float BlockerRing = 0.55f;  // metres from the post's middle, inside its radius at scale 3.5

        public int Entity, Blip;
        public Vector3 Pos;
        readonly List<int> blockers = new List<int>();
        static bool blockerLogged;
        Vector3 forward, right;
        readonly float scale;

        readonly GoalShow show;

        public Goal(float scale)
        {
            this.scale = scale;
            show = new GoalShow(scale);
        }

        // Every model the gate needs, to request before a level starts.
        public static string[] Models => new[] { Model }.Concat(GoalShow.Models).Concat(BlockerModels).ToArray();

        public bool IsBlocker(int entity) => blockers.Contains(entity);

        void SpawnBlockers()
        {
            string model = BlockerModels.FirstOrDefault(m => Function.Call<bool>(Hash.HAS_MODEL_LOADED, Joaat(m)));
            if (!blockerLogged)
            {
                blockerLogged = true;
                Log("goal: post blockers " + (model ?? "none loaded"));
            }
            if (model == null)
                return;
            for (int side = -1; side <= 1; side += 2)
            {
                Vector3 axis = Pos + right * (side * PostX * scale);
                for (int i = 0; i < BlockersPerPost; i++)
                {
                    Vector3 p = axis;
                    if (i > 0)
                    {
                        double a = 2.0 * Math.PI * i / (BlockersPerPost - 1);
                        p += right * (float)Math.Cos(a) * BlockerRing + forward * (float)Math.Sin(a) * BlockerRing;
                    }
                    int obj = Function.Call<int>(Hash.CREATE_OBJECT, Joaat(model), p.X, p.Y, p.Z, false, false, false);
                    if (obj == 0)
                        continue;
                    Function.Call(Hash.SET_ENTITY_COORDS_NO_OFFSET, obj, p.X, p.Y, p.Z, false, false, false);
                    Function.Call(Hash.FREEZE_ENTITY_POSITION, obj, true);
                    Function.Call(Hash.SET_ENTITY_INVINCIBLE, obj, true);
                    Function.Call(Hash.SET_DISABLE_FRAG_DAMAGE, obj, true);
                    Function.Call(Hash.SET_DISABLE_BREAKING, obj, true);
                    Function.Call(Hash.SET_ENTITY_VISIBLE, obj, false, false);
                    blockers.Add(obj);
                }
            }
        }

        // The gate's panel faces its back (the model's +z): turn it so that side faces the way the ball comes from.
        public bool Spawn(Vector3 pos, float heading, Vector3 from)
        {
            Clear();
            Pos = pos;
            double f = heading * Math.PI / 180.0;
            if (-(float)Math.Sin(f) * (from.X - pos.X) + (float)Math.Cos(f) * (from.Y - pos.Y) > 0f)
                heading += 180f;
            double h = heading * Math.PI / 180.0;
            forward = new Vector3(-(float)Math.Sin(h), (float)Math.Cos(h), 0f);
            right = new Vector3((float)Math.Cos(h), (float)Math.Sin(h), 0f);
            uint hash = Joaat(Model);
            Entity = Function.Call<int>(Hash.CREATE_OBJECT, hash, pos.X, pos.Y, pos.Z, false, false, false);
            if (Entity == 0)
                return false;
            Function.Call(Hash.SET_ENTITY_HEADING, Entity, heading);
            Function.Call(Hash.SET_ENTITY_COORDS_NO_OFFSET, Entity, pos.X, pos.Y, pos.Z, false, false, false);
            Function.Call(Hash.FREEZE_ENTITY_POSITION, Entity, true);
            // The prop's collision is its two posts (build_gta.py), so cars stop against them.
            Function.Call(Hash.SET_ENTITY_COLLISION, Entity, true, true);
            show.Spawn(Entity, pos, right, forward);
            SpawnBlockers();

            Blip = Function.Call<int>(Hash.ADD_BLIP_FOR_COORD, pos.X, pos.Y, pos.Z);
            Function.Call(Hash.SET_BLIP_SPRITE, Blip, 38);  // chequered flag
            Function.Call(Hash.SET_BLIP_COLOUR, Blip, 5);
            Function.Call(Hash.SET_BLIP_ROUTE, Blip, true);
            Function.Call(Hash.SET_BLIP_ROUTE_COLOUR, Blip, 5);
            // The blip's own route only draws on the pause map while on foot; a multi-route set to show
            // on foot draws on the minimap too. HUD colour 12 is yellow.
            Function.Call(Hash.START_GPS_MULTI_ROUTE, 12, true, true);
            Function.Call(Hash.ADD_POINT_TO_GPS_MULTI_ROUTE, pos.X, pos.Y, pos.Z);
            Function.Call(Hash.SET_GPS_MULTI_ROUTE_RENDER, true);
            return true;
        }

        // Have the GPS route to the goal again from where the player is now.
        public void Reroute()
        {
            if (Blip == 0)
                return;
            Function.Call(Hash.SET_BLIP_ROUTE, Blip, false);
            Function.Call(Hash.SET_BLIP_ROUTE, Blip, true);
            Function.Call(Hash.SET_BLIP_ROUTE_COLOUR, Blip, 5);
        }

        public void Clear()
        {
            if (Blip != 0)
                Function.Call(Hash.CLEAR_GPS_MULTI_ROUTE);
            RemoveBlip(Blip);
            show.Clear();
            foreach (int obj in blockers)
                DeleteObject(obj);
            blockers.Clear();
            DeleteObject(Entity);
            Entity = Blip = 0;
        }

        public void Update(int timeFrames, Vector3 ball, float radius, float dt) => show.Update(timeFrames, ball, radius, dt);

        public void Burst(Vector3 ballVel) => show.Burst(ballVel);

        // GTA metres -> (across the gate, through it, up).
        Vector3 Local(Vector3 p)
        {
            Vector3 d = p - Pos;
            return new Vector3(Vector3.Dot(d, right), Vector3.Dot(d, forward), d.Z);
        }

        // The ball (GTA metres) rolled through between the posts, either way, between these two positions.
        public bool Crossed(Vector3 from, Vector3 to)
        {
            if (Entity == 0)
                return false;
            Vector3 a = Local(from), b = Local(to);
            if ((a.Y < 0f) == (b.Y < 0f) || a.Y == b.Y)
                return false;
            float t = a.Y / (a.Y - b.Y);
            Vector3 at = a + (b - a) * t;
            return Math.Abs(at.X) < InnerX * scale && at.Z > -scale && at.Z < Height * scale;
        }

        // The two posts as upright cylinders: the closest point on each, as a plane for the ball to hit.
        public bool Post(int side, Vector3 ball, float radius, out Vector3 point, out Vector3 normal)
        {
            point = normal = Vector3.Zero;
            if (Entity == 0)
                return false;
            Vector3 axis = Pos + right * (side * PostX * scale);
            Vector3 d = ball - axis;
            if (d.Z < -radius || d.Z > Height * scale)
                return false;
            d.Z = 0f;
            float dist = d.Length();
            float r = PostRadius * scale;
            if (dist > r + radius || dist < 1e-4f)
                return false;
            normal = d / dist;
            point = new Vector3(axis.X, axis.Y, ball.Z) + normal * r;
            return true;
        }
    }
}
