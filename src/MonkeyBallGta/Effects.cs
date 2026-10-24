// The ball's effects, from effect.c and ball.c (smb-decomp):
//  - a hard hit (ball.c func_8003C550, when the hit is harder than 0.11 units a frame): a flash on
//    the surface (ET_COLIFLASH), gold stars (ET_COLISTAR_PARTICLE, the CRASH_STAR model) and sparks
//    (ET_COLI_PARTICLE, short bright lines), as many as the hit was hard, thrown off the surface;
//  - the goal (init_physball_from_ball, ET_LEVITATE): a sparkle (CROSS_LIGHT) a frame around the ball
//    that drifts down to the ground under it and shrinks away there.
// Everything moves in Monkey Ball units once a physics step. Stars are mbgta_star props; the rest are
// sprites drawn over the picture by Hud.
using System;
using System.Collections.Generic;
using GTA.Math;
using GTA.Native;
using static MonkeyBallGta.Util;

namespace MonkeyBallGta
{
    class Effects
    {
        public const string StarModel = "mbgta_star";
        const int MaxStars = 16;

        class Particle
        {
            public Vector3 Pos, Prev, Vel, Normal;
            public float Floor;          // the surface they bounce on: height along Normal of the hit's plane
            public int Timer, Age;
            public float Scale, R = 0.9f, G = 0.8f, B = 0.4f;
            public short RotX, RotY, RotZ, SpinX, SpinY, SpinZ;
            public int Prop;
        }

        readonly List<Particle> sparks = new List<Particle>(), stars = new List<Particle>(), sparkles = new List<Particle>();
        readonly List<(Vector3 pos, float size, float grow, int timer, float alpha)> flashes = new List<(Vector3, float, float, int, float)>();
        readonly Stack<int> freeStars = new Stack<int>();
        readonly List<int> allStars = new List<int>();
        readonly Random rng = new Random();
        readonly float scale;

        public Effects(float scale) { this.scale = scale; }

        float Rand() => (float)rng.NextDouble();
        Vector3 Rand3(float half) => new Vector3(Rand() * 2f * half - half, Rand() * 2f * half - half, Rand() * 2f * half - half);

        public void Spawn()
        {
            Remove();
            uint hash = Joaat(StarModel);
            if (!Function.Call<bool>(Hash.HAS_MODEL_LOADED, hash))
                return;
            for (int i = 0; i < MaxStars; i++)
            {
                int e = Function.Call<int>(Hash.CREATE_OBJECT, hash, 0f, 0f, -200f, false, false, false);
                if (e == 0)
                    break;
                Function.Call(Hash.FREEZE_ENTITY_POSITION, e, true);
                Function.Call(Hash.SET_ENTITY_COLLISION, e, false, false);
                Function.Call(Hash.SET_ENTITY_VISIBLE, e, false, false);
                allStars.Add(e);
                freeStars.Push(e);
            }
        }

        public void Remove()
        {
            foreach (int e in allStars)
                DeleteObject(e);
            allStars.Clear();
            freeStars.Clear();
            Clear();
        }

        public void Clear()
        {
            foreach (Particle s in stars)
                Release(s);
            sparks.Clear();
            stars.Clear();
            sparkles.Clear();
            flashes.Clear();
        }

        void Release(Particle s)
        {
            if (s.Prop == 0)
                return;
            Function.Call(Hash.SET_ENTITY_VISIBLE, s.Prop, false, false);
            freeStars.Push(s.Prop);
            s.Prop = 0;
        }

        // ball.c func_8003C550. ballPos and vel in units (and units a frame); normal points out of the
        // surface toward the ball; into is the hit's speed into it (negative).
        public void Hit(Vector3 ballPos, Vector3 vel, Vector3 normal, float into)
        {
            Vector3 at = ballPos - normal * SmbBall.Radius;
            Vector3 slide = vel * 0.5f;
            slide -= normal * Vector3.Dot(normal, slide);
            int count = Math.Min(32, (int)Math.Abs(into / 0.0165f));
            float kick = Math.Abs(into / 0.33f) + 1f;
            float floor = Vector3.Dot(at, normal);

            float full = (float)Math.Sqrt(Math.Abs(10f * into));
            flashes.Add((at + normal * 0.02f, full * 0.25f, full, 12, 1f));

            for (int i = count >> 1; i > 0; i--)
            {
                var p = new Particle { Pos = at, Prev = at, Normal = normal, Floor = floor, Timer = 15 + (int)(45 * Rand()) };
                p.Vel = slide + Rand3(0.025f) * kick + normal * (kick * (Rand() * 0.055f + 0.015f));
                p.RotY = (short)rng.Next(0x8000);
                p.RotZ = (short)rng.Next(0x8000);
                p.SpinY = (short)(rng.Next(0x1000) + 0x1000);
                if (freeStars.Count > 0)
                {
                    p.Prop = freeStars.Pop();
                    Function.Call(Hash.SET_ENTITY_VISIBLE, p.Prop, true, false);
                }
                stars.Add(p);
            }
            for (int i = count - (count >> 1); i > 0; i--)
            {
                var p = new Particle { Pos = at, Prev = at, Normal = normal, Floor = floor, Timer = 15 + (int)(45 * Rand()) };
                p.Vel = slide * 0.5f + Rand3(0.025f) * kick + normal * (kick * (Rand() * 0.05f + 0.06f));
                sparks.Add(p);
            }
        }

        // ET_LEVITATE, once a step through the goal. floorY: the ground's height under the ball.
        public void Sparkle(Vector3 ballPos, float floorY)
        {
            var p = new Particle { Pos = ballPos + Rand3(0.375f), Timer = 60, Scale = 0.25f, Floor = floorY };
            p.Vel = new Vector3(0f, 0.0040833335f, 0f);
            sparkles.Add(p);
        }

        public void Step()
        {
            for (int i = flashes.Count - 1; i >= 0; i--)
            {
                var f = flashes[i];
                f.size += 0.1f * (f.grow - f.size);
                f.timer--;
                if (f.timer < 7.5f && f.timer > 0)
                    f.alpha *= (f.timer - 1f) / f.timer;
                if (f.timer <= 0)
                    flashes.RemoveAt(i);
                else
                    flashes[i] = f;
            }
            StepFalling(sparks, 0.008f, 0.992f, 0.025f);
            StepFalling(stars, 0.004f, 0.985f, 8.090365f * 0.015f * 0.5f);
            foreach (Particle s in stars)
            {
                s.SpinX -= (short)(s.SpinX >> 2);
                s.SpinY -= (short)(s.SpinY >> 5);
                s.SpinZ -= (short)(s.SpinZ >> 3);
                s.RotX += s.SpinX;
                s.RotY += s.SpinY;
                s.RotZ += s.SpinZ;
            }
            for (int i = sparkles.Count - 1; i >= 0; i--)
            {
                Particle p = sparkles[i];
                p.Vel.Y -= 0.0008166666f;
                p.Pos.Y += p.Vel.Y;
                bool gone = --p.Timer <= 0;
                if (p.Pos.Y < p.Floor + 0.5f * p.Scale)
                {
                    p.Pos.Y = p.Floor + 0.5f * p.Scale;
                    if (p.Scale > 0.05f)
                        p.Scale -= 0.05f;
                    else
                        gone = true;
                }
                if (gone)
                    sparkles.RemoveAt(i);
            }
        }

        // Sparks and stars: gravity, drag, a bounce off the plane they came from, dimming as they go.
        void StepFalling(List<Particle> list, float gravity, float drag, float rest)
        {
            for (int i = list.Count - 1; i >= 0; i--)
            {
                Particle p = list[i];
                p.Prev = p.Pos;
                p.Vel.Y -= gravity;
                p.Vel *= drag;
                p.Pos += p.Vel;
                p.Age++;
                float above = Vector3.Dot(p.Pos, p.Normal) - p.Floor;
                if (above < rest)
                {
                    p.Pos += p.Normal * (rest - above);
                    float into = Vector3.Dot(p.Vel, p.Normal);
                    if (into < 0f)
                        p.Vel -= p.Normal * (1.9f * into);
                }
                if (p.Timer < 24)
                {
                    p.R *= 0.96f;
                    p.G *= 0.87f;
                    p.B *= 0.86f;
                }
                if (--p.Timer <= 0)
                {
                    Release(p);
                    list.RemoveAt(i);
                }
            }
        }

        // Once a frame, after the camera is placed.
        public void Draw(Hud hud)
        {
            foreach (var f in flashes)
            {
                Vector3 w = World(f.pos);
                hud.Billboard("fx_flash", w, f.size * 2f * scale, 0f, 255, 255, 255, (int)(200 * f.alpha));
                hud.Billboard("fx_circle", w, f.size * 1.2f * scale, 0f, 255, 255, 255, (int)(160 * f.alpha));
            }
            foreach (Particle p in sparks)
            {
                if (p.Age < 1)
                    continue;
                float fade = p.Timer < 24 ? p.Timer / 24f : 1f;
                hud.Streak(World(p.Pos), World(p.Pos - p.Vel * 1.5f), 0.035f * scale, (int)(255 * p.R), (int)(255 * p.G), (int)(255 * p.B), (int)(255 * fade));
            }
            foreach (Particle p in stars)
            {
                if (p.Prop == 0)
                    continue;
                Vector3 w = World(p.Pos);
                Function.Call(Hash.SET_ENTITY_COORDS_NO_OFFSET, p.Prop, w.X, w.Y, w.Z, false, false, false);
                Function.Call(Hash.SET_ENTITY_ROTATION, p.Prop, p.RotX * 360f / 65536f, p.RotZ * 360f / 65536f, p.RotY * 360f / 65536f, 2, true);
            }
            foreach (Particle p in sparkles)
            {
                float size = p.Scale * (p.Timer < 10 ? p.Timer / 10f : 1f);
                int alpha = p.Timer >= 56 ? (int)(255 * (60 - p.Timer) / 4f) : 255;
                hud.Billboard("fx_cross", World(p.Pos), size * scale, 0f, 255, 255, 255, alpha);
            }
        }

        Vector3 World(Vector3 smb) => new Vector3(smb.X, -smb.Z, smb.Y) * scale;
    }
}
