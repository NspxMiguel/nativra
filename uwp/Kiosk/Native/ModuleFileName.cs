using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Kiosk.Native
{
    /// <summary>
    /// Tells a mapped program where it lives.
    ///
    /// A game works out where its data sits by asking the system for its own
    /// path. Asked honestly, the system names the host application, and the
    /// game then looks for its files next to something else entirely. These
    /// answer with the path the game was actually installed to.
    /// </summary>
    public static class ModuleFileName
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private delegate uint WideDelegate(IntPtr module, IntPtr buffer, uint size);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint NarrowDelegate(IntPtr module, IntPtr buffer, uint size);

        // Held so the collector cannot take them while native code holds their
        // addresses.
        private static WideDelegate wide;
        private static NarrowDelegate narrow;
        private static string path = "";

        public static void Install(SystemImports imports, string executablePath)
        {
            path = executablePath;

            wide = (module, buffer, size) =>
            {
                var text = path;
                var room = (int)size;
                if (room <= 0) return 0;
                var copied = Math.Min(text.Length, room - 1);
                for (var i = 0; i < copied; i++)
                {
                    Marshal.WriteInt16(buffer, i * 2, text[i]);
                }
                Marshal.WriteInt16(buffer, copied * 2, 0);
                return (uint)copied;
            };

            narrow = (module, buffer, size) =>
            {
                var bytes = Encoding.UTF8.GetBytes(path);
                var room = (int)size;
                if (room <= 0) return 0;
                var copied = Math.Min(bytes.Length, room - 1);
                for (var i = 0; i < copied; i++)
                {
                    Marshal.WriteByte(buffer, i, bytes[i]);
                }
                Marshal.WriteByte(buffer, copied, 0);
                return (uint)copied;
            };

            var wideAddress = Marshal.GetFunctionPointerForDelegate(wide);
            var narrowAddress = Marshal.GetFunctionPointerForDelegate(narrow);
            foreach (var module in new[] { "KERNEL32.dll", "kernel32.dll" })
            {
                imports.Overrides[module + "!GetModuleFileNameW"] = wideAddress;
                imports.Overrides[module + "!GetModuleFileNameA"] = narrowAddress;
            }
        }
    }
}
