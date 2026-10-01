using System;
using System.Runtime.InteropServices;

namespace Nativra.X86.Cpu
{
    /// <summary>
    /// Host virtual memory: reserve, commit and protect. Windows uses the
    /// VirtualAlloc family; Linux (for local test runs) uses mmap/mprotect.
    /// The console build swaps in the *FromApp variants through
    /// <see cref="Override"/>, since an app container may not call the plain ones.
    /// </summary>
    public static class HostPages
    {
        public interface IBackend
        {
            IntPtr Reserve(ulong size);
            bool Commit(IntPtr address, ulong size);
            bool Protect(IntPtr address, ulong size, bool write, bool execute);

            /// <summary>Gives the pages back; the next commit finds them zeroed.</summary>
            void Decommit(IntPtr address, ulong size);
            void Release(IntPtr address, ulong size);
        }

        /// <summary>Set by the host application before first use when the defaults are not allowed.</summary>
        public static IBackend Override;

        public static IBackend Current => Override ?? (IsWindows ? (IBackend)new Windows() : new Posix());

        public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        private sealed class Windows : IBackend
        {
            private const uint MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000, MEM_RELEASE = 0x8000, MEM_DECOMMIT = 0x4000;
            private const uint PAGE_NOACCESS = 0x01, PAGE_READONLY = 0x02, PAGE_READWRITE = 0x04;
            private const uint PAGE_EXECUTE_READ = 0x20, PAGE_EXECUTE_READWRITE = 0x40;

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint type, uint protect);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint protect, out uint old);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool VirtualFree(IntPtr address, UIntPtr size, uint type);

            public IntPtr Reserve(ulong size) =>
                VirtualAlloc(IntPtr.Zero, (UIntPtr)size, MEM_RESERVE, PAGE_NOACCESS);

            public bool Commit(IntPtr address, ulong size) =>
                VirtualAlloc(address, (UIntPtr)size, MEM_COMMIT, PAGE_READWRITE) != IntPtr.Zero;

            public bool Protect(IntPtr address, ulong size, bool write, bool execute)
            {
                var protect = execute
                    ? (write ? PAGE_EXECUTE_READWRITE : PAGE_EXECUTE_READ)
                    : (write ? PAGE_READWRITE : PAGE_READONLY);
                return VirtualProtect(address, (UIntPtr)size, protect, out _);
            }

            public void Decommit(IntPtr address, ulong size) => VirtualFree(address, (UIntPtr)size, MEM_DECOMMIT);

            public void Release(IntPtr address, ulong size) => VirtualFree(address, UIntPtr.Zero, MEM_RELEASE);
        }

        private sealed class Posix : IBackend
        {
            private const int PROT_NONE = 0, PROT_READ = 1, PROT_WRITE = 2, PROT_EXEC = 4;
            private const int MAP_PRIVATE = 0x02, MAP_FIXED = 0x10;

            // The same flags have different values on macOS (BSD) and Linux.
            private static readonly bool Mac = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
            private static readonly int MAP_ANONYMOUS = Mac ? 0x1000 : 0x20;
            private static readonly int MAP_NORESERVE = Mac ? 0x40 : 0x4000;

            [DllImport("libc", SetLastError = true)]
            private static extern IntPtr mmap(IntPtr addr, UIntPtr length, int prot, int flags, int fd, IntPtr offset);

            [DllImport("libc", SetLastError = true)]
            private static extern int mprotect(IntPtr addr, UIntPtr len, int prot);

            [DllImport("libc", SetLastError = true)]
            private static extern int munmap(IntPtr addr, UIntPtr length);

            public IntPtr Reserve(ulong size)
            {
                var p = mmap(IntPtr.Zero, (UIntPtr)size, PROT_NONE,
                    MAP_PRIVATE | MAP_ANONYMOUS | MAP_NORESERVE, -1, IntPtr.Zero);
                return p == new IntPtr(-1) ? IntPtr.Zero : p;
            }

            // Guest pages are 4 KB; Apple silicon's host pages are 16 KB, and
            // mprotect only takes whole host pages.
            private static readonly ulong HostPage = (ulong)Environment.SystemPageSize;

            private static void Round(IntPtr address, ulong size, out IntPtr start, out UIntPtr length)
            {
                var from = (ulong)address.ToInt64() & ~(HostPage - 1);
                var to = ((ulong)address.ToInt64() + size + HostPage - 1) & ~(HostPage - 1);
                start = new IntPtr((long)from);
                length = (UIntPtr)(to - from);
            }

            public bool Commit(IntPtr address, ulong size)
            {
                Round(address, size, out var start, out var length);
                return mprotect(start, length, PROT_READ | PROT_WRITE) == 0;
            }

            public bool Protect(IntPtr address, ulong size, bool write, bool execute)
            {
                Round(address, size, out var start, out var length);
                return mprotect(start, length,
                    PROT_READ | (write ? PROT_WRITE : 0) | (execute ? PROT_EXEC : 0)) == 0;
            }

            // Mapping fresh anonymous pages over the range is the portable way to
            // get them back zeroed: macOS's MADV_DONTNEED keeps the old contents.
            // Part of a host page cannot be dropped without its neighbours, so
            // that part is only zeroed and stays accessible.
            public unsafe void Decommit(IntPtr address, ulong size)
            {
                if (((ulong)address.ToInt64() & (HostPage - 1)) != 0 || (size & (HostPage - 1)) != 0)
                {
                    var p = (byte*)address;
                    for (ulong i = 0; i < size; i++) p[i] = 0;
                    return;
                }
                mmap(address, (UIntPtr)size, PROT_NONE,
                    MAP_PRIVATE | MAP_ANONYMOUS | MAP_NORESERVE | MAP_FIXED, -1, IntPtr.Zero);
            }

            public void Release(IntPtr address, ulong size) => munmap(address, (UIntPtr)size);
        }
    }
}
