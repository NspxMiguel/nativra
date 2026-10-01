using System;
using System.Net;
using System.Net.Sockets;
using Nativra.X86.Cpu;

namespace Nativra.X86.Loader
{
    // mswsock.dll: the Microsoft extensions to Winsock that an IOCP server is built on. AcceptEx and
    // GetAcceptExSockaddrs are mswsock exports (wsock32 forwards them, ws2_32 does not have them);
    // ConnectEx is not an export at all, and a program reaches all of them through
    // WSAIoctl(SIO_GET_EXTENSION_FUNCTION_POINTER) with the function's GUID. Both ways hand out the
    // same entry points, as the real DLL does. TransmitFile, TransmitPackets, DisconnectEx and
    // WSARecvMsg are not offered: the lookup of those fails with WSAEOPNOTSUPP, and a program that
    // imports TransmitFile finds it failing with the same error (so it can take its send path).
    //
    // The two calls are overlapped calls, and run on the machinery of GuestKernel.NetworkEvents.cs:
    // one that cannot finish when it is made stays a PendingIo, which PumpNetwork finishes when the
    // host has a connection (or the data after it, or the end of the connect) and which completes
    // through the OVERLAPPED's event and the I/O completion port of the handle it was made on (for
    // AcceptEx the listening socket). Closing either socket of an AcceptEx ends it as aborted.
    public sealed partial class GuestKernel
    {
        // The GUIDs WSAIoctl(SIO_GET_EXTENSION_FUNCTION_POINTER) names the extension functions by.
        private static readonly Guid WsaIdAcceptEx = new Guid(0xb5367df1, 0xcbac, 0x11cf, 0x95, 0xca, 0x00, 0x80, 0x5f, 0x48, 0xa1, 0x92);
        private static readonly Guid WsaIdGetAcceptExSockaddrs = new Guid(0xb5367df2, 0xcbac, 0x11cf, 0x95, 0xca, 0x00, 0x80, 0x5f, 0x48, 0xa1, 0x92);
        private static readonly Guid WsaIdTransmitFile = new Guid(0xb5367df0, 0xcbac, 0x11cf, 0x95, 0xca, 0x00, 0x80, 0x5f, 0x48, 0xa1, 0x92);
        private static readonly Guid WsaIdTransmitPackets = new Guid(0xd9689da0, 0x1f90, 0x11d3, 0x99, 0x71, 0x00, 0xc0, 0x4f, 0x68, 0xc8, 0x76);
        private static readonly Guid WsaIdConnectEx = new Guid(0x25a207b9, 0xddf3, 0x4660, 0x8e, 0xe9, 0x76, 0xe5, 0x8c, 0x74, 0x06, 0x3e);
        private static readonly Guid WsaIdDisconnectEx = new Guid(0x7fda2e11, 0x8630, 0x436f, 0xa0, 0x31, 0xf5, 0x36, 0xa6, 0xee, 0xc1, 0x57);
        private static readonly Guid WsaIdWsaRecvMsg = new Guid(0xf689d7c8, 0x6f1f, 0x436b, 0x8a, 0x53, 0xe5, 0x4f, 0xe3, 0x51, 0xc3, 0x22);
        private static readonly Guid WsaIdWsaSendMsg = new Guid(0xa441e712, 0x754f, 0x43ca, 0x84, 0xa7, 0x0d, 0xee, 0x44, 0xcf, 0x60, 0x6d);

        // What the call takes of an address area beyond the sockaddr itself (the "16 bytes more" Microsoft asks for).
        private const uint AcceptAddressSlack = 16;

        private void InstallMswsock(GuestImports i)
        {
            const string mswsock = "mswsock.dll";
            // The exports of mswsock.dll; wsock32.dll forwards the same three.
            void Export(string name, int args, HostCall body, bool startup)
            {
                var guarded = Guarded(body, NetFail.Zero, startup);
                i.Register(mswsock, name, CallConv.Stdcall, args, guarded);
                i.Register("wsock32.dll", name, CallConv.Stdcall, args, guarded);
            }
            Export("AcceptEx", 8, c => AcceptEx(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3), c.Arg(4), c.Arg(5), c.Arg(6), c.Arg(7)), true);
            Export("GetAcceptExSockaddrs", 8, c =>
            {
                GetAcceptExSockaddrs(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3), c.Arg(4), c.Arg(5), c.Arg(6), c.Arg(7));
                return 0;
            }, false);
            Export("TransmitFile", 7, c => TransmitFile(c.Arg(0)), true);

            // ConnectEx has no export; its entry point is a host function of this layer's own, found by GUID.
            i.Register("nativra.dll", "ConnectEx", CallConv.Stdcall, 7, Guarded(
                c => ConnectEx(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3), c.Arg(4), c.Arg(5), c.Arg(6)), NetFail.Zero, true));
        }

        // --- WSAIoctl(SIO_GET_EXTENSION_FUNCTION_POINTER) -------------------------------------------------------

        /// <summary>
        /// The function the GUID at <paramref name="input"/> names, as its entry point into
        /// <paramref name="output"/> (4 bytes); 0, or the Winsock error. A GUID that is known but not offered
        /// is WSAEOPNOTSUPP (the program takes its fallback), one that is nobody's is WSAEINVAL.
        /// </summary>
        private uint ExtensionFunction(uint input, uint inputLength, uint output, uint outputLength)
        {
            if (input == 0 || inputLength < 16 || output == 0 || outputLength < 4) return WsaEinval;
            var id = new Guid(memory.ReadBytes(input, 16));
            string module, name;
            if (id == WsaIdAcceptEx) { module = "mswsock.dll"; name = "AcceptEx"; }
            else if (id == WsaIdGetAcceptExSockaddrs) { module = "mswsock.dll"; name = "GetAcceptExSockaddrs"; }
            else if (id == WsaIdConnectEx) { module = "nativra.dll"; name = "ConnectEx"; }
            else if (id == WsaIdWsaSendMsg) { module = "ws2_32.dll"; name = "WSASendMsg"; }
            else if (id == WsaIdTransmitFile || id == WsaIdTransmitPackets || id == WsaIdDisconnectEx || id == WsaIdWsaRecvMsg)
                return WsaEopnotsupp;
            else return WsaEinval;
            memory.Write32(output, process.Imports.Bind(module, name, -1));
            return 0;
        }

        // --- AcceptEx -------------------------------------------------------------------------------------------

        // The output buffer is, in order: the first receive data (ReceiveLength bytes), the local address area
        // (LocalLength bytes) and the remote address area (RemoteLength bytes). Microsoft documents the areas
        // only as "an internal format", which GetAcceptExSockaddrs alone reads, and asks for 16 bytes more than
        // the sockaddr each. As the format Windows' own socket driver writes (it is how the ReactOS driver, built
        // against Windows' behaviour, lays it out), an area holds the sockaddr from its first byte, and the
        // sockaddr's length as 16 bits in its last two bytes. An area of length 0 is not filled.

        private uint AcceptEx(uint listenHandle, uint acceptHandle, uint buffer, uint receiveLength, uint localLength,
            uint remoteLength, uint bytesOut, uint overlapped)
        {
            // BeginOverlapped answers 0 for a call that finished and SOCKET_ERROR for a failure or WSA_IO_PENDING; AcceptEx is a BOOL.
            return AcceptExCall(listenHandle, acceptHandle, buffer, receiveLength, localLength, remoteLength, bytesOut, overlapped) == 0 ? 1u : 0u;
        }

        private uint AcceptExCall(uint listenHandle, uint acceptHandle, uint buffer, uint receiveLength, uint localLength,
            uint remoteLength, uint bytesOut, uint overlapped)
        {
            if (overlapped == 0) return SockFail(ErrorInvalidParameter);
            memory.Write32(overlapped, StatusPending);   // Windows sets this before it looks at anything else
            if (!TrySocket(listenHandle, out var listener) || !TrySocket(acceptHandle, out var acceptor)) return SocketFailed;
            if (!listener.Listening) return SockFail(WsaEinval);
            if (buffer == 0 || remoteLength == 0) return SockFail(WsaEfault);
            // The socket to accept into is a new, stream socket of the listener's family, neither bound nor connected.
            if (acceptor == listener || acceptor.Type != SocketType.Stream || acceptor.Family != listener.Family ||
                acceptor.Bound || acceptor.Connected || acceptor.Connecting || acceptor.Listening || acceptor.AcceptPending)
                return SockFail(WsaEinval);
            var total = (long)receiveLength + localLength + remoteLength;
            if (total > MaxTransfer) return SockFail(WsaEinval);
            if (!RangeMapped(buffer, total)) return SockFail(WsaEfault);   // the output buffer is locked by the call itself

            var op = new PendingIo
            {
                Socket = listener, Acceptor = acceptor, Kind = IoKind.Accept, Overlapped = overlapped, Buffer = buffer,
                ReceiveLength = receiveLength, LocalLength = localLength, RemoteLength = remoteLength,
            };
            acceptor.AcceptPending = true;
            return BeginOverlapped(op, bytesOut, 0);
        }

        private bool RangeMapped(uint address, long length)
        {
            if (length <= 0) return true;
            var last = (ulong)address + (ulong)length - 1;
            if (last > 0xFFFFFFFFUL) return false;
            for (var page = (ulong)address & ~(ulong)(GuestMemory.PageSize - 1); page <= last; page += GuestMemory.PageSize)
                if (!memory.IsMapped((uint)page)) return false;
            return true;
        }

        /// <summary>
        /// Whether the address areas can take an address of the listener's family. Windows checks at the time
        /// a connection arrives, not when the call is made, and leaves the connection waiting when it fails.
        /// An area of length 0 (a local address nobody asks for) is allowed.
        /// </summary>
        private static bool AcceptAreasFit(PendingIo op, AddressFamily family)
        {
            var needed = SockaddrSize(family) + AcceptAddressSlack;
            return (op.LocalLength == 0 || op.LocalLength >= needed) && op.RemoteLength >= needed;
        }

        /// <summary>
        /// A pending AcceptEx on the listener takes the next connection before accept, select or an event can
        /// see it (a connection a call is waiting for goes to that call, as it does in Windows' own driver).
        /// </summary>
        private void ServiceAccepts(GuestSocket listener)
        {
            if (pendingIo.Count == 0) return;
            var waiting = false;
            foreach (var op in pendingIo)
                if (op.Kind == IoKind.Accept && !op.Accepted && op.Socket == listener) { waiting = true; break; }
            if (waiting) PumpPendingIo();
        }

        /// <summary>
        /// Does what the host allows of a pending AcceptEx: takes a connection, makes it the guest's accept
        /// socket and fills the output buffer's addresses; then, with a receive length, waits for the first
        /// data (or the end of the stream, which completes it with 0 bytes). True when the call has ended.
        /// </summary>
        private bool AdvanceAccept(PendingIo op)
        {
            var listener = op.Socket;
            var acceptor = op.Acceptor;
            try
            {
                if (!op.Accepted)
                {
                    if (!listener.Host.Poll(0, SelectMode.SelectRead)) return false;
                    // Too small an area ends the call, and leaves the connection in the queue.
                    if (!AcceptAreasFit(op, listener.Family)) { op.Error = ErrorInsufficientBuffer; return true; }
                    Socket host;
                    try { host = listener.Host.Accept(); }
                    catch (SocketException e) when (e.SocketErrorCode == SocketError.WouldBlock) { return false; }
                    catch (SocketException e)
                    {
                        // A connection the peer reset before it was taken is WSAECONNRESET for the call.
                        op.Error = e.SocketErrorCode == SocketError.ConnectionAborted ? WsaEconnreset : CodeOf(e.SocketErrorCode);
                        return true;
                    }
                    IPEndPoint local, peer;
                    try
                    {
                        local = host.LocalEndPoint as IPEndPoint;
                        peer = host.RemoteEndPoint as IPEndPoint;
                    }
                    catch (SocketException) { CloseHost(host); op.Error = WsaEconnreset; return true; }
                    catch (ObjectDisposedException) { CloseHost(host); op.Error = WsaEconnreset; return true; }
                    AttachAccepted(acceptor, host, peer);
                    op.Accepted = true;
                    WriteAcceptArea(op.Buffer + op.ReceiveLength, op.LocalLength, local);
                    WriteAcceptArea(op.Buffer + op.ReceiveLength + op.LocalLength, op.RemoteLength, peer);
                }
                if (op.ReceiveLength == 0) return true;

                var error = TryReceive(acceptor, 0, op.ReceiveLength, out var n, out _);
                if (error == WsaEwouldblock) return false;
                if (error != 0) { op.Error = error; return true; }
                op.Bytes = Scatter(new[] { new Piece(op.Buffer, op.ReceiveLength) }, receiveBuffer, n);
                return true;
            }
            catch (SocketException e) { op.Error = CodeOf(e.SocketErrorCode); return true; }
            catch (ObjectDisposedException) { op.Error = WsaEnotsock; return true; }
            catch (GuestFaultException) { op.Error = WsaEfault; return true; }
        }

        /// <summary>
        /// The connection taken from the listener becomes the guest's accept socket: the same handle, now
        /// connected. It keeps the tuning the program gave it (options set on the host socket it had are
        /// carried over); it does not take the listener's properties, as in Windows, until the program
        /// sets SO_UPDATE_ACCEPT_CONTEXT.
        /// </summary>
        private void AttachAccepted(GuestSocket acceptor, Socket host, IPEndPoint peer)
        {
            var old = acceptor.Host;
            host.Blocking = false;
            Advisory(() => host.NoDelay = old.NoDelay);
            Advisory(() => host.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive,
                (int)old.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive)));
            Advisory(() =>
            {
                var linger = old.LingerState;
                if (linger != null && linger.Enabled) host.LingerState = linger;
            });
            if (acceptor.SendBufferSize != 0) Advisory(() => host.SendBufferSize = acceptor.SendBufferSize);
            if (acceptor.ReceiveBufferSize != 0) Advisory(() => host.ReceiveBufferSize = acceptor.ReceiveBufferSize);

            acceptor.Host = host;
            acceptor.Bound = true;
            acceptor.Connected = true;
            acceptor.ConnectedAt = Milliseconds;
            acceptor.Peer = peer;
            acceptor.ReadArmed = true;
            acceptor.WriteArmed = true;
            CloseHost(old);
        }

        private void WriteAcceptArea(uint area, uint length, IPEndPoint endpoint)
        {
            if (length == 0 || endpoint == null) return;
            var bytes = SockaddrBytes(endpoint);
            memory.WriteBytes(area, bytes);
            memory.Write16(area + length - 2, (ushort)bytes.Length);
        }

        /// <summary>
        /// Splits what AcceptEx wrote: the local and the remote sockaddr (pointers into the buffer) and their
        /// lengths. An area the caller gave no room (length 0) was never filled: its outputs are left alone.
        /// </summary>
        private void GetAcceptExSockaddrs(uint buffer, uint receiveLength, uint localLength, uint remoteLength,
            uint localOut, uint localLengthOut, uint remoteOut, uint remoteLengthOut)
        {
            var local = buffer + receiveLength;
            if (localLength >= 2) ReadAcceptArea(local, localLength, localOut, localLengthOut);
            if (remoteLength >= 2) ReadAcceptArea(local + localLength, remoteLength, remoteOut, remoteLengthOut);
        }

        private void ReadAcceptArea(uint area, uint length, uint addressOut, uint lengthOut)
        {
            var size = memory.Read16(area + length - 2);
            if (addressOut != 0) memory.Write32(addressOut, area);
            if (lengthOut != 0) memory.Write32(lengthOut, size);
        }

        // --- ConnectEx ------------------------------------------------------------------------------------------

        // ConnectEx(s, name, namelen, sendBuffer, sendLength, bytesSent, overlapped): an overlapped connect of a
        // bound socket, which then sends the optional first data. The call finishes when the connect has ended
        // and the data has gone; its byte count is the data sent, and a refused or unreachable connect ends it
        // with that error.

        private uint ConnectEx(uint handle, uint name, uint nameLength, uint sendBuffer, uint sendLength, uint bytesOut, uint overlapped) =>
            ConnectExCall(handle, name, nameLength, sendBuffer, sendLength, bytesOut, overlapped) == 0 ? 1u : 0u;

        private uint ConnectExCall(uint handle, uint name, uint nameLength, uint sendBuffer, uint sendLength, uint bytesOut, uint overlapped)
        {
            if (overlapped == 0) return SockFail(ErrorInvalidParameter);
            memory.Write32(overlapped, StatusPending);
            if (!TrySocket(handle, out var s)) return SocketFailed;
            Settle(s);
            if (s.Connected && s.Type == SocketType.Stream) return SockFail(WsaEisconn);
            // Neither a stream socket that is not bound (ConnectEx needs a bound one) nor one that is already doing something else.
            if (s.Type != SocketType.Stream || !s.Bound || s.Listening || s.Connecting || s.AcceptPending) return SockFail(WsaEinval);

            var error = ReadSockaddr(name, nameLength, out var endpoint);
            if (error != 0) return SockFail(error);
            if (endpoint.AddressFamily != s.Family) return SockFail(WsaEafnosupport);
            if (IsAny(endpoint.Address) || endpoint.Port == 0) return SockFail(WsaEaddrnotavail);
            byte[] data = null;
            if (sendBuffer != 0 && sendLength != 0)
            {
                data = Gather(new[] { new Piece(sendBuffer, sendLength) });
                if (data == null) return SockFail(WsaEnobufs);
            }

            try
            {
                s.Host.Connect(endpoint);
                s.Connected = true;
                s.ConnectedAt = Milliseconds;
                s.WriteArmed = true;
                Post(s, NetFdConnect, 0);
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.WouldBlock || e.SocketErrorCode == SocketError.InProgress)
            {
                s.Connecting = true;   // the connect is being made: Settle notices when it ends, and PumpNetwork finishes the call
            }
            s.Peer = endpoint;

            var op = new PendingIo { Socket = s, Kind = IoKind.Connect, Overlapped = overlapped, Data = data };
            return BeginOverlapped(op, bytesOut, 0);
        }

        /// <summary>A pending ConnectEx: waits for the connect to end, then sends its data; true when the call has ended.</summary>
        private bool AdvanceConnect(PendingIo op)
        {
            var s = op.Socket;
            Settle(s);
            if (s.Connecting) return false;
            if (s.ConnectError != 0)
            {
                op.Error = s.ConnectError;   // the call reports it: nobody is left to ask SO_ERROR
                s.ConnectError = 0;
                return true;
            }
            if (!s.Connected) { op.Error = WsaEnotconn; return true; }
            while (op.Data != null && op.Sent < op.Data.Length)
            {
                var error = TrySend(s, op.Data, op.Sent, op.Data.Length - op.Sent, 0, null, out var sent);
                if (error == WsaEwouldblock) return false;
                if (error != 0) { op.Error = error; return true; }
                op.Sent += sent;
            }
            op.Bytes = (uint)op.Sent;
            return true;
        }

        // --- TransmitFile ---------------------------------------------------------------------------------------

        /// <summary>TransmitFile fails with WSAEOPNOTSUPP: sending a file's bytes from the host is not offered.</summary>
        private uint TransmitFile(uint handle)
        {
            if (!TrySocket(handle, out _)) return 0;
            SetSocketError(WsaEopnotsupp);
            return 0;
        }
    }
}
