using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nativra.X86.Loader;

namespace Kiosk.Native
{
    /// <summary>A float stereo source voice feeding the console's XAudio2 2.9 engine.</summary>
    internal sealed class X86DirectSoundOutput : IGuestSoundOutput
    {
        [DllImport("xaudio2_9.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern int XAudio2Create(out IntPtr engine, uint flags, uint processor);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CreateMasteringVoiceDelegate(IntPtr engine, out IntPtr voice, uint channels, uint rate,
            uint flags, IntPtr device, IntPtr chain, uint category);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CreateSourceVoiceDelegate(IntPtr engine, out IntPtr voice, IntPtr format, uint flags,
            float maxRatio, IntPtr callback, IntPtr sends, IntPtr chain);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int VoiceStartDelegate(IntPtr voice, uint flags, uint operationSet);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SubmitDelegate(IntPtr voice, IntPtr buffer, IntPtr wma);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void GetStateDelegate(IntPtr voice, IntPtr state, uint flags);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void DestroyDelegate(IntPtr voice);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint ReleaseDelegate(IntPtr engine);
        private readonly Queue<IntPtr> pending = new Queue<IntPtr>();
        private IntPtr engine, mastering, source;
        private long submitted;
        // Frames played by voices already torn down: a rebuilt voice counts from zero,
        // and the guest's play cursor must never go back.
        private long playedBefore;
        public int SampleRate => 48000;
        private static T Method<T>(IntPtr obj, int index) where T : class =>
            Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), index * IntPtr.Size), typeof(T)) as T;
        public void Start()
        {
            var code = XAudio2Create(out engine, 0, 1);
            if (code < 0 || engine == IntPtr.Zero) throw new COMException("XAudio2Create failed", code);
            code = Method<CreateMasteringVoiceDelegate>(engine, 7)(engine, out mastering, 2, 48000, 0,
                IntPtr.Zero, IntPtr.Zero, 0);
            if (code < 0) throw new COMException("CreateMasteringVoice failed 0x" + code.ToString("X8"), code);
            var format = Marshal.AllocHGlobal(18);
            try
            {
                Marshal.WriteInt16(format, 0, 3); // WAVE_FORMAT_IEEE_FLOAT
                Marshal.WriteInt16(format, 2, 2);
                Marshal.WriteInt32(format, 4, SampleRate);
                Marshal.WriteInt32(format, 8, SampleRate * 8);
                Marshal.WriteInt16(format, 12, 8);
                Marshal.WriteInt16(format, 14, 32);
                Marshal.WriteInt16(format, 16, 0);
                code = Method<CreateSourceVoiceDelegate>(engine, 5)(engine, out source, format, 0, 2,
                    IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (code < 0) throw new COMException("CreateSourceVoice failed 0x" + code.ToString("X8"), code);
            }
            finally { Marshal.FreeHGlobal(format); }
            code = Method<VoiceStartDelegate>(source, 19)(source, 0, 0);
            if (code < 0) throw new COMException("SourceVoice.Start failed 0x" + code.ToString("X8"), code);
        }
        private void State(out uint queued, out long played)
        {
            if (source == IntPtr.Zero) { queued = 0; played = 0; return; }
            var state = Marshal.AllocHGlobal(24);
            try
            {
                Method<GetStateDelegate>(source, 25)(source, state, 0);
                queued = (uint)Marshal.ReadInt32(state, IntPtr.Size);
                played = Marshal.ReadInt64(state, IntPtr.Size + 8);
            }
            finally { Marshal.FreeHGlobal(state); }
        }
        public long FramesPlayed { get { State(out _, out var played); return playedBefore + played; } }
        public int FramesQueued => (int)Math.Max(0, submitted - FramesPlayed);
        public void Write(float[] stereo)
        {
            if (source == IntPtr.Zero || stereo == null || stereo.Length == 0) return;
            State(out var queued, out _);
            while (pending.Count > queued) Marshal.FreeHGlobal(pending.Dequeue());
            var samples = Marshal.AllocHGlobal(stereo.Length * 4);
            Marshal.Copy(stereo, 0, samples, stereo.Length);
            var buffer = Marshal.AllocHGlobal(48);
            try
            {
                for (var n = 0; n < 48; n += 4) Marshal.WriteInt32(buffer, n, 0);
                Marshal.WriteInt32(buffer, 4, stereo.Length * 4);
                Marshal.WriteIntPtr(buffer, 8, samples);
                var code = Method<SubmitDelegate>(source, 21)(source, buffer, IntPtr.Zero);
                if (code < 0) { Marshal.FreeHGlobal(samples); throw new COMException("SubmitSourceBuffer failed 0x" + code.ToString("X8"), code); }
                pending.Enqueue(samples); submitted += stereo.Length / 2;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        public void Stop()
        {
            if (source != IntPtr.Zero)
            {
                State(out _, out var played);
                playedBefore += played;
                submitted = playedBefore;   // what was queued on the old voice is gone with it
                Method<DestroyDelegate>(source, 18)(source);
                source = IntPtr.Zero;
            }
            if (mastering != IntPtr.Zero) { Method<DestroyDelegate>(mastering, 18)(mastering); mastering = IntPtr.Zero; }
            while (pending.Count != 0) Marshal.FreeHGlobal(pending.Dequeue());
            if (engine != IntPtr.Zero) { Method<ReleaseDelegate>(engine, 2)(engine); engine = IntPtr.Zero; }
        }
        public void Dispose() => Stop();
    }
}
