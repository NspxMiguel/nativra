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
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr LineDelegate();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr SplitDelegate(IntPtr line, IntPtr count);

        private static WideDelegate wide;
        private static NarrowDelegate narrow;
        private static LineDelegate lineWide;
        private static LineDelegate lineNarrow;
        private static SplitDelegate split;
        private static IntPtr wideLine;
        private static IntPtr narrowLine;
        private static string path = "";

        /// <summary>
        /// Where each of the game's own modules really lives, by base address.
        /// Adobe AIR finds its application by walking up from its runtime DLL
        /// in "Adobe AIR\Versions\1.0"; answered with the executable's path
        /// for every module, it walked out of the game folder and reported the
        /// application descriptor missing.
        /// </summary>
        private static readonly Dictionary<long, string> modulePaths = new Dictionary<long, string>();

        public static void Register(IntPtr module, string fullPath)
        {
            if (module == IntPtr.Zero || string.IsNullOrEmpty(fullPath)) return;
            lock (modulePaths) modulePaths[module.ToInt64()] = fullPath;
        }

        private static string PathFor(IntPtr module)
        {
            if (module == IntPtr.Zero) return path;
            lock (modulePaths) return modulePaths.TryGetValue(module.ToInt64(), out var known) ? known : path;
        }

        /// <summary>
        /// What the game believes it was started with.
        ///
        /// The command line handed to an entry point is only half the story:
        /// most programs read `GetCommandLineW` instead, and that answers with
        /// the host application's line — which names this app, not the game,
        /// and carries none of the switches the game was started for. Every
        /// option passed in would be silently dropped, which is exactly the
        /// sort of failure that looks like the option not working.
        /// </summary>

        /// <summary>
        /// Splits a command line the way Windows splits it.
        ///
        /// This lives in the shell library, which a packaged app does not have
        /// loaded — so a game asking for its arguments was getting nothing at
        /// all, not even its own name. A program whose argument zero is missing
        /// does not know where it is, and an engine works out where its data
        /// sits from exactly that.
        ///
        /// The rules are Microsoft's, quirks included: a quote opens or closes
        /// a run, two backslashes are one backslash, and a backslash before a
        /// quote makes the quote ordinary.
        /// </summary>
        private static List<string> Split(string line)
        {
            var found = new List<string>();
            if (line == null) return found;

            var current = new StringBuilder();
            var quoted = false;
            var started = false;
            var slashes = 0;

            void Slashes(bool beforeQuote)
            {
                if (beforeQuote)
                {
                    current.Append('\\', slashes / 2);
                    if (slashes % 2 == 1) current.Append('"');
                }
                else
                {
                    current.Append('\\', slashes);
                }
                slashes = 0;
            }

            foreach (var letter in line)
            {
                if (letter == '\\')
                {
                    slashes++;
                    started = true;
                    continue;
                }
                if (letter == '"')
                {
                    var literal = slashes % 2 == 1;
                    Slashes(true);
                    if (!literal) quoted = !quoted;
                    started = true;
                    continue;
                }
                Slashes(false);
                if ((letter == ' ' || letter == '\t') && !quoted)
                {
                    if (started) found.Add(current.ToString());
                    current.Clear();
                    started = false;
                    continue;
                }
                current.Append(letter);
                started = true;
            }
            Slashes(false);
            if (started) found.Add(current.ToString());
            return found;
        }

        /// <summary>One block holding the pointers and the strings they name.</summary>
        private static IntPtr Pack(List<string> parts)
        {
            var bytes = parts.Count * IntPtr.Size;
            foreach (var part in parts) bytes += (part.Length + 1) * 2;

            var block = Marshal.AllocHGlobal(bytes);
            var text = block + parts.Count * IntPtr.Size;
            for (var i = 0; i < parts.Count; i++)
            {
                Marshal.WriteIntPtr(block, i * IntPtr.Size, text);
                for (var letter = 0; letter < parts[i].Length; letter++)
                {
                    Marshal.WriteInt16(text, letter * 2, parts[i][letter]);
                }
                Marshal.WriteInt16(text, parts[i].Length * 2, 0);
                text += (parts[i].Length + 1) * 2;
            }
            return block;
        }

        public static void SetCommandLine(SystemImports imports, string line)
        {
            wideLine = Marshal.StringToHGlobalUni(line);
            narrowLine = Marshal.StringToHGlobalAnsi(line);
            lineWide = () => wideLine;
            lineNarrow = () => narrowLine;

            split = (given, count) =>
            {
                var text = given == IntPtr.Zero ? line : Marshal.PtrToStringUni(given);
                var parts = Split(text);
                if (parts.Count == 0) parts.Add(path);
                if (count != IntPtr.Zero) Marshal.WriteInt32(count, parts.Count);
                return Pack(parts);
            };

            foreach (var module in new[]
            {
                "SHELL32.dll", "shell32.dll", "Shell32.dll",
                "api-ms-win-shell-shellcom-l1-1-0.dll",
            })
            {
                imports.Overrides[module + "!CommandLineToArgvW"] =
                    Marshal.GetFunctionPointerForDelegate(split);
            }

            foreach (var module in new[]
            {
                "KERNEL32.dll", "kernel32.dll", "KERNELBASE.dll", "kernelbase.dll",
                "api-ms-win-core-processenvironment-l1-1-0.dll",
                "api-ms-win-core-processenvironment-l1-2-0.dll",
            })
            {
                imports.Overrides[module + "!GetCommandLineW"] =
                    Marshal.GetFunctionPointerForDelegate(lineWide);
                imports.Overrides[module + "!GetCommandLineA"] =
                    Marshal.GetFunctionPointerForDelegate(lineNarrow);
            }
        }

        public static void Install(SystemImports imports, string executablePath)
        {
            path = executablePath;

            wide = (module, buffer, size) =>
            {
                var text = PathFor(module);
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
                var bytes = Encoding.UTF8.GetBytes(PathFor(module));
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
