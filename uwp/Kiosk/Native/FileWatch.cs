using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Kiosk.Native
{
    /// <summary>
    /// Writes down which files the game opens, and which it fails to open.
    ///
    /// A game that stops without saying why is usually waiting on something it
    /// asked the file system for. Its own log would say so, but the log is the
    /// first thing that goes missing when the log cannot be opened — which is
    /// exactly the case this was written to settle.
    ///
    /// Opening files is one of the hottest calls a game makes, so almost none
    /// of them are recorded: only the ones that fail, and the handful whose
    /// name says they matter. Recording all of them would change the timing of
    /// the thing being measured, which is how a measurement stops being one.
    /// </summary>
    internal static class FileWatch
    {
        private const int Keep = 40;
        private static readonly IntPtr InvalidHandle = new IntPtr(-1);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr CreateFileDelegate(
            IntPtr name, uint access, uint share, IntPtr security,
            uint disposition, uint flags, IntPtr template);

        private static CreateFileDelegate wide;
        private static CreateFileDelegate real;

        /// <summary>What was opened, and how it went. Read under its own lock.</summary>
        public static readonly List<string> Seen = new List<string>();

        /// <summary>How many opens failed, whether or not each one was kept.</summary>
        public static long Failures;

        private static void Say(string line)
        {
            lock (Seen)
            {
                if (Seen.Count < Keep) Seen.Add(line);
            }
        }

        /// <summary>
        /// A name worth keeping even when the open succeeds: the log the engine
        /// was told to write, and the files it loads its own code from. The
        /// rest — textures, bundles, the thousands of small reads a scene makes
        /// — are only interesting when they fail.
        /// </summary>
        private static bool WorthKeeping(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var lower = path.ToLowerInvariant();
            return lower.EndsWith(".log")
                || lower.EndsWith("global-metadata.dat")
                || lower.EndsWith(".dll")
                || lower.EndsWith("boot.config");
        }

        /// <summary>Answers an open by name, or zero to let it through.</summary>
        public static Func<IntPtr, IntPtr> Intercept;

        public static void Install(SystemImports imports)
        {
            real = null;
            var address = imports.SystemAddress("kernel32.dll", "CreateFileW");
            if (address != IntPtr.Zero)
            {
                real = Marshal.GetDelegateForFunctionPointer<CreateFileDelegate>(address);
            }

            wide = (name, access, share, security, disposition, flags, template) =>
            {
                // Paths that name something the bridge provides, such as the
                // listed controller, are answered before the file system.
                var ours = Intercept == null ? IntPtr.Zero : Intercept(name);
                if (ours != IntPtr.Zero) return ours;
                if (real == null) return InvalidHandle;

                var handle = real(
                    name, access, share, security, disposition, flags, template);

                // Reading the name costs a copy, so it is only read when there
                // is a reason to: the open failed, or there is still room and
                // the name might be one of the few that matter.
                var failed = handle == InvalidHandle;
                if (!failed && Seen.Count >= Keep) return handle;

                string path = null;
                try
                {
                    path = name == IntPtr.Zero ? null : Marshal.PtrToStringUni(name);
                }
                catch
                {
                    // An unreadable name is not worth failing the open over.
                }

                if (failed)
                {
                    Failures++;
                    Say("failed " + (path ?? "?"));
                }
                else if (WorthKeeping(path))
                {
                    Say("opened " + path);
                }
                return handle;
            };

            foreach (var module in new[]
                     {
                         "KERNEL32.dll", "kernel32.dll", "KERNELBASE.dll",
                         "api-ms-win-core-file-l1-1-0.dll",
                         "api-ms-win-core-file-l1-2-0.dll",
                         "api-ms-win-core-file-l1-2-1.dll",
                     })
            {
                imports.Overrides[module + "!CreateFileW"] =
                    Marshal.GetFunctionPointerForDelegate(wide);
            }
        }
    }
}
