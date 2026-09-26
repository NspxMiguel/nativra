using System;

namespace Nativra.X86.Cpu
{
    /// <summary>
    /// Raised when guest code touches an address nothing is mapped at. Carries
    /// the guest address so the caller can turn it into a 32-bit access
    /// violation for the guest's own exception handlers.
    /// </summary>
    public sealed class GuestFaultException : Exception
    {
        public uint Address { get; }
        public bool Write { get; }

        public GuestFaultException(uint address, bool write)
            : base($"guest {(write ? "write" : "read")} fault at 0x{address:X8}")
        {
            Address = address;
            Write = write;
        }
    }

    /// <summary>
    /// A 32-bit guest address space, in one of two backings:
    ///
    /// * managed pages (4 KB, allocated on demand, looked up through a flat
    ///   table) — the interpreter-only mode unit tests use on any platform;
    /// * a native reservation of the whole 4 GB, committed page by page, where
    ///   guest address + <see cref="HostBase"/> is the host pointer. The JIT
    ///   emits code against that base, and the interpreter reads the very same
    ///   bytes, so both engines always agree on what the guest sees.
    /// </summary>
    public sealed unsafe class GuestMemory : ICodeReader, IDisposable
    {
        public const int PageShift = 12;
        public const int PageSize = 1 << PageShift;
        private const uint PageMask = PageSize - 1;
        private const int PageCount = 1 << (32 - PageShift);

        // Headroom past 4 GB so a 16-byte access at the very top cannot leave the reservation.
        private const ulong NativeSpan = (1UL << 32) + 0x10000;

        private readonly byte[][] pages;
        private readonly bool[] committed;
        private byte* host;

        /// <summary>Host address of guest address 0, or zero for managed backing.</summary>
        public IntPtr HostBase => (IntPtr)host;

        public bool IsNative => host != null;

        public GuestMemory() : this(false) { }

        /// <param name="native">Reserve a real 4 GB region the JIT can address directly.</param>
        public GuestMemory(bool native)
        {
            if (native)
            {
                host = (byte*)HostPages.Current.Reserve(NativeSpan);
                if (host == null) throw new OutOfMemoryException("could not reserve the 4 GB guest space");
                committed = new bool[PageCount];
            }
            else
            {
                pages = new byte[PageCount][];
            }
        }

        public void Dispose()
        {
            if (host != null)
            {
                HostPages.Current.Release((IntPtr)host, NativeSpan);
                host = null;
            }
        }

        /// <summary>Makes a range addressable, zero-filled. Already-mapped pages keep their contents.</summary>
        public void Map(uint address, uint size)
        {
            if (size == 0) return;
            var first = address >> PageShift;
            var last = (uint)(((ulong)address + size - 1) >> PageShift);
            for (var page = first; page <= last; page++)
            {
                if (host != null)
                {
                    if (!committed[page])
                    {
                        if (!HostPages.Current.Commit((IntPtr)(host + ((ulong)page << PageShift)), PageSize))
                            throw new OutOfMemoryException($"could not commit guest page 0x{page << PageShift:X8}");
                        committed[page] = true;
                    }
                }
                else if (pages[page] == null)
                {
                    pages[page] = new byte[PageSize];
                }
                if (page == PageCount - 1) break;
            }
        }

        public void Unmap(uint address, uint size)
        {
            if (size == 0) return;
            var first = address >> PageShift;
            var last = (uint)(((ulong)address + size - 1) >> PageShift);
            for (var page = first; page <= last; page++)
            {
                if (host != null)
                {
                    if (committed[page])
                    {
                        // Keep the reservation, drop the page: the next touch faults,
                        // and mapping it again finds it zeroed.
                        HostPages.Current.Decommit((IntPtr)(host + ((ulong)page << PageShift)), PageSize);
                        committed[page] = false;
                    }
                }
                else
                {
                    pages[page] = null;
                }
                if (page == PageCount - 1) break;
            }
        }

        public bool IsMapped(uint address) =>
            host != null ? committed[address >> PageShift] : pages[address >> PageShift] != null;

        /// <summary>
        /// Finds a free, page-aligned range at or above <paramref name="hint"/>.
        /// Used for stacks, heaps and TEB/PEB blocks; returns 0 when nothing fits.
        /// </summary>
        public uint FindFree(uint size, uint hint = 0x00100000)
        {
            var need = (size + PageMask) >> PageShift;
            uint run = 0;
            for (var page = hint >> PageShift; page < PageCount; page++)
            {
                run = IsMappedPage(page) ? 0 : run + 1;
                if (run == need) return (page - need + 1) << PageShift;
            }
            return 0;
        }

        private bool IsMappedPage(uint page) => host != null ? committed[page] : pages[page] != null;

        /// <summary>Host pointer for a guest address, after checking the page is there.</summary>
        private byte* NativeAt(uint address, int size, bool write)
        {
            if (!committed[address >> PageShift]) throw new GuestFaultException(address, write);
            var end = address + (uint)(size - 1);
            if ((end >> PageShift) != (address >> PageShift) && !committed[end >> PageShift])
                throw new GuestFaultException(end, write);
            return host + address;
        }

        private byte[] PageFor(uint address, bool write)
        {
            var page = pages[address >> PageShift];
            if (page == null) throw new GuestFaultException(address, write);
            return page;
        }

        public byte Read8(uint address) =>
            host != null ? *NativeAt(address, 1, false) : PageFor(address, false)[address & PageMask];

        public void Write8(uint address, byte value)
        {
            if (host != null) *NativeAt(address, 1, true) = value;
            else PageFor(address, true)[address & PageMask] = value;
        }

        public ushort Read16(uint address)
        {
            if (host != null) return *(ushort*)NativeAt(address, 2, false);
            if ((address & PageMask) <= PageSize - 2)
            {
                var page = PageFor(address, false);
                var at = address & PageMask;
                return (ushort)(page[at] | (page[at + 1] << 8));
            }
            return (ushort)(Read8(address) | (Read8(address + 1) << 8));
        }

        public uint Read32(uint address)
        {
            if (host != null) return *(uint*)NativeAt(address, 4, false);
            if ((address & PageMask) <= PageSize - 4)
            {
                var page = PageFor(address, false);
                return BitConverter.ToUInt32(page, (int)(address & PageMask));
            }
            return (uint)(Read8(address) | (Read8(address + 1) << 8) |
                          (Read8(address + 2) << 16) | (Read8(address + 3) << 24));
        }

        public ulong Read64(uint address) => Read32(address) | ((ulong)Read32(address + 4) << 32);

        public void Write16(uint address, ushort value)
        {
            if (host != null) { *(ushort*)NativeAt(address, 2, true) = value; return; }
            if ((address & PageMask) <= PageSize - 2)
            {
                var page = PageFor(address, true);
                var at = address & PageMask;
                page[at] = (byte)value;
                page[at + 1] = (byte)(value >> 8);
                return;
            }
            Write8(address, (byte)value);
            Write8(address + 1, (byte)(value >> 8));
        }

        public void Write32(uint address, uint value)
        {
            if (host != null) { *(uint*)NativeAt(address, 4, true) = value; return; }
            if ((address & PageMask) <= PageSize - 4)
            {
                var page = PageFor(address, true);
                var at = (int)(address & PageMask);
                page[at] = (byte)value;
                page[at + 1] = (byte)(value >> 8);
                page[at + 2] = (byte)(value >> 16);
                page[at + 3] = (byte)(value >> 24);
                return;
            }
            Write8(address, (byte)value);
            Write8(address + 1, (byte)(value >> 8));
            Write8(address + 2, (byte)(value >> 16));
            Write8(address + 3, (byte)(value >> 24));
        }

        // --- atomics --------------------------------------------------------
        // Guest threads are green threads on one host thread today, but the
        // memory is shared with host code (audio callbacks, COM objects), so
        // the Interlocked family is a real locked operation on the host
        // address whenever the guest space is native.

        /// <summary>Atomically: if [address] == comparand then [address] = value. Returns the old value.</summary>
        public uint CompareExchange32(uint address, uint value, uint comparand)
        {
            if (host != null)
                return (uint)System.Threading.Interlocked.CompareExchange(ref *(int*)NativeAt(address, 4, true), (int)value, (int)comparand);
            var old = Read32(address);
            if (old == comparand) Write32(address, value);
            return old;
        }

        /// <summary>Atomically: [address] = value. Returns the old value.</summary>
        public uint Exchange32(uint address, uint value)
        {
            if (host != null)
                return (uint)System.Threading.Interlocked.Exchange(ref *(int*)NativeAt(address, 4, true), (int)value);
            var old = Read32(address);
            Write32(address, value);
            return old;
        }

        /// <summary>Atomically: [address] += addend. Returns the old value.</summary>
        public uint ExchangeAdd32(uint address, uint addend)
        {
            if (host != null)
                return (uint)(System.Threading.Interlocked.Add(ref *(int*)NativeAt(address, 4, true), (int)addend) - (int)addend);
            var old = Read32(address);
            Write32(address, old + addend);
            return old;
        }

        /// <summary>Atomically: if [address] == comparand then [address] = value (64-bit). Returns the old value.</summary>
        public ulong CompareExchange64(uint address, ulong value, ulong comparand)
        {
            if (host != null)
                return (ulong)System.Threading.Interlocked.CompareExchange(ref *(long*)NativeAt(address, 8, true), (long)value, (long)comparand);
            var old = Read64(address);
            if (old == comparand) Write64(address, value);
            return old;
        }

        public void Write64(uint address, ulong value)
        {
            Write32(address, (uint)value);
            Write32(address + 4, (uint)(value >> 32));
        }

        public void WriteBytes(uint address, byte[] data, int offset = 0, int count = -1)
        {
            if (count < 0) count = data.Length - offset;
            for (var i = 0; i < count; i++) Write8(address + (uint)i, data[offset + i]);
        }

        public byte[] ReadBytes(uint address, int count)
        {
            var result = new byte[count];
            for (var i = 0; i < count; i++) result[i] = Read8(address + (uint)i);
            return result;
        }

        /// <summary>A NUL-terminated byte string, capped so a missing terminator cannot run away.</summary>
        public string ReadAnsi(uint address, int max = 4096)
        {
            var bytes = new System.Collections.Generic.List<byte>();
            for (var i = 0; i < max; i++)
            {
                var b = Read8(address + (uint)i);
                if (b == 0) break;
                bytes.Add(b);
            }
            return System.Text.Encoding.ASCII.GetString(bytes.ToArray());
        }

        public string ReadUnicode(uint address, int max = 4096)
        {
            var chars = new System.Text.StringBuilder();
            for (var i = 0; i < max; i++)
            {
                var c = Read16(address + (uint)(i * 2));
                if (c == 0) break;
                chars.Append((char)c);
            }
            return chars.ToString();
        }

        public void WriteAnsi(uint address, string text)
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(text);
            WriteBytes(address, bytes);
            Write8(address + (uint)bytes.Length, 0);
        }

        public void WriteUnicode(uint address, string text)
        {
            for (var i = 0; i < text.Length; i++) Write16(address + (uint)(i * 2), text[i]);
            Write16(address + (uint)(text.Length * 2), 0);
        }
    }
}
