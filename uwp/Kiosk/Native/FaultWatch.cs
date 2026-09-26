using System;
using System.Runtime.InteropServices;

namespace Kiosk.Native
{
    /// <summary>
    /// Writes down what killed the process, before it finishes dying.
    ///
    /// A native fault is not something managed code can catch: by the time
    /// anything here could run, the process is already going. What Windows
    /// does offer is a look at every exception the moment it is raised, before
    /// anyone has decided what to do about it — which is enough to record the
    /// code, the instruction and the address that was touched.
    ///
    /// The difference that makes is the difference between "the engine died"
    /// and "the engine read address zero at this instruction", and only the
    /// second one can be fixed.
    /// </summary>
    public static class FaultWatch
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int HandlerDelegate(IntPtr pointers);

        [DllImport("api-ms-win-core-errorhandling-l1-1-1.dll", SetLastError = true)]
        private static extern IntPtr AddVectoredExceptionHandler(
            uint first, IntPtr handler);

        private const int ContinueSearch = 0;

        // Written to from the handler, which must not allocate: a managed
        // allocation inside an exception handler is its own kind of crash.
        private const int Room = 8;
        // Per fault: code, address, two parameters, then the top of the stack.
        private const int StackWords = 64;
        private const int SlotBytes = 32 + StackWords * 8;
        private static IntPtr slab;
        private static int at;

        private static HandlerDelegate handler;

        /// <summary>Exceptions worth ignoring: the ordinary noise of a running program.</summary>
        private static bool Boring(uint code)
        {
            // A C++ throw, a managed throw, and the breakpoint a debugger-less
            // process raises and swallows. Engines raise these constantly.
            // The last two are a program talking to a debugger that is not
            // there: OutputDebugString raises one every time it is called.
            return code == 0xE06D7363 || code == 0xE0434352 || code == 0x40010006
                || code == 0x406D1388 || code == 0x4001000A;
        }

        public static void Install()
        {
            if (slab != IntPtr.Zero) return;
            slab = Marshal.AllocHGlobal(Room * SlotBytes);
            for (var i = 0; i < Room * SlotBytes / 8; i++) Marshal.WriteInt64(slab, i * 8, 0);

            handler = pointers =>
            {
                try
                {
                    if (pointers == IntPtr.Zero) return ContinueSearch;
                    var record = Marshal.ReadIntPtr(pointers, 0);
                    var context = Marshal.ReadIntPtr(pointers, 8);
                    if (record == IntPtr.Zero) return ContinueSearch;

                    var code = (uint)Marshal.ReadInt32(record, 0);
                    if (Boring(code)) return ContinueSearch;

                    var slot = at;
                    if (slot >= Room) return ContinueSearch;
                    at = slot + 1;

                    var into = slab + slot * SlotBytes;
                    Marshal.WriteInt64(into, 0, code);
                    Marshal.WriteInt64(into, 8, Marshal.ReadIntPtr(record, 16).ToInt64());

                    // For an access violation the parameters say whether it was
                    // a read or a write, and which address was touched.
                    var count = Marshal.ReadInt32(record, 24);
                    Marshal.WriteInt64(into, 16, count > 0 ? Marshal.ReadInt64(record, 32) : -1);
                    Marshal.WriteInt64(into, 24, count > 1 ? Marshal.ReadInt64(record, 40) : -1);

                    // CONTEXT.Rip sits at 0xF8 on x64; it names the instruction
                    // even when the exception record does not.
                    if (context != IntPtr.Zero && slot < Room)
                    {
                        Marshal.WriteInt64(into, 8, Marshal.ReadInt64(context, 0xF8));
                        // The words above the stack pointer (CONTEXT.Rsp at
                        // 0x98): the return addresses among them say who
                        // raised it, which RaiseException's own address does not.
                        var rsp = new IntPtr(Marshal.ReadInt64(context, 0x98));
                        for (var i = 0; i < StackWords; i++)
                            Marshal.WriteInt64(into, 32 + i * 8, Marshal.ReadInt64(rsp, i * 8));
                    }
                }
                catch
                {
                    // Recording is best effort; never make the fault worse.
                }
                return ContinueSearch;
            };

            try
            {
                AddVectoredExceptionHandler(
                    1, Marshal.GetFunctionPointerForDelegate(handler));
            }
            catch
            {
                // Without it the loss is the detail, not the run.
            }
        }

        private static string Frames(IntPtr words)
        {
            var frames = new System.Collections.Generic.List<string>();
            for (var i = 0; i < StackWords && frames.Count < 12; i++)
            {
                var named = StackSampler.Describe(Marshal.ReadInt64(words, i * 8));
                if (named.StartsWith("0x", StringComparison.Ordinal)) continue;
                if (frames.Count > 0 && frames[frames.Count - 1] == named) continue;
                frames.Add(named);
            }
            return string.Join(" < ", frames);
        }

        /// <summary>What was recorded, in the order it happened.</summary>
        public static System.Collections.Generic.List<string> Faults()
        {
            var found = new System.Collections.Generic.List<string>();
            if (slab == IntPtr.Zero) return found;
            for (var slot = 0; slot < at && slot < Room; slot++)
            {
                var from = slab + slot * SlotBytes;
                var code = Marshal.ReadInt64(from, 0);
                if (code == 0) continue;
                var kind = Marshal.ReadInt64(from, 16);
                found.Add(
                    "fault 0x" + ((uint)code).ToString("X8") +
                    " at " + ImageLookup.Describe(Marshal.ReadInt64(from, 8)) +
                    (kind < 0 ? "" :
                        (kind == 1 ? " writing 0x" : " reading 0x") +
                        Marshal.ReadInt64(from, 24).ToString("X")) +
                    " | " + Frames(from + 32));
            }
            return found;
        }
    }
}
