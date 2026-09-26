using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nativra.X86.Cpu;

namespace Nativra.X86.Loader
{
    /// <summary>
    /// One COM interface the guest can hold: its methods in vtable order, each
    /// with a signature saying how its 32-bit arguments become 64-bit ones.
    ///
    /// Signature codes, one per argument after <c>this</c>:
    /// <list type="bullet">
    /// <item><c>u</c> a 32-bit value, zero-extended (DWORD, UINT, BOOL, an enum, a float on the stack)</item>
    /// <item><c>s</c> a signed 32-bit value, sign-extended (INT, LONG)</item>
    /// <item><c>h</c> a handle or HWND, zero-extended</item>
    /// <item><c>p</c> a pointer to plain data (no pointers inside): the guest address plus the host base</item>
    /// <item><c>f</c> a float passed in a register (only as the first argument)</item>
    /// <item><c>i:Name</c> an interface the guest passes in: its host object</item>
    /// <item><c>o:Name</c> an interface the method hands out (IFoo**): wrapped for the guest</item>
    /// <item><c>q</c> REFIID + void** (two arguments): the object the IID names, wrapped</item>
    /// <item><c>x</c> a HANDLE* for sharing, always passed as null</item>
    /// <item><c>P</c> D3DPRESENT_PARAMETERS in/out; <c>C</c> D3DDEVICE_CREATION_PARAMETERS out</item>
    /// <item><c>L</c> D3DLOCKED_RECT out; <c>B</c> D3DLOCKED_BOX out; <c>V</c> void** data out</item>
    /// </list>
    /// A method name ending in <c>:F</c> returns a float (in ST0 for the guest).
    /// </summary>
    public sealed class ComInterface
    {
        public string Name { get; }
        public Guid Iid { get; }
        public ComInterface Parent { get; }
        public IReadOnlyList<ComMethod> Methods => methods;
        /// <summary>False for vtables without IUnknown (XAudio2 voices): no QueryInterface/AddRef/Release.</summary>
        public bool IsUnknown { get; }

        /// <summary>For an abstract type (a base texture): the concrete interface of a host object.</summary>
        public Func<IntPtr, ComInterface> Resolve { get; set; }

        internal readonly List<ComMethod> methods = new List<ComMethod>();
        internal uint GuestVtable;

        internal ComInterface(string name, Guid iid, ComInterface parent, bool unknown)
        {
            Name = name;
            Iid = iid;
            Parent = parent;
            IsUnknown = unknown;
            if (parent != null) methods.AddRange(parent.methods);
        }

        public bool IsA(ComInterface other)
        {
            for (var t = this; t != null; t = t.Parent) if (t == other) return true;
            return false;
        }

        public override string ToString() => Name;
    }

    public sealed class ComMethod
    {
        public string Name { get; }
        public int Slot { get; }
        internal readonly string[] Args;
        internal readonly bool FloatReturn;
        internal readonly int GuestDwords;

        internal ComMethod(string name, int slot, string[] args, bool floatReturn)
        {
            Name = name;
            Slot = slot;
            Args = args;
            FloatReturn = floatReturn;
            var dwords = 0;
            foreach (var a in args) dwords += a == "q" ? 2 : 1;
            GuestDwords = dwords;
        }
    }

    /// <summary>
    /// Lets 32-bit guest code hold and call 64-bit host COM objects.
    ///
    /// A host object reaches the guest as a small proxy in guest memory whose
    /// first word is a guest vtable; each vtable entry is an import sentinel,
    /// so a guest call through it lands in a handler that turns the stdcall
    /// arguments into host ones and calls the real method. Guest memory is
    /// host memory at a fixed offset, so a pointer to plain data (a matrix, a
    /// vertex array, a caps structure) is passed through by adding the host
    /// base: nothing is copied. Only interfaces, and the few structures that
    /// hold a pointer or a window handle, are translated.
    ///
    /// Reference counts are forwarded one for one: a proxy holds the reference
    /// its creator handed out, and goes away when the host's count reaches zero.
    /// </summary>
    public sealed class GuestCom
    {
        private readonly GuestProcess process;
        private readonly GuestKernel kernel;
        private readonly Dictionary<string, ComInterface> byName = new Dictionary<string, ComInterface>(StringComparer.Ordinal);
        private readonly Dictionary<Guid, ComInterface> byIid = new Dictionary<Guid, ComInterface>();
        private readonly Dictionary<long, uint> proxies = new Dictionary<long, uint>();       // host object -> guest proxy
        private readonly Dictionary<uint, Proxy> byGuest = new Dictionary<uint, Proxy>();
        private readonly Dictionary<IntPtr, Delegate> callers = new Dictionary<IntPtr, Delegate>();
        private readonly IntPtr scratch;   // host-side out slots and structure copies
        private const int ScratchSize = 1024;

        private sealed class Proxy
        {
            public IntPtr Host;
            public ComInterface Interface;
        }

        /// <summary>Calls made through proxies, by interface and method (the probe reports them).</summary>
        public Dictionary<string, long> Calls { get; } = new Dictionary<string, long>(StringComparer.Ordinal);

        public GuestCom(GuestProcess process, GuestKernel kernel)
        {
            if (!process.Memory.IsNative)
                throw new ArgumentException("the COM bridge needs a native guest space (guest address + host base = host pointer)");
            this.process = process;
            this.kernel = kernel;
            scratch = Marshal.AllocHGlobal(ScratchSize);
        }

        private GuestMemory Memory => process.Memory;
        private long HostBase => Memory.HostBase.ToInt64();

        public static readonly Guid IUnknownIid = new Guid("00000000-0000-0000-C000-000000000046");

        /// <summary>
        /// Declares an interface. <paramref name="methods"/> are the ones it adds
        /// to its parent, as "Name(codes)"; a root interface is given the three
        /// IUnknown methods first unless <paramref name="unknown"/> is false.
        /// </summary>
        public ComInterface Define(string name, Guid iid, string parent, bool unknown, params string[] methods)
        {
            ComInterface parentInterface = null;
            if (parent != null && !byName.TryGetValue(parent, out parentInterface))
                throw new ArgumentException("unknown parent interface " + parent);
            var face = new ComInterface(name, iid, parentInterface, parentInterface?.IsUnknown ?? unknown);
            if (parentInterface == null && unknown)
            {
                AddMethod(face, "QueryInterface(q)");
                AddMethod(face, "AddRef()");
                AddMethod(face, "Release()");
            }
            foreach (var m in methods) AddMethod(face, m);
            byName[name] = face;
            if (iid != Guid.Empty) byIid[iid] = face;
            return face;
        }

        public ComInterface Find(string name) => byName.TryGetValue(name, out var face) ? face : null;

        private static void AddMethod(ComInterface face, string text)
        {
            var floatReturn = text.EndsWith(":F", StringComparison.Ordinal);
            if (floatReturn) text = text.Substring(0, text.Length - 2);
            var open = text.IndexOf('(');
            var name = text.Substring(0, open);
            var inside = text.Substring(open + 1, text.Length - open - 2);
            var args = inside.Length == 0 ? new string[0] : inside.Split(',');
            face.methods.Add(new ComMethod(name, face.methods.Count, args, floatReturn));
        }

        /// <summary>
        /// The guest's pointer for a host object (made on first sight and kept,
        /// so the guest sees one address per object). The guest takes over the
        /// reference the caller holds.
        /// </summary>
        public uint Wrap(IntPtr host, ComInterface face)
        {
            if (host == IntPtr.Zero) return 0;
            if (proxies.TryGetValue(host.ToInt64(), out var known))
            {
                // Same object again: keep the richer of the two interfaces.
                var proxy = byGuest[known];
                if (face.IsA(proxy.Interface) && face != proxy.Interface)
                {
                    proxy.Interface = face;
                    Memory.Write32(known, VtableFor(face));
                }
                return known;
            }
            if (face.Resolve != null) face = face.Resolve(host) ?? face;

            var guest = kernel.Heap.Alloc(16, zero: true);
            Memory.Write32(guest, VtableFor(face));
            Memory.Write64(guest + 4, (ulong)host.ToInt64());
            proxies[host.ToInt64()] = guest;
            byGuest[guest] = new Proxy { Host = host, Interface = face };
            return guest;
        }

        /// <summary>The host object behind a guest pointer (zero for null or a pointer that is not a proxy).</summary>
        public IntPtr Unwrap(uint guest)
        {
            if (guest == 0) return IntPtr.Zero;
            return byGuest.TryGetValue(guest, out var proxy) ? proxy.Host : IntPtr.Zero;
        }

        public int ProxyCount => byGuest.Count;

        private void Forget(uint guest)
        {
            if (!byGuest.TryGetValue(guest, out var proxy)) return;
            byGuest.Remove(guest);
            proxies.Remove(proxy.Host.ToInt64());
            kernel.Heap.Free(guest);
        }

        private uint VtableFor(ComInterface face)
        {
            if (face.GuestVtable != 0) return face.GuestVtable;
            var table = kernel.Heap.Alloc((uint)face.Methods.Count * 4);
            for (var n = 0; n < face.Methods.Count; n++)
            {
                var method = face.Methods[n];
                var function = face.Name + "::" + method.Name;
                process.Imports.Register("nativra-com.dll", function, CallConv.Stdcall, 1 + method.GuestDwords,
                    c => Invoke(c, method));
                Memory.Write32(table + (uint)n * 4, process.Imports.Bind("nativra-com.dll", function, -1));
            }
            face.GuestVtable = table;
            return table;
        }

        // --- calling --------------------------------------------------------

        private IntPtr GuestToHost(uint guest) => guest == 0 ? IntPtr.Zero : new IntPtr(HostBase + guest);

        /// <summary>A host pointer inside the guest space as a guest address (0 when it is outside).</summary>
        public uint HostToGuest(IntPtr host)
        {
            var offset = host.ToInt64() - HostBase;
            return offset > 0 && offset < 0x100000000L ? (uint)offset : 0;
        }

        private ulong Invoke(GuestCall c, ComMethod method)
        {
            var self = c.Arg(0);
            if (!byGuest.TryGetValue(self, out var proxy)) return 0x80004003;   // E_POINTER
            var key = proxy.Interface.Name + "::" + method.Name;
            Calls.TryGetValue(key, out var n);
            Calls[key] = n + 1;

            var host = proxy.Host;
            var function = Marshal.ReadIntPtr(Marshal.ReadIntPtr(host), method.Slot * IntPtr.Size);
            var args = new List<IntPtr>(method.Args.Length + 2) { host };
            var slot = 0;          // next free 8-byte slot in scratch
            var guestArg = 1;
            var outs = new List<Action>();

            for (var a = 0; a < method.Args.Length; a++)
            {
                var code = method.Args[a];
                var value = c.Arg(guestArg++);
                switch (code[0])
                {
                    case 'u': case 'h': case 'f':
                        args.Add(new IntPtr((long)value));
                        break;
                    case 's':
                        args.Add(new IntPtr((int)value));
                        break;
                    case 'p':
                        args.Add(GuestToHost(value));
                        break;
                    case 'x':
                        args.Add(IntPtr.Zero);
                        break;
                    case 'i':
                        args.Add(Unwrap(value));
                        break;
                    case 'o':
                    {
                        var target = value;
                        var at = Slot(ref slot, 1);
                        args.Add(target == 0 ? IntPtr.Zero : at);
                        var face = byName[code.Substring(2)];
                        if (target != 0) outs.Add(() => Memory.Write32(target, Wrap(Marshal.ReadIntPtr(at), face)));
                        break;
                    }
                    case 'q':
                    {
                        var iidAddress = value;
                        var target = c.Arg(guestArg++);
                        var at = Slot(ref slot, 1);
                        args.Add(GuestToHost(iidAddress));
                        args.Add(target == 0 ? IntPtr.Zero : at);
                        if (target != 0) outs.Add(() => Memory.Write32(target, WrapByIid(Marshal.ReadIntPtr(at), iidAddress)));
                        break;
                    }
                    case 'V':
                    {
                        var target = value;
                        var at = Slot(ref slot, 1);
                        args.Add(target == 0 ? IntPtr.Zero : at);
                        if (target != 0) outs.Add(() => Memory.Write32(target, HostToGuest(Marshal.ReadIntPtr(at))));
                        break;
                    }
                    case 'L':
                    {
                        // D3DLOCKED_RECT: { INT Pitch; void* pBits } — 8 bytes here, 16 on the host.
                        var target = value;
                        var at = Slot(ref slot, 2);
                        args.Add(target == 0 ? IntPtr.Zero : at);
                        if (target != 0) outs.Add(() =>
                        {
                            Memory.Write32(target, (uint)Marshal.ReadInt32(at));
                            Memory.Write32(target + 4, HostToGuest(Marshal.ReadIntPtr(at, 8)));
                        });
                        break;
                    }
                    case 'B':
                    {
                        // D3DLOCKED_BOX: { INT RowPitch; INT SlicePitch; void* pBits } — 12 bytes here, 16 on the host.
                        var target = value;
                        var at = Slot(ref slot, 2);
                        args.Add(target == 0 ? IntPtr.Zero : at);
                        if (target != 0) outs.Add(() =>
                        {
                            Memory.Write32(target, (uint)Marshal.ReadInt32(at));
                            Memory.Write32(target + 4, (uint)Marshal.ReadInt32(at, 4));
                            Memory.Write32(target + 8, HostToGuest(Marshal.ReadIntPtr(at, 8)));
                        });
                        break;
                    }
                    case 'P':
                    {
                        // D3DPRESENT_PARAMETERS: hDeviceWindow is 4 bytes at +28 here,
                        // 8 bytes at +32 on the host, which moves everything after it.
                        var guest = value;
                        var at = Slot(ref slot, 8);
                        args.Add(guest == 0 ? IntPtr.Zero : at);
                        if (guest != 0)
                        {
                            PresentToHost(guest, at);
                            outs.Add(() => PresentToGuest(at, guest));
                        }
                        break;
                    }
                    case 'C':
                    {
                        // D3DDEVICE_CREATION_PARAMETERS: { UINT, D3DDEVTYPE, HWND, DWORD }.
                        var target = value;
                        var at = Slot(ref slot, 3);
                        args.Add(target == 0 ? IntPtr.Zero : at);
                        if (target != 0) outs.Add(() =>
                        {
                            Memory.Write32(target, (uint)Marshal.ReadInt32(at));
                            Memory.Write32(target + 4, (uint)Marshal.ReadInt32(at, 4));
                            Memory.Write32(target + 8, (uint)Marshal.ReadInt64(at, 8));
                            Memory.Write32(target + 12, (uint)Marshal.ReadInt32(at, 16));
                        });
                        break;
                    }
                    default:
                        throw new InvalidOperationException("bad signature code " + code + " in " + key);
                }
            }

            var hostArgs = args.ToArray();
            ulong result;
            if (method.FloatReturn)
            {
                var f = CallFloat(function, hostArgs);
                process.Interpreter.Fpu.Push(f);
                result = 0;
            }
            else if (method.Args.Length > 0 && method.Args[0] == "f")
            {
                result = (uint)CallWithFloat(function, hostArgs).ToInt64();
            }
            else
            {
                result = (uint)CallHost(function, hostArgs).ToInt64();
            }

            foreach (var o in outs) o();
            if (method.Name == "Release" && proxy.Interface.IsUnknown && (uint)result == 0) Forget(self);
            else if (!proxy.Interface.IsUnknown && method.Name == "DestroyVoice") Forget(self);
            return result;
        }

        private IntPtr Slot(ref int slot, int count)
        {
            var at = scratch + slot * 8;
            slot += count;
            if (slot * 8 > ScratchSize) throw new InvalidOperationException("COM bridge scratch exhausted");
            for (var n = 0; n < count; n++) Marshal.WriteInt64(at, n * 8, 0);
            return at;
        }

        private uint WrapByIid(IntPtr host, uint iidAddress)
        {
            if (host == IntPtr.Zero) return 0;
            var iid = new Guid(Memory.ReadBytes(iidAddress, 16));
            if (proxies.TryGetValue(host.ToInt64(), out var known))
            {
                // A known object asked for a base interface: the proxy serves it.
                if (iid == IUnknownIid || !byIid.TryGetValue(iid, out var wanted) || byGuest[known].Interface.IsA(wanted))
                    return known;
                return Wrap(host, wanted);
            }
            if (byIid.TryGetValue(iid, out var face)) return Wrap(host, face);
            return 0;
        }

        private void PresentToHost(uint g, IntPtr h)
        {
            for (uint o = 0; o < 28; o += 4) Marshal.WriteInt32(h, (int)o, (int)Memory.Read32(g + o));
            Marshal.WriteInt64(h, 32, Memory.Read32(g + 28));
            for (uint o = 32; o < 56; o += 4) Marshal.WriteInt32(h, (int)o + 8, (int)Memory.Read32(g + o));
        }

        private void PresentToGuest(IntPtr h, uint g)
        {
            for (uint o = 0; o < 28; o += 4) Memory.Write32(g + o, (uint)Marshal.ReadInt32(h, (int)o));
            Memory.Write32(g + 28, (uint)Marshal.ReadInt64(h, 32));
            for (uint o = 32; o < 56; o += 4) Memory.Write32(g + o, (uint)Marshal.ReadInt32(h, (int)o + 8));
        }

        // --- host calls, by arity ---------------------------------------------

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr H1(IntPtr a);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr H2(IntPtr a, IntPtr b);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr H3(IntPtr a, IntPtr b, IntPtr c);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr H4(IntPtr a, IntPtr b, IntPtr c, IntPtr d);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr H5(IntPtr a, IntPtr b, IntPtr c, IntPtr d, IntPtr e);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr H6(IntPtr a, IntPtr b, IntPtr c, IntPtr d, IntPtr e, IntPtr f);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr H7(IntPtr a, IntPtr b, IntPtr c, IntPtr d, IntPtr e, IntPtr f, IntPtr g);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr H8(IntPtr a, IntPtr b, IntPtr c, IntPtr d, IntPtr e, IntPtr f, IntPtr g, IntPtr h);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr H9(IntPtr a, IntPtr b, IntPtr c, IntPtr d, IntPtr e, IntPtr f, IntPtr g, IntPtr h, IntPtr i);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr H10(IntPtr a, IntPtr b, IntPtr c, IntPtr d, IntPtr e, IntPtr f, IntPtr g, IntPtr h, IntPtr i, IntPtr j);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr H11(IntPtr a, IntPtr b, IntPtr c, IntPtr d, IntPtr e, IntPtr f, IntPtr g, IntPtr h, IntPtr i, IntPtr j, IntPtr k);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr H12(IntPtr a, IntPtr b, IntPtr c, IntPtr d, IntPtr e, IntPtr f, IntPtr g, IntPtr h, IntPtr i, IntPtr j, IntPtr k, IntPtr l);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr HFloat1(IntPtr a, float b);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr HFloat2(IntPtr a, float b, IntPtr c);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr HFloat3(IntPtr a, float b, IntPtr c, IntPtr d);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate float RFloat1(IntPtr a);

        private T Caller<T>(IntPtr function) where T : class
        {
            if (!callers.TryGetValue(function, out var d) || !(d is T))
            {
                d = Marshal.GetDelegateForFunctionPointer(function, typeof(T));
                callers[function] = d;
            }
            return d as T;
        }

        /// <summary>
        /// A function pointer made from a managed delegate comes back as that
        /// delegate, not as the shape asked for (host objects written in C#,
        /// which the tests use). Call it with its own parameter types.
        /// </summary>
        private static IntPtr CallManaged(Delegate d, IntPtr[] a)
        {
            var parameters = d.Method.GetParameters();
            var values = new object[parameters.Length];
            for (var n = 0; n < parameters.Length; n++)
            {
                var type = parameters[n].ParameterType;
                var raw = a[n].ToInt64();
                if (type == typeof(IntPtr)) values[n] = a[n];
                else if (type == typeof(uint)) values[n] = (uint)raw;
                else if (type == typeof(int)) values[n] = (int)raw;
                else if (type == typeof(float)) values[n] = BitConverter.ToSingle(BitConverter.GetBytes((uint)raw), 0);
                else values[n] = Convert.ChangeType(raw, type);
            }
            var result = d.DynamicInvoke(values);
            switch (result)
            {
                case null: return IntPtr.Zero;
                case IntPtr ip: return ip;
                case float f: return new IntPtr(BitConverter.ToInt32(BitConverter.GetBytes(f), 0));
                default: return new IntPtr(Convert.ToInt64(result));
            }
        }

        private IntPtr CallHost(IntPtr f, IntPtr[] a)
        {
            if (callers.TryGetValue(f, out var known) && known.GetType().DeclaringType != typeof(GuestCom))
                return CallManaged(known, a);
            var probe = Marshal.GetDelegateForFunctionPointer(f, typeof(H1));
            if (probe.GetType() != typeof(H1))
            {
                callers[f] = probe;
                return CallManaged(probe, a);
            }
            switch (a.Length)
            {
                case 1: return Caller<H1>(f)(a[0]);
                case 2: return Caller<H2>(f)(a[0], a[1]);
                case 3: return Caller<H3>(f)(a[0], a[1], a[2]);
                case 4: return Caller<H4>(f)(a[0], a[1], a[2], a[3]);
                case 5: return Caller<H5>(f)(a[0], a[1], a[2], a[3], a[4]);
                case 6: return Caller<H6>(f)(a[0], a[1], a[2], a[3], a[4], a[5]);
                case 7: return Caller<H7>(f)(a[0], a[1], a[2], a[3], a[4], a[5], a[6]);
                case 8: return Caller<H8>(f)(a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7]);
                case 9: return Caller<H9>(f)(a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7], a[8]);
                case 10: return Caller<H10>(f)(a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7], a[8], a[9]);
                case 11: return Caller<H11>(f)(a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7], a[8], a[9], a[10]);
                case 12: return Caller<H12>(f)(a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7], a[8], a[9], a[10], a[11]);
                default: throw new InvalidOperationException("too many arguments for the COM bridge: " + a.Length);
            }
        }

        private IntPtr CallWithFloat(IntPtr f, IntPtr[] a)
        {
            var probe = Marshal.GetDelegateForFunctionPointer(f, typeof(H1));
            if (probe.GetType() != typeof(H1)) return CallManaged(probe, a);
            var value = BitConverter.ToSingle(BitConverter.GetBytes((uint)a[1].ToInt64()), 0);
            switch (a.Length)
            {
                case 2: return Caller<HFloat1>(f)(a[0], value);
                case 3: return Caller<HFloat2>(f)(a[0], value, a[2]);
                case 4: return Caller<HFloat3>(f)(a[0], value, a[2], a[3]);
                default: throw new InvalidOperationException("unsupported float signature");
            }
        }

        private double CallFloat(IntPtr f, IntPtr[] a)
        {
            if (a.Length != 1) throw new InvalidOperationException("unsupported float return signature");
            return Caller<RFloat1>(f)(a[0]);
        }
    }
}
