using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Kiosk.Native
{
    /// <summary>
    /// Answers "which image owns this address" for images the system does not
    /// know about.
    ///
    /// A program's exception machinery asks this constantly: to unwind, it has
    /// to find the module a return address belongs to. The system only knows
    /// the modules it loaded itself, so for anything mapped by hand it answers
    /// nothing, and the caller walks into it. This is the measured point where
    /// Unity's engine died — the last call before the process went.
    /// </summary>
    public static class ImageLookup
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr PcToFileHeaderDelegate(IntPtr pc, out IntPtr baseOfImage);

        private static PcToFileHeaderDelegate ours;
        private static PcToFileHeaderDelegate system;
        private static readonly List<PeImage> images = new List<PeImage>();


        /// <summary>
        /// Which image an address belongs to, as a name and an offset.
        ///
        /// A bare address in a report is only good for comparing to itself.
        /// The same address written as a module and an offset can be looked up
        /// in a disassembler, which is the difference between knowing that a
        /// program is stuck and knowing where.
        /// </summary>
        public static string Describe(long address)
        {
            var at = (IntPtr)address;
            lock (images)
            {
                foreach (var image in images)
                {
                    var start = image.BaseAddress.ToInt64();
                    if (address >= start && address < start + image.ImageSize)
                    {
                        return image.Name + "+0x" + (address - start).ToString("X");
                    }
                }
            }
            return "0x" + address.ToString("X");
        }

        public static void Track(PeImage image)
        {
            lock (images) images.Add(image);
        }

        public static void Install(SystemImports imports, IntPtr systemImplementation)
        {
            if (systemImplementation != IntPtr.Zero)
            {
                system = Marshal.GetDelegateForFunctionPointer<PcToFileHeaderDelegate>(
                    systemImplementation);
            }

            ours = (IntPtr pc, out IntPtr baseOfImage) =>
            {
                lock (images)
                {
                    foreach (var image in images)
                    {
                        if (!image.Contains(pc)) continue;
                        baseOfImage = image.BaseAddress;
                        return image.BaseAddress;
                    }
                }
                if (system != null) return system(pc, out baseOfImage);
                baseOfImage = IntPtr.Zero;
                return IntPtr.Zero;
            };

            var address = Marshal.GetFunctionPointerForDelegate(ours);
            foreach (var module in new[] { "KERNEL32.dll", "kernel32.dll", "ntdll.dll" })
            {
                imports.Overrides[module + "!RtlPcToFileHeader"] = address;
            }
        }
    }
}
