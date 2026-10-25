// AiAi inside the ball (the mbgta_aiai prop and its mbgta_aiai clips, made by gta/aiai.py).
//
// How the monkey stands and turns follows ball.c (smb-decomp) step for step:
//  - func_8003699C: the monkey is dragged round by part of the ball's roll each frame (the roll's
//    quaternion with x, y, z times 0.65 and w over 0.65), then tipped back toward upright by up to
//    5 degrees a frame, unless the ball is spinning so fast it tumbles with it.
//  - func_80036CAC: when moving it turns to face the way the ball is going, up to 4 degrees a frame.
//    Its forward is -x in this frame.
//  - func_8003765C: it's drawn 0.12 units below the ball's centre, along its own up.
// The animation (u_choose_ape_anim, u_set_ape_anim) is picked by speed: the ball's speed times 1.5
// times 216 against each move motion's threshold from aiai_common.mal, played at that motion's rate.
// Standing still it holds KAMAE; falling with no ground below it flails (BATA); the goal and time
// over play a win or lose motion.
using System;
using System.Runtime.InteropServices;
using GTA.Math;
using GTA.Native;
using static MonkeyBallGta.Util;

namespace MonkeyBallGta
{
    class Monkey
    {
        const string Model = "mbgta_aiai";
        const string Dict = "mbgta_aiai";
        const float Offset = -0.12f;          // units along its up, from the ball's centre
        const float StillSpeed = 0.00027777777f;
        const float FallSpeed = -0.16203702986f;
        const float SpeedScale = 1.5f * 216f;
        const float Blend = 8f;               // PLAY_ENTITY_ANIM blend-in; higher is quicker
        static readonly short Upright = 0x38E, Turn = 0x2D8;

        // aiai_common.mal move1..move13: the speed each starts below, its motion and its playback rate.
        static readonly float[] MoveBelow = { 0f, 1f, 2.5f, 4f, 5.5f, 7f, 8.5f, 10f, 13f, 16f, 30f, 65f, 177f };
        static readonly string[] MoveClip = { "a", "a", "a", "b", "b", "b", "c", "c", "d", "d", "e", "f", "g" };
        static readonly float[] MoveRate = { 0.96f, 1.152f, 1.44f, 0.869f, 0.952f, 1.053f, 0.706f, 0.923f, 0.889f, 1.143f, 0.923f, 0.869f, 1f };
        static readonly string[] Wins = { "ape_win_02_mb2", "ape_win_03_mb2", "ape_win_04_mb2", "ape_win_05_mb2", "ape_win_06_mb2" };
        static readonly string[] Loses = { "ape_lose_01_mb2", "ape_lose_02_mb2" };

        public enum End { None, Goal, TimeOver, FallOut }

        readonly Random rng = new Random();
        int entity, dictWait;
        bool dictLoaded, dictFailed;
        Quaternion frame = Quaternion.Identity;  // Monkey Ball space; forward -x, up +y
        string clip;
        int checkAt;
        float speed;
        bool still, falling;
        End end;
        string endClip;

        public bool Ready => !Function.Call<bool>(Hash.IS_MODEL_IN_CDIMAGE, Joaat(Model)) || Function.Call<bool>(Hash.HAS_MODEL_LOADED, Joaat(Model));

        public void Request()
        {
            uint hash = Joaat(Model);
            if (Function.Call<bool>(Hash.IS_MODEL_IN_CDIMAGE, hash))
                Function.Call(Hash.REQUEST_MODEL, hash);
            else
                Log($"monkey: {Model} isn't installed");
            WithDict(d => Function.Call(Hash.REQUEST_ANIM_DICT, d));
            dictWait = 0;
            dictLoaded = dictFailed = false;
        }

        // Make the prop, facing the way of the given Monkey Ball yaw (the camera's).
        public void Spawn(Vector3 smbPos, short yaw, float scale)
        {
            Remove();
            uint hash = Joaat(Model);
            if (!Function.Call<bool>(Hash.HAS_MODEL_LOADED, hash))
                return;
            Vector3 p = ToGta(smbPos) * scale;
            entity = Function.Call<int>(Hash.CREATE_OBJECT, hash, p.X, p.Y, p.Z, false, false, false);
            if (entity == 0)
            {
                Log("monkey: CREATE_OBJECT returned 0");
                return;
            }
            Function.Call(Hash.FREEZE_ENTITY_POSITION, entity, true);
            Function.Call(Hash.SET_ENTITY_COLLISION, entity, false, false);
            Reset(yaw);
        }

        // A new try: stand up, face along yaw, forget the last level's ending.
        public void Reset(short yaw)
        {
            // The camera looks along RotY(0, 0, -1); turn the -x forward that way.
            frame = Axis(new Vector3(0f, 1f, 0f), SmbMath.Rad(yaw) - (float)Math.PI / 2f);
            end = End.None;
            clip = null;
        }

        public void Remove()
        {
            DeleteObject(entity);
            entity = 0;
            clip = null;
        }

        public void Visible(bool on)
        {
            if (entity != 0)
                Function.Call(Hash.SET_ENTITY_VISIBLE, entity, on, false);
        }

        public void Finish(End how)
        {
            end = how;
            endClip = how == End.Goal ? Wins[rng.Next(Wins.Length)] : how == End.TimeOver ? Loses[rng.Next(Loses.Length)] : "ape_bata";
        }

        // Once a frame before the physics steps: is there ground below? (raycast_stage_down)
        public void Look(SmbBall ball, float scale, int ignore)
        {
            Vector3 p = ToGta(ball.Pos) * scale;
            falling = ball.Vel.Y < FallSpeed && !Raycast.Cast(p, p - new Vector3(0f, 0f, 300f), ignore, out _, 1);
        }

        // Once per physics step, after the ball has moved and collided.
        public void Step(SmbBall ball)
        {
            var up = new Vector3(0f, 1f, 0f);
            Vector3 moved = ball.Pos - ball.PrevPos;
            moved.Y = 0f;
            float dist = moved.Length();
            float w = 1f;
            if (dist > 1e-6f)
            {
                Quaternion roll = Axis(Vector3.Cross(up, moved / dist), dist / SmbBall.Radius);
                roll = new Quaternion(roll.X * 0.65f, roll.Y * 0.65f, roll.Z * 0.65f, roll.W / 0.65f);
                roll.Normalize();
                w = roll.W;
                frame = Mul(roll, frame);
                frame.Normalize();
            }
            if (w >= 0.9941f)
            {
                Vector3 localUp = Rotate(Conj(frame), up);
                float off = 1f - Vector3.Dot(localUp, up);
                if (off > 0.01f)
                {
                    Vector3 axis = off > 1.999f ? new Vector3(1f, 0f, 0f) : Vector3.Cross(up, localUp);
                    frame = Mul(frame, Axis(axis.Normalized, SmbMath.Rad(Upright)));
                }
                else
                {
                    frame = Mul(frame, FromDirs(up, localUp));
                }
                frame.Normalize();
            }

            Vector3 v = ball.Vel;
            v.Y = 0f;
            still = v.Length() < StillSpeed;
            speed = 0f;
            if (end == End.Goal || (!still && !falling))
            {
                if (!still)
                {
                    Vector3 local = Rotate(Conj(frame), v.Normalized);
                    var back = new Vector3(-1f, 0f, 0f);
                    Quaternion turn;
                    if (local.X > -0.992f)
                    {
                        Vector3 axis = 1f - local.X > 1e-8f ? Vector3.Cross(back, local) : up;
                        turn = axis.Length() > 1e-6f ? Axis(axis.Normalized, SmbMath.Rad(Turn)) : Quaternion.Identity;
                    }
                    else
                    {
                        turn = FromDirs(back, local);
                    }
                    frame = Mul(frame, turn);
                    frame.Normalize();
                }
                speed = ball.Vel.Length() * SpeedScale;
            }
        }

        // Once a frame: put the prop where the monkey is and keep its animation right.
        public void Draw(SmbBall ball, float scale)
        {
            if (entity == 0 || !Function.Call<bool>(Hash.DOES_ENTITY_EXIST, entity))
                return;
            Vector3 p = ToGta(ball.Pos + Rotate(frame, new Vector3(0f, Offset, 0f))) * scale;
            // The model faces -z; the frame's forward is -x: a quarter turn about y between them.
            Quaternion q = Mul(frame, Axis(new Vector3(0f, 1f, 0f), (float)Math.PI / 2f));
            Vector3 qv = ToGta(new Vector3(q.X, q.Y, q.Z));
            Function.Call(Hash.SET_ENTITY_COORDS_NO_OFFSET, entity, p.X, p.Y, p.Z, false, false, false);
            Function.Call(Hash.SET_ENTITY_QUATERNION, entity, qv.X, qv.Y, qv.Z, q.W);
            Animate();
        }

        void Animate()
        {
            if (!dictLoaded)
            {
                if (dictFailed)
                    return;
                bool loaded = false;
                WithDict(d => loaded = Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, d));
                if (!loaded)
                {
                    if (++dictWait == 600)
                    {
                        dictFailed = true;
                        Log("monkey: the mbgta_aiai clip dictionary never loaded");
                    }
                    return;
                }
                dictLoaded = true;
                Log("monkey: clips loaded");
            }

            string want;
            float rate = 1f;
            bool loop = true;
            if (end != End.None)
            {
                want = endClip;  // the win and lose motions are cycles in the .mta too: loop them
            }
            else if (falling)
                want = "ape_bata";
            else if (still)
                want = "ape_kamae";
            else
            {
                // u_set_ape_anim: the first motion whose threshold is above the speed, else the last.
                int i = 0;
                while (i < MoveBelow.Length - 1 && MoveBelow[i] <= speed)
                    i++;
                want = "ape_move_" + MoveClip[i];
                rate = MoveRate[i];
            }

            // Only start a clip when it changes: restarting one every frame would hold it on its first frame.
            // One that has stopped anyway (the game dropped it) is started again, or AiAi T-poses.
            if (want != clip || (++checkAt % 15 == 0 && !Playing(want)))
            {
                // From one walk or run into the next, keep the stride where it was.
                float phase = 0f;
                if (clip != null && clip.StartsWith("ape_move_") && want.StartsWith("ape_move_"))
                    phase = Phase(clip);
                Play(want, loop, phase);
                clip = want;
            }
            WithNames(clip, (d, a) => Function.Call(Hash.SET_ENTITY_ANIM_SPEED, entity, d, a, rate));
        }

        bool Playing(string anim)
        {
            bool r = false;
            WithNames(anim, (d, a) => r = Function.Call<bool>(Hash.IS_ENTITY_PLAYING_ANIM, entity, d, a, 3));
            return r;
        }

        float Phase(string anim)
        {
            float r = 0f;
            WithNames(anim, (d, a) => r = Function.Call<float>(Hash.GET_ENTITY_ANIM_CURRENT_TIME, entity, d, a));
            return r;
        }

        void Play(string anim, bool loop, float phase)
        {
            bool ok = false;
            // PLAY_ENTITY_ANIM(entity, anim, dict, blend, loop, stay in the last frame, ?, start phase, flags)
            WithNames(anim, (d, a) => ok = Function.Call<bool>(Hash.PLAY_ENTITY_ANIM, entity, a, d, Blend, loop, true, false, phase, 0));
            if (!ok && clip != anim)
                Log($"monkey: PLAY_ENTITY_ANIM {anim} failed");
        }

        static void WithDict(Action<IntPtr> f)
        {
            IntPtr d = Utf8(Dict);
            try { f(d); }
            finally { Marshal.FreeHGlobal(d); }
        }

        static void WithNames(string anim, Action<IntPtr, IntPtr> f)
        {
            IntPtr d = Utf8(Dict), a = Utf8(anim);
            try { f(d, a); }
            finally
            {
                Marshal.FreeHGlobal(d);
                Marshal.FreeHGlobal(a);
            }
        }

        static Vector3 ToGta(Vector3 v) => new Vector3(v.X, -v.Z, v.Y);

        // Quaternion helpers, (x, y, z, w). Mul(a, b) rotates by b first, then a (GTA.Math's own * is
        // left to its SlimDX convention, so it isn't used here).
        static Quaternion Mul(Quaternion a, Quaternion b) => new Quaternion(
            a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
            a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
            a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
            a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

        static Quaternion Axis(Vector3 axis, float angle)
        {
            float s = (float)Math.Sin(angle / 2f);
            return new Quaternion(axis.X * s, axis.Y * s, axis.Z * s, (float)Math.Cos(angle / 2f));
        }

        static Quaternion Conj(Quaternion q) => new Quaternion(-q.X, -q.Y, -q.Z, q.W);

        static Vector3 Rotate(Quaternion q, Vector3 v)
        {
            var u = new Vector3(q.X, q.Y, q.Z);
            Vector3 t = Vector3.Cross(u, v) * 2f;
            return v + t * q.W + Vector3.Cross(u, t);
        }

        // The rotation taking direction a onto direction b.
        static Quaternion FromDirs(Vector3 a, Vector3 b)
        {
            Vector3 c = Vector3.Cross(a, b);
            float d = Vector3.Dot(a, b);
            if (d < -0.9999f)
            {
                Vector3 axis = Math.Abs(a.X) < 0.9f ? Vector3.Cross(a, new Vector3(1f, 0f, 0f)) : Vector3.Cross(a, new Vector3(0f, 1f, 0f));
                return Axis(axis.Normalized, (float)Math.PI);
            }
            var q = new Quaternion(c.X, c.Y, c.Z, 1f + d);
            q.Normalize();
            return q;
        }
    }
}
