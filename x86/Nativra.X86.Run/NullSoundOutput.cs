using System;
using System.Diagnostics;
using Nativra.X86.Loader;

namespace Nativra.X86.Run
{
    internal sealed class NullSoundOutput : IGuestSoundOutput
    {
        private readonly Stopwatch clock = new Stopwatch();
        private long submitted;
        public int SampleRate => 48000;
        public long FramesPlayed => Math.Min(submitted, (long)(clock.Elapsed.TotalSeconds * SampleRate));
        public int FramesQueued => (int)Math.Max(0, submitted - FramesPlayed);
        public void Start() => clock.Start();
        public void Write(float[] interleavedStereo) => submitted += interleavedStereo.Length / 2;
        public void Stop() => clock.Stop();
        public void Dispose() { }
    }
}
