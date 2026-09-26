using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nativra.X86.Cpu;
using Nativra.X86.Loader;
using Xunit;

namespace Nativra.X86.Tests
{
    /// <summary>
    /// The 32-to-64-bit COM bridge against a host object built from managed
    /// delegates: the guest's calls go through the proxy's vtable sentinels
    /// exactly as a game's would.
    /// </summary>
    public sealed class GuestComTests : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int QiFn(IntPtr self, IntPtr iid, IntPtr result);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint RefFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int AddFn(IntPtr self, uint a, uint b, IntPtr result);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int MakeFn(IntPtr self, IntPtr result);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int PresentFn(IntPtr self, IntPtr parameters);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int LockFn(IntPtr self, IntPtr locked);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ScaleFn(IntPtr self, float factor);

        private static readonly Guid CalcIid = new Guid("11111111-2222-3333-4444-555555555555");

        private readonly List<Delegate> keep = new List<Delegate>();
        private readonly List<IntPtr> blocks = new List<IntPtr>();
        private readonly Dictionary<long, int> refs = new Dictionary<long, int>();
        private IntPtr vtable;
        private readonly GuestProcess p;
        private readonly GuestKernel kernel;
        private readonly GuestCom com;
        private readonly ComInterface calc;
        private uint lockTarget;
        private float scaled;
        private long seenWindow;
        private int seenWindowed;

        public GuestComTests()
        {
            // The bridge hands the host guest pointers as host pointers, which
            // only a native guest space makes possible.
            p = new GuestProcess(new GuestMemory(native: true), useJit: false);
            kernel = new GuestKernel(p);
            kernel.Install();
            com = new GuestCom(p, kernel);
            calc = com.Define("ICalc", CalcIid, null, true,
                "Add(u,u,p)", "Make(o:ICalc)", "TakePresent(P)", "Lock(L)", "Scale(f)");
            BuildVtable();
        }

        public void Dispose()
        {
            foreach (var b in blocks) Marshal.FreeHGlobal(b);
            p.Dispose();
        }

        private void BuildVtable()
        {
            var methods = new Delegate[]
            {
                new QiFn((self, iid, result) =>
                {
                    var bytes = new byte[16];
                    Marshal.Copy(iid, bytes, 0, 16);
                    var asked = new Guid(bytes);
                    if (asked != CalcIid && asked != GuestCom.IUnknownIid) { Marshal.WriteIntPtr(result, IntPtr.Zero); return unchecked((int)0x80004002); }
                    refs[self.ToInt64()]++;
                    Marshal.WriteIntPtr(result, self);
                    return 0;
                }),
                new RefFn(self => (uint)++refs[self.ToInt64()]),
                new RefFn(self => (uint)--refs[self.ToInt64()]),
                new AddFn((self, a, b, result) => { Marshal.WriteInt32(result, (int)(a + b)); return 0; }),
                new MakeFn((self, result) => { Marshal.WriteIntPtr(result, NewObject()); return 0; }),
                new PresentFn((self, pp) =>
                {
                    seenWindow = Marshal.ReadInt64(pp, 32);        // hDeviceWindow, 8 bytes on the host
                    seenWindowed = Marshal.ReadInt32(pp, 40);      // Windowed, after it
                    Marshal.WriteInt32(pp, 0, 1234);               // the runtime fills in a zero width
                    return 0;
                }),
                new LockFn((self, locked) =>
                {
                    Marshal.WriteInt32(locked, 0, 256);                                                  // Pitch
                    Marshal.WriteIntPtr(locked, 8, new IntPtr(p.Memory.HostBase.ToInt64() + lockTarget));   // pBits
                    return 0;
                }),
                new ScaleFn((self, factor) => { scaled = factor; return 0; }),
            };
            keep.AddRange(methods);
            vtable = Marshal.AllocHGlobal(methods.Length * IntPtr.Size);
            blocks.Add(vtable);
            for (var n = 0; n < methods.Length; n++)
                Marshal.WriteIntPtr(vtable, n * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(methods[n]));
        }

        private IntPtr NewObject()
        {
            var obj = Marshal.AllocHGlobal(16);
            blocks.Add(obj);
            Marshal.WriteIntPtr(obj, vtable);
            refs[obj.ToInt64()] = 1;
            return obj;
        }

        /// <summary>Calls method <paramref name="slot"/> of a guest object the way guest code does: through its vtable.</summary>
        private uint Call(uint obj, int slot, params uint[] args)
        {
            var entry = p.Memory.Read32(p.Memory.Read32(obj) + (uint)slot * 4);
            var all = new uint[args.Length + 1];
            all[0] = obj;
            Array.Copy(args, 0, all, 1, args.Length);
            var result = p.Call(entry, out var eax, 10_000, all);
            Assert.True(result.Ok, result.ToString());
            return eax;
        }

        [Fact]
        public void PlainArgumentsAndDataPointersPassStraightThrough()
        {
            var obj = com.Wrap(NewObject(), calc);
            var sum = kernel.Heap.Alloc(4);
            Assert.Equal(0u, Call(obj, 3, 2, 3, sum));
            Assert.Equal(5u, p.Memory.Read32(sum));
            Assert.Equal(obj, com.Wrap(com.Unwrap(obj), calc));   // one guest address per object
        }

        [Fact]
        public void InterfacesHandedOutAreWrappedAndGoAwayOnTheLastRelease()
        {
            var obj = com.Wrap(NewObject(), calc);
            var slot = kernel.Heap.Alloc(4);
            Assert.Equal(0u, Call(obj, 4, slot));
            var made = p.Memory.Read32(slot);
            Assert.NotEqual(0u, made);
            Assert.NotEqual(obj, made);
            Assert.Equal(2, com.ProxyCount);

            Assert.Equal(2u, Call(made, 1));   // AddRef
            Assert.Equal(1u, Call(made, 2));   // Release
            Assert.Equal(0u, Call(made, 2));   // Release: gone
            Assert.Equal(1, com.ProxyCount);
            Assert.Equal(IntPtr.Zero, com.Unwrap(made));
        }

        [Fact]
        public void QueryInterfaceAnswersWithTheSameProxy()
        {
            var obj = com.Wrap(NewObject(), calc);
            var iid = kernel.Heap.Alloc(16);
            p.Memory.WriteBytes(iid, CalcIid.ToByteArray());
            var result = kernel.Heap.Alloc(4);
            Assert.Equal(0u, Call(obj, 0, iid, result));
            Assert.Equal(obj, p.Memory.Read32(result));

            p.Memory.WriteBytes(iid, Guid.NewGuid().ToByteArray());
            Assert.Equal(0x80004002u, Call(obj, 0, iid, result));   // E_NOINTERFACE
            Assert.Equal(0u, p.Memory.Read32(result));
        }

        [Fact]
        public void PresentParametersAreWidenedAroundTheWindowHandle()
        {
            var obj = com.Wrap(NewObject(), calc);
            var pp = kernel.Heap.Alloc(56, zero: true);
            p.Memory.Write32(pp + 28, 0x00010010);   // hDeviceWindow
            p.Memory.Write32(pp + 32, 1);            // Windowed
            Assert.Equal(0u, Call(obj, 5, pp));
            Assert.Equal(0x00010010L, seenWindow);
            Assert.Equal(1, seenWindowed);
            Assert.Equal(1234u, p.Memory.Read32(pp));   // written back
            Assert.Equal(1u, p.Memory.Read32(pp + 32));
        }

        [Fact]
        public void LockedRectPointsIntoTheGuestSpace()
        {
            var obj = com.Wrap(NewObject(), calc);
            lockTarget = kernel.Heap.Alloc(4096);
            var locked = kernel.Heap.Alloc(8);
            Assert.Equal(0u, Call(obj, 6, locked));
            Assert.Equal(256u, p.Memory.Read32(locked));
            Assert.Equal(lockTarget, p.Memory.Read32(locked + 4));
        }

        [Fact]
        public void FloatArgumentGoesInAFloatRegister()
        {
            var obj = com.Wrap(NewObject(), calc);
            Assert.Equal(0u, Call(obj, 7, BitConverter.ToUInt32(BitConverter.GetBytes(2.5f), 0)));
            Assert.Equal(2.5f, scaled);
        }
    }
}
