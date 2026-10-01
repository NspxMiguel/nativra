using System.Collections.Generic;

namespace Nativra.X86.Loader
{
    // I/O completion ports. A port is a queue a thread waits on with
    // GetQueuedCompletionStatus; packets come from PostQueuedCompletionStatus
    // and from overlapped I/O on a handle associated with the port. All I/O
    // here completes in place, so the packet is queued as the call returns —
    // which Windows also does for an associated handle unless it was told to
    // skip the port on synchronous success.
    public sealed partial class GuestKernel
    {
        private const uint WaitTimeoutError = 258;   // WAIT_TIMEOUT as GetLastError reports it
        // FILE_SKIP_COMPLETION_PORT_ON_SUCCESS; FILE_SKIP_SET_EVENT_ON_HANDLE (2) concerns only the file object's own
        // event, which nothing here waits on, so it is stored and has no effect.
        private const uint SkipPortOnSuccess = 1;

        private sealed class CompletionPort : Waitable
        {
            // bytes, key, overlapped, status: a posted packet's "overlapped" is any value
            // the program chose, so its status is kept here, never read through it.
            public readonly Queue<uint[]> Packets = new Queue<uint[]>();
            public override bool Ready(uint thread) => Packets.Count > 0;
            public override void Consume(uint thread) { }
        }

        private sealed class PortBinding
        {
            public uint Port, Key, Modes;
        }

        private readonly Dictionary<uint, PortBinding> portBindings = new Dictionary<uint, PortBinding>();

        private void InstallCompletion(GuestImports i)
        {
            const string k = "kernel32.dll";
            i.Register(k, "CreateIoCompletionPort", CallConv.Stdcall, 4, c => CreateCompletionPort(c.Arg(0), c.Arg(1), c.Arg(2)));
            i.Register(k, "PostQueuedCompletionStatus", CallConv.Stdcall, 4, c =>
            {
                if (!(Object(c.Arg(0)) is CompletionPort port)) { process.LastError = ErrorInvalidHandle; return 0; }
                port.Packets.Enqueue(new[] { c.Arg(1), c.Arg(2), c.Arg(3), 0u });
                return 1;
            });
            i.Register(k, "GetQueuedCompletionStatus", CallConv.Stdcall, 5, c =>
                DequeueCompletion(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3), c.Arg(4)));
            i.Register(k, "GetQueuedCompletionStatusEx", CallConv.Stdcall, 6, c =>
                DequeueCompletions(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3), c.Arg(4)));
            i.Register(k, "SetFileCompletionNotificationModes", CallConv.Stdcall, 2, c =>
            {
                if (portBindings.TryGetValue(c.Arg(0), out var binding)) binding.Modes = c.Arg(1);
                else portBindings[c.Arg(0)] = new PortBinding { Modes = c.Arg(1) };
                return 1;
            });
        }

        private uint CreateCompletionPort(uint handle, uint existing, uint key)
        {
            const uint InvalidHandleValue = 0xFFFFFFFF;
            var port = existing;
            if (port == 0)
            {
                port = NewHandle();
                waitables[port] = new CompletionPort();
            }
            else if (!(Object(port) is CompletionPort))
            {
                process.LastError = ErrorInvalidParameter;
                return 0;
            }
            if (handle == InvalidHandleValue) return port;

            if (!portBindings.TryGetValue(handle, out var binding)) portBindings[handle] = binding = new PortBinding();
            binding.Port = port;
            binding.Key = key;
            return port;
        }

        /// <summary>
        /// The completion of an overlapped call on <paramref name="handle"/>:
        /// queued on its port, if it has one and was not told to skip it.
        /// <paramref name="synchronous"/> says the call finished before it
        /// returned (success, not WSA_IO_PENDING / ERROR_IO_PENDING): only such
        /// a completion is dropped by FILE_SKIP_COMPLETION_PORT_ON_SUCCESS; a
        /// call that returned pending and succeeded later is always queued.
        /// (FILE_SKIP_SET_EVENT_ON_HANDLE spares the file object's own event,
        /// which nothing here waits on; the OVERLAPPED's explicit event is
        /// always signalled, as Microsoft documents.)
        /// </summary>
        private void QueueCompletion(uint handle, uint overlapped, uint bytes, uint status, bool synchronous)
        {
            if (!portBindings.TryGetValue(handle, out var binding)) return;
            if (binding.Port == 0 || !(Object(binding.Port) is CompletionPort port)) return;
            if (synchronous && status == 0 && (binding.Modes & SkipPortOnSuccess) != 0) return;
            // The low bit of hEvent asks for no packet.
            if ((memory.Read32(overlapped + 0x10) & 1) != 0) return;
            port.Packets.Enqueue(new[] { bytes, binding.Key, overlapped, status });
        }

        private uint DequeueCompletion(uint portHandle, uint bytesOut, uint keyOut, uint overlappedOut, uint timeout)
        {
            if (!(Object(portHandle) is CompletionPort port)) { process.LastError = ErrorInvalidHandle; return 0; }
            if (NetworkBusy) PumpNetwork();   // overlapped socket calls that ended since queue their packets here
            if (port.Packets.Count == 0)
            {
                if (process.WaitTimedOut(timeout))
                {
                    if (overlappedOut != 0) memory.Write32(overlappedOut, 0);
                    process.LastError = WaitTimeoutError;
                    return 0;
                }
                if (NetworkBusy) process.BlockOnHost(); else process.Block();
                return 0;
            }
            var packet = port.Packets.Dequeue();
            if (bytesOut != 0) memory.Write32(bytesOut, packet[0]);
            if (keyOut != 0) memory.Write32(keyOut, packet[1]);
            if (overlappedOut != 0) memory.Write32(overlappedOut, packet[2]);
            // A packet for a failed I/O is still dequeued, but the call reports the failure.
            if (packet[3] != 0)
            {
                process.LastError = packet[3] == StatusEndOfFile ? ErrorHandleEof : SocketErrorOfStatus(packet[3]);
                return 0;
            }
            return 1;
        }

        // OVERLAPPED_ENTRY: lpCompletionKey +0, lpOverlapped +4, Internal +8, bytes +0xC.
        private uint DequeueCompletions(uint portHandle, uint entries, uint count, uint removedOut, uint timeout)
        {
            if (!(Object(portHandle) is CompletionPort port)) { process.LastError = ErrorInvalidHandle; return 0; }
            if (entries == 0 || count == 0) { process.LastError = ErrorInvalidParameter; return 0; }
            if (NetworkBusy) PumpNetwork();
            if (port.Packets.Count == 0)
            {
                if (process.WaitTimedOut(timeout))
                {
                    if (removedOut != 0) memory.Write32(removedOut, 0);
                    process.LastError = WaitTimeoutError;
                    return 0;
                }
                if (NetworkBusy) process.BlockOnHost(); else process.Block();
                return 0;
            }
            uint n = 0;
            while (n < count && port.Packets.Count > 0)
            {
                var packet = port.Packets.Dequeue();
                var at = entries + n * 16;
                memory.Write32(at + 0x0, packet[1]);
                memory.Write32(at + 0x4, packet[2]);
                memory.Write32(at + 0x8, packet[3]);
                memory.Write32(at + 0xC, packet[0]);
                n++;
            }
            if (removedOut != 0) memory.Write32(removedOut, n);
            return 1;
        }
    }
}
