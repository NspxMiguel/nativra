using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Kiosk.Native
{
    /// <summary>
    /// Answers the calls a game makes to start other programs.
    ///
    /// Unity launches its crash handler as a child process before it does
    /// anything else. An app container has no way to start one, and the call
    /// took the process down — measured as the last thing the engine did.
    /// Failing the call cleanly is what the engine already knows how to
    /// handle: it carries on without a crash handler.
    /// </summary>
    public static class ProcessStubs
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CreateProcessDelegate(
            IntPtr applicationName, IntPtr commandLine, IntPtr processAttributes,
            IntPtr threadAttributes, int inheritHandles, uint creationFlags,
            IntPtr environment, IntPtr currentDirectory, IntPtr startupInfo,
            IntPtr processInformation);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ShellExecuteDelegate(
            IntPtr window, IntPtr operation, IntPtr file, IntPtr parameters,
            IntPtr directory, int show);

        private static CreateProcessDelegate createProcess;
        private static ShellExecuteDelegate shellExecute;


        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void ExitDelegate(uint code);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int KillDelegate(IntPtr process, uint code);

        private static ExitDelegate leave;
        private static KillDelegate kill;

        /// <summary>What the game asked to do to the process, and with what code.</summary>
        public static string Attempted = "nothing";

        /// <summary>
        /// Stops the game from taking the application with it.
        ///
        /// A game owns its process and ends it when it decides it is done —
        /// which here is this application, the screen it draws on, and the
        /// launcher the player came from. So the call is caught: the thread
        /// that made it is parked, its exit code written down, and everything
        /// else carries on. Whatever the game wanted to end, it ends alone.
        /// </summary>
        private static void Install(SystemImports imports, string[] modules)
        {
            leave = code =>
            {
                Attempted = "ExitProcess(" + code + ")";
                // Parked, not returned to: the caller believes it is gone, and
                // code after a call that never returns is not written to run.
                while (true) System.Threading.Thread.Sleep(1000);
            };

            kill = (process, code) =>
            {
                Attempted = "TerminateProcess(" + code + ")";
                return 1;
            };

            foreach (var module in modules)
            {
                imports.Overrides[module + "!ExitProcess"] =
                    Marshal.GetFunctionPointerForDelegate(leave);
                imports.Overrides[module + "!TerminateProcess"] =
                    Marshal.GetFunctionPointerForDelegate(kill);
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void CrtExitDelegate(int code);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void CrtAbortDelegate();
        private static CrtExitDelegate crtExit;
        private static CrtAbortDelegate crtAbort;

        [DllImport("api-ms-win-core-rtlsupport-l1-1-0.dll")]
        private static extern ushort RtlCaptureStackBackTrace(uint skip, uint count, IntPtr[] frames, IntPtr hash);

        /// <summary>Who asked to leave: the game frames on the caller's stack.</summary>
        private static string Caller()
        {
            try
            {
                var frames = new IntPtr[32];
                var got = RtlCaptureStackBackTrace(1, 32, frames, IntPtr.Zero);
                var named = new List<string>();
                for (var i = 0; i < got && named.Count < 10; i++)
                {
                    var text = StackSampler.Describe(frames[i].ToInt64());
                    if (text.StartsWith("~", StringComparison.Ordinal) || text.StartsWith("0x", StringComparison.Ordinal)) continue;
                    named.Add(text);
                }
                return named.Count == 0 ? "" : " from " + string.Join(" < ", named);
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// The C runtime's ways out: exit and its relatives, and abort, which
        /// ends in __fastfail that no exception handler sees. They call
        /// ExitProcess from inside ucrtbase, past the hook above, so a game
        /// that gave up this way left "exit=nothing" and no reason.
        /// </summary>
        private static void InstallCrt(SystemImports imports)
        {
            crtExit = code =>
            {
                Attempted = "exit(" + code + ")" + Caller();
                while (true) System.Threading.Thread.Sleep(1000);
            };
            crtAbort = () =>
            {
                Attempted = "abort()" + Caller();
                while (true) System.Threading.Thread.Sleep(1000);
            };
            foreach (var module in new[] { "api-ms-win-crt-runtime-l1-1-0.dll", "ucrtbase.dll", "UCRTBASE.dll", "MSVCR120.dll", "MSVCR110.dll", "MSVCR100.dll" })
            {
                foreach (var name in new[] { "exit", "_exit", "_Exit", "quick_exit" })
                    imports.Overrides[module + "!" + name] = Marshal.GetFunctionPointerForDelegate(crtExit);
                imports.Overrides[module + "!abort"] = Marshal.GetFunctionPointerForDelegate(crtAbort);
            }
        }

        public static void Install(SystemImports imports)
        {
            InstallCrt(imports);
            Install(imports, new[]
            {
                "KERNEL32.dll", "kernel32.dll", "KERNELBASE.dll", "kernelbase.dll",
                "api-ms-win-core-processthreads-l1-1-0.dll",
                "api-ms-win-core-processthreads-l1-1-1.dll",
            });

            // ERROR_ACCESS_DENIED, which is the truth and a code every caller
            // already has a path for.
            createProcess = (a, b, c, d, e, f, g, h, i, j) =>
            {
                SetLastError(5);
                return 0;
            };
            shellExecute = (a, b, c, d, e, f) => 0;

            var create = Marshal.GetFunctionPointerForDelegate(createProcess);
            var shell = Marshal.GetFunctionPointerForDelegate(shellExecute);
            foreach (var module in new[] { "KERNEL32.dll", "kernel32.dll" })
            {
                imports.Overrides[module + "!CreateProcessW"] = create;
                imports.Overrides[module + "!CreateProcessA"] = create;
            }
            foreach (var module in new[] { "SHELL32.dll", "shell32.dll" })
            {
                imports.Overrides[module + "!ShellExecuteW"] = shell;
                imports.Overrides[module + "!ShellExecuteA"] = shell;
            }
        }

        [DllImport("api-ms-win-core-errorhandling-l1-1-0.dll")]
        private static extern void SetLastError(uint code);
    }
}
