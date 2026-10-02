using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace Nativra.X86.Loader
{
    // Readiness: select, WSAPoll, WSAEventSelect with WSAEnumNetworkEvents, and
    // overlapped WSASend/WSARecv. All of it rests on one check of what the
    // host socket can do right now (Check). Nothing waits on the host: a call
    // that has to wait blocks the guest thread and is asked again, and the
    // state the host network changes on its own (an event a socket is bound
    // to, an overlapped call still pending) is brought up to date by
    // PumpNetwork whenever the guest waits, sleeps or asks a completion port.
    public sealed partial class GuestKernel
    {
        // WSAEventSelect events.
        private const uint NetFdRead = 0x01, NetFdWrite = 0x02, NetFdAccept = 0x08, NetFdConnect = 0x10, NetFdClose = 0x20;

        private const uint StatusPending = 0x103, StatusCancelled = 0xC0000120, StatusBufferOverflow = 0x80000005;
        private const uint StatusBufferTooSmall = 0xC0000023;   // with ErrorInsufficientBuffer (122), AcceptEx's address areas that are too small
        private const uint StatusCustomSocket = 0xE0010000;   // 0xE001xxxx: a Winsock error with no NTSTATUS of its own

        [Flags]
        private enum Ready { None = 0, Read = 1, Write = 2, Error = 4, Hangup = 8 }

        private readonly List<GuestSocket> eventSockets = new List<GuestSocket>();

        private enum IoKind { Receive, Send, Accept, Connect, Disconnect }

        /// <summary>An overlapped WSARecv, WSASend, AcceptEx or ConnectEx that could not finish at once.</summary>
        private sealed class PendingIo
        {
            public GuestSocket Socket;     // the handle the call was made on (AcceptEx: the listening socket)
            public IoKind Kind;
            public uint Overlapped;
            public Piece[] Pieces;         // receive: where the data goes
            public byte[] Data;            // send (and ConnectEx's data): what goes, and how much has
            public int Sent;
            public uint Flags;
            public uint From, FromLength;  // recvfrom's address out
            public IPEndPoint Target;      // sendto's address
            public bool Immediate;         // still inside the call that started it
            public bool Finished;
            public uint Error, Bytes;

            // AcceptEx: the guest socket that becomes the connection, the one output buffer (the first data, then the
            // local and the remote address area), and whether the connection has been taken already; with a receive
            // length the call then waits for the first data on the accepted socket.
            public GuestSocket Acceptor;
            public uint Buffer, ReceiveLength, LocalLength, RemoteLength;
            public bool Accepted;
        }

        private readonly List<PendingIo> pendingIo = new List<PendingIo>();

        private bool NetworkBusy => pendingIo.Count > 0 || eventSockets.Count > 0;

        /// <summary>Applies what the host network did meanwhile: overlapped calls finish, bound events are signalled.</summary>
        private void PumpNetwork()
        {
            if (pendingIo.Count > 0) PumpPendingIo();
            for (var n = 0; n < eventSockets.Count; n++) UpdateEvents(eventSockets[n]);
        }

        // --- readiness -------------------------------------------------------------------------------------

        /// <summary>
        /// What a socket could do right now. A refused connect shows as an
        /// error only (select's except set), as on Windows; a stream that is
        /// readable with nothing to read has been closed by its peer.
        /// </summary>
        private Ready Check(GuestSocket s)
        {
            Settle(s);
            if (s.ConnectError != 0) return Ready.Error | Ready.Hangup;
            if (s.Connecting) return Ready.None;
            var ready = Ready.None;
            try
            {
                if (s.Listening)
                {
                    ServiceAccepts(s);
                    return s.Host.Poll(0, SelectMode.SelectRead) ? Ready.Read : Ready.None;
                }
                if (s.Type == SocketType.Stream && !s.Connected) return Ready.None;
                if (s.Peeked != null || s.Host.Poll(0, SelectMode.SelectRead))   // a peeked datagram is still to be read
                {
                    ready |= Ready.Read;
                    if (s.Type == SocketType.Stream && s.Host.Available == 0) ready |= Ready.Hangup;
                }
                if (s.Host.Poll(0, SelectMode.SelectWrite)) ready |= Ready.Write;
            }
            catch (SocketException) { ready |= Ready.Error; }
            catch (ObjectDisposedException) { ready |= Ready.Error; }
            return ready;
        }

        // --- select ----------------------------------------------------------------------------------------

        // fd_set: u_int fd_count, then SOCKET fd_array[FD_SETSIZE] (64 unless the program redefined it).
        private List<uint> ReadFdSet(uint address)
        {
            var handles = new List<uint>();
            if (address == 0) return handles;
            var count = Math.Min(memory.Read32(address), 1024u);
            for (uint n = 0; n < count; n++) handles.Add(memory.Read32(address + 4 + n * 4));
            return handles;
        }

        private void WriteFdSet(uint address, List<uint> handles)
        {
            if (address == 0) return;
            memory.Write32(address, (uint)handles.Count);
            for (var n = 0; n < handles.Count; n++) memory.Write32(address + 4 + (uint)n * 4, handles[n]);
        }

        private uint FdIsSet(uint handle, uint set)
        {
            if (set == 0) return 0;
            var count = Math.Min(memory.Read32(set), 1024u);
            for (uint n = 0; n < count; n++)
                if (memory.Read32(set + 4 + n * 4) == handle) return 1;
            return 0;
        }

        private uint Select(uint readSet, uint writeSet, uint exceptSet, uint timeout)
        {
            var reads = ReadFdSet(readSet);
            var writes = ReadFdSet(writeSet);
            var excepts = ReadFdSet(exceptSet);
            if (reads.Count + writes.Count + excepts.Count == 0) return SockFail(WsaEinval);   // nothing to wait on

            // timeval { long tv_sec; long tv_usec; }: NULL waits for ever, {0,0} only looks.
            var wait = Infinite;
            if (timeout != 0)
            {
                var seconds = (int)memory.Read32(timeout);
                var micro = (int)memory.Read32(timeout + 4);
                if (seconds < 0 || micro < 0) return SockFail(WsaEinval);
                wait = (uint)Math.Min((long)seconds * 1000 + (micro + 999L) / 1000, 0xFFFFFFFEL);
            }

            var state = new Dictionary<uint, Ready>();
            Ready Of(uint handle, out bool known)
            {
                known = true;
                if (state.TryGetValue(handle, out var cached)) return cached;
                if (!sockets.TryGetValue(handle, out var s)) { known = false; return Ready.None; }
                return state[handle] = Check(s);
            }

            var readable = new List<uint>();
            var writable = new List<uint>();
            var failed = new List<uint>();
            foreach (var h in reads)
            {
                var r = Of(h, out var known);
                if (!known) return Unknown();
                if ((r & Ready.Read) != 0) readable.Add(h);
            }
            foreach (var h in writes)
            {
                var r = Of(h, out var known);
                if (!known) return Unknown();
                if ((r & Ready.Write) != 0) writable.Add(h);
            }
            foreach (var h in excepts)
            {
                var r = Of(h, out var known);
                if (!known) return Unknown();
                if ((r & Ready.Error) != 0) failed.Add(h);
            }

            var total = readable.Count + writable.Count + failed.Count;
            if (total > 0)
            {
                // The sets are rewritten to the sockets that are ready.
                WriteFdSet(readSet, readable);
                WriteFdSet(writeSet, writable);
                WriteFdSet(exceptSet, failed);
                return (uint)total;
            }
            if (process.WaitTimedOut(wait))
            {
                var none = new List<uint>();
                WriteFdSet(readSet, none);
                WriteFdSet(writeSet, none);
                WriteFdSet(exceptSet, none);
                return 0;
            }
            process.BlockOnHost();
            return 0;
        }

        private uint Unknown() => SockFail(process.CurrentThread.Blocked ? WsaEintr : WsaEnotsock);

        // --- WSAPoll ---------------------------------------------------------------------------------------

        private const ushort PollRdNorm = 0x0100, PollWrNorm = 0x0010, PollErr = 0x0001, PollHup = 0x0002, PollNval = 0x0004;

        // WSAPOLLFD: SOCKET fd, SHORT events, SHORT revents.
        private uint WsaPoll(uint array, uint count, uint timeout)
        {
            if (array == 0) return SockFail(WsaEfault);
            if (count == 0 || count > 4096) return SockFail(WsaEinval);

            var revents = new ushort[count];
            var any = 0;
            for (uint n = 0; n < count; n++)
            {
                var at = array + n * 8;
                var events = memory.Read16(at + 4);
                ushort r = 0;
                if (!sockets.TryGetValue(memory.Read32(at), out var s)) r = PollNval;
                else
                {
                    var ready = Check(s);
                    if ((events & PollRdNorm) != 0 && (ready & Ready.Read) != 0) r |= PollRdNorm;
                    if ((events & PollWrNorm) != 0 && (ready & Ready.Write) != 0) r |= PollWrNorm;
                    if ((ready & Ready.Error) != 0) r |= PollErr;
                    if ((ready & Ready.Hangup) != 0) r |= PollHup;
                }
                revents[n] = r;
                if (r != 0) any++;
            }

            var millis = (int)timeout;
            if (any == 0 && !process.WaitTimedOut(millis < 0 ? Infinite : (uint)millis))
            {
                process.BlockOnHost();
                return 0;
            }
            for (uint n = 0; n < count; n++) memory.Write16(array + n * 8 + 6, revents[n]);
            return (uint)any;
        }

        // --- WSAEventSelect --------------------------------------------------------------------------------

        private static int EventIndex(uint fd)
        {
            var index = 0;
            while ((fd >> index) > 1) index++;
            return index;
        }

        /// <summary>Records an event for the guest and signals the event object the socket is bound to.</summary>
        private void Post(GuestSocket s, uint fd, uint error)
        {
            if ((s.EventMask & fd) == 0) return;
            s.EventsPosted |= fd;
            s.EventErrors[EventIndex(fd)] = error;
            if (waitables.TryGetValue(s.Event, out var w) && w is GuestEvent e) e.Signaled = true;
        }

        private uint EventSelect(uint handle, uint eventHandle, uint mask)
        {
            if (!TrySocket(handle, out var s)) return SocketFailed;
            if (mask == 0)
            {
                s.EventMask = 0;
                s.Event = 0;
                s.EventsPosted = 0;
                eventSockets.Remove(s);
                return 0;
            }
            if (!(Object(eventHandle) is GuestEvent)) return SockFail(ErrorInvalidHandle);
            s.Event = eventHandle;
            s.EventMask = mask;
            s.NonBlocking = true;   // WSAEventSelect puts the socket in non-blocking mode
            s.EventsPosted = 0;
            Array.Clear(s.EventErrors, 0, s.EventErrors.Length);
            s.ReadArmed = true;
            s.AcceptArmed = true;
            s.ClosePosted = false;
            s.WriteArmed = s.Connected || s.Type == SocketType.Dgram;
            if (!eventSockets.Contains(s)) eventSockets.Add(s);
            UpdateEvents(s);   // whatever is already true is reported at once
            return 0;
        }

        /// <summary>
        /// What the socket's events say now. FD_READ, FD_WRITE and FD_ACCEPT
        /// are edges: reported once, and again after the call that would act on
        /// them (recv, a send that was refused, accept) has been made.
        /// </summary>
        private void UpdateEvents(GuestSocket s)
        {
            if (s.EventMask == 0) return;
            Settle(s);
            try
            {
                if (s.Listening)
                {
                    ServiceAccepts(s);
                    if (s.AcceptArmed && (s.EventMask & NetFdAccept) != 0 && s.Host.Poll(0, SelectMode.SelectRead))
                    {
                        s.AcceptArmed = false;
                        Post(s, NetFdAccept, 0);
                    }
                    return;
                }
                if (s.Type == SocketType.Stream && !s.Connected) return;

                var readable = s.Peeked != null || s.Host.Poll(0, SelectMode.SelectRead);
                if (s.Type == SocketType.Stream)
                {
                    var data = readable && s.Host.Available > 0;
                    if (data && s.ReadArmed && (s.EventMask & NetFdRead) != 0)
                    {
                        s.ReadArmed = false;
                        Post(s, NetFdRead, 0);
                    }
                    if (readable && !data && !s.ClosePosted && (s.EventMask & NetFdClose) != 0)
                    {
                        // Readable with nothing to read: closed by the peer, or the connection failed.
                        var peeked = s.Host.Receive(receiveBuffer, 0, 1, SocketFlags.Peek, out var err);
                        if (err == SocketError.Success && peeked == 0) { s.ClosePosted = true; Post(s, NetFdClose, 0); }
                        else if (err != SocketError.Success && err != SocketError.WouldBlock) { s.ClosePosted = true; Post(s, NetFdClose, CodeOf(err)); }
                    }
                }
                else if (readable && s.ReadArmed && (s.EventMask & NetFdRead) != 0)
                {
                    s.ReadArmed = false;
                    Post(s, NetFdRead, 0);
                }
                if (s.WriteArmed && (s.EventMask & NetFdWrite) != 0 && s.Host.Poll(0, SelectMode.SelectWrite))
                {
                    s.WriteArmed = false;
                    Post(s, NetFdWrite, 0);
                }
            }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }

        // WSANETWORKEVENTS: long lNetworkEvents, int iErrorCode[FD_MAX_EVENTS = 10].
        private uint EnumNetworkEvents(uint handle, uint eventHandle, uint output)
        {
            if (!TrySocket(handle, out var s)) return SocketFailed;
            if (output == 0) return SockFail(WsaEfault);
            UpdateEvents(s);
            memory.Write32(output, s.EventsPosted);
            for (var n = 0; n < s.EventErrors.Length; n++) memory.Write32(output + 4 + (uint)n * 4, s.EventErrors[n]);
            s.EventsPosted = 0;
            Array.Clear(s.EventErrors, 0, s.EventErrors.Length);
            if (eventHandle != 0) SignalEvent(eventHandle, false);   // the event is reset with the record
            return 0;
        }

        // --- overlapped WSASend / WSARecv ------------------------------------------------------------------

        /// <summary>The WSABUF array: { u_long len; char *buf; } each.</summary>
        private Piece[] ReadBuffers(uint array, uint count)
        {
            if (array == 0 || count == 0 || count > 1024) return null;
            var pieces = new Piece[count];
            for (uint n = 0; n < count; n++) pieces[n] = new Piece(memory.Read32(array + n * 8 + 4), memory.Read32(array + n * 8));
            return pieces;
        }

        /// <summary>The Win32 side of a Winsock failure, as the NTSTATUS an OVERLAPPED's Internal field holds.</summary>
        private static uint NtStatusOf(uint error)
        {
            switch (error)
            {
                case 0: return 0;
                case WsaOperationAborted: return StatusCancelled;
                case WsaEconnreset: return 0xC000020D;    // STATUS_CONNECTION_RESET
                case 10053: return 0xC0000241;            // WSAECONNABORTED: STATUS_CONNECTION_ABORTED
                case 10061: return 0xC0000236;            // WSAECONNREFUSED: STATUS_CONNECTION_REFUSED
                case WsaEtimedout: return 0xC00000B5;     // STATUS_IO_TIMEOUT
                case WsaEmsgsize: return StatusBufferOverflow;
                case ErrorInsufficientBuffer: return StatusBufferTooSmall;   // AcceptEx with address areas too small
                default: return StatusCustomSocket | (error & 0xFFFF);
            }
        }

        /// <summary>The Winsock error behind an OVERLAPPED status written by <see cref="NtStatusOf"/>; any other status is itself.</summary>
        private static uint SocketErrorOfStatus(uint status)
        {
            switch (status)
            {
                case StatusCancelled: return WsaOperationAborted;
                case 0xC000020D: return WsaEconnreset;
                case 0xC0000241: return 10053;
                case 0xC0000236: return 10061;
                case 0xC00000B5: return WsaEtimedout;
                case StatusBufferOverflow: return WsaEmsgsize;
                case StatusBufferTooSmall: return ErrorInsufficientBuffer;
                default: return (status & 0xFFFF0000) == StatusCustomSocket ? status & 0xFFFF : status;
            }
        }

        private uint WsaReceive(uint handle, uint buffers, uint count, uint bytesOut, uint flagsPointer, uint from, uint fromLength, uint overlapped, uint completion)
        {
            if (!TrySocket(handle, out var s)) return SocketFailed;
            if (completion != 0) return SockFail(WsaEopnotsupp);   // completion routines run as APCs: no alertable wait here
            var pieces = ReadBuffers(buffers, count);
            if (pieces == null) return SockFail(WsaEinval);
            var flags = flagsPointer != 0 ? memory.Read32(flagsPointer) : 0;

            if (overlapped == 0)
            {
                // Without an OVERLAPPED it is recv, with the socket's own blocking mode.
                var error = ReceiveCall(s, pieces, flags, from, fromLength, out var received);
                if (error == Waiting) return 0;
                if (bytesOut != 0 && (error == 0 || error == WsaEmsgsize)) memory.Write32(bytesOut, received);
                if (flagsPointer != 0) memory.Write32(flagsPointer, 0);
                return error != 0 ? SockFail(error) : 0;
            }

            var op = new PendingIo
            {
                Socket = s, Kind = IoKind.Receive, Overlapped = overlapped, Pieces = pieces, Flags = flags,
                From = from, FromLength = fromLength,
            };
            return BeginOverlapped(op, bytesOut, flagsPointer);
        }

        private uint WsaTransmit(uint handle, uint buffers, uint count, uint bytesOut, uint flags, uint to, uint toLength, uint overlapped, uint completion)
        {
            if (!TrySocket(handle, out var s)) return SocketFailed;
            if (completion != 0) return SockFail(WsaEopnotsupp);
            var pieces = ReadBuffers(buffers, count);
            if (pieces == null) return SockFail(WsaEinval);
            var error = DestinationOf(s, to, toLength, out var target);
            if (error != 0) return SockFail(error);

            if (overlapped == 0)
            {
                error = TransmitCall(s, pieces, flags, target, out var sent);
                if (error == Waiting) return 0;
                if (error != 0) return SockFail(error);
                if (bytesOut != 0) memory.Write32(bytesOut, sent);
                return 0;
            }

            var data = Gather(pieces);
            if (data == null) return SockFail(WsaEnobufs);
            var op = new PendingIo
            {
                Socket = s, Kind = IoKind.Send, Overlapped = overlapped, Data = data, Flags = flags, Target = target,
            };
            return BeginOverlapped(op, bytesOut, 0);
        }

        /// <summary>
        /// Starts an overlapped call. It completes at once when it can (the
        /// call returns 0, and the completion is still queued, as on Windows);
        /// otherwise it stays pending and the call fails with WSA_IO_PENDING.
        /// A call that fails outright reports the failure and queues nothing.
        /// </summary>
        private uint BeginOverlapped(PendingIo op, uint bytesOut, uint flagsPointer)
        {
            op.Immediate = true;
            memory.Write32(op.Overlapped, StatusPending);
            memory.Write32(op.Overlapped + 4, 0);
            pendingIo.Add(op);
            PumpPendingIo();
            op.Immediate = false;
            if (!op.Finished) return SockFail(WsaIoPending);
            // A datagram too long for the buffer fills it, and fails.
            if (bytesOut != 0 && (op.Error == 0 || op.Error == WsaEmsgsize)) memory.Write32(bytesOut, op.Bytes);
            if (op.Error != 0) return SockFail(op.Error);
            if (flagsPointer != 0) memory.Write32(flagsPointer, 0);
            return 0;
        }

        /// <summary>
        /// The queue a call waits in: calls on one socket in one direction finish in the order they were
        /// made. An AcceptEx that has taken its connection is waiting for data on the accepted socket,
        /// so it no longer holds up the next AcceptEx on the listener.
        /// </summary>
        private static ulong LineOf(PendingIo op)
        {
            var socket = op.Kind == IoKind.Accept && op.Accepted ? op.Acceptor : op.Socket;
            var send = op.Kind == IoKind.Send || op.Kind == IoKind.Connect;
            return ((ulong)socket.Handle << 1) | (send ? 1UL : 0UL);
        }

        private void PumpPendingIo()
        {
            List<ulong> behind = null;
            for (var n = 0; n < pendingIo.Count; n++)
            {
                var op = pendingIo[n];
                var line = LineOf(op);
                if (behind != null && behind.Contains(line)) continue;
                if (!Advance(op))
                {
                    if (behind == null) behind = new List<ulong>();
                    behind.Add(line);
                    continue;
                }
                pendingIo.RemoveAt(n--);
                Complete(op);
            }
        }

        /// <summary>Does what the host allows of a pending call now; true when the call has ended, however it ended.</summary>
        private bool Advance(PendingIo op)
        {
            if (op.Kind == IoKind.Accept) return AdvanceAccept(op);
            if (op.Kind == IoKind.Connect) return AdvanceConnect(op);
            if (op.Kind == IoKind.Disconnect) return true;   // DisconnectEx has done its work by the time it is queued
            var s = op.Socket;
            if (op.Kind == IoKind.Receive)
            {
                var capacity = TotalLength(op.Pieces);
                var error = TryReceive(s, op.Flags, capacity, out var n, out var peer);
                if (error == WsaEwouldblock) return false;
                if (error != 0) { op.Error = error; return true; }
                op.Bytes = Scatter(op.Pieces, receiveBuffer, (int)Math.Min(n, capacity));
                WriteSockaddrOut(op.From, op.FromLength, peer, false);
                if (s.Type == SocketType.Dgram && n > capacity) op.Error = WsaEmsgsize;
                return true;
            }

            while (true)
            {
                var error = TrySend(s, op.Data, op.Sent, op.Data.Length - op.Sent, op.Flags, op.Target, out var sent);
                if (error == WsaEwouldblock) return false;
                if (error != 0) { op.Error = error; return true; }
                op.Sent += sent;
                if (op.Sent >= op.Data.Length || s.Type != SocketType.Stream)
                {
                    op.Bytes = (uint)op.Sent;
                    return true;
                }
            }
        }

        private void Complete(PendingIo op)
        {
            op.Finished = true;
            if (op.Acceptor != null) op.Acceptor.AcceptPending = false;
            if (op.Immediate && op.Error != 0)
            {
                // The call that started it reports the failure itself and nothing is queued.
                memory.Write32(op.Overlapped, NtStatusOf(op.Error));
                memory.Write32(op.Overlapped + 4, op.Bytes);
                return;
            }
            // Finished inside the call that started it: a synchronous completion, which FILE_SKIP_COMPLETION_PORT_ON_SUCCESS may drop.
            CompleteOverlapped(op.Socket.Handle, op.Overlapped, op.Bytes, NtStatusOf(op.Error), op.Immediate);
        }

        /// <summary>
        /// A socket that closes ends its pending calls as aborted; their packets are still queued. An
        /// AcceptEx ends when either of its two sockets, the listener or the one to accept into, closes.
        /// </summary>
        private void CancelPending(GuestSocket s)
        {
            for (var n = 0; n < pendingIo.Count; n++)
            {
                var op = pendingIo[n];
                if (op.Socket != s && op.Acceptor != s) continue;
                pendingIo.RemoveAt(n--);
                op.Error = WsaOperationAborted;
                op.Bytes = 0;
                op.Immediate = false;
                Complete(op);
            }
        }

        private uint WsaOverlappedResult(uint handle, uint overlapped, uint bytesOut, bool wait, uint flagsOut)
        {
            if (overlapped == 0) return SockFail(ErrorInvalidParameter);
            if (!TrySocket(handle, out _)) return 0;
            if (NetworkBusy) PumpNetwork();
            var status = memory.Read32(overlapped);
            if (status == StatusPending)
            {
                if (!wait)
                {
                    SetSocketError(WsaIoIncomplete);
                    return 0;
                }
                process.BlockOnHost();
                return 0;
            }
            if (bytesOut != 0) memory.Write32(bytesOut, memory.Read32(overlapped + 4));
            if (flagsOut != 0) memory.Write32(flagsOut, 0);
            if (status == 0) return 1;
            SetSocketError(SocketErrorOfStatus(status));
            return 0;
        }
    }
}
