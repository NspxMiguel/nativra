using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Kiosk.Native
{
    /// <summary>
    /// The multimedia clock, and the sound card that is not there.
    ///
    /// `timeGetTime` is the oldest clock in Windows and half the games ever
    /// written still pace themselves by it. A stand-in that answers zero does
    /// not merely lose precision — it stops time, and every loop written as
    /// "wait until the clock has moved" waits forever. That is the difference
    /// between a game that is slow and a game that is hung, and it is one
    /// function.
    ///
    /// The wave functions are the opposite case: the honest answer is that
    /// this console has no wave-out device, which is true, and which makes an
    /// audio layer move on to something else instead of talking to nothing.
    /// </summary>
    public static class TimerStubs
    {
        private const int TIMERR_NOERROR = 0;
        private const int MMSYSERR_NODRIVER = 6;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint TickDelegate();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int PeriodDelegate(uint period);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CapsDelegate(IntPtr caps, uint size);

        private static TickDelegate tick;
        private static PeriodDelegate period;
        private static CapsDelegate caps;

        public static void Install(SystemImports imports)
        {
            tick = () => (uint)Environment.TickCount;
            period = value => TIMERR_NOERROR;

            // TIMECAPS is two words: the smallest and largest period the timer
            // will accept. A game reads them before asking for one.
            caps = (target, size) =>
            {
                if (target == IntPtr.Zero) return MMSYSERR_NODRIVER;
                Marshal.WriteInt32(target, 0, 1);
                Marshal.WriteInt32(target, 4, 1000000);
                return TIMERR_NOERROR;
            };

            var ours = new Dictionary<string, IntPtr>
            {
                { "timeGetTime", Marshal.GetFunctionPointerForDelegate(tick) },
                { "timeBeginPeriod", Marshal.GetFunctionPointerForDelegate(period) },
                { "timeEndPeriod", Marshal.GetFunctionPointerForDelegate(period) },
                { "timeGetDevCaps", Marshal.GetFunctionPointerForDelegate(caps) },
            };

            var answers = new Dictionary<string, long>
            {
                // No wave device, said plainly, in the words the interface uses.
                { "waveOutGetNumDevs", 0 },
                { "waveInGetNumDevs", 0 },
                { "waveOutOpen", MMSYSERR_NODRIVER },
                { "waveOutClose", MMSYSERR_NODRIVER },
                { "waveOutReset", MMSYSERR_NODRIVER },
                { "waveOutWrite", MMSYSERR_NODRIVER },
                { "waveOutPrepareHeader", MMSYSERR_NODRIVER },
                { "waveOutUnprepareHeader", MMSYSERR_NODRIVER },
                { "waveOutGetPosition", MMSYSERR_NODRIVER },
                { "waveOutGetDevCapsA", MMSYSERR_NODRIVER },
                { "waveOutGetDevCapsW", MMSYSERR_NODRIVER },
                { "waveInOpen", MMSYSERR_NODRIVER },
                { "waveInClose", MMSYSERR_NODRIVER },
                { "waveInStart", MMSYSERR_NODRIVER },
                { "waveInReset", MMSYSERR_NODRIVER },
                { "waveInAddBuffer", MMSYSERR_NODRIVER },
                { "waveInPrepareHeader", MMSYSERR_NODRIVER },
                { "waveInUnprepareHeader", MMSYSERR_NODRIVER },
                { "waveInGetDevCapsA", MMSYSERR_NODRIVER },
                { "waveInGetDevCapsW", MMSYSERR_NODRIVER },
                { "timeSetEvent", 0 },
                { "timeKillEvent", TIMERR_NOERROR },
                { "timeGetSystemTime", TIMERR_NOERROR },
            };

            foreach (var module in new[] { "WINMM.dll", "winmm.dll", "Winmm.dll" })
            {
                foreach (var pair in ours) imports.Overrides[module + "!" + pair.Key] = pair.Value;
                foreach (var pair in answers) imports.Answers[module + "!" + pair.Key] = pair.Value;
            }
        }
    }
}
