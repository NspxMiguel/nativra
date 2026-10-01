using System;
using System.Threading.Tasks;

namespace Kiosk.Native
{
    /// <summary>
    /// Shared bound for the storage-broker `*FromAppW` family (used directly by
    /// <see cref="CrtFiles"/>, <see cref="FileWatch"/> and <see cref="X86Files"/>):
    /// one of them (`GetFileAttributesExFromAppW`) was found to hang outright on
    /// a real game (see docs/progress/hades-deadlock-2609.md). This covers the
    /// directory/delete/move/copy/list siblings that are not on the hot per-file
    /// path — `CreateFileFromAppW` and attribute lookups stay direct, since
    /// wrapping the hottest call blind, without an on-console timing comparison,
    /// risks trading a rare hang for a constant regression.
    /// </summary>
    internal static class BoundedBroker
    {
        public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

        /// <summary>Runs a bool-returning broker call with a bound; a timeout counts as failure
        /// with Win32 error 2 (not found), the same fallback <see cref="FileWatch"/>'s fix uses.</summary>
        public static bool Call(Func<bool> call, out int error)
        {
            var task = Task.Run(() =>
            {
                var ok = call();
                var code = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                return (ok, code);
            });
            if (task.Wait(Timeout))
            {
                var (ok, code) = task.Result;
                error = code;
                return ok;
            }
            error = 2;
            return false;
        }

        /// <summary>Same bound for a broker call that returns a handle rather than a bool
        /// (e.g. FindFirstFileExFromAppW); a timeout counts as the given invalid value.</summary>
        public static IntPtr Call(Func<IntPtr> call, IntPtr invalid, out int error)
        {
            var task = Task.Run(() =>
            {
                var handle = call();
                var code = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                return (handle, code);
            });
            if (task.Wait(Timeout))
            {
                var (handle, code) = task.Result;
                error = code;
                return handle;
            }
            error = 2;
            return invalid;
        }
    }
}
