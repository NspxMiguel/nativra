using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Kiosk.Native
{
    /// <summary>
    /// A stand-in for a COM object, method by method.
    ///
    /// A COM object is a pointer to a table of function pointers, so standing
    /// in for one is a matter of building another table: every entry forwards
    /// to the original, except the handful we mean to answer differently. The
    /// forwarding entries are generated code — they swap the object pointer in
    /// the first argument register and jump, which leaves the caller's return
    /// address alone and costs nothing per call.
    ///
    /// This is what lets a game's graphics calls land somewhere the console
    /// will accept without the game being changed: it asks the same question,
    /// and one answer in the table is ours.
    /// </summary>
    public sealed class ComProxy
    {
        private const uint MEM_COMMIT = 0x1000;
        private const uint MEM_RESERVE = 0x2000;
        private const uint PAGE_READWRITE = 0x04;
        private const uint PAGE_EXECUTE_READ = 0x20;

        [DllImport("api-ms-win-core-memory-l1-1-3.dll", SetLastError = true)]
        private static extern IntPtr VirtualAllocFromApp(
            IntPtr address, UIntPtr size, uint allocationType, uint protect);

        [DllImport("api-ms-win-core-memory-l1-1-3.dll", SetLastError = true)]
        private static extern bool VirtualProtectFromApp(
            IntPtr address, UIntPtr size, uint newProtect, out uint oldProtect);

        private const int ThunkSize = 32;
        private const int Capacity = 256;

        private IntPtr page;
        private int used;

        /// <summary>Kept so the collector cannot take what native code holds.</summary>
        private readonly List<object> alive = new List<object>();

        public void Keep(object thing) => alive.Add(thing);

        /// <summary>The object each proxy stands in for, by proxy address.</summary>
        public readonly Dictionary<IntPtr, IntPtr> Behind =
            new Dictionary<IntPtr, IntPtr>();

        /// <summary>mov rcx, original ; mov rax, target ; jmp rax.</summary>
        private IntPtr Forward(IntPtr original, IntPtr target)
        {
            if (page == IntPtr.Zero)
            {
                page = VirtualAllocFromApp(
                    IntPtr.Zero, (UIntPtr)(ThunkSize * Capacity),
                    MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
                if (page == IntPtr.Zero) return target;
            }
            if (used >= Capacity) return target;

            var code = new List<byte> { 0x48, 0xB9 };
            code.AddRange(BitConverter.GetBytes(original.ToInt64()));
            code.AddRange(new byte[] { 0x48, 0xB8 });
            code.AddRange(BitConverter.GetBytes(target.ToInt64()));
            code.AddRange(new byte[] { 0xFF, 0xE0 });

            var at = page + used * ThunkSize;
            Marshal.Copy(code.ToArray(), 0, at, code.Count);
            used++;
            return at;
        }

        /// <summary>
        /// Builds the stand-in. <paramref name="methods"/> is how many entries
        /// the interface has, counting the three of IUnknown — getting it wrong
        /// by too few loses methods, and by too many reads past the table.
        /// </summary>
        public IntPtr Wrap(IntPtr original, int methods, Dictionary<int, IntPtr> ours)
        {
            if (original == IntPtr.Zero) return IntPtr.Zero;

            Unseal();
            var realTable = Marshal.ReadIntPtr(original);
            var table = Marshal.AllocHGlobal(IntPtr.Size * methods);
            for (var slot = 0; slot < methods; slot++)
            {
                IntPtr entry;
                if (ours != null && ours.TryGetValue(slot, out var mine))
                {
                    entry = mine;
                }
                else
                {
                    entry = Forward(
                        original, Marshal.ReadIntPtr(realTable, slot * IntPtr.Size));
                }
                Marshal.WriteIntPtr(table, slot * IntPtr.Size, entry);
            }
            Seal();

            // The object itself: a pointer to the table, and the original kept
            // beside it so an override can reach what it is standing in for.
            var proxy = Marshal.AllocHGlobal(IntPtr.Size * 2);
            Marshal.WriteIntPtr(proxy, 0, table);
            Marshal.WriteIntPtr(proxy, IntPtr.Size, original);
            Behind[proxy] = original;
            return proxy;
        }

        /// <summary>The object a proxy stands in for, from inside an override.</summary>
        public static IntPtr Original(IntPtr proxy) =>
            proxy == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(proxy, IntPtr.Size);

        /// <summary>One method of a COM object, by slot.</summary>
        public static IntPtr Method(IntPtr instance, int slot) =>
            Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size);

        private void Unseal()
        {
            if (page == IntPtr.Zero) return;
            VirtualProtectFromApp(
                page, (UIntPtr)(ThunkSize * Capacity), PAGE_READWRITE, out _);
        }

        private void Seal()
        {
            if (page == IntPtr.Zero) return;
            VirtualProtectFromApp(
                page, (UIntPtr)(ThunkSize * Capacity), PAGE_EXECUTE_READ, out _);
        }
    }
}
