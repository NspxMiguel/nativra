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
        // Two factories at thirty-two entries each, a device at forty-three,
        // a display device, and an adapter for every one of those — the old
        // ceiling of 256 was within reach of an ordinary startup, and going
        // over it does not fail loudly: it hands back the console's own
        // function to be called with our stand-in as its object.
        private const int Capacity = 2048;

        private IntPtr page;
        private int used;

        /// <summary>Kept so the collector cannot take what native code holds.</summary>
        private readonly List<object> alive = new List<object>();

        // Everything mutable in here is shared by every thread that touches a
        // graphics object, and they arrive together: a game initialising its
        // renderer does so from its loader thread, its main thread and its
        // render thread within the same few milliseconds. A table written by
        // two of them at once is a hang with no stack to look at, which is
        // what this cost to find.
        private readonly object gate = new object();

        public void Keep(object thing)
        {
            lock (gate) alive.Add(thing);
        }

        /// <summary>
        /// Takes the next slot on the code page, for one caller at a time.
        /// </summary>
        private bool Room(out IntPtr at)
        {
            lock (gate)
            {
                at = IntPtr.Zero;
                if (!HavePage() || used >= Capacity) return false;
                at = page + used * ThunkSize;
                used++;
                return true;
            }
        }

        /// <summary>The object each proxy stands in for, by proxy address.</summary>
        public readonly Dictionary<IntPtr, IntPtr> Behind =
            new Dictionary<IntPtr, IntPtr>();

        /// <summary>mov rcx, original ; mov rax, target ; jmp rax.</summary>
        /// <summary>
        /// Makes the page the thunks live on, once. A thunk that cannot be
        /// built has to say so rather than quietly hand back the function it
        /// was meant to wrap: that function would then be called with the
        /// stand-in in place of the object it belongs to, which is a crash
        /// with no explanation attached.
        /// </summary>
        private bool HavePage()
        {
            if (page != IntPtr.Zero) return true;
            page = VirtualAllocFromApp(
                IntPtr.Zero, (UIntPtr)(ThunkSize * Capacity),
                MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            return page != IntPtr.Zero;
        }

        private IntPtr Forward(IntPtr original, IntPtr target)
        {
            IntPtr at;
            if (!Room(out at)) return target;

            var code = new List<byte> { 0x48, 0xB9 };
            code.AddRange(BitConverter.GetBytes(original.ToInt64()));
            code.AddRange(new byte[] { 0x48, 0xB8 });
            code.AddRange(BitConverter.GetBytes(target.ToInt64()));
            code.AddRange(new byte[] { 0xFF, 0xE0 });

            Marshal.Copy(code.ToArray(), 0, at, code.Count);
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
            lock (gate) Behind[proxy] = original;
            return proxy;
        }


        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int AskDelegate(IntPtr self, IntPtr riid, IntPtr result);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint CountDelegate(IntPtr self);

        private static AskDelegate ask;
        private static CountDelegate up;
        private static CountDelegate down;

        /// <summary>
        /// Builds a COM object out of nothing but managed methods.
        ///
        /// The three IUnknown entries are the same for every object made this
        /// way and are filled in here: asked for any interface, an object
        /// hands back itself, which is true for the small single-interface
        /// objects this is for. The caller supplies the rest, in order.
        ///
        /// This is how a library that can only be reached by asking the system
        /// for a class — an audio device enumerator, say — gets reached on a
        /// system that has no such class to give.
        /// </summary>
        /// <summary>Which interfaces each made object admits to being.</summary>
        private static readonly Dictionary<long, HashSet<string>> admits =
            new Dictionary<long, HashSet<string>>();

        private static readonly Dictionary<long, Dictionary<string, IntPtr>> interfacesByObject =
            new Dictionary<long, Dictionary<string, IntPtr>>();

        /// <summary>Joins two vtables under one canonical IUnknown identity.</summary>
        public void LinkInterfacePair(IntPtr first, string firstId, IntPtr second, string secondId)
        {
            var interfaces = new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase)
            {
                ["00000000-0000-0000-c000-000000000046"] = first,
                [firstId] = first,
                [secondId] = second,
            };
            lock (admits)
            {
                interfacesByObject[first.ToInt64()] = interfaces;
                interfacesByObject[second.ToInt64()] = interfaces;
            }
        }

        public IntPtr Create(IntPtr[] methods, string[] interfaces = null)
        {
            if (ask == null)
            {
                ask = (self, riid, result) =>
                {
                    if (result == IntPtr.Zero) return unchecked((int)0x80004003);
                    Marshal.WriteIntPtr(result, IntPtr.Zero);

                    // Claiming to be every interface asked for is how a made
                    // object gets called through a table it does not have.
                    // What it is, it says; what it is not, it refuses.
                    HashSet<string> known;
                    lock (admits) admits.TryGetValue(self.ToInt64(), out known);
                    if (known != null && riid != IntPtr.Zero)
                    {
                        string wanted;
                        try
                        {
                            wanted = Marshal.PtrToStructure<Guid>(riid).ToString();
                        }
                        catch
                        {
                            return unchecked((int)0x80004002);
                        }
                        lock (admits)
                        {
                            if (interfacesByObject.TryGetValue(self.ToInt64(), out var interfaces)
                                && interfaces.TryGetValue(wanted, out var target))
                            {
                                Marshal.WriteIntPtr(result, target);
                                return 0;
                            }
                        }
                        if (!known.Contains(wanted)) return unchecked((int)0x80004002);
                    }

                    Marshal.WriteIntPtr(result, self);
                    return 0;
                };
                up = self => 2;
                down = self => 1;
            }

            var total = 3 + methods.Length;
            var table = Marshal.AllocHGlobal(IntPtr.Size * total);
            Marshal.WriteIntPtr(table, 0, Marshal.GetFunctionPointerForDelegate(ask));
            Marshal.WriteIntPtr(table, IntPtr.Size, Marshal.GetFunctionPointerForDelegate(up));
            Marshal.WriteIntPtr(
                table, IntPtr.Size * 2, Marshal.GetFunctionPointerForDelegate(down));
            for (var i = 0; i < methods.Length; i++)
            {
                Marshal.WriteIntPtr(table, IntPtr.Size * (3 + i), methods[i]);
            }

            var made = Marshal.AllocHGlobal(IntPtr.Size * 2);
            Marshal.WriteIntPtr(made, 0, table);
            Marshal.WriteIntPtr(made, IntPtr.Size, IntPtr.Zero);

            if (interfaces != null)
            {
                var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "00000000-0000-0000-c000-000000000046",
                };
                foreach (var id in interfaces) known.Add(id);
                lock (admits) admits[made.ToInt64()] = known;
            }
            return made;
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
