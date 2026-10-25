// What hangs on the goal gate (stobj_goal.c): the timer panel's LCD digits, the tape across the posts,
// and the party ball under the timer, which bursts into confetti when the ball goes through.
//
// Positions are in Monkey Ball units on the gate (x across, y up, z through, the panel facing +z),
// scaled to metres. The LCD shows the clock in hundredths: three large digits 0.6666 apart, then two
// small ones 0.45 apart, each digit model already sitting in its ones place. A second set, turned half
// a turn, reads from behind on the back panel (the game only has the front one).
using System;
using System.Collections.Generic;
using GTA.Math;
using GTA.Native;
using static MonkeyBallGta.Util;

namespace MonkeyBallGta
{
    class GoalShow
    {
        static readonly string[] Pieces = { "deepgreen", "green", "orange", "red", "yellow" };
        public static readonly string[] Models = MakeModels();  // after Pieces: static fields start in order
        const int PiecesPerColour = 12;
        const float BagY = 2.8f;              // the party ball hangs from here (u_spawn_goal_stobjs)
        const float TapeY = 0.5f, TapeHalf = 0.875f;
        const int TapePoints = 8, TapeBreak = 3;  // the link between points 3 and 4 snaps
        const float OpenFrames = 20f, OpenAngle = 115f;

        readonly float scale;
        int goal;
        Vector3 origin, right, forward;
        readonly int[,] lcd = new int[5, 10], lcdBack = new int[5, 10];
        readonly int[] shown = { -1, -1, -1, -1, -1 };
        int bag, bagA, bagB;
        float openness = -1f;
        readonly int[] tape = new int[TapePoints - 1];
        readonly Vector3[] pts = new Vector3[TapePoints], prev = new Vector3[TapePoints];
        bool broken;
        readonly Random rng = new Random();

        class Piece
        {
            public int Entity;
            public Vector3 Pos, Vel, Axis;
            public float Angle, Spin;
        }
        readonly List<Piece> confetti = new List<Piece>();

        public GoalShow(float scale) { this.scale = scale; }

        static string[] MakeModels()
        {
            var list = new List<string> { "mbgta_bag", "mbgta_bag_a", "mbgta_bag_b", "mbgta_tape" };
            for (int d = 0; d < 10; d++)
            {
                list.Add("mbgta_lcd_l_" + d);
                list.Add("mbgta_lcd_s_" + d);
            }
            foreach (string p in Pieces)
                list.Add("mbgta_paper_" + p);
            return list.ToArray();
        }

        // A point on the gate (Monkey Ball units) in the world.
        Vector3 World(float x, float y, float z) => origin + (right * x - forward * z) * scale + new Vector3(0f, 0f, y * scale);
        // The same, as an offset for ATTACH_ENTITY_TO_ENTITY (the gate's own axes: x right, y forward, z up).
        Vector3 Offset(float x, float y, float z) => new Vector3(x, -z, y) * scale;

        public void Spawn(int goalEntity, Vector3 pos, Vector3 right, Vector3 forward)
        {
            Clear();
            goal = goalEntity;
            origin = pos;
            this.right = right;
            this.forward = forward;
            for (int slot = 0; slot < 5; slot++)
            {
                bool large = slot < 3;
                float dx = large ? -0.6666f * slot : -0.45f * (slot - 3);
                for (int d = 0; d < 10; d++)
                {
                    string model = (large ? "mbgta_lcd_l_" : "mbgta_lcd_s_") + d;
                    int e = Create(model, pos);
                    Attach(e, Offset(dx, 0f, 0f), 0f);
                    Function.Call(Hash.SET_ENTITY_VISIBLE, e, false, false);
                    lcd[slot, d] = e;
                    e = Create(model, pos);
                    Attach(e, Offset(-dx, 0f, 0f), 0f, 180f);  // the back of the goal is the front turned (build_gta.py GOAL)
                    Function.Call(Hash.SET_ENTITY_VISIBLE, e, false, false);
                    lcdBack[slot, d] = e;
                }
                shown[slot] = -1;
            }
            bag = Create("mbgta_bag", pos);
            Attach(bag, Offset(0f, BagY, 0f), 0f);
            openness = -1f;
            for (int i = 0; i < TapePoints; i++)
                pts[i] = prev[i] = World(-TapeHalf + 2f * TapeHalf * i / (TapePoints - 1), TapeY, 0f);
            for (int i = 0; i < tape.Length; i++)
                tape[i] = Create("mbgta_tape", pos);
            broken = false;
            DrawTape();
        }

        public void Clear()
        {
            foreach (int e in lcd)
                DeleteObject(e);
            foreach (int e in lcdBack)
                DeleteObject(e);
            Array.Clear(lcd, 0, lcd.Length);
            Array.Clear(lcdBack, 0, lcdBack.Length);
            foreach (int e in tape)
                DeleteObject(e);
            Array.Clear(tape, 0, tape.Length);
            DeleteObject(bag);
            DeleteObject(bagA);
            DeleteObject(bagB);
            bag = bagA = bagB = 0;
            foreach (Piece p in confetti)
                DeleteObject(p.Entity);
            confetti.Clear();
            goal = 0;
        }

        int Create(string model, Vector3 at)
        {
            int e = Function.Call<int>(Hash.CREATE_OBJECT, Joaat(model), at.X, at.Y, at.Z, false, false, false);
            if (e == 0)
                return 0;
            Function.Call(Hash.FREEZE_ENTITY_POSITION, e, true);
            Function.Call(Hash.SET_ENTITY_COLLISION, e, false, false);
            return e;
        }

        // Fixed to the gate; yRot turns it about the gate's forward axis (the party ball's halves swing that
        // way), zRot about its up axis.
        void Attach(int e, Vector3 offset, float yRot, float zRot = 0f)
        {
            if (e != 0 && goal != 0)
                Function.Call(Hash.ATTACH_ENTITY_TO_ENTITY, e, goal, 0, offset.X, offset.Y, offset.Z, 0f, yRot, zRot, false, false, false, false, 2, true);
        }

        // The ball went through: the tape snaps and the party ball opens.
        public void Burst(Vector3 ballVel)
        {
            if (goal == 0)
                return;
            broken = true;
            for (int i = 0; i < TapePoints; i++)
                prev[i] = pts[i] - ballVel * (0.6f / 60f);  // the tape is dragged along with the ball
            DeleteObject(tape[TapeBreak]);
            tape[TapeBreak] = 0;

            DeleteObject(bag);
            bag = 0;
            Vector3 at = World(0f, BagY, 0f);
            bagA = Create("mbgta_bag_a", at);
            bagB = Create("mbgta_bag_b", at);
            openness = 0f;
            Swing();
            Vector3 burst = World(0f, BagY - 0.75f, 0f);
            for (int c = 0; c < Pieces.Length; c++)
            {
                for (int k = 0; k < PiecesPerColour; k++)
                {
                    int e = Create("mbgta_paper_" + Pieces[c], burst);
                    if (e == 0)
                        continue;
                    double a = rng.NextDouble() * Math.PI * 2.0;
                    float out_ = 1.5f + (float)rng.NextDouble() * 4f, up = 1f + (float)rng.NextDouble() * 5f;
                    var axis = new Vector3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f);
                    confetti.Add(new Piece
                    {
                        Entity = e,
                        Pos = burst,
                        Vel = new Vector3((float)Math.Cos(a) * out_, (float)Math.Sin(a) * out_, up) + ballVel * 0.2f,
                        Axis = axis.Length() > 1e-3f ? axis.Normalized : Vector3.WorldUp,
                        Angle = (float)(rng.NextDouble() * Math.PI * 2.0),
                        Spin = 4f + (float)rng.NextDouble() * 10f,
                    });
                }
            }
        }

        void Swing()
        {
            float t = Math.Min(1f, openness / OpenFrames);
            float angle = OpenAngle * (1f - (1f - t) * (1f - t));
            Function.Call(Hash.DETACH_ENTITY, bagA, false, false);
            Function.Call(Hash.DETACH_ENTITY, bagB, false, false);
            Attach(bagA, Offset(0f, BagY, 0f), angle);
            Attach(bagB, Offset(0f, BagY, 0f), -angle);
        }

        // Once a frame: the clock on the panel, and whatever is flying about.
        public void Update(int timeFrames, Vector3 ballPos, float ballRadius, float dt)
        {
            if (goal == 0)
                return;
            int time = Math.Max(0, timeFrames) * 100 / 60;
            int[] digits = { time / 100 % 10, time / 1000 % 10, time / 10000 % 10, time % 10, time / 10 % 10 };
            for (int slot = 0; slot < 5; slot++)
            {
                if (digits[slot] == shown[slot])
                    continue;
                if (shown[slot] >= 0)
                {
                    Function.Call(Hash.SET_ENTITY_VISIBLE, lcd[slot, shown[slot]], false, false);
                    Function.Call(Hash.SET_ENTITY_VISIBLE, lcdBack[slot, shown[slot]], false, false);
                }
                Function.Call(Hash.SET_ENTITY_VISIBLE, lcd[slot, digits[slot]], true, false);
                Function.Call(Hash.SET_ENTITY_VISIBLE, lcdBack[slot, digits[slot]], true, false);
                shown[slot] = digits[slot];
            }

            if (openness >= 0f && openness < OpenFrames)
            {
                openness += dt * 60f;
                Swing();
            }
            if (broken)
                StepTape(ballPos, ballRadius, dt);
            DrawTape();
            foreach (Piece p in confetti)
            {
                // Paper flutters: strong air drag, light gravity.
                p.Vel *= (float)Math.Pow(0.2, dt);
                p.Vel.Z -= 4f * dt;
                p.Pos += p.Vel * dt;
                p.Angle += p.Spin * dt;
                Quaternion q = Quaternion.RotationAxis(p.Axis, p.Angle);
                Function.Call(Hash.SET_ENTITY_COORDS_NO_OFFSET, p.Entity, p.Pos.X, p.Pos.Y, p.Pos.Z, false, false, false);
                Function.Call(Hash.SET_ENTITY_QUATERNION, p.Entity, q.X, q.Y, q.Z, q.W);
            }
        }

        // The two loose ends hang from the posts: Verlet points, pinned at the ends, kept apart by their
        // links, pushed out of the ball.
        void StepTape(Vector3 ballPos, float ballRadius, float dt)
        {
            float link = 2f * TapeHalf / (TapePoints - 1) * scale;
            var g = new Vector3(0f, 0f, -9.8f * dt * dt);
            for (int i = 1; i < TapePoints - 1; i++)
            {
                Vector3 p = pts[i];
                pts[i] += (pts[i] - prev[i]) * 0.97f + g;
                prev[i] = p;
                Vector3 d = pts[i] - ballPos;
                if (d.Length() < ballRadius && d.Length() > 1e-4f)
                    pts[i] = ballPos + d.Normalized * ballRadius;
            }
            for (int iter = 0; iter < 4; iter++)
            {
                for (int i = 0; i < TapePoints - 1; i++)
                {
                    if (i == TapeBreak)
                        continue;
                    Vector3 d = pts[i + 1] - pts[i];
                    float len = d.Length();
                    if (len < 1e-5f)
                        continue;
                    Vector3 fix = d * ((len - link) / len);
                    bool pinA = i == 0, pinB = i + 1 == TapePoints - 1;
                    if (pinA && !pinB)
                        pts[i + 1] -= fix;
                    else if (pinB && !pinA)
                        pts[i] += fix;
                    else if (!pinA)
                    {
                        pts[i] += fix * 0.5f;
                        pts[i + 1] -= fix * 0.5f;
                    }
                }
            }
        }

        // Each piece sits between two points, its length along the link and its face upright.
        void DrawTape()
        {
            for (int i = 0; i < tape.Length; i++)
            {
                if (tape[i] == 0)
                    continue;
                Vector3 a = pts[i], b = pts[i + 1];
                Vector3 x = b - a;
                if (x.Length() < 1e-5f)
                    continue;
                x.Normalize();
                Vector3 z = Vector3.WorldUp - x * Vector3.Dot(Vector3.WorldUp, x);
                z = z.Length() > 1e-3f ? z.Normalized : Vector3.Cross(x, right).Normalized;
                Vector3 y = Vector3.Cross(z, x);
                Quaternion q = FromBasis(x, y, z);
                Vector3 m = (a + b) * 0.5f;
                Function.Call(Hash.SET_ENTITY_COORDS_NO_OFFSET, tape[i], m.X, m.Y, m.Z, false, false, false);
                Function.Call(Hash.SET_ENTITY_QUATERNION, tape[i], q.X, q.Y, q.Z, q.W);
            }
        }

        // The rotation whose columns are x, y, z.
        static Quaternion FromBasis(Vector3 x, Vector3 y, Vector3 z)
        {
            float tr = x.X + y.Y + z.Z, qw, qx, qy, qz;
            if (tr > 0f)
            {
                float s = (float)Math.Sqrt(tr + 1f) * 2f;
                qw = s / 4f; qx = (y.Z - z.Y) / s; qy = (z.X - x.Z) / s; qz = (x.Y - y.X) / s;
            }
            else if (x.X > y.Y && x.X > z.Z)
            {
                float s = (float)Math.Sqrt(1f + x.X - y.Y - z.Z) * 2f;
                qw = (y.Z - z.Y) / s; qx = s / 4f; qy = (y.X + x.Y) / s; qz = (z.X + x.Z) / s;
            }
            else if (y.Y > z.Z)
            {
                float s = (float)Math.Sqrt(1f + y.Y - x.X - z.Z) * 2f;
                qw = (z.X - x.Z) / s; qx = (y.X + x.Y) / s; qy = s / 4f; qz = (z.Y + y.Z) / s;
            }
            else
            {
                float s = (float)Math.Sqrt(1f + z.Z - x.X - y.Y) * 2f;
                qw = (x.Y - y.X) / s; qx = (z.X + x.Z) / s; qy = (z.Y + y.Z) / s; qz = s / 4f;
            }
            return new Quaternion(qx, qy, qz, qw);
        }
    }
}
