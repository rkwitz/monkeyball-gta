// Helpers shared by the scripts. Raw natives only: ScriptHookVDotNet's memory helpers (used by Model,
// Prop, Notification and every string argument) fail to initialise on game builds newer than the
// SHVDN release, and that takes the game down. So strings go to natives as our own UTF-8 buffers.
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using GTA.Math;
using GTA.Native;

namespace MonkeyBallGta
{
    static class Util
    {
        // An exception that reaches ScriptHookVDotNet's handler crashes the game on this build.
        public static void Guard(Action action)
        {
            try { action(); }
            catch (Exception ex) { Log(ex.ToString()); }
        }

        public static void Notify(string text)
        {
            IntPtr entry = Utf8("STRING"), body = Utf8(text);
            try
            {
                Function.Call(Hash.BEGIN_TEXT_COMMAND_THEFEED_POST, entry);
                Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, body);
                Function.Call(Hash.END_TEXT_COMMAND_THEFEED_POST_TICKER, false, true);
            }
            finally
            {
                Marshal.FreeHGlobal(entry);
                Marshal.FreeHGlobal(body);
            }
        }

        // DELETE_OBJECT takes a pointer to the handle; pass our own memory rather than SHVDN's OutputArgument.
        public static void DeleteObject(int obj)
        {
            if (!Function.Call<bool>(Hash.DOES_ENTITY_EXIST, obj))
                return;
            Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, obj, false, true);
            IntPtr p = Marshal.AllocHGlobal(8);
            try
            {
                Marshal.WriteInt64(p, obj);
                Function.Call(Hash.DELETE_OBJECT, p);
            }
            finally { Marshal.FreeHGlobal(p); }
        }

        // Free the result with Marshal.FreeHGlobal.
        public static IntPtr Utf8(string s)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(s);
            IntPtr p = Marshal.AllocHGlobal(bytes.Length + 1);
            Marshal.Copy(bytes, 0, p, bytes.Length);
            Marshal.WriteByte(p, bytes.Length, 0);
            return p;
        }

        // Scratch memory for natives' out-parameters. A native Vector3 is three floats, each padded to 8 bytes.
        public static readonly IntPtr OutA = Marshal.AllocHGlobal(24), OutB = Marshal.AllocHGlobal(24);

        public static Vector3 ReadVector(IntPtr p) => new Vector3(ReadFloat(p, 0), ReadFloat(p, 8), ReadFloat(p, 16));

        public static float ReadFloat(IntPtr p, int offset = 0) =>
            BitConverter.ToSingle(BitConverter.GetBytes(Marshal.ReadInt32(p, offset)), 0);

        // On-screen text for this frame (call every frame it should show). x, y are 0..1 of the screen.
        public static void DrawText(string text, float x, float y, float scale, int font, int r, int g, int b, bool centre = true)
        {
            IntPtr entry = Utf8("STRING"), body = Utf8(text);
            try
            {
                Function.Call(Hash.SET_TEXT_FONT, font);
                Function.Call(Hash.SET_TEXT_SCALE, scale, scale);
                Function.Call(Hash.SET_TEXT_COLOUR, r, g, b, 255);
                Function.Call(Hash.SET_TEXT_CENTRE, centre);
                Function.Call(Hash.SET_TEXT_OUTLINE);
                Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_TEXT, entry);
                Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, body);
                Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_TEXT, x, y, 0);
            }
            finally
            {
                Marshal.FreeHGlobal(entry);
                Marshal.FreeHGlobal(body);
            }
        }

        public static void PlaySound(string name, string set)
        {
            IntPtr n = Utf8(name), st = Utf8(set);
            try { Function.Call(Hash.PLAY_SOUND_FRONTEND, -1, n, st, true); }
            finally
            {
                Marshal.FreeHGlobal(n);
                Marshal.FreeHGlobal(st);
            }
        }

        // REMOVE_BLIP takes a pointer to the handle.
        public static void RemoveBlip(int blip)
        {
            if (blip == 0 || !Function.Call<bool>(Hash.DOES_BLIP_EXIST, blip))
                return;
            Marshal.WriteInt64(OutA, blip);
            Function.Call(Hash.REMOVE_BLIP, OutA);
        }

        // The size of an entity's model, in metres.
        public static Vector3 ModelSize(int entity)
        {
            int model = Function.Call<int>(Hash.GET_ENTITY_MODEL, entity);
            Function.Call(Hash.GET_MODEL_DIMENSIONS, model, OutA, OutB);
            return ReadVector(OutB) - ReadVector(OutA);
        }

        // An entity's model box (its corners, in the entity's own space).
        public static void ModelBox(int entity, out Vector3 min, out Vector3 max)
        {
            int model = Function.Call<int>(Hash.GET_ENTITY_MODEL, entity);
            Function.Call(Hash.GET_MODEL_DIMENSIONS, model, OutA, OutB);
            min = ReadVector(OutA);
            max = ReadVector(OutB);
        }

        public static void Log(string text)
        {
            try { File.AppendAllText(Path.Combine("scripts", "MonkeyBallGta.log"), $"[{DateTime.Now:HH:mm:ss}] {text}\r\n"); }
            catch (IOException) { }
        }

        // GTA's model name hash (Jenkins one-at-a-time over the lower-case name).
        public static uint Joaat(string s)
        {
            uint h = 0;
            foreach (byte b in Encoding.ASCII.GetBytes(s.ToLowerInvariant()))
            {
                h += b;
                h += h << 10;
                h ^= h >> 6;
            }
            h += h << 3;
            h ^= h >> 11;
            h += h << 15;
            return h;
        }
    }
}
