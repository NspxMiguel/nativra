using System;
using System.Runtime.InteropServices;

namespace Kiosk.Native
{
    /// <summary>
    /// SteamUser016's 22 MSVC x64 slots. Declarations are independently written;
    /// version/parameter order cross-checked against pinned Proton wrappers.
    /// This is an offline ABI surface, not an authenticated Steam client.
    /// </summary>
    internal static class SteamInterfaceContracts
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate IntPtr Factory(IntPtr version);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate IntPtr UserFactory(int user, IntPtr version);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate IntPtr ClientUserFactory(IntPtr self, int user, int pipe, IntPtr version);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate IntPtr ClientUtilsFactory(IntPtr self, int pipe, IntPtr version);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate ulong ScalarSteamId(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int Integer(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal delegate bool Boolean(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate IntPtr StructureSteamId(IntPtr self, IntPtr result);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int Connect(IntPtr self, IntPtr blob, int capacity, ulong server, uint ip, ushort port, [MarshalAs(UnmanagedType.I1)] bool secure);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void Disconnect(IntPtr self, uint ip, ushort port);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void Usage(IntPtr self, ulong game, int usage, IntPtr extra);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool Folder(IntPtr self, IntPtr buffer, int capacity);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void Nothing(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int AvailableVoice(IntPtr self, IntPtr compressed, IntPtr uncompressed, uint rate);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int Voice(IntPtr self, [MarshalAs(UnmanagedType.I1)] bool compressed, IntPtr buffer, uint capacity,
            IntPtr written, [MarshalAs(UnmanagedType.I1)] bool uncompressed, IntPtr raw, uint rawCapacity, IntPtr rawWritten, uint rate);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int Decompress(IntPtr self, IntPtr input, uint size, IntPtr output, uint capacity, IntPtr written, uint rate);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate uint AuthTicket(IntPtr self, IntPtr ticket, int capacity, IntPtr written);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int BeginAuth(IntPtr self, IntPtr ticket, int size, ulong user);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void EndAuth(IntPtr self, ulong user);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void CancelAuth(IntPtr self, uint ticket);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int License(IntPtr self, ulong user, uint app);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void Advertise(IntPtr self, ulong server, uint ip, ushort port);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate ulong RequestTicket(IntPtr self, IntPtr data, int size);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal delegate bool EncryptedTicket(IntPtr self, IntPtr ticket, int capacity, IntPtr written);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool ReleasePipe(IntPtr self, int pipe);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ConnectUser(IntPtr self, int pipe);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int LocalUser(IntPtr self, IntPtr pipe, int accountType);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void ReleaseUser(IntPtr self, int pipe, int user);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr ClientInterface(IntPtr self, int user, int pipe, IntPtr version);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr UtilsInterface(IntPtr self, int pipe, IntPtr version);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void WarningHook(IntPtr self, IntPtr callback);

        internal static IntPtr[] Client012(Func<Delegate, IntPtr> keep,
            Func<string, IntPtr> resolve, Func<IntPtr, string> text)
        {
            Func<string, IntPtr> getter = expected => keep(new ClientInterface((self, user, pipe, version) =>
                user == 1 && pipe == 1 && expected != null && text(version) == expected
                    ? resolve(expected) : IntPtr.Zero));
            var unsupported = getter(null);
            return new[]
            {
                keep(new Integer(self => 1)),
                keep(new ReleasePipe((self, pipe) => pipe == 1)),
                keep(new ConnectUser((self, pipe) => pipe == 1 ? 1 : 0)),
                keep(new LocalUser((self, pipe, accountType) => { ClearCount(pipe); return 0; })),
                keep(new ReleaseUser((self, pipe, user) => { })),
                getter("SteamUser016"),
                unsupported,
                keep(new Disconnect((self, ip, port) => { })),
                getter("SteamFriends011"),
                keep(new UtilsInterface((self, pipe, version) =>
                    pipe == 1 && text(version) == "SteamUtils005" ? resolve("SteamUtils005") : IntPtr.Zero)),
                unsupported, unsupported, unsupported,
                getter("STEAMUSERSTATS_INTERFACE_VERSION010"),
                unsupported,
                getter("STEAMAPPS_INTERFACE_VERSION005"),
                unsupported,
                getter("STEAMREMOTESTORAGE_INTERFACE_VERSION006"),
                unsupported,
                keep(new Nothing(self => { })),
                keep(new Integer(self => 0)),
                keep(new WarningHook((self, callback) => { })),
                keep(new Boolean(self => false)),
                unsupported, unsupported, unsupported,
                unsupported // Slot 26: GetISteamUGC, present in SteamClient012.
            };
        }

        private static void ClearCount(IntPtr output)
        {
            if (output != IntPtr.Zero) Marshal.WriteInt32(output, 0);
        }

        internal static IntPtr[] User016(Func<Delegate, IntPtr> keep, Func<ulong> steamId)
        {
            return new[]
            {
                keep(new Integer(self => 1)),
                keep(new Boolean(self => false)),
                keep(new StructureSteamId((self, result) =>
                {
                    if (result != IntPtr.Zero) Marshal.WriteInt64(result, unchecked((long)steamId()));
                    return result;
                })),
                keep(new Connect((self, blob, capacity, server, ip, port, secure) => 0)),
                keep(new Disconnect((self, ip, port) => { })),
                keep(new Usage((self, game, usage, extra) => { })),
                keep(new Folder((self, buffer, capacity) =>
                {
                    if (buffer != IntPtr.Zero && capacity > 0) Marshal.WriteByte(buffer, 0);
                    return false;
                })),
                keep(new Nothing(self => { })),
                keep(new Nothing(self => { })),
                keep(new AvailableVoice((self, compressed, uncompressed, rate) =>
                {
                    ClearCount(compressed); ClearCount(uncompressed);
                    return 1; // NotInitialized
                })),
                keep(new Voice((self, compressed, buffer, capacity, written, uncompressed, raw, rawCapacity, rawWritten, rate) =>
                {
                    ClearCount(written); ClearCount(rawWritten);
                    return 1;
                })),
                keep(new Decompress((self, input, size, output, capacity, written, rate) =>
                {
                    ClearCount(written);
                    return 1;
                })),
                keep(new Integer(self => 0)),
                keep(new AuthTicket((self, ticket, capacity, written) =>
                {
                    ClearCount(written);
                    return 0; // Invalid handle; no ticket bytes were produced.
                })),
                keep(new BeginAuth((self, ticket, size, user) => 1)), // InvalidTicket
                keep(new EndAuth((self, user) => { })),
                keep(new CancelAuth((self, ticket) => { })),
                keep(new License((self, user, app) => 2)), // NoAuth
                keep(new Boolean(self => false)),
                keep(new Advertise((self, server, ip, port) => { })),
                keep(new RequestTicket((self, data, size) => 0)),
                keep(new EncryptedTicket((self, ticket, capacity, written) =>
                {
                    ClearCount(written);
                    return false;
                }))
            };
        }
    }
}
