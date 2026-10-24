// Shape tests against the world, cars, people and props: synchronous rays, and swept spheres whose
// result arrives on a later frame.
// GET_SHAPE_TEST_RESULT writes through pointers; we give it our own buffers (see Util.cs for why).
using System;
using System.Runtime.InteropServices;
using GTA.Math;
using GTA.Native;

namespace MonkeyBallGta
{
    struct RayHit
    {
        public Vector3 Point, Normal;
        public int Entity;    // 0 for the map itself
        public float Distance;
    }

    static class Raycast
    {
        // Shape test flags: 1 map, 2 vehicles, 8 peds, 16 objects, 256 vegetation (left out: rolls through bushes).
        public const int World = 1 | 2 | 8 | 16;
        public const int Vegetation = 256;
        const int Options = 4;  // ignore only surfaces marked no-collision (fences and sign boards count)

        // Native Vector3 out-values are three floats, each padded to 8 bytes.
        static readonly IntPtr hitBuf = Marshal.AllocHGlobal(8), endBuf = Marshal.AllocHGlobal(24),
            normalBuf = Marshal.AllocHGlobal(24), entityBuf = Marshal.AllocHGlobal(8);

        public static bool Cast(Vector3 from, Vector3 to, int ignore, out RayHit hit, int flags = World)
        {
            int handle = Function.Call<int>(Hash.START_EXPENSIVE_SYNCHRONOUS_SHAPE_TEST_LOS_PROBE,
                from.X, from.Y, from.Z, to.X, to.Y, to.Z, flags, ignore, Options);
            return Result(handle, from, to, out hit) == 2 && hit.Distance >= 0f;
        }

        // A sphere of this radius swept from 'from' to 'to'. Read it with Poll on a later frame.
        public static int StartSweep(Vector3 from, Vector3 to, float radius, int ignore, int flags = World)
        {
            return Function.Call<int>(Hash.START_SHAPE_TEST_CAPSULE,
                from.X, from.Y, from.Z, to.X, to.Y, to.Z, radius, flags, ignore, Options);
        }

        // 1 = not finished yet (call again next frame), otherwise done: true in 'hit' when it found something.
        public static int Poll(int handle, Vector3 from, out RayHit hit)
        {
            int status = Result(handle, from, from, out hit);
            return status == 1 ? 1 : status == 2 && hit.Distance >= 0f ? 2 : 0;
        }

        static int Result(int handle, Vector3 from, Vector3 to, out RayHit hit)
        {
            hit = default;
            hit.Distance = -1f;
            Marshal.WriteInt64(hitBuf, 0);
            Marshal.WriteInt64(entityBuf, 0);
            int status = Function.Call<int>(Hash.GET_SHAPE_TEST_RESULT, handle, hitBuf, endBuf, normalBuf, entityBuf);
            if (status != 2 || Marshal.ReadInt32(hitBuf) == 0)
                return status;
            hit.Point = ReadVector(endBuf);
            hit.Normal = ReadVector(normalBuf);
            hit.Entity = Marshal.ReadInt32(entityBuf);
            hit.Distance = (hit.Point - from).Length();
            float n = hit.Normal.Length();
            hit.Normal = n > 1e-4f ? hit.Normal / n : (to - from).Length() > 1e-4f ? -(to - from).Normalized : Vector3.WorldUp;
            return status;
        }

        static Vector3 ReadVector(IntPtr p)
        {
            float F(int o) => BitConverter.ToSingle(BitConverter.GetBytes(Marshal.ReadInt32(p, o)), 0);
            return new Vector3(F(0), F(8), F(16));
        }
    }
}
