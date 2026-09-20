using System;
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

        public static void Install(SystemImports imports)
        {
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
