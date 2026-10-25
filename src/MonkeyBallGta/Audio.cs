// Monkey Ball's own sounds and music, played by a small mixer of our own on a waveOut device, since
// GTA's audio engine only plays sounds packed into its own banks.
//
// The files are the WAVs the converter extracts from your own copy of the game (they are never part
// of this repository), copied to scripts\MonkeyBallGta\sfx and scripts\MonkeyBallGta\music. Any that
// are missing are skipped. The mixer runs on its own thread and never calls the game; the script
// only queues voices and tells it when the game is paused.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using static MonkeyBallGta.Util;

namespace MonkeyBallGta
{
    class Audio : IDisposable
    {
        const int Rate = 44100, Block = 1024, Buffers = 4;
        const float SfxVolume = 0.18f, MusicVolume = 0.45f;
        static readonly string Root = Path.Combine("scripts", "MonkeyBallGta");

        class Clip
        {
            public float[] Samples;  // interleaved stereo
            public int Rate;
            public int Frames => Samples.Length / 2;
        }

        class Voice
        {
            public Clip Clip;
            public double Pos;
            public float Volume;
            public bool Loop, Music;
        }

        readonly Dictionary<string, Clip> clips = new Dictionary<string, Clip>();
        readonly List<Voice> voices = new List<Voice>();
        readonly object gate = new object();
        Voice music, roll;
        IntPtr device;
        readonly IntPtr[] headers = new IntPtr[Buffers], data = new IntPtr[Buffers];
        readonly short[] pcm = new short[Block * 2];
        readonly float[] mix = new float[Block * 2];
        Thread thread;
        volatile bool running;
        long lastTick = Environment.TickCount;
        public volatile bool Paused;

        public bool Start()
        {
            var fmt = new WaveFormat { Tag = 1, Channels = 2, Rate = Rate, BytesPerSecond = Rate * 4, BlockAlign = 4, Bits = 16 };
            int err = waveOutOpen(out device, -1, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
            if (err != 0)
            {
                Log($"audio: waveOutOpen failed ({err})");
                return false;
            }
            for (int i = 0; i < Buffers; i++)
            {
                data[i] = Marshal.AllocHGlobal(Block * 4);
                headers[i] = Marshal.AllocHGlobal(HeaderSize);
                for (int b = 0; b < HeaderSize; b += 4)
                    Marshal.WriteInt32(headers[i], b, 0);
                Marshal.WriteIntPtr(headers[i], 0, data[i]);
                Marshal.WriteInt32(headers[i], 8, Block * 4);
                waveOutPrepareHeader(device, headers[i], HeaderSize);
                Marshal.WriteInt32(headers[i], FlagsOffset, Marshal.ReadInt32(headers[i], FlagsOffset) | Done);
            }
            running = true;
            thread = new Thread(Run) { IsBackground = true, Name = "mbgta audio" };
            thread.Start();
            return true;
        }

        // Call every game frame; the mixer goes quiet if the game stops ticking (paused, loading, alt-tab).
        public void Tick(bool paused)
        {
            Paused = paused;
            Interlocked.Exchange(ref lastTick, Environment.TickCount);
        }

        public void Play(string name, float volume = 1f)
        {
            Clip c = Load(Path.Combine("sfx", name + ".wav"));
            if (c == null)
                return;
            lock (gate)
                voices.Add(new Voice { Clip = c, Volume = volume * SfxVolume });
        }

        // Music loads on a worker thread (a track is tens of megabytes); it starts when it's ready.
        public void PlayMusic(string name)
        {
            StopMusic();
            var v = new Voice { Loop = true, Music = true, Volume = MusicVolume };
            lock (gate)
                music = v;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Clip c = Load(Path.Combine("music", name + ".wav"));
                lock (gate)
                {
                    if (c == null || music != v)
                        return;
                    v.Clip = c;
                    voices.Add(v);
                }
            });
        }

        public void StopMusic()
        {
            lock (gate)
            {
                if (music != null)
                    voices.Remove(music);
                music = null;
            }
        }

        // The rolling sound: a loop whose volume follows the ball.
        public void Roll(float volume)
        {
            lock (gate)
            {
                if (roll == null)
                {
                    Clip c = Load(Path.Combine("sfx", "ball_roll.wav"));
                    if (c == null)
                        return;
                    roll = new Voice { Clip = c, Loop = true };
                    voices.Add(roll);
                }
                roll.Volume = volume * SfxVolume;
            }
        }

        public void StopAll()
        {
            lock (gate)
            {
                voices.Clear();
                music = roll = null;
            }
        }

        Clip Load(string relative)
        {
            lock (clips)
            {
                if (clips.TryGetValue(relative, out Clip cached))
                    return cached;
            }
            Clip c = null;
            string path = Path.Combine(Root, relative);
            try
            {
                if (File.Exists(path))
                    c = ReadWav(File.ReadAllBytes(path));
                else
                    Log($"audio: missing {path}");
            }
            catch (Exception ex) { Log($"audio: couldn't read {path}: {ex.Message}"); }
            lock (clips)
                clips[relative] = c;
            return c;
        }

        // 16-bit PCM, mono or stereo, any rate.
        static Clip ReadWav(byte[] b)
        {
            int channels = 1, rate = Rate, bits = 16, pos = 12;
            while (pos + 8 <= b.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(b, pos, 4);
                int size = BitConverter.ToInt32(b, pos + 4);
                int body = pos + 8;
                if (id == "fmt ")
                {
                    channels = BitConverter.ToInt16(b, body + 2);
                    rate = BitConverter.ToInt32(b, body + 4);
                    bits = BitConverter.ToInt16(b, body + 14);
                }
                else if (id == "data")
                {
                    if (bits != 16)
                        return null;
                    size = Math.Min(size, b.Length - body);
                    int frames = size / (2 * channels);
                    if (frames < 2)
                        return null;
                    var s = new float[frames * 2];
                    for (int i = 0; i < frames; i++)
                    {
                        float l = BitConverter.ToInt16(b, body + i * 2 * channels) / 32768f;
                        float r = channels > 1 ? BitConverter.ToInt16(b, body + i * 2 * channels + 2) / 32768f : l;
                        s[i * 2] = l;
                        s[i * 2 + 1] = r;
                    }
                    return new Clip { Samples = s, Rate = rate };
                }
                pos = body + size + (size & 1);
            }
            return null;
        }

        // An exception escaping this thread would take the whole game down, so none may.
        void Run()
        {
            try { Loop(); }
            catch (Exception ex) { Log("audio: mixer stopped: " + ex); }
        }

        void Loop()
        {
            while (running)
            {
                bool wrote = false;
                for (int i = 0; i < Buffers; i++)
                {
                    if ((Marshal.ReadInt32(headers[i], FlagsOffset) & Done) == 0)
                        continue;
                    Fill();
                    Marshal.Copy(pcm, 0, data[i], pcm.Length);
                    Marshal.WriteInt32(headers[i], FlagsOffset, Marshal.ReadInt32(headers[i], FlagsOffset) & ~Done);
                    waveOutWrite(device, headers[i], HeaderSize);
                    wrote = true;
                }
                if (!wrote)
                    Thread.Sleep(4);
            }
        }

        void Fill()
        {
            Array.Clear(mix, 0, mix.Length);
            bool quiet = Paused || Environment.TickCount - Interlocked.Read(ref lastTick) > 250;
            if (!quiet)
            {
                lock (gate)
                {
                    for (int v = voices.Count - 1; v >= 0; v--)
                    {
                        Voice voice = voices[v];
                        float[] s = voice.Clip.Samples;
                        int frames = voice.Clip.Frames;
                        double step = (double)voice.Clip.Rate / Rate;
                        bool ended = false;
                        for (int i = 0; i < Block; i++)
                        {
                            if (voice.Pos >= frames - 1)
                            {
                                if (!voice.Loop)
                                {
                                    ended = true;
                                    break;
                                }
                                voice.Pos -= frames - 1;
                            }
                            int at = (int)voice.Pos;
                            float t = (float)(voice.Pos - at);
                            mix[i * 2] += (s[at * 2] + (s[at * 2 + 2] - s[at * 2]) * t) * voice.Volume;
                            mix[i * 2 + 1] += (s[at * 2 + 1] + (s[at * 2 + 3] - s[at * 2 + 1]) * t) * voice.Volume;
                            voice.Pos += step;
                        }
                        if (ended)
                            voices.RemoveAt(v);
                    }
                }
            }
            for (int i = 0; i < mix.Length; i++)
            {
                float x = mix[i];
                x = x > 1f ? 1f : x < -1f ? -1f : x;
                pcm[i] = (short)(x * 32767f);
            }
        }

        public void Dispose()
        {
            running = false;
            thread?.Join(500);
            if (device != IntPtr.Zero)
            {
                waveOutReset(device);
                for (int i = 0; i < Buffers; i++)
                    waveOutUnprepareHeader(device, headers[i], HeaderSize);
                waveOutClose(device);
                device = IntPtr.Zero;
            }
            for (int i = 0; i < Buffers; i++)
            {
                if (headers[i] != IntPtr.Zero) Marshal.FreeHGlobal(headers[i]);
                if (data[i] != IntPtr.Zero) Marshal.FreeHGlobal(data[i]);
                headers[i] = data[i] = IntPtr.Zero;
            }
        }

        // WAVEHDR on 64-bit Windows: lpData, dwBufferLength, dwBytesRecorded, dwUser, dwFlags, dwLoops,
        // lpNext, reserved. We keep it in unmanaged memory so the driver can write its flags back.
        const int HeaderSize = 48, FlagsOffset = 24, Done = 1;

        [StructLayout(LayoutKind.Sequential)]
        struct WaveFormat
        {
            public short Tag, Channels;
            public int Rate, BytesPerSecond;
            public short BlockAlign, Bits, Extra;
        }

        [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr device, int id, ref WaveFormat format, IntPtr callback, IntPtr instance, int flags);
        [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr device, IntPtr header, int size);
        [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr device, IntPtr header, int size);
        [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr device, IntPtr header, int size);
        [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr device);
        [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr device);
    }
}
