using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using Nativra.X86.Cpu;

namespace Nativra.X86.Loader
{
    // ws2_32 and wsock32 on System.Net.Sockets. A guest socket is a handle
    // for a host Socket that is always non-blocking on the host, because the
    // one host thread the guest runs on must never wait. The guest's own
    // blocking mode is emulated on top of that: a blocking call that would
    // have to wait blocks its green thread (GuestProcess.BlockOnHost) and is
    // asked again when the thread next runs, with SO_RCVTIMEO/SO_SNDTIMEO as
    // its deadline; a non-blocking call fails with WSAEWOULDBLOCK. Winsock's
    // error numbers are the very values of the host's SocketError, so a host
    // failure maps straight across, and no host exception reaches the guest.
    //
    // This file holds the registration table, the socket table and stream /
    // datagram I/O. select, WSAPoll, WSAEventSelect and overlapped I/O are in
    // GuestKernel.NetworkEvents.cs, name resolution in GuestKernel.NetworkNames.cs.
    public sealed partial class GuestKernel
    {
        // Winsock error numbers.
        private const uint WsaEintr = 10004, WsaEacces = 10013, WsaEfault = 10014, WsaEinval = 10022, WsaEmfile = 10024;
        private const uint WsaEwouldblock = 10035, WsaEalready = 10037, WsaEnotsock = 10038, WsaEdestaddrreq = 10039;
        private const uint WsaEmsgsize = 10040, WsaEprototype = 10041, WsaEnoprotoopt = 10042, WsaEprotonosupport = 10043;
        private const uint WsaEsocktnosupport = 10044, WsaEopnotsupp = 10045, WsaEafnosupport = 10047;
        private const uint WsaEaddrinuse = 10048, WsaEaddrnotavail = 10049, WsaEnetdown = 10050, WsaEconnreset = 10054;
        private const uint WsaEnobufs = 10055, WsaEisconn = 10056, WsaEnotconn = 10057, WsaEshutdown = 10058;
        private const uint WsaEtimedout = 10060, WsaVerNotSupported = 10092, WsaNotInitialised = 10093;
        private const uint WsaOperationAborted = 995, WsaIoIncomplete = 996, WsaIoPending = 997;

        private const uint SocketFailed = 0xFFFFFFFF;       // SOCKET_ERROR and INVALID_SOCKET
        private const uint Waiting = 0xFFFFFFF0;            // internal: the call waits and is asked again
        private const int MaxSockets = 4096;
        private const int MaxTransfer = 16 * 1024 * 1024;   // the most one call gathers from guest memory

        // Winsock option and ioctl numbers.
        private const uint SolSocket = 0xFFFF, IpprotoIp = 0, IpprotoTcp = 6, IpprotoIpv6 = 41;
        private const uint FdFionbio = 0x8004667E, FdFionread = 0x4004667F, FdSiocatmark = 0x40047307;
        private const uint SioUdpConnReset = 0x9800000C, SioKeepAliveVals = 0x98000004, SioGetExtensionFunctionPointer = 0xC8000006;

        private uint socketError;       // WSAGetLastError: per process here, as it always was
        private int networkStarted;     // WSAStartup calls not yet matched by WSACleanup

        private readonly Dictionary<uint, GuestSocket> sockets = new Dictionary<uint, GuestSocket>();

        // A datagram can be up to 65507 bytes; it is always received whole and
        // then cut to the guest's buffer, so WSAEMSGSIZE behaves as on Windows
        // on every host (a Unix host would truncate without saying so).
        private readonly byte[] receiveBuffer = new byte[65536 + 64];

        // A blocking send of many bytes, or a MSG_WAITALL receive, that has to
        // wait part-way: its progress, kept across the calls the thread makes
        // while it waits. A thread that is waiting repeats the very call it was
        // in, so the key is the thread, the socket and the direction: a call that
        // ends some other way (the socket closed under it) cannot leave progress
        // behind for the next call the thread makes, and closing a socket drops
        // what was kept for it.
        private sealed class Transfer
        {
            public byte[] Data;
            public int Done;
        }

        private enum TransferKind { Send, Receive }

        private readonly struct TransferKey : IEquatable<TransferKey>
        {
            public readonly uint Thread, Socket;
            public readonly TransferKind Kind;
            public TransferKey(uint thread, uint socket, TransferKind kind) { Thread = thread; Socket = socket; Kind = kind; }
            public bool Equals(TransferKey other) => Thread == other.Thread && Socket == other.Socket && Kind == other.Kind;
            public override bool Equals(object obj) => obj is TransferKey other && Equals(other);
            public override int GetHashCode() => unchecked((int)((Thread * 397u) ^ (Socket * 31u) ^ (uint)Kind));
        }

        private readonly Dictionary<TransferKey, Transfer> transfers = new Dictionary<TransferKey, Transfer>();

        private void DropTransfers(uint socket)
        {
            List<TransferKey> stale = null;
            foreach (var key in transfers.Keys)
                if (key.Socket == socket) (stale ?? (stale = new List<TransferKey>())).Add(key);
            if (stale == null) return;
            foreach (var key in stale) transfers.Remove(key);
        }

        /// <summary>What gethostname answers; it also resolves to this machine's own addresses.</summary>
        public string HostName { get; set; } = "xbox";

        private sealed class GuestSocket
        {
            public uint Handle;
            public Socket Host;
            public AddressFamily Family;
            public SocketType Type;
            public bool NonBlocking;                  // the guest's FIONBIO state; the host socket is always non-blocking
            public uint ReceiveTimeout, SendTimeout;  // milliseconds, 0 = none
            public bool Bound, Listening, Connected, Connecting;
            public bool AcceptPending;                // an AcceptEx names this socket as the one to accept into
            public byte[] Peeked;                     // a datagram a MSG_PEEK took from the host: kept here for the next receive
            public IPEndPoint PeekedFrom;
            public long ConnectedAt;                  // Milliseconds when the connection was made (SO_CONNECT_TIME)
            public uint ConnectError;                 // a connect that failed, until the guest has been told
            public uint HostErrorTaken;               // the host's own SO_ERROR for that failure: Windows keeps reporting it after it was read
            public uint ConnectWaiter;                // the thread inside a blocking connect
            public bool ShutdownReceive, ShutdownSend;
            public IPEndPoint Peer;
            public int ReceiveBufferSize, SendBufferSize;   // as the guest set them (0 = never set)

            // WSAEventSelect: the event, the events asked for, and what has been recorded for the guest.
            public uint Event, EventMask, EventsPosted;
            public readonly uint[] EventErrors = new uint[10];
            public bool ReadArmed = true, WriteArmed, AcceptArmed = true, ClosePosted;
        }

        private struct Piece
        {
            public readonly uint Address, Length;
            public Piece(uint address, uint length) { Address = address; Length = length; }
        }

        // What a registered import answers when the host fails under it.
        private enum NetFail { MinusOne, Zero, Code }

        private HostCall Guarded(HostCall body, NetFail fail, bool startup)
        {
            return c =>
            {
                if (startup && networkStarted == 0) return Failed(fail, WsaNotInitialised);
                try
                {
                    return body(c);
                }
                catch (Exception e) when (IsHostFailure(e))
                {
                    return Failed(fail, ErrorOf(e));
                }
            };
        }

        private ulong Failed(NetFail fail, uint error)
        {
            SetSocketError(error);
            return fail == NetFail.Code ? error : fail == NetFail.Zero ? 0u : SocketFailed;
        }

        private static bool IsHostFailure(Exception e) =>
            e is SocketException || e is InvalidOperationException || e is ArgumentException ||
            e is NotSupportedException || e is UnauthorizedAccessException || e is System.IO.IOException ||
            e is AggregateException || e is GuestFaultException;

        private static uint ErrorOf(Exception e)
        {
            if (e is SocketException se) return CodeOf(se.SocketErrorCode);
            if (e is AggregateException ae && ae.InnerException is SocketException inner) return CodeOf(inner.SocketErrorCode);
            if (e is ObjectDisposedException) return WsaEnotsock;
            if (e is NotSupportedException) return WsaEopnotsupp;
            if (e is UnauthorizedAccessException) return WsaEacces;
            if (e is GuestFaultException) return WsaEfault;
            return WsaEinval;
        }

        private static uint CodeOf(SocketError error)
        {
            var code = (int)error;
            return code >= 10000 || code == 995 || code == 996 || code == 997 ? (uint)code : WsaEnetdown;
        }

        private void SetSocketError(uint error)
        {
            socketError = error;
            process.LastError = error;   // GetLastError and WSAGetLastError are one slot on Windows
        }

        private uint SockFail(uint error)
        {
            SetSocketError(error);
            return SocketFailed;
        }

        /// <summary>The socket for a handle; a socket closed while a thread waited on it interrupts that wait.</summary>
        private bool TrySocket(uint handle, out GuestSocket socket)
        {
            if (sockets.TryGetValue(handle, out socket)) return true;
            SetSocketError(process.CurrentThread.Blocked ? WsaEintr : WsaEnotsock);
            return false;
        }

        /// <summary>
        /// A call that cannot answer yet: a non-blocking socket fails with
        /// WSAEWOULDBLOCK; a blocking one waits (and is asked again) until the
        /// timeout, which is the socket's own option, 0 for none.
        /// </summary>
        private uint NetWait(GuestSocket s, uint timeout)
        {
            if (s.NonBlocking) return SockFail(WsaEwouldblock);
            if (process.WaitTimedOut(timeout == 0 ? Infinite : timeout)) return SockFail(WsaEtimedout);
            process.BlockOnHost();
            return 0;
        }

        /// <summary>Closes every socket (WSACleanup of the last client, or the host tearing the guest down).</summary>
        public void CloseNetwork()
        {
            foreach (var s in new List<GuestSocket>(sockets.Values)) ReleaseSocket(s);
            sockets.Clear();
            transfers.Clear();
            pendingIo.Clear();
            eventSockets.Clear();
        }

        private void InstallNetwork(GuestImports i)
        {
            const string ws = "ws2_32.dll";
            // The classic BSD calls are in both DLLs, by name and by ordinal. The two DLLs number them alike
            // except for three: wsock32 keeps the order of the Winsock 1.1 library (inet_addr 10, inet_ntoa 11,
            // ioctlsocket 12) where ws2_32 has ioctlsocket 10, inet_addr 11, inet_ntoa 12.
            void Both(string name, int ordinal, int args, HostCall body, NetFail fail = NetFail.MinusOne, bool startup = true, int wsockOrdinal = 0)
            {
                var guarded = Guarded(body, fail, startup);
                i.Register(ws, name, CallConv.Stdcall, args, guarded);
                i.RegisterOrdinal(ws, ordinal, CallConv.Stdcall, args, guarded);
                i.Register("wsock32.dll", name, CallConv.Stdcall, args, guarded);
                i.RegisterOrdinal("wsock32.dll", wsockOrdinal != 0 ? wsockOrdinal : ordinal, CallConv.Stdcall, args, guarded);
            }
            // The Winsock 2 additions are in ws2_32 only.
            void Ws(string name, int args, HostCall body, NetFail fail = NetFail.MinusOne, bool startup = true) =>
                i.Register(ws, name, CallConv.Stdcall, args, Guarded(body, fail, startup));

            Both("WSAStartup", 115, 2, c => Startup(c.Arg(0), c.Arg(1)), NetFail.Code, false);
            Both("WSACleanup", 116, 0, c =>
            {
                if (networkStarted == 0) return SockFail(WsaNotInitialised);
                if (--networkStarted == 0) CloseNetwork();
                return 0;
            }, NetFail.MinusOne, false);
            Both("WSAGetLastError", 111, 0, c => socketError, NetFail.MinusOne, false);
            Both("WSASetLastError", 112, 1, c => { SetSocketError(c.Arg(0)); return 0; }, NetFail.MinusOne, false);
            Both("WSAIsBlocking", 114, 0, c => 0, NetFail.MinusOne, false);
            Both("WSACancelBlockingCall", 113, 0, c => SockFail(WsaEinval), NetFail.MinusOne, false);

            Both("socket", 23, 3, c => CreateSocket(c.Arg(0), c.Arg(1), c.Arg(2), false));
            Ws("WSASocketA", 6, c => CreateSocket(c.Arg(0), c.Arg(1), c.Arg(2), (c.Arg(5) & 1) != 0));
            Ws("WSASocketW", 6, c => CreateSocket(c.Arg(0), c.Arg(1), c.Arg(2), (c.Arg(5) & 1) != 0));
            Both("closesocket", 3, 1, c => CloseSocket(c.Arg(0)));
            Both("bind", 2, 3, c => Bind(c.Arg(0), c.Arg(1), c.Arg(2)));
            Both("listen", 13, 2, c => Listen(c.Arg(0), (int)c.Arg(1)));
            Both("accept", 1, 3, c => Accept(c.Arg(0), c.Arg(1), c.Arg(2)));
            Ws("WSAAccept", 5, c => Accept(c.Arg(0), c.Arg(1), c.Arg(2)));
            Both("connect", 4, 3, c => Connect(c.Arg(0), c.Arg(1), c.Arg(2)));
            Ws("WSAConnect", 7, c => Connect(c.Arg(0), c.Arg(1), c.Arg(2)));
            Both("send", 19, 4, c => SendCall(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3), 0, 0));
            Both("sendto", 20, 6, c => SendCall(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3), c.Arg(4), c.Arg(5)));
            Both("recv", 16, 4, c => RecvCall(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3), 0, 0));
            Both("recvfrom", 17, 6, c => RecvCall(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3), c.Arg(4), c.Arg(5)));
            Both("shutdown", 22, 2, c => Shutdown(c.Arg(0), c.Arg(1)));
            Both("select", 18, 5, c => Select(c.Arg(1), c.Arg(2), c.Arg(3), c.Arg(4)));
            Both("__WSAFDIsSet", 151, 2, c => FdIsSet(c.Arg(0), c.Arg(1)), NetFail.Zero, false);
            Both("ioctlsocket", 10, 3, c => IoctlSocket(c.Arg(0), c.Arg(1), c.Arg(2)), wsockOrdinal: 12);
            Both("setsockopt", 21, 5, c => SetSockOpt(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3), c.Arg(4)));
            Both("getsockopt", 7, 5, c => GetSockOpt(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3), c.Arg(4)));
            Both("getsockname", 6, 3, c => GetSockName(c.Arg(0), c.Arg(1), c.Arg(2), false));
            Both("getpeername", 5, 3, c => GetSockName(c.Arg(0), c.Arg(1), c.Arg(2), true));

            Ws("WSAIoctl", 9, c => WsaIoctl(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3), c.Arg(4), c.Arg(5), c.Arg(6), c.Arg(8)));
            Ws("WSASend", 7, c => WsaTransmit(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3), c.Arg(4), 0, 0, c.Arg(5), c.Arg(6)));
            Ws("WSASendTo", 9, c => WsaTransmit(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3), c.Arg(4), c.Arg(5), c.Arg(6), c.Arg(7), c.Arg(8)));
            // WSAMSG: name, namelen, lpBuffers, dwBufferCount, Control { len, buf }, dwFlags. The ancillary data is not used.
            Ws("WSASendMsg", 6, c => c.Arg(1) == 0 ? SockFail(WsaEfault)
                : WsaTransmit(c.Arg(0), memory.Read32(c.Arg(1) + 8), memory.Read32(c.Arg(1) + 12), c.Arg(3), c.Arg(2),
                    memory.Read32(c.Arg(1)), memory.Read32(c.Arg(1) + 4), c.Arg(4), c.Arg(5)));
            Ws("WSARecv", 7, c => WsaReceive(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3), c.Arg(4), 0, 0, c.Arg(5), c.Arg(6)));
            Ws("WSARecvFrom", 9, c => WsaReceive(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3), c.Arg(4), c.Arg(5), c.Arg(6), c.Arg(7), c.Arg(8)));
            Ws("WSAGetOverlappedResult", 5, c => WsaOverlappedResult(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3) != 0, c.Arg(4)), NetFail.Zero);
            Ws("WSAPoll", 3, c => WsaPoll(c.Arg(0), c.Arg(1), c.Arg(2)));

            Ws("WSACreateEvent", 0, c => CreateEvent(true, false), NetFail.Zero, false);
            Ws("WSACloseEvent", 1, c => CloseEvent(c.Arg(0)), NetFail.Zero, false);
            Ws("WSAResetEvent", 1, c => SignalEvent(c.Arg(0), false), NetFail.Zero, false);
            Ws("WSASetEvent", 1, c => SignalEvent(c.Arg(0), true), NetFail.Zero, false);
            Ws("WSAWaitForMultipleEvents", 5, c => WaitAny(Handles(c.Arg(1), c.Arg(0)), c.Arg(2) != 0, c.Arg(3)), NetFail.MinusOne, false);
            Ws("WSAEventSelect", 3, c => EventSelect(c.Arg(0), c.Arg(1), c.Arg(2)));
            Ws("WSAEnumNetworkEvents", 3, c => EnumNetworkEvents(c.Arg(0), c.Arg(1), c.Arg(2)));
            // Window-message notification would need user32's message queue.
            Ws("WSAAsyncSelect", 4, c => SockFail(WsaEopnotsupp));

            Both("htons", 9, 1, c => (uint)(ushort)((c.Arg(0) >> 8) | (c.Arg(0) << 8)), NetFail.MinusOne, false);
            Both("ntohs", 15, 1, c => (uint)(ushort)((c.Arg(0) >> 8) | (c.Arg(0) << 8)), NetFail.MinusOne, false);
            Both("htonl", 8, 1, c => SwapBytes(c.Arg(0)), NetFail.MinusOne, false);
            Both("ntohl", 14, 1, c => SwapBytes(c.Arg(0)), NetFail.MinusOne, false);

            Both("inet_addr", 11, 1, c => InetAddr(c.Arg(0)), NetFail.MinusOne, false, wsockOrdinal: 10);
            Both("inet_ntoa", 12, 1, c => InetNtoa(c.Arg(0)), NetFail.Zero, false, wsockOrdinal: 11);
            Ws("inet_pton", 3, c => InetPton(c.Arg(0), ReadText(c.Arg(1), false), c.Arg(2)), NetFail.MinusOne, false);
            Ws("InetPtonA", 3, c => InetPton(c.Arg(0), ReadText(c.Arg(1), false), c.Arg(2)), NetFail.MinusOne, false);
            Ws("InetPtonW", 3, c => InetPton(c.Arg(0), ReadText(c.Arg(1), true), c.Arg(2)), NetFail.MinusOne, false);
            Ws("inet_ntop", 4, c => InetNtop(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3), false), NetFail.Zero, false);
            Ws("InetNtopA", 4, c => InetNtop(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3), false), NetFail.Zero, false);
            Ws("InetNtopW", 4, c => InetNtop(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3), true), NetFail.Zero, false);

            Both("gethostname", 57, 2, c => GetHostName(c.Arg(0), c.Arg(1)));
            Both("gethostbyname", 52, 1, c => GetHostByName(c.Arg(0)), NetFail.Zero);
            Both("gethostbyaddr", 51, 3, c => GetHostByAddr(c.Arg(0), c.Arg(1), c.Arg(2)), NetFail.Zero);
            Both("getprotobyname", 53, 1, c => GetProtoByName(c.Arg(0)), NetFail.Zero);
            Both("getprotobynumber", 54, 1, c => GetProtoByNumber(c.Arg(0)), NetFail.Zero);
            Both("getservbyname", 55, 2, c => GetServByName(c.Arg(0), c.Arg(1)), NetFail.Zero);
            Both("getservbyport", 56, 2, c => GetServByPort(c.Arg(0), c.Arg(1)), NetFail.Zero);
            Ws("getaddrinfo", 4, c => GetAddrInfo(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3), false), NetFail.Code);
            Ws("GetAddrInfoW", 4, c => GetAddrInfo(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3), true), NetFail.Code);
            Ws("freeaddrinfo", 1, c => { FreeAddrInfo(c.Arg(0)); return 0; }, NetFail.Zero, false);
            Ws("FreeAddrInfoW", 1, c => { FreeAddrInfo(c.Arg(0)); return 0; }, NetFail.Zero, false);
            Ws("getnameinfo", 7, c => GetNameInfo(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3), c.Arg(4), c.Arg(5), c.Arg(6), false), NetFail.Code);
            Ws("GetNameInfoW", 7, c => GetNameInfo(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3), c.Arg(4), c.Arg(5), c.Arg(6), true), NetFail.Code);

            InstallMswsock(i);
            InstallWininet(i);
        }

        private uint Startup(uint version, uint data)
        {
            var requested = version & 0xFFFF;
            if ((requested & 0xFF) == 0) return WsaVerNotSupported;
            if (data == 0) return WsaEfault;
            networkStarted++;
            memory.WriteBytes(data, new byte[400]);
            memory.Write16(data, (ushort)Math.Min(requested, 0x0202));
            memory.Write16(data + 2, 0x0202);
            WriteText(data + 4, "WinSock 2.0", false);
            WriteText(data + 261, "Running", false);
            return 0;
        }

        private static uint SwapBytes(uint v) =>
            ((v & 0xFF) << 24) | ((v & 0xFF00) << 8) | ((v >> 8) & 0xFF00) | (v >> 24);

        private uint CloseEvent(uint handle)
        {
            foreach (var s in eventSockets)
                if (s.Event == handle) { s.Event = 0; s.EventMask = 0; }
            eventSockets.RemoveAll(s => s.EventMask == 0);
            return CloseHandle(handle);
        }

        // --- creating and closing ------------------------------------------------------------------------

        private uint CreateSocket(uint af, uint type, uint protocol, bool overlapped)
        {
            AddressFamily family;
            if (af == 2 || af == 0) family = AddressFamily.InterNetwork;
            else if (af == 23) family = AddressFamily.InterNetworkV6;
            else return SockFail(WsaEafnosupport);

            // A type of 0 lets the protocol pick it (WSASocket).
            if (type == 0) type = protocol == 6 ? 1u : protocol == 17 ? 2u : 0u;
            SocketType socketType;
            ProtocolType protocolType;
            switch (type)
            {
                case 1:
                    if (protocol != 0 && protocol != 6) return SockFail(protocol == 17 ? WsaEprototype : WsaEprotonosupport);
                    socketType = SocketType.Stream;
                    protocolType = ProtocolType.Tcp;
                    break;
                case 2:
                    if (protocol != 0 && protocol != 17) return SockFail(protocol == 6 ? WsaEprototype : WsaEprotonosupport);
                    socketType = SocketType.Dgram;
                    protocolType = ProtocolType.Udp;
                    break;
                default:
                    return SockFail(WsaEsocktnosupport);   // raw sockets are not available to a game
            }
            if (sockets.Count >= MaxSockets) return SockFail(WsaEmfile);

            var host = new Socket(family, socketType, protocolType) { Blocking = false };
            var handle = NewHandle();
            sockets[handle] = new GuestSocket { Handle = handle, Host = host, Family = family, Type = socketType };
            return handle;
        }

        private uint CloseSocket(uint handle)
        {
            if (!sockets.TryGetValue(handle, out var s)) return SockFail(WsaEnotsock);
            sockets.Remove(handle);
            ReleaseSocket(s);
            return 0;
        }

        private void ReleaseSocket(GuestSocket s)
        {
            CancelPending(s);   // overlapped calls end as aborted, their packets still queued
            DropTransfers(s.Handle);
            eventSockets.Remove(s);
            portBindings.Remove(s.Handle);
            CloseHost(s.Host);
        }

        /// <summary>
        /// Closes a host socket without ever waiting. With SO_LINGER on and a positive time, the host's
        /// close holds the calling thread (the one thread the whole guest runs on) until the peer has
        /// taken the queued data or the time runs out. A plain close is graceful as well: what is queued
        /// is still delivered, followed by the FIN. Linger on with a zero time (an abortive close, a
        /// reset) does not wait, and stays.
        /// </summary>
        private static void CloseHost(Socket host)
        {
            try
            {
                var linger = host.LingerState;
                if (linger != null && linger.Enabled && linger.LingerTime > 0) host.LingerState = new LingerOption(false, 0);
            }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
            try { host.Close(); }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }

        // --- addresses -------------------------------------------------------------------------------------

        private static bool IsAny(IPAddress a) => a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any);

        private static uint SockaddrSize(AddressFamily family) => family == AddressFamily.InterNetworkV6 ? 28u : 16u;

        /// <summary>A sockaddr_in or sockaddr_in6 from the guest: the port is big-endian, the address in network order.</summary>
        private uint ReadSockaddr(uint address, uint length, out IPEndPoint endpoint)
        {
            endpoint = null;
            if (address == 0 || length < 2) return WsaEfault;
            var family = memory.Read16(address);
            if (family == 2)
            {
                if (length < 16) return WsaEfault;
                var port = (memory.Read8(address + 2) << 8) | memory.Read8(address + 3);
                endpoint = new IPEndPoint(new IPAddress(memory.ReadBytes(address + 4, 4)), port);
                return 0;
            }
            if (family == 23)
            {
                if (length < 28) return WsaEfault;
                var port = (memory.Read8(address + 2) << 8) | memory.Read8(address + 3);
                endpoint = new IPEndPoint(new IPAddress(memory.ReadBytes(address + 8, 16), memory.Read32(address + 24)), port);
                return 0;
            }
            return WsaEafnosupport;
        }

        private byte[] SockaddrBytes(IPEndPoint endpoint)
        {
            var address = endpoint.Address.GetAddressBytes();
            byte[] bytes;
            if (endpoint.AddressFamily == AddressFamily.InterNetworkV6)
            {
                bytes = new byte[28];
                bytes[0] = 23;
                Array.Copy(address, 0, bytes, 8, 16);
                var scope = (uint)endpoint.Address.ScopeId;
                bytes[24] = (byte)scope; bytes[25] = (byte)(scope >> 8); bytes[26] = (byte)(scope >> 16); bytes[27] = (byte)(scope >> 24);
            }
            else
            {
                bytes = new byte[16];
                bytes[0] = 2;
                Array.Copy(address, 0, bytes, 4, 4);
            }
            bytes[2] = (byte)(endpoint.Port >> 8);
            bytes[3] = (byte)endpoint.Port;
            return bytes;
        }

        /// <summary>
        /// Writes an address through the (name, namelen*) pair of accept,
        /// getsockname and recvfrom. A buffer that is too small is
        /// WSAEFAULT for those that care; recvfrom takes what fits.
        /// </summary>
        private uint WriteSockaddrOut(uint name, uint lengthPointer, IPEndPoint endpoint, bool strict)
        {
            if (name == 0 || lengthPointer == 0 || endpoint == null) return 0;
            var bytes = SockaddrBytes(endpoint);
            var room = memory.Read32(lengthPointer);
            if (room < bytes.Length && strict) return WsaEfault;
            memory.WriteBytes(name, bytes, 0, (int)Math.Min(room, (uint)bytes.Length));
            memory.Write32(lengthPointer, (uint)bytes.Length);
            return 0;
        }

        private static IPEndPoint AnyEndpoint(AddressFamily family) =>
            new IPEndPoint(family == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);

        // --- bind, listen, accept, connect -------------------------------------------------------------------

        private uint Bind(uint handle, uint name, uint length)
        {
            if (!TrySocket(handle, out var s)) return SocketFailed;
            var error = ReadSockaddr(name, length, out var endpoint);
            if (error != 0) return SockFail(error);
            if (endpoint.AddressFamily != s.Family) return SockFail(WsaEafnosupport);
            if (s.Bound || s.Connected || s.Connecting || s.Listening || s.AcceptPending) return SockFail(WsaEinval);
            s.Host.Bind(endpoint);
            s.Bound = true;
            return 0;
        }

        private uint Listen(uint handle, int backlog)
        {
            if (!TrySocket(handle, out var s)) return SocketFailed;
            if (s.Type != SocketType.Stream) return SockFail(WsaEopnotsupp);
            if (s.Connected || s.Connecting) return SockFail(WsaEisconn);
            s.Host.Listen(backlog <= 0 ? 128 : Math.Min(backlog, 1024));
            s.Listening = true;
            s.Bound = true;
            return 0;
        }

        private uint Accept(uint handle, uint address, uint lengthPointer)
        {
            if (!TrySocket(handle, out var s)) return SocketFailed;
            if (!s.Listening) return SockFail(WsaEinval);
            if (address != 0 && (lengthPointer == 0 || memory.Read32(lengthPointer) < SockaddrSize(s.Family)))
                return SockFail(WsaEfault);

            ServiceAccepts(s);   // an AcceptEx already waiting for a connection has it before accept does
            Socket accepted = null;
            if (s.Host.Poll(0, SelectMode.SelectRead))
            {
                try { accepted = s.Host.Accept(); }
                catch (SocketException e) when (e.SocketErrorCode == SocketError.WouldBlock) { }
            }
            if (accepted == null) return NetWait(s, 0);   // accept has no timeout of its own

            accepted.Blocking = false;
            var peer = accepted.RemoteEndPoint as IPEndPoint;
            var handleOut = NewHandle();
            var n = new GuestSocket
            {
                Handle = handleOut, Host = accepted, Family = s.Family, Type = s.Type,
                Bound = true, Connected = true, WriteArmed = true, Peer = peer, ConnectedAt = Milliseconds,
                NonBlocking = s.NonBlocking, ReceiveTimeout = s.ReceiveTimeout, SendTimeout = s.SendTimeout,
                Event = s.Event, EventMask = s.EventMask,   // the new socket has the listening one's properties, events included
            };
            sockets[handleOut] = n;
            if (n.EventMask != 0) eventSockets.Add(n);
            s.AcceptArmed = true;
            WriteSockaddrOut(address, lengthPointer, peer, true);
            return handleOut;
        }

        private uint Connect(uint handle, uint name, uint length)
        {
            if (!TrySocket(handle, out var s)) return SocketFailed;
            Settle(s);

            // The thread inside a blocking connect asks again until it ends.
            if (s.ConnectWaiter == Me)
            {
                if (s.Connecting) { process.BlockOnHost(); return 0; }
                s.ConnectWaiter = 0;
                if (s.ConnectError == 0) return 0;
            }
            if (s.ConnectError != 0)
            {
                var failed = s.ConnectError;
                s.ConnectError = 0;
                return SockFail(failed);
            }
            if (s.Connecting) return SockFail(WsaEalready);
            if (s.Listening || s.AcceptPending) return SockFail(WsaEinval);
            if (s.Connected && s.Type == SocketType.Stream) return SockFail(WsaEisconn);

            var error = ReadSockaddr(name, length, out var endpoint);
            if (error != 0) return SockFail(error);
            if (endpoint.AddressFamily != s.Family) return SockFail(WsaEafnosupport);
            if (IsAny(endpoint.Address) || endpoint.Port == 0) return SockFail(WsaEaddrnotavail);

            try
            {
                s.Host.Connect(endpoint);
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.WouldBlock || e.SocketErrorCode == SocketError.InProgress)
            {
                // The connection is being made: select and WSAPoll say writable once it is.
                s.Connecting = true;
                s.Bound = true;
                s.Peer = endpoint;
                if (s.NonBlocking) return SockFail(WsaEwouldblock);
                s.ConnectWaiter = Me;
                process.BlockOnHost();
                return 0;
            }
            s.Connected = true;
            s.ConnectedAt = Milliseconds;
            s.Bound = true;
            s.Peer = endpoint;
            s.WriteArmed = true;
            Post(s, NetFdConnect, 0);
            return 0;
        }

        /// <summary>
        /// Notices that a connect the host was still making has ended, and
        /// records how: connected, or failed with the reason the guest will ask for.
        /// </summary>
        private void Settle(GuestSocket s)
        {
            if (!s.Connecting) return;
            uint error = 0;
            try
            {
                // Writable once connected; a refused or unreachable connect only shows as an error.
                if (!s.Host.Poll(0, SelectMode.SelectWrite) && !s.Host.Poll(0, SelectMode.SelectError)) return;
                error = (uint)(int)s.Host.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Error);
            }
            catch (SocketException e) { error = CodeOf(e.SocketErrorCode); }
            catch (ObjectDisposedException) { error = WsaEnotsock; }

            s.Connecting = false;
            if (error == 0)
            {
                s.Connected = true;
                s.ConnectedAt = Milliseconds;
                s.WriteArmed = true;
            }
            else { s.ConnectError = error; s.HostErrorTaken = error; }
            Post(s, NetFdConnect, error);
        }

        // --- send ------------------------------------------------------------------------------------------

        private uint SendCall(uint handle, uint buffer, uint length, uint flags, uint to, uint toLength)
        {
            if (!TrySocket(handle, out var s)) return SocketFailed;
            var error = DestinationOf(s, to, toLength, out var target);
            if (error != 0) return SockFail(error);
            error = TransmitCall(s, new[] { new Piece(buffer, length) }, flags, target, out var count);
            if (error == Waiting) return 0;
            return error != 0 ? SockFail(error) : count;
        }

        private uint DestinationOf(GuestSocket s, uint to, uint toLength, out IPEndPoint target)
        {
            target = null;
            if (to == 0) return 0;
            var error = ReadSockaddr(to, toLength, out target);
            if (error != 0) return error;
            if (target.AddressFamily != s.Family) return WsaEafnosupport;
            // 0.0.0.0 and port 0 name nobody, whatever a Unix host makes of them.
            if (IsAny(target.Address) || target.Port == 0) return WsaEaddrnotavail;
            return 0;
        }

        private static SocketFlags HostFlags(uint flags)
        {
            var host = SocketFlags.None;
            if ((flags & 1) != 0) host |= SocketFlags.OutOfBand;
            if ((flags & 2) != 0) host |= SocketFlags.Peek;
            if ((flags & 4) != 0) host |= SocketFlags.DontRoute;
            return host;
        }

        private byte[] Gather(Piece[] pieces)
        {
            long total = 0;
            foreach (var p in pieces) total += p.Length;
            if (total > MaxTransfer) return null;
            var data = new byte[total];
            var at = 0;
            foreach (var p in pieces)
            {
                if (p.Length == 0) continue;
                var bytes = memory.ReadBytes(p.Address, (int)p.Length);
                Array.Copy(bytes, 0, data, at, bytes.Length);
                at += bytes.Length;
            }
            return data;
        }

        private uint Scatter(Piece[] pieces, byte[] data, int count)
        {
            var offset = 0;
            foreach (var p in pieces)
            {
                if (offset >= count) break;
                var n = (int)Math.Min(p.Length, (uint)(count - offset));
                memory.WriteBytes(p.Address, data, offset, n);
                offset += n;
            }
            return (uint)offset;
        }

        private static long TotalLength(Piece[] pieces)
        {
            long total = 0;
            foreach (var p in pieces) total += p.Length;
            return total;
        }

        /// <summary>
        /// One send now. 0 with the bytes the host took; WSAEWOULDBLOCK when
        /// it took none; otherwise the failure.
        /// </summary>
        private uint TrySend(GuestSocket s, byte[] data, int offset, int count, uint flags, IPEndPoint target, out int sent)
        {
            sent = 0;
            if (s.ShutdownSend) return WsaEshutdown;
            try
            {
                if (s.Type == SocketType.Stream)
                {
                    if (!s.Connected)
                    {
                        Settle(s);
                        if (!s.Connected) return WsaEnotconn;
                    }
                    sent = s.Host.Send(data, offset, Math.Min(count, 1 << 18), HostFlags(flags), out var err);
                    if (err == SocketError.Success) return 0;
                    sent = 0;
                    if (err == SocketError.WouldBlock) { s.WriteArmed = true; return WsaEwouldblock; }
                    // The peer is gone (a local shutdown never gets this far): Windows says reset.
                    return err == SocketError.Shutdown ? WsaEconnreset : CodeOf(err);
                }

                if (count > 65507) return WsaEmsgsize;
                if (target == null && !s.Connected) return WsaEdestaddrreq;
                if (target != null) sent = s.Host.SendTo(data, offset, count, HostFlags(flags), target);
                else sent = s.Host.Send(data, offset, count, HostFlags(flags));
                s.Bound = true;   // a datagram socket is bound by its first send, to whatever the host chose
                return 0;
            }
            catch (SocketException e)
            {
                sent = 0;
                if (e.SocketErrorCode == SocketError.WouldBlock) { s.WriteArmed = true; return WsaEwouldblock; }
                return CodeOf(e.SocketErrorCode);
            }
            catch (ObjectDisposedException) { return WsaEnotsock; }
        }

        /// <summary>
        /// send and WSASend without an OVERLAPPED. A blocking socket takes the
        /// whole buffer (a stream may need several turns); a non-blocking one
        /// takes what fits. Returns 0 with the byte count, <see cref="Waiting"/>, or the failure.
        /// </summary>
        private uint TransmitCall(GuestSocket s, Piece[] pieces, uint flags, IPEndPoint target, out uint count)
        {
            count = 0;
            var key = new TransferKey(Me, s.Handle, TransferKind.Send);
            var resumed = transfers.TryGetValue(key, out var t);
            if (!resumed)
            {
                t = new Transfer { Data = Gather(pieces) };
                if (t.Data == null) return WsaEnobufs;
            }
            transfers.Remove(key);

            while (true)
            {
                var error = TrySend(s, t.Data, t.Done, t.Data.Length - t.Done, flags, target, out var sent);
                if (error == WsaEwouldblock)
                {
                    if (s.NonBlocking)
                    {
                        if (t.Done == 0) return WsaEwouldblock;
                        break;   // a non-blocking send reports what it did manage
                    }
                    if (process.WaitTimedOut(s.SendTimeout == 0 ? Infinite : s.SendTimeout)) return WsaEtimedout;
                    process.BlockOnHost();
                    transfers[key] = t;
                    return Waiting;
                }
                if (error != 0) return error;
                t.Done += sent;
                if (t.Done >= t.Data.Length || s.Type != SocketType.Stream) break;
            }
            count = (uint)t.Done;
            return 0;
        }

        // --- receive ---------------------------------------------------------------------------------------

        /// <summary>
        /// One receive now. 0 with the bytes in the receive buffer (0 bytes
        /// from a stream is the peer's orderly close); WSAEWOULDBLOCK when
        /// there is nothing; otherwise the failure. A datagram comes whole.
        /// </summary>
        private uint TryReceive(GuestSocket s, uint flags, long capacity, out int count, out IPEndPoint from)
        {
            count = 0;
            from = null;
            if (s.ShutdownReceive) return WsaEshutdown;
            try
            {
                if (s.Type == SocketType.Stream)
                {
                    if (!s.Connected)
                    {
                        Settle(s);
                        if (!s.Connected) return WsaEnotconn;
                    }
                    if (capacity == 0) return 0;
                    if (!s.Host.Poll(0, SelectMode.SelectRead)) return WsaEwouldblock;
                    count = s.Host.Receive(receiveBuffer, 0, (int)Math.Min(capacity, receiveBuffer.Length), HostFlags(flags & 0x7), out var err);
                    if (err != SocketError.Success)
                    {
                        count = 0;
                        return err == SocketError.WouldBlock ? WsaEwouldblock : CodeOf(err);
                    }
                    if ((flags & 2) == 0) s.ReadArmed = true;
                    from = s.Peer;
                    return 0;
                }

                if (!s.Bound && !s.Connected) return WsaEinval;
                var peek = (flags & 2) != 0;
                if (s.Peeked != null)
                {
                    // The datagram an earlier MSG_PEEK took from the host: it is the next one, peeked or received.
                    count = s.Peeked.Length;
                    Buffer.BlockCopy(s.Peeked, 0, receiveBuffer, 0, count);
                    from = s.PeekedFrom;
                    if (!peek)
                    {
                        s.Peeked = null;
                        s.PeekedFrom = null;
                        s.ReadArmed = true;
                    }
                    return 0;
                }
                if (!s.Host.Poll(0, SelectMode.SelectRead)) return WsaEwouldblock;
                EndPoint remote = AnyEndpoint(s.Family);
                // A peek is never asked of the host: how a host's peek leaves its readiness, its events and
                // its queue is the host's own (a Windows host stopped a peeked datagram from being read again).
                count = s.Host.ReceiveFrom(receiveBuffer, 0, receiveBuffer.Length, HostFlags(flags & 0x5), ref remote);
                from = remote as IPEndPoint;
                if (peek) KeepDatagram(s, count, from);
                else s.ReadArmed = true;
                return 0;
            }
            catch (SocketException e)
            {
                count = 0;
                return e.SocketErrorCode == SocketError.WouldBlock ? WsaEwouldblock : CodeOf(e.SocketErrorCode);
            }
            catch (ObjectDisposedException) { return WsaEnotsock; }
        }

        /// <summary>Keeps the datagram just read into the receive buffer as the socket's next one (what MSG_PEEK and FIONREAD leave).</summary>
        private void KeepDatagram(GuestSocket s, int count, IPEndPoint from)
        {
            s.Peeked = new byte[count];
            Buffer.BlockCopy(receiveBuffer, 0, s.Peeked, 0, count);
            s.PeekedFrom = from;
        }

        private uint RecvCall(uint handle, uint buffer, uint length, uint flags, uint from, uint fromLength)
        {
            if (!TrySocket(handle, out var s)) return SocketFailed;
            var error = ReceiveCall(s, new[] { new Piece(buffer, length) }, flags, from, fromLength, out var count);
            if (error == Waiting) return 0;
            return error != 0 ? SockFail(error) : count;
        }

        /// <summary>
        /// recv, recvfrom and WSARecv without an OVERLAPPED. Returns 0 with the
        /// byte count, <see cref="Waiting"/>, or the failure (a datagram
        /// longer than the buffer fills it and fails with WSAEMSGSIZE).
        /// </summary>
        private uint ReceiveCall(GuestSocket s, Piece[] pieces, uint flags, uint from, uint fromLength, out uint count)
        {
            count = 0;
            var capacity = TotalLength(pieces);
            var waitAll = (flags & 8) != 0 && s.Type == SocketType.Stream && (flags & 2) == 0;   // MSG_WAITALL
            var key = new TransferKey(Me, s.Handle, TransferKind.Receive);
            var resumed = transfers.TryGetValue(key, out var t);
            if (!resumed) t = new Transfer();
            transfers.Remove(key);

            while (true)
            {
                var error = TryReceive(s, flags, capacity - t.Done, out var n, out var peer);
                if (error == WsaEwouldblock)
                {
                    if (s.NonBlocking) return t.Done > 0 ? Finish(t, out count) : WsaEwouldblock;
                    if (process.WaitTimedOut(s.ReceiveTimeout == 0 ? Infinite : s.ReceiveTimeout)) return WsaEtimedout;
                    process.BlockOnHost();
                    transfers[key] = t;
                    return Waiting;
                }
                if (error != 0) return error;

                if (s.Type == SocketType.Dgram)
                {
                    var taken = Scatter(pieces, receiveBuffer, (int)Math.Min(n, capacity));
                    WriteSockaddrOut(from, fromLength, peer, false);
                    count = taken;
                    return n > capacity ? WsaEmsgsize : 0;
                }

                // A stream: fill from where an earlier turn stopped.
                Scatter(Skip(pieces, t.Done), receiveBuffer, n);
                t.Done += n;
                WriteSockaddrOut(from, fromLength, peer, false);
                if (!waitAll || n == 0 || t.Done >= capacity) return Finish(t, out count);
            }
        }

        private static uint Finish(Transfer t, out uint count)
        {
            count = (uint)t.Done;
            return 0;
        }

        /// <summary>The pieces that remain once <paramref name="skip"/> bytes of them are filled.</summary>
        private static Piece[] Skip(Piece[] pieces, int skip)
        {
            if (skip == 0) return pieces;
            var rest = new List<Piece>();
            long left = skip;
            foreach (var p in pieces)
            {
                if (left >= p.Length) { left -= p.Length; continue; }
                rest.Add(new Piece(p.Address + (uint)left, p.Length - (uint)left));
                left = 0;
            }
            return rest.ToArray();
        }

        private uint Shutdown(uint handle, uint how)
        {
            if (!TrySocket(handle, out var s)) return SocketFailed;
            if (how > 2) return SockFail(WsaEinval);
            if (s.Type == SocketType.Stream && !s.Connected)
            {
                Settle(s);
                if (!s.Connected) return SockFail(WsaEnotconn);
            }
            s.Host.Shutdown(how == 0 ? SocketShutdown.Receive : how == 1 ? SocketShutdown.Send : SocketShutdown.Both);
            if (how != 1) s.ShutdownReceive = true;
            if (how != 0) s.ShutdownSend = true;
            return 0;
        }

        // --- ioctl ---------------------------------------------------------------------------------------

        /// <summary>The bytes recv would return now: a stream's backlog, or the size of the next datagram.</summary>
        private uint BytesAvailable(GuestSocket s)
        {
            try
            {
                if (s.Listening) return 0;
                if (s.Type == SocketType.Stream) return s.Connected ? (uint)Math.Max(0, s.Host.Available) : 0;
                if (s.Peeked != null) return (uint)s.Peeked.Length;
                if (!s.Host.Poll(0, SelectMode.SelectRead)) return 0;
                // The size of the next datagram, which is read to know it (the host's own count includes headers
                // on some platforms, and its peek is not relied on) and kept for the receive that takes it.
                EndPoint remote = AnyEndpoint(s.Family);
                var size = s.Host.ReceiveFrom(receiveBuffer, 0, receiveBuffer.Length, SocketFlags.None, ref remote);
                KeepDatagram(s, size, remote as IPEndPoint);
                return (uint)size;
            }
            catch (SocketException) { return 0; }
            catch (ObjectDisposedException) { return 0; }
        }

        private uint SetBlocking(GuestSocket s, bool nonBlocking)
        {
            // WSAEventSelect puts the socket in non-blocking mode and keeps it there.
            if (!nonBlocking && s.EventMask != 0) return SockFail(WsaEinval);
            s.NonBlocking = nonBlocking;
            return 0;
        }

        private uint IoctlSocket(uint handle, uint command, uint argument)
        {
            if (!TrySocket(handle, out var s)) return SocketFailed;
            if (argument == 0) return SockFail(WsaEfault);
            switch (command)
            {
                case FdFionbio: return SetBlocking(s, memory.Read32(argument) != 0);
                case FdFionread: memory.Write32(argument, BytesAvailable(s)); return 0;
                case FdSiocatmark: memory.Write32(argument, 1); return 0;   // no out-of-band data is ever pending
                default: return SockFail(WsaEinval);
            }
        }

        private uint WsaIoctl(uint handle, uint code, uint input, uint inputLength, uint output, uint outputLength, uint returnedPointer, uint completion)
        {
            if (!TrySocket(handle, out var s)) return SocketFailed;
            if (completion != 0) return SockFail(WsaEopnotsupp);   // completion routines run as APCs, which this layer has no alertable wait for
            uint returned = 0;
            switch (code)
            {
                case FdFionbio:
                    if (input == 0 || inputLength < 4) return SockFail(WsaEinval);
                    if (SetBlocking(s, memory.Read32(input) != 0) == SocketFailed) return SocketFailed;
                    break;
                case FdFionread:
                    if (output == 0 || outputLength < 4) return SockFail(WsaEinval);
                    memory.Write32(output, BytesAvailable(s));
                    returned = 4;
                    break;
                case SioUdpConnReset:
                case SioKeepAliveVals:
                    break;   // accepted: the host's defaults stand
                case SioGetExtensionFunctionPointer:
                {
                    var error = ExtensionFunction(input, inputLength, output, outputLength);
                    if (error != 0) return SockFail(error);
                    returned = 4;
                    break;
                }
                default:
                    return SockFail(WsaEopnotsupp);   // the rest of the ioctls: the program falls back
            }
            if (returnedPointer != 0) memory.Write32(returnedPointer, returned);
            return 0;
        }

        // --- getsockname, getpeername -----------------------------------------------------------------------

        private uint GetSockName(uint handle, uint name, uint lengthPointer, bool peer)
        {
            if (!TrySocket(handle, out var s)) return SocketFailed;
            if (name == 0 || lengthPointer == 0) return SockFail(WsaEfault);
            IPEndPoint endpoint;
            if (peer)
            {
                Settle(s);
                if (!s.Connected) return SockFail(WsaEnotconn);
                endpoint = s.Peer ?? s.Host.RemoteEndPoint as IPEndPoint;
            }
            else
            {
                endpoint = s.Host.LocalEndPoint as IPEndPoint;
                if (endpoint != null && endpoint.Port == 0) endpoint = null;   // not bound yet
            }
            if (endpoint == null) return SockFail(WsaEinval);
            var error = WriteSockaddrOut(name, lengthPointer, endpoint, true);
            return error != 0 ? SockFail(error) : 0;
        }

        // --- socket options --------------------------------------------------------------------------------

        private uint SetSockOpt(uint handle, uint level, uint name, uint value, uint length)
        {
            if (!TrySocket(handle, out var s)) return SocketFailed;
            if (value == 0 || length == 0) return SockFail(WsaEfault);
            // Options are a DWORD, or a single byte or word from a program that sized it to its own type.
            var number = length >= 4 ? memory.Read32(value) : length == 2 ? memory.Read16(value) : memory.Read8(value);
            var on = number != 0;
            var host = s.Host;
            switch (level)
            {
                case SolSocket:
                    switch (name)
                    {
                        case 0x0004: host.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, on); return 0;      // SO_REUSEADDR
                        case 0xFFFFFFFB: host.ExclusiveAddressUse = on; return 0;                                                      // SO_EXCLUSIVEADDRUSE
                        case 0x0008: host.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, on); return 0;         // SO_KEEPALIVE
                        case 0x0020: host.EnableBroadcast = on; return 0;                                                               // SO_BROADCAST
                        case 0x0010: return 0;                                                                                          // SO_DONTROUTE: nothing to do
                        case 0x0100: Advisory(() => host.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.OutOfBandInline, on)); return 0;   // SO_OOBINLINE
                        case 0x0080:                                                                                                    // SO_LINGER
                            if (length < 4) return SockFail(WsaEfault);
                            Advisory(() => host.LingerState = new LingerOption(memory.Read16(value) != 0, memory.Read16(value + 2)));
                            return 0;
                        case 0xFFFFFF7F: Advisory(() => host.LingerState = new LingerOption(false, 0)); return 0;                      // SO_DONTLINGER
                        // SO_SNDBUF, SO_RCVBUF: a size the host will not give (its own ceiling) is not the game's failure; it reads back what it set.
                        case 0x1001: Advisory(() => host.SendBufferSize = (int)Math.Min(number, 0x7FFFFFFF)); s.SendBufferSize = (int)number; return 0;
                        case 0x1002: Advisory(() => host.ReceiveBufferSize = (int)Math.Min(number, 0x7FFFFFFF)); s.ReceiveBufferSize = (int)number; return 0;
                        case 0x1005: s.SendTimeout = number; return 0;                                                                  // SO_SNDTIMEO
                        case 0x1006: s.ReceiveTimeout = number; return 0;                                                               // SO_RCVTIMEO
                        case 0x3002:   // SO_CONDITIONAL_ACCEPT
                        case 0x7010:   // SO_UPDATE_CONNECT_CONTEXT
                            return 0;
                        case 0x700B:   // SO_UPDATE_ACCEPT_CONTEXT: the option value is the listening socket
                            // The socket AcceptEx accepted takes the listening socket's properties, as the one accept returns has them.
                            if (length >= 4 && s.Connected && sockets.TryGetValue(number, out var listener) && listener.Listening)
                            {
                                s.NonBlocking = listener.NonBlocking;
                                s.ReceiveTimeout = listener.ReceiveTimeout;
                                s.SendTimeout = listener.SendTimeout;
                                s.Event = listener.Event;
                                s.EventMask = listener.EventMask;
                                if (s.EventMask != 0 && !eventSockets.Contains(s)) eventSockets.Add(s);
                            }
                            return 0;
                        default: return SockFail(WsaEnoprotoopt);
                    }
                case IpprotoTcp:
                    switch (name)
                    {
                        case 1: host.NoDelay = on; return 0;                    // TCP_NODELAY
                        case 3: return 0;                                       // TCP_KEEPALIVE: the host's timers stand
                        default: return SockFail(WsaEnoprotoopt);
                    }
                case IpprotoIp:
                    switch (name)
                    {
                        case 4: host.Ttl = (short)Math.Min(number, 255); return 0;                                                        // IP_TTL
                        case 3: Advisory(() => host.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.TypeOfService, (int)number)); return 0;   // IP_TOS
                        case 14: host.DontFragment = on; return 0;                                                                         // IP_DONTFRAGMENT
                        case 10: host.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, (int)number); return 0;   // IP_MULTICAST_TTL
                        case 11: host.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, on); return 0;               // IP_MULTICAST_LOOP
                        case 12:                                                                                                           // IP_ADD_MEMBERSHIP
                        case 13:                                                                                                           // IP_DROP_MEMBERSHIP
                        {
                            if (length < 8) return SockFail(WsaEfault);
                            var group = new IPAddress(memory.ReadBytes(value, 4));
                            var iface = new IPAddress(memory.ReadBytes(value + 4, 4));
                            host.SetSocketOption(SocketOptionLevel.IP, name == 12 ? SocketOptionName.AddMembership : SocketOptionName.DropMembership,
                                new MulticastOption(group, iface));
                            return 0;
                        }
                        default: return SockFail(WsaEnoprotoopt);
                    }
                case IpprotoIpv6:
                    switch (name)
                    {
                        case 27: host.DualMode = !on; return 0;                    // IPV6_V6ONLY
                        case 4: host.Ttl = (short)Math.Min(number, 255); return 0;   // IPV6_UNICAST_HOPS
                        default: return SockFail(WsaEnoprotoopt);
                    }
                default:
                    return SockFail(WsaEnoprotoopt);
            }
        }

        /// <summary>An option that only tunes the host: a host that refuses it must not fail a game that merely asked.</summary>
        private static void Advisory(Action apply)
        {
            try { apply(); }
            catch (SocketException) { }
            catch (NotSupportedException) { }
            catch (ArgumentException) { }
        }

        private uint GetSockOpt(uint handle, uint level, uint name, uint value, uint lengthPointer)
        {
            if (!TrySocket(handle, out var s)) return SocketFailed;
            if (value == 0 || lengthPointer == 0) return SockFail(WsaEfault);
            var room = memory.Read32(lengthPointer);
            var host = s.Host;
            uint answer;
            switch (level)
            {
                case SolSocket:
                    switch (name)
                    {
                        case 0x0004: answer = Flag(host.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress)); break;
                        case 0xFFFFFFFB: answer = host.ExclusiveAddressUse ? 1u : 0u; break;
                        case 0x0008: answer = Flag(host.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive)); break;
                        case 0x0020: answer = host.EnableBroadcast ? 1u : 0u; break;
                        case 0x0010: answer = 0; break;
                        case 0x0002: answer = s.Listening ? 1u : 0u; break;                                  // SO_ACCEPTCONN
                        case 0x0080:                                                                         // SO_LINGER
                        {
                            if (room < 4) return SockFail(WsaEfault);
                            var linger = host.LingerState;
                            memory.Write16(value, (ushort)(linger != null && linger.Enabled ? 1 : 0));
                            memory.Write16(value + 2, (ushort)(linger != null ? linger.LingerTime : 0));
                            memory.Write32(lengthPointer, 4);
                            return 0;
                        }
                        case 0x1001: answer = s.SendBufferSize != 0 ? (uint)s.SendBufferSize : (uint)host.SendBufferSize; break;
                        case 0x1002: answer = s.ReceiveBufferSize != 0 ? (uint)s.ReceiveBufferSize : (uint)host.ReceiveBufferSize; break;
                        case 0x1005: answer = s.SendTimeout; break;
                        case 0x1006: answer = s.ReceiveTimeout; break;
                        case 0x1007: answer = TakeSocketError(s); break;                                     // SO_ERROR
                        case 0x1008: answer = s.Type == SocketType.Stream ? 1u : 2u; break;                  // SO_TYPE
                        case 0x700C:                                                                         // SO_CONNECT_TIME: seconds connected, or -1
                            Settle(s);
                            answer = s.Connected ? (uint)Math.Max(0, (Milliseconds - s.ConnectedAt) / 1000) : 0xFFFFFFFF;
                            break;
                        case 0x2003:                                                                         // SO_MAX_MSG_SIZE
                            if (s.Type == SocketType.Stream) return SockFail(WsaEnoprotoopt);
                            answer = 65507;
                            break;
                        default: return SockFail(WsaEnoprotoopt);
                    }
                    break;
                case IpprotoTcp:
                    if (name != 1) return SockFail(WsaEnoprotoopt);
                    answer = host.NoDelay ? 1u : 0u;
                    break;
                case IpprotoIp:
                    if (name == 4) answer = (uint)host.Ttl;
                    else if (name == 14) answer = host.DontFragment ? 1u : 0u;
                    else return SockFail(WsaEnoprotoopt);
                    break;
                case IpprotoIpv6:
                    if (name == 27) answer = host.DualMode ? 0u : 1u;
                    else if (name == 4) answer = (uint)host.Ttl;
                    else return SockFail(WsaEnoprotoopt);
                    break;
                default:
                    return SockFail(WsaEnoprotoopt);
            }
            if (room < 4) return SockFail(WsaEfault);
            memory.Write32(value, answer);
            memory.Write32(lengthPointer, 4);
            return 0;
        }

        private static uint Flag(object value) => value is int n && n != 0 ? 1u : 0u;

        /// <summary>SO_ERROR: the failure of a connect that ended, once, then whatever the host has pending.</summary>
        private uint TakeSocketError(GuestSocket s)
        {
            Settle(s);
            if (s.ConnectError != 0)
            {
                var error = s.ConnectError;
                s.ConnectError = 0;
                return error;
            }
            try
            {
                var host = (uint)(int)s.Host.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Error);
                // SO_ERROR is read once: Windows keeps answering the refused connect's code, Linux and
                // macOS clear it, and the guest was already told.
                return host != 0 && host == s.HostErrorTaken ? 0u : host;
            }
            catch (SocketException) { return 0; }
        }
    }
}
