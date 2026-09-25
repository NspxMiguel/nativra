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
            private const int MAP_PRIVATE = 0x02, MAP_ANONYMOUS = 0x20, MAP_NORESERVE = 0x4000;

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

            public bool Commit(IntPtr address, ulong size) =>
                mprotect(address, (UIntPtr)size, PROT_READ | PROT_WRITE) == 0;

            public bool Protect(IntPtr address, ulong size, bool write, bool execute) =>
                mprotect(address, (UIntPtr)size,
                    PROT_READ | (write ? PROT_WRITE : 0) | (execute ? PROT_EXEC : 0)) == 0;

            [DllImport("libc", SetLastError = true)]
            private static extern int madvise(IntPtr addr, UIntPtr length, int advice);

            public void Decommit(IntPtr address, ulong size)
            {
                const int MADV_DONTNEED = 4;
                madvise(address, (UIntPtr)size, MADV_DONTNEED);
                mprotect(address, (UIntPtr)size, PROT_NONE);
            }

            public void Release(IntPtr address, ulong size) => munmap(address, (UIntPtr)size);
        }
    }
}
