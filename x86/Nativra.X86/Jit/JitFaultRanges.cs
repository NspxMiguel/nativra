using System.Collections.Generic;

namespace Nativra.X86.Jit
{
    /// <summary>Exact block ownership, independent of the executable page registry.</summary>
    internal sealed class JitFaultRanges
    {
        internal struct Range
        {
            public ulong Start, End, Stub;
            public int[] HostOffsets;
            public uint[] GuestEips;
        }

        private readonly Dictionary<ulong, Range> entries = new Dictionary<ulong, Range>();

        public int Count => entries.Count;

        public void Register(ulong start, int length, ulong stub, int[] offsets, uint[] eips)
        {
            entries[start] = new Range
            {
                Start = start, End = start + (ulong)length, Stub = stub,
                HostOffsets = offsets, GuestEips = eips,
            };
        }

        public void Unregister(ulong start) => entries.Remove(start);

        public Range? Find(ulong address)
        {
            foreach (var entry in entries.Values)
                if (address >= entry.Start && address < entry.End) return entry;
            return null;
        }
    }
}
