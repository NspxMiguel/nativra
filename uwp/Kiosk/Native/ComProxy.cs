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
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void SeenDelegate(long slot);

        private SeenDelegate seen;
        private IntPtr seenPointer;
        private string watching;

        /// <summary>
        /// Which numbered methods of a watched object were called, and how
        /// often. Reading a table entry by entry is the only way to answer
        /// "which of these thirty-two did it use" without guessing.
        /// </summary>
        public static readonly Dictionary<string, long> Used =
            new Dictionary<string, long>();

        /// <summary>
        /// A forwarding thunk that says it was used before forwarding.
        ///
        /// The plain one is two instructions and leaves no trace, which is
        /// right for everything except the one object under suspicion. For
        /// that one, knowing which entry an engine reached for is the whole
        /// question — a swap chain appeared that this bridge never made, and
        /// every theory about where it came from was wrong.
        /// </summary>
        private IntPtr Watched(IntPtr original, IntPtr target, int slot)
        {
            if (seen == null)
            {
                seen = number =>
                {
                    var key = watching + " slot " + number;
                    lock (Used)
                    {
                        Used.TryGetValue(key, out var count);
                        Used[key] = count + 1;
                    }
                };
                seenPointer = Marshal.GetFunctionPointerForDelegate(seen);
            }
            if (page == IntPtr.Zero || used >= Capacity) return target;

            var code = new List<byte>();
            code.AddRange(new byte[] { 0x48, 0x83, 0xEC, 0x48 });        // sub rsp, 0x48
            code.AddRange(new byte[] { 0x48, 0x89, 0x54, 0x24, 0x20 });  // mov [rsp+20], rdx
            code.AddRange(new byte[] { 0x4C, 0x89, 0x44, 0x24, 0x28 });  // mov [rsp+28], r8
            code.AddRange(new byte[] { 0x4C, 0x89, 0x4C, 0x24, 0x30 });  // mov [rsp+30], r9
            code.AddRange(new byte[] { 0x48, 0xB9 });                    // mov rcx, slot
            code.AddRange(BitConverter.GetBytes((long)slot));
            code.AddRange(new byte[] { 0x48, 0xB8 });                    // mov rax, recorder
            code.AddRange(BitConverter.GetBytes(seenPointer.ToInt64()));
            code.AddRange(new byte[] { 0xFF, 0xD0 });                    // call rax
            code.AddRange(new byte[] { 0x48, 0x8B, 0x54, 0x24, 0x20 });  // mov rdx, [rsp+20]
            code.AddRange(new byte[] { 0x4C, 0x8B, 0x44, 0x24, 0x28 });  // mov r8, [rsp+28]
            code.AddRange(new byte[] { 0x4C, 0x8B, 0x4C, 0x24, 0x30 });  // mov r9, [rsp+30]
            code.AddRange(new byte[] { 0x48, 0x83, 0xC4, 0x48 });        // add rsp, 0x48
            code.AddRange(new byte[] { 0x48, 0xB9 });                    // mov rcx, original
            code.AddRange(BitConverter.GetBytes(original.ToInt64()));
            code.AddRange(new byte[] { 0x48, 0xB8 });                    // mov rax, target
            code.AddRange(BitConverter.GetBytes(target.ToInt64()));
            code.AddRange(new byte[] { 0xFF, 0xE0 });                    // jmp rax

            var at = page + used * ThunkSize;
            Marshal.Copy(code.ToArray(), 0, at, code.Count);
            used++;
            return at;
        }

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
        public IntPtr Wrap(
            IntPtr original, int methods, Dictionary<int, IntPtr> ours, string watch = null)
        {
            if (original == IntPtr.Zero) return IntPtr.Zero;

            Unseal();
            watching = watch;
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
                    var real = Marshal.ReadIntPtr(realTable, slot * IntPtr.Size);
                    entry = watch == null
                        ? Forward(original, real)
                        : Watched(original, real, slot);
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

        public IntPtr Create(IntPtr[] methods, string[] interfaces = null)
        {
            if (ask == null)
            {
                ask = (self, riid, result) =>
                {
                    if (result == IntPtr.Zero) return unchecked((int)0x80004003);

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
