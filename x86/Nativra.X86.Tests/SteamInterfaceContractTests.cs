using System;
using System.Runtime.InteropServices;
using Kiosk.Native;
using Xunit;

namespace Nativra.X86.Tests
{
    public sealed class SteamInterfaceContractTests
    {
        private const ulong UserId = 0x1122334455667788;

        private static T Export<T>(string name) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(SteamBridge.Resolve(name));

        private static IntPtr User()
        {
            SteamBridge.Active = true;
            SteamBridge.SteamId = UserId;
            var version = Marshal.StringToHGlobalAnsi("SteamUser016");
            try { return Export<SteamInterfaceContracts.Factory>("SteamInternal_CreateInterface")(version); }
            finally { Marshal.FreeHGlobal(version); }
        }

        private static T Slot<T>(IntPtr obj, int slot) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size));

        private sealed class TraceOwner
        {
            internal IntPtr Wrap(string name, IntPtr target) => target;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static WeakReference CreateTracedTable()
        {
            var owner = new TraceOwner();
            SteamClassic.Tracer = owner.Wrap;
            try { SteamClassic.Table("SyntheticLifetime", new IntPtr(1)); }
            finally { SteamClassic.Tracer = null; }
            return new WeakReference(owner);
        }

        [Fact]
        public void CachedTracedTablesKeepTheirRecorderOwnerAlive()
        {
            var owner = CreateTracedTable();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Assert.True(owner.IsAlive);
        }

        [Fact]
        public void FactoriesRejectUnknownVersionsAndNeverManufactureExports()
        {
            var user = User();
            Assert.NotEqual(IntPtr.Zero, user);
            var factory = Export<SteamInterfaceContracts.Factory>("SteamInternal_CreateInterface");
            foreach (var name in new[] { "SteamUser999", "SteamUser016suffix", "ISteamUser016", "SteamClient999", "" })
            {
                var text = Marshal.StringToHGlobalAnsi(name);
                try { Assert.Equal(IntPtr.Zero, factory(text)); }
                finally { Marshal.FreeHGlobal(text); }
            }
            Assert.Equal(IntPtr.Zero, factory(IntPtr.Zero));
            Assert.Equal(IntPtr.Zero, SteamBridge.Resolve("SteamAPI_UnknownName"));
            Assert.Equal(IntPtr.Zero, SteamBridge.Resolve("SteamAPI_UnknownFunction"));
            Assert.Equal(IntPtr.Zero, SteamBridge.Resolve("UnityRenderingExtEvent"));
        }

        [Fact]
        public void FlatScalarAndClassicStructureReturnsPreserveAllBitsAndCanariesAcrossGc()
        {
            var user = User();
            var output = Marshal.AllocHGlobal(24);
            try
            {
                for (var iteration = 0; iteration < 3; iteration++)
                {
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    Assert.Equal(user, User());
                    Marshal.WriteInt64(output, 0, -1);
                    Marshal.WriteInt64(output, 8, -1);
                    Marshal.WriteInt64(output, 16, -1);
                    var result = IntPtr.Add(output, 8);
                    Assert.Equal(result, Slot<SteamInterfaceContracts.StructureSteamId>(user, 2)(user, result));
                    Assert.Equal(UserId, unchecked((ulong)Marshal.ReadInt64(result)));
                    Assert.Equal(-1, Marshal.ReadInt64(output));
                    Assert.Equal(-1, Marshal.ReadInt64(output, 16));
                    Assert.Equal(UserId, Export<SteamInterfaceContracts.ScalarSteamId>("SteamAPI_ISteamUser_GetSteamID")(user));
                    Assert.False(Slot<SteamInterfaceContracts.Boolean>(user, 1)(user));
                    Assert.False(Export<SteamInterfaceContracts.Boolean>("SteamAPI_ISteamUser_BLoggedOn")(user));
                }
            }
            finally { Marshal.FreeHGlobal(output); }
        }

        [Fact]
        public void UnsupportedTicketsInitializeOnlyTheirCountAndDoNotAuthenticate()
        {
            var user = User();
            var memory = Marshal.AllocHGlobal(16);
            try
            {
                for (var offset = 0; offset < 16; offset++) Marshal.WriteByte(memory, offset, 0xA5);
                var count = IntPtr.Add(memory, 4);
                Assert.Equal(0u, Slot<SteamInterfaceContracts.AuthTicket>(user, 13)(user, memory, 4, count));
                Assert.Equal(0, Marshal.ReadInt32(count));
                Assert.False(Slot<SteamInterfaceContracts.EncryptedTicket>(user, 21)(user, memory, 4, count));
                for (var offset = 0; offset < 16; offset++)
                    Assert.Equal(offset >= 4 && offset < 8 ? (byte)0 : (byte)0xA5, Marshal.ReadByte(memory, offset));
            }
            finally { Marshal.FreeHGlobal(memory); }
        }

        private static unsafe IntPtr Call(IntPtr function, IntPtr a = default, IntPtr b = default,
            IntPtr c = default, IntPtr d = default)
        {
            return ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>)function)(a, b, c, d);
        }

        [Fact]
        public void ManualPayloadRemainsValidUntilExplicitReleaseAndShutdownCancelsPendingWork()
        {
            User();
            var message = Marshal.AllocHGlobal(24);
            try
            {
                var post = SteamBridge.Resolve("SteamAPI_ISteamUserStats_RequestCurrentStats");
                var poll = SteamBridge.Resolve("SteamAPI_ManualDispatch_GetNextCallback");
                var release = SteamBridge.Resolve("SteamAPI_ManualDispatch_FreeLastCallback");
                var shutdown = SteamBridge.Resolve("SteamAPI_Shutdown");
                Call(shutdown);
                Call(post); Call(post);
                Assert.Equal(new IntPtr(1), Call(poll, new IntPtr(1), message));
                var payload = Marshal.ReadIntPtr(message, 8);
                var value = Marshal.ReadInt64(payload, 16);
                GC.Collect();
                Assert.Equal(IntPtr.Zero, Call(poll, new IntPtr(1), message));
                Assert.Equal(value, Marshal.ReadInt64(payload, 16));
                Assert.Equal(IntPtr.Zero, Marshal.ReadIntPtr(message, 8));
                Call(release, new IntPtr(1));
                Assert.Equal(new IntPtr(1), Call(poll, new IntPtr(1), message));
                Call(shutdown);
                Assert.Equal(IntPtr.Zero, Call(poll, new IntPtr(1), message));
                Call(shutdown); // Idempotent; no double free.
                Assert.NotEqual(IntPtr.Zero, User());
            }
            finally
            {
                SteamClassic.Shutdown();
                Marshal.FreeHGlobal(message);
            }
        }

        [Fact]
        public void CallbackCanCancelALaterTargetAndShutdownPreventsDeliveryAfterReinit()
        {
            User();
            SteamClassic.Shutdown();
            var table = Marshal.AllocHGlobal(3 * IntPtr.Size);
            var first = Marshal.AllocHGlobal(16);
            var second = Marshal.AllocHGlobal(16);
            var delivered = 0;
            var payloadResult = 0;
            SteamClassic.RunCallback callback = (self, payload) =>
            {
                delivered++;
                payloadResult = Marshal.ReadInt32(payload, 8);
                Call(SteamBridge.Resolve("SteamAPI_UnregisterCallback"), second);
            };
            try
            {
                Marshal.WriteIntPtr(table, IntPtr.Size, Marshal.GetFunctionPointerForDelegate(callback));
                foreach (var target in new[] { first, second })
                {
                    Marshal.WriteInt64(target, 8, 0);
                    Marshal.WriteIntPtr(target, table);
                    Call(SteamBridge.Resolve("SteamAPI_RegisterCallback"), target, new IntPtr(1101));
                }
                var stats = Call(SteamBridge.Resolve("SteamUserStats"));
                var request = Marshal.ReadIntPtr(Marshal.ReadIntPtr(stats));
                Call(request, stats);
                GC.Collect();
                Call(SteamBridge.Resolve("SteamAPI_RunCallbacks"));
                Assert.Equal(1, delivered);
                Assert.Equal(1, payloadResult);
                Call(request, stats);
                Call(SteamBridge.Resolve("SteamAPI_Shutdown"));
                Assert.Equal(0, Marshal.ReadByte(first, 8));
                Assert.Equal(0, Marshal.ReadByte(second, 8));
                Call(SteamBridge.Resolve("SteamAPI_Init"));
                Call(SteamBridge.Resolve("SteamAPI_RunCallbacks"));
                Assert.Equal(1, delivered);
            }
            finally
            {
                SteamClassic.Shutdown();
                GC.KeepAlive(callback);
                Marshal.FreeHGlobal(first); Marshal.FreeHGlobal(second); Marshal.FreeHGlobal(table);
            }
        }

        [SkippableFact]
        public void WindowsNativeCallerExercisesEveryUser016Slot()
        {
            Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows));
            var fixture = Environment.GetEnvironmentVariable("NATIVRA_STEAM_ABI_FIXTURE");
            Skip.If(string.IsNullOrEmpty(fixture), "Build native/tests/steam-abi on Windows first.");
            var library = NativeLibrary.Load(fixture);
            try
            {
                var run = Marshal.GetDelegateForFunctionPointer<NativeProbe>(NativeLibrary.GetExport(library, "probe_user016"));
                var user = User();
                for (var iteration = 0; iteration < 3; iteration++)
                {
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    Assert.Equal(0, run(user, UserId));
                    SteamClassic.Shutdown();
                    Assert.Equal(user, User());
                }
                var factories = Marshal.GetDelegateForFunctionPointer<FactoryProbe>(NativeLibrary.GetExport(library, "probe_factories"));
                Assert.Equal(0, factories(
                    SteamBridge.Resolve("SteamInternal_CreateInterface"),
                    SteamBridge.Resolve("SteamInternal_FindOrCreateUserInterface"),
                    SteamBridge.Resolve("SteamAPI_ISteamUser_GetSteamID"),
                    SteamBridge.Resolve("SteamUser"), UserId));
            }
            finally { NativeLibrary.Free(library); }
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int FactoryProbe(IntPtr create, IntPtr find, IntPtr flat, IntPtr classic, ulong expected);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NativeProbe(IntPtr user, ulong expected);
    }
}
