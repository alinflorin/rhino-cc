using System;
using System.IO;
using System.Threading;

namespace RhinoCC.Chat
{
    /// <summary>Two soft notes, made on the spot so there is no sound file to ship.</summary>
    internal static class Chime
    {
        private static byte[] _wav;

        public static void Play()
        {
            // off the UI thread: playing waits for the sound to end
            var t = new Thread(() =>
            {
                try
                {
                    _wav = _wav ?? Make();
                    using (var ms = new MemoryStream(_wav))
                    using (var player = new System.Media.SoundPlayer(ms)) player.PlaySync();
                }
                catch (Exception e) { RhinoCC.Core.Log.Write("chime failed: " + e.Message); }
            }) { IsBackground = true };
            t.Start();
        }

        private static byte[] Make()
        {
            const int rate = 22050;
            var notes = new[] { 659.25, 880.0 };
            var gap = (int)(0.16 * rate);
            var length = (int)(0.7 * rate);
            var total = gap * (notes.Length - 1) + length;
            var samples = new short[total];
            for (var n = 0; n < notes.Length; n++)
                for (var i = 0; i < length; i++)
                {
                    var t = i / (double)rate;
                    // quick rise, long fade
                    var env = Math.Min(t / 0.02, 1.0) * Math.Exp(-5 * t);
                    var v = (int)(Math.Sin(2 * Math.PI * notes[n] * t) * env * 0.35 * short.MaxValue);
                    var at = n * gap + i;
                    samples[at] = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, samples[at] + v));
                }
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                var bytes = samples.Length * 2;
                w.Write(new[] { 'R', 'I', 'F', 'F' });
                w.Write(36 + bytes);
                w.Write(new[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' });
                w.Write(16);
                w.Write((short)1);
                w.Write((short)1);
                w.Write(rate);
                w.Write(rate * 2);
                w.Write((short)2);
                w.Write((short)16);
                w.Write(new[] { 'd', 'a', 't', 'a' });
                w.Write(bytes);
                foreach (var s in samples) w.Write(s);
                w.Flush();
                return ms.ToArray();
            }
        }
    }
}
