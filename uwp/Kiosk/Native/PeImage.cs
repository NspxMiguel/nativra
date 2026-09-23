using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Kiosk.Native
{
    /// <summary>
    /// Loads a Windows binary into this process by hand: map the sections,
    /// rebase them, fill in the import table and mark the code executable.
    ///
    /// The console allows generating code at runtime — measured with JitProbe —
    /// and that is the whole reason this can work at all. What the platform
    /// refuses is LoadLibrary on a file that is not part of the package, which
    /// is exactly the step replaced here.
    /// </summary>
    public sealed class PeImage : IDisposable
    {
        private const uint MEM_COMMIT = 0x1000;
        private const uint MEM_RESERVE = 0x2000;
        private const uint MEM_RELEASE = 0x8000;
        private const uint PAGE_READWRITE = 0x04;
        private const uint PAGE_READONLY = 0x02;
        private const uint PAGE_EXECUTE_READ = 0x20;
        private const uint PAGE_EXECUTE_READWRITE = 0x40;

        [DllImport("api-ms-win-core-memory-l1-1-3.dll", SetLastError = true)]
        private static extern IntPtr VirtualAllocFromApp(
            IntPtr address, UIntPtr size, uint allocationType, uint protect);

        [DllImport("api-ms-win-core-memory-l1-1-3.dll", SetLastError = true)]
        private static extern bool VirtualProtectFromApp(
            IntPtr address, UIntPtr size, uint newProtect, out uint oldProtect);

        [DllImport("api-ms-win-core-memory-l1-1-0.dll", SetLastError = true)]
        private static extern bool VirtualFree(IntPtr address, UIntPtr size, uint freeType);

        // Without these two a mapped image crashes the moment its own runtime
        // starts: x64 code cannot unwind without its function table, and a
        // module with thread-local data reads rubbish until its callbacks run.
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool RtlAddFunctionTable(IntPtr table, uint count, ulong baseAddress);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void TlsCallback(IntPtr instance, uint reason, IntPtr reserved);

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryBasicInformation
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public uint AllocationProtect;
            public uint Alignment1;
            public IntPtr RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
            public uint Alignment2;
        }

        // The table of blocks is not a process-heap allocation, so asking the
        // heap how big it is faults. Asking the memory manager what is readable
        // around it does not, and bounding the copy is all that is needed.
        [DllImport("api-ms-win-core-memory-l1-1-0.dll", EntryPoint = "VirtualQuery",
            SetLastError = true)]
        private static extern UIntPtr QueryMemory(
            IntPtr address, out MemoryBasicInformation info, UIntPtr length);

        /// <summary>
        /// Off by default. Setting up thread-local storage rewrites a pointer
        /// inside the thread environment block, and getting that wrong takes
        /// the process down — so it happens only when a measurement asks.
        /// </summary>
        /// <summary>
        /// How far the thread-local setup is allowed to go. Each step is one
        /// instruction that can take the process down, so they are taken one
        /// at a time and written down before they are taken:
        /// 1 read the thread block, 2 read its table, 3 measure the table,
        /// 4 write the module's index, 5 write the block into the table.
        /// </summary>
        public static int TlsLevel;

        public static bool EnableTls => TlsLevel > 0;

        /// <summary>
        /// Whether the thread's table of blocks may be replaced with a longer
        /// one. Writing into a table that is already long enough touches one
        /// pointer; replacing it moves everything the runtime is using.
        /// </summary>
        /// <summary>What the last attempt did, so a crash leaves a trail.</summary>
        public static string TlsNote = "";

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr TebDelegate();

        private IntPtr baseAddress;
        private uint imageSize;

        public string Name { get; }

        /// <summary>Imports nothing could answer. Empty means the load is complete.</summary>
        public List<string> Unresolved { get; } = new List<string>();

        public IntPtr EntryPoint { get; private set; }

        private readonly Dictionary<string, IntPtr> exports =
            new Dictionary<string, IntPtr>(StringComparer.Ordinal);

        private PeImage(string name)
        {
            Name = name;
        }

        public static PeImage Load(string name, byte[] file, Func<string, string, IntPtr> resolver)
        {
            var image = new PeImage(name);
            image.Map(file);
            image.Relocate();
            image.BindImports(file, resolver);
            image.Protect(file);
            image.ReadExports();
            image.RegisterExceptions();
            try { image.SetUpTls(); }
            finally { ThreadTls.Restore(); }
            return image;
        }

        // ------------------------------------------------------------- headers

        private int ntHeader;
        private bool pe32Plus;
        private ulong preferredBase;
        private ushort sectionCount;
        private int sectionTable;
        private int optionalHeader;

        private static ushort U16(byte[] b, int at) => BitConverter.ToUInt16(b, at);
        private static uint U32(byte[] b, int at) => BitConverter.ToUInt32(b, at);
        private static ulong U64(byte[] b, int at) => BitConverter.ToUInt64(b, at);

        private void Map(byte[] file)
        {
            if (file.Length < 0x40 || U16(file, 0) != 0x5A4D)
            {
                throw new BadImageFormatException($"{Name}: not a PE");
            }
            ntHeader = (int)U32(file, 0x3C);
            if (U32(file, ntHeader) != 0x00004550)
            {
                throw new BadImageFormatException($"{Name}: no PE signature");
            }

            sectionCount = U16(file, ntHeader + 6);
            var optionalSize = U16(file, ntHeader + 20);
            optionalHeader = ntHeader + 24;
            sectionTable = optionalHeader + optionalSize;

            var magic = U16(file, optionalHeader);
            pe32Plus = magic == 0x20B;
            if (!pe32Plus)
            {
                throw new BadImageFormatException($"{Name}: 32-bit images are not loaded here");
            }

            imageSize = U32(file, optionalHeader + 56);
            preferredBase = U64(file, optionalHeader + 24);
            var headerSize = U32(file, optionalHeader + 60);

            // Asking for the preferred address first saves relocating; failing
            // that is normal and the relocation pass handles it.
            baseAddress = VirtualAllocFromApp(
                (IntPtr)(long)preferredBase, (UIntPtr)imageSize,
                MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (baseAddress == IntPtr.Zero)
            {
                baseAddress = VirtualAllocFromApp(
                    IntPtr.Zero, (UIntPtr)imageSize,
                    MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            }
            if (baseAddress == IntPtr.Zero)
            {
                throw new OutOfMemoryException(
                    $"{Name}: could not reserve {imageSize} bytes");
            }

            Marshal.Copy(file, 0, baseAddress, (int)headerSize);

            for (var i = 0; i < sectionCount; i++)
            {
                var section = sectionTable + i * 40;
                var virtualAddress = U32(file, section + 12);
                var rawSize = U32(file, section + 16);
                var rawPointer = U32(file, section + 20);
                if (rawSize == 0) continue;
                Marshal.Copy(
                    file, (int)rawPointer,
                    baseAddress + (int)virtualAddress, (int)rawSize);
            }

            EntryPoint = U32(file, optionalHeader + 16) == 0
                ? IntPtr.Zero
                : baseAddress + (int)U32(file, optionalHeader + 16);
        }

        private int DirectoryRva(int index)
        {
            // The data directories follow the optional header; on PE32+ they
            // start 112 bytes in.
            return (int)U32(MarshalRead(optionalHeader + 112 + index * 8, 4), 0);
        }

        private int DirectorySize(int index) =>
            (int)U32(MarshalRead(optionalHeader + 112 + index * 8 + 4, 4), 0);

        /// <summary>Reads back from the mapped image rather than the file.</summary>
        private byte[] MarshalRead(int offset, int length)
        {
            var buffer = new byte[length];
            Marshal.Copy(baseAddress + offset, buffer, 0, length);
            return buffer;
        }

        // ---------------------------------------------------------- relocation

        private void Relocate()
        {
            var delta = (long)baseAddress - (long)preferredBase;
            if (delta == 0) return;

            var rva = DirectoryRva(5);
            var size = DirectorySize(5);
            if (rva == 0 || size == 0)
            {
                throw new BadImageFormatException(
                    $"{Name}: needs rebasing and carries no relocations");
            }

            var at = rva;
            var end = rva + size;
            while (at < end)
            {
                var header = MarshalRead(at, 8);
                var pageRva = (int)U32(header, 0);
                var blockSize = (int)U32(header, 4);
                if (blockSize < 8) break;

                var count = (blockSize - 8) / 2;
                var entries = MarshalRead(at + 8, count * 2);
                for (var i = 0; i < count; i++)
                {
                    var entry = U16(entries, i * 2);
                    var type = entry >> 12;
                    var offset = entry & 0xFFF;
                    // Type 10 is the only one a 64-bit image uses; 0 is padding.
                    if (type != 10) continue;
                    var target = baseAddress + pageRva + offset;
                    var value = (long)Marshal.ReadInt64(target);
                    Marshal.WriteInt64(target, value + delta);
                }
                at += blockSize;
            }
        }

        // ------------------------------------------------------------- imports

        private void BindImports(byte[] file, Func<string, string, IntPtr> resolver)
        {
            var rva = DirectoryRva(1);
            if (rva == 0) return;

            var at = rva;
            while (true)
            {
                var descriptor = MarshalRead(at, 20);
                var nameRva = (int)U32(descriptor, 12);
                if (nameRva == 0) break;

                var module = ReadCString(nameRva);
                var lookup = (int)U32(descriptor, 0);
                var address = (int)U32(descriptor, 16);
                var thunk = lookup != 0 ? lookup : address;

                for (var i = 0; ; i++)
                {
                    var entry = (ulong)Marshal.ReadInt64(baseAddress + thunk + i * 8);
                    if (entry == 0) break;

                    string function;
                    if ((entry & 0x8000000000000000UL) != 0)
                    {
                        function = "#" + (entry & 0xFFFF);
                    }
                    else
                    {
                        // The name is preceded by a two-byte hint.
                        function = ReadCString((int)(entry & 0x7FFFFFFF) + 2);
                    }

                    var resolved = resolver(module, function);
                    if (resolved == IntPtr.Zero)
                    {
                        Unresolved.Add(module + "!" + function);
                    }
                    Marshal.WriteIntPtr(baseAddress + address + i * 8, resolved);
                }
                at += 20;
            }
        }

        private string ReadCString(int rva)
        {
            var bytes = new List<byte>(32);
            for (var i = 0; i < 512; i++)
            {
                var b = Marshal.ReadByte(baseAddress + rva + i);
                if (b == 0) break;
                bytes.Add(b);
            }
            return System.Text.Encoding.ASCII.GetString(bytes.ToArray());
        }

        // ------------------------------------------------------------ finishing

        private void Protect(byte[] file)
        {
            const uint SCN_MEM_EXECUTE = 0x20000000;
            const uint SCN_MEM_WRITE = 0x80000000;

            for (var i = 0; i < sectionCount; i++)
            {
                var section = sectionTable + i * 40;
                var virtualSize = U32(file, section + 8);
                var virtualAddress = U32(file, section + 12);
                var characteristics = U32(file, section + 36);
                if (virtualSize == 0) continue;

                var protect = PAGE_READONLY;
                if ((characteristics & SCN_MEM_EXECUTE) != 0)
                {
                    protect = (characteristics & SCN_MEM_WRITE) != 0
                        ? PAGE_EXECUTE_READWRITE
                        : PAGE_EXECUTE_READ;
                }
                else if ((characteristics & SCN_MEM_WRITE) != 0)
                {
                    protect = PAGE_READWRITE;
                }

                VirtualProtectFromApp(
                    baseAddress + (int)virtualAddress, (UIntPtr)virtualSize,
                    protect, out _);
            }
        }

        private void ReadExports()
        {
            var rva = DirectoryRva(0);
            if (rva == 0) return;

            var header = MarshalRead(rva, 40);
            var count = (int)U32(header, 24);
            var functions = (int)U32(header, 28);
            var names = (int)U32(header, 32);
            var ordinals = (int)U32(header, 36);
            if (names == 0) return;

            for (var i = 0; i < count; i++)
            {
                var nameRva = (int)U32(MarshalRead(names + i * 4, 4), 0);
                var ordinal = U16(MarshalRead(ordinals + i * 2, 2), 0);
                var address = (int)U32(MarshalRead(functions + ordinal * 4, 4), 0);
                exports[ReadCString(nameRva)] = baseAddress + address;
            }
        }

        /// <summary>
        /// x64 unwinding is table-driven: code that throws, or a runtime that
        /// uses structured exceptions internally, needs its .pdata registered
        /// or the first exception takes the process down.
        /// </summary>
        private void RegisterExceptions()
        {
            try
            {
                var rva = DirectoryRva(3);
                var size = DirectorySize(3);
                if (rva == 0 || size == 0) return;
                // Each RUNTIME_FUNCTION is three DWORDs.
                RtlAddFunctionTable(
                    baseAddress + rva, (uint)(size / 12), (ulong)baseAddress.ToInt64());
                ExceptionsRegistered = true;
            }
            catch
            {
                // Not being able to register is worth knowing, not worth dying for.
            }
        }

        public bool ExceptionsRegistered { get; private set; }
        public int TlsCallbacksRun { get; private set; }

        /// <summary>
        /// Thread-local storage, which the real loader sets up and which a
        /// module compiled with __declspec(thread) cannot live without.
        ///
        /// Three things have to happen: the module is given a slot number, a
        /// copy of its template is made for this thread, and the thread's own
        /// table of blocks is grown to hold it. That table hangs off the thread
        /// environment block, and reaching it needs one instruction the
        /// language cannot write — so it is assembled at runtime, which this
        /// console allows.
        /// </summary>
        private void SetUpTls()
        {
            try
            {
                if (!EnableTls) return;
                var rva = DirectoryRva(9);
                if (rva == 0) { TlsNote = "no tls directory"; return; }

                var directory = MarshalRead(rva, 40);
                var start = BitConverter.ToInt64(directory, 0);
                var end = BitConverter.ToInt64(directory, 8);
                var indexAddress = BitConverter.ToInt64(directory, 16);
                var callbacks = BitConverter.ToInt64(directory, 24);
                var zeroFill = BitConverter.ToUInt32(directory, 32);
                TlsNote = $"dir ok size={end - start}+{zeroFill}";
                Step?.Invoke(TlsNote);
                if (TlsLevel < 1) return;

                var teb = CurrentTeb();
                TlsNote += $" teb=0x{teb.ToInt64():X}";
                Step?.Invoke(TlsNote);
                if (teb == IntPtr.Zero || TlsLevel < 2) return;

                var slotsPointer = teb + 0x58;
                var existing = Marshal.ReadIntPtr(slotsPointer);
                TlsNote += $" table=0x{existing.ToInt64():X}";
                Step?.Invoke(TlsNote);
                if (TlsLevel < 3) return;

                var existingSlots = 0;
                if (existing != IntPtr.Zero)
                {
                    var length = (UIntPtr)(uint)Marshal.SizeOf<MemoryBasicInformation>();
                    if (QueryMemory(existing, out var info, length) != UIntPtr.Zero)
                    {
                        // How much is readable from the pointer to the end of
                        // its region: an upper bound, which is what keeps a
                        // copy from reading off the end.
                        var readable = (long)info.RegionSize
                            - ((long)existing - (long)info.BaseAddress);
                        if (readable > 0 && readable < (1 << 20))
                        {
                            existingSlots = (int)(readable / 8);
                        }
                        TlsNote += $" readable={readable} protect=0x{info.Protect:X}";
                    }
                    else
                    {
                        TlsNote += " query refused";
                    }
                }
                Step?.Invoke(TlsNote);
                if (TlsLevel < 4) return;

                var templateSize = checked((int)(end - start));
                var total = checked(templateSize + (int)zeroFill);
                var template = new byte[Math.Max(templateSize, 0)];
                if (templateSize > 0) Marshal.Copy((IntPtr)start, template, 0, templateSize);
                var slot = ThreadTls.Remember(template, total);
                TlsNote += $" slot={slot} of {existingSlots}";
                Step?.Invoke(TlsNote);

                Marshal.WriteInt32((IntPtr)indexAddress, slot);
                TlsNote += " index written";
                Step?.Invoke(TlsNote);
                if (TlsLevel < 5) return;

                if (!ThreadTls.Adopt()) throw new InvalidOperationException(ThreadTls.LastError);
                TlsNote += " private vector installed";
                TlsSlot = slot;

                // And the same copy for every thread made after this one. The
                // loader used to fill in only the thread that did the loading,
                // so the main thread had its variables and the thirty the game
                // starts afterwards had whatever was at that address.
                Step?.Invoke(TlsNote);
                if (TlsLevel < 6 || callbacks == 0) return;

                for (var i = 0; i < 64; i++)
                {
                    var entry = Marshal.ReadIntPtr((IntPtr)(callbacks + i * 8));
                    if (entry == IntPtr.Zero) break;
                    var callback = Marshal.GetDelegateForFunctionPointer<TlsCallback>(entry);
                    callback(baseAddress, 1, IntPtr.Zero);
                    TlsCallbacksRun++;
                }
            }
            catch (Exception error)
            {
                TlsNote += " | " + error.GetType().Name + ": " + error.Message;
            }
        }

        /// <summary>Called after each step so a crash leaves the last one on disk.</summary>
        public static Action<string> Step;

        /// <summary>
        /// Points the process at a mapped image. A program's startup code asks
        /// the system which image it is — and gets the host application, whose
        /// headers say nothing about it. The answer lives at 0x10 inside the
        /// process block, which hangs off the thread block at 0x60.
        /// </summary>
        public static IntPtr SetProcessImageBase(IntPtr newBase)
        {
            var teb = CurrentTeb();
            if (teb == IntPtr.Zero) return IntPtr.Zero;
            var peb = Marshal.ReadIntPtr(teb + 0x60);
            if (peb == IntPtr.Zero) return IntPtr.Zero;
            var previous = Marshal.ReadIntPtr(peb + 0x10);
            Marshal.WriteIntPtr(peb + 0x10, newBase);
            return previous;
        }

        public int TlsSlot { get; private set; } = -1;

        private static IntPtr tebReader;

        internal static int ReadableBytes(IntPtr address)
        {
            var size = (UIntPtr)(uint)Marshal.SizeOf<MemoryBasicInformation>();
            if (QueryMemory(address, out var info, size) == UIntPtr.Zero ||
                (info.Protect & 0x101) != 0) return 0;
            var remaining = info.RegionSize.ToInt64() - (address.ToInt64() - info.BaseAddress.ToInt64());
            return (int)Math.Max(0, Math.Min(remaining, 32768));
        }

        /// <summary>
        /// mov rax, gs:[0x30] ; ret — the thread environment block's own
        /// address, which no managed call exposes.
        /// </summary>
        internal static IntPtr CurrentTeb()
        {
            if (tebReader == IntPtr.Zero)
            {
                var code = new byte[]
                {
                    0x65, 0x48, 0x8B, 0x04, 0x25, 0x30, 0x00, 0x00, 0x00, // mov rax, gs:[0x30]
                    0xC3,                                                 // ret
                };
                var page = VirtualAllocFromApp(
                    IntPtr.Zero, (UIntPtr)64, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
                if (page == IntPtr.Zero) return IntPtr.Zero;
                Marshal.Copy(code, 0, page, code.Length);
                VirtualProtectFromApp(page, (UIntPtr)64, PAGE_EXECUTE_READ, out _);
                tebReader = page;
            }
            return Marshal.GetDelegateForFunctionPointer<TebDelegate>(tebReader)();
        }

        public IntPtr Export(string name) =>
            exports.TryGetValue(name, out var address) ? address : IntPtr.Zero;

        public int ExportCount => exports.Count;

        public IntPtr BaseAddress => baseAddress;

        public uint ImageSize => imageSize;

        /// <summary>Whether an address falls inside this image.</summary>
        public bool Contains(IntPtr address)
        {
            var value = (ulong)address.ToInt64();
            var start = (ulong)baseAddress.ToInt64();
            return value >= start && value < start + imageSize;
        }

        public void Dispose()
        {
            if (baseAddress == IntPtr.Zero) return;
            VirtualFree(baseAddress, UIntPtr.Zero, MEM_RELEASE);
            baseAddress = IntPtr.Zero;
        }
    }
}
