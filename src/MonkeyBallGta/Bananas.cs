// Bananas along the road to the goal (item_coin.c): a single every so often on the GPS route, and a
// bunch now and then. Each spins in place; rolling into one collects it, for 1 banana and 100 points
// (a bunch: 10 and 1000), with the game's pickup sound. A retry puts them all back.
using System;
using System.Collections.Generic;
using GTA.Math;
using GTA.Native;
using static MonkeyBallGta.Util;

namespace MonkeyBallGta
{
    class Bananas
    {
        public const string Single = "mbgta_banana", Bunch = "mbgta_bunch";
        public static readonly string[] Models = { Single, Bunch };
        const float Spacing = 110f, FromEnds = 60f;   // metres along the route
        const int BunchEvery = 5, Max = 30;
        // s_bananaInfos: collect radius (units) and spin (16-bit angle a frame).
        const float SingleRadius = 0.5f, BunchRadius = 0.75f;
        const short SingleSpin = 1024, BunchSpin = 768;

        struct Item
        {
            public Vector3 Pos;
            public bool IsBunch;
            public int Entity;
            public short Rot;
        }

        readonly List<Item> items = new List<Item>();
        readonly float scale;
        public bool Placed => items.Count > 0;

        public Bananas(float scale) { this.scale = scale; }

        // Lay them along the route (GTA positions on the road, start to goal).
        public void Place(List<Vector3> route)
        {
            Clear();
            if (route.Count < 2)
                return;
            float along = 0f, next = FromEnds, total = 0f;
            for (int i = 1; i < route.Count; i++)
                total += (route[i] - route[i - 1]).Length();
            for (int i = 1; i < route.Count && items.Count < Max; i++)
            {
                Vector3 a = route[i - 1], b = route[i];
                float len = (b - a).Length();
                while (next <= along + len && next <= total - FromEnds && items.Count < Max)
                {
                    Vector3 p = a + (b - a) * ((next - along) / Math.Max(len, 1e-3f));
                    bool bunch = items.Count % BunchEvery == BunchEvery - 1;
                    // Bananas float at about the ball's centre height.
                    items.Add(new Item { Pos = p + new Vector3(0f, 0f, (bunch ? BunchRadius : SingleRadius) * scale + 0.3f), IsBunch = bunch });
                    next += Spacing;
                }
                along += len;
            }
            Respawn();
        }

        // Every banana back for a new try.
        public void Respawn()
        {
            for (int i = 0; i < items.Count; i++)
            {
                Item it = items[i];
                DeleteObject(it.Entity);
                uint hash = Joaat(it.IsBunch ? Bunch : Single);
                it.Entity = Function.Call<int>(Hash.CREATE_OBJECT, hash, it.Pos.X, it.Pos.Y, it.Pos.Z, false, false, false);
                if (it.Entity != 0)
                {
                    Function.Call(Hash.FREEZE_ENTITY_POSITION, it.Entity, true);
                    Function.Call(Hash.SET_ENTITY_COLLISION, it.Entity, false, false);
                }
                items[i] = it;
            }
        }

        public void Clear()
        {
            foreach (Item it in items)
                DeleteObject(it.Entity);
            items.Clear();
        }

        // Once a step: spin them, and collect any the ball touches. Returns (bananas, points) collected.
        public (int bananas, int points) Step(Vector3 ball, float ballRadius, Audio audio)
        {
            int got = 0, points = 0;
            for (int i = 0; i < items.Count; i++)
            {
                Item it = items[i];
                if (it.Entity == 0)
                    continue;
                it.Rot += it.IsBunch ? BunchSpin : SingleSpin;
                float reach = ballRadius + (it.IsBunch ? BunchRadius : SingleRadius) * scale;
                if ((it.Pos - ball).Length() < reach)
                {
                    DeleteObject(it.Entity);
                    it.Entity = 0;
                    got += it.IsBunch ? 10 : 1;
                    points += it.IsBunch ? 1000 : 100;
                    if (it.IsBunch)
                    {
                        audio.Play("banana_bunch");
                        audio.Play("monkey_banana_bunch");
                    }
                    else
                        audio.Play("banana");
                }
                items[i] = it;
            }
            return (got, points);
        }

        public void Draw()
        {
            foreach (Item it in items)
            {
                if (it.Entity == 0)
                    continue;
                Function.Call(Hash.SET_ENTITY_ROTATION, it.Entity, 0f, 0f, it.Rot * 360f / 65536f, 2, true);
            }
        }
    }
}
