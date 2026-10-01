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
        private const uint SkipPortOnSuccess = 1, SkipSetEventOnHandle = 2;

        private sealed class CompletionPort : Waitable
        {
            public readonly Queue<uint[]> Packets = new Queue<uint[]>();   // bytes, key, overlapped
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
                port.Packets.Enqueue(new[] { c.Arg(1), c.Arg(2), c.Arg(3) });
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
        /// Returns whether the handle's event should still be set.
        /// </summary>
        private bool QueueCompletion(uint handle, uint overlapped, uint bytes, uint status)
        {
            if (!portBindings.TryGetValue(handle, out var binding)) return true;
            var signal = (binding.Modes & SkipSetEventOnHandle) == 0;
            if (binding.Port == 0 || !(Object(binding.Port) is CompletionPort port)) return signal;
            if (status == 0 && (binding.Modes & SkipPortOnSuccess) != 0) return signal;
            // The low bit of hEvent asks for no packet.
            if ((memory.Read32(overlapped + 0x10) & 1) != 0) return signal;
            port.Packets.Enqueue(new[] { bytes, binding.Key, overlapped });
            return signal;
        }

        private uint DequeueCompletion(uint portHandle, uint bytesOut, uint keyOut, uint overlappedOut, uint timeout)
        {
            if (!(Object(portHandle) is CompletionPort port)) { process.LastError = ErrorInvalidHandle; return 0; }
            if (port.Packets.Count == 0)
            {
                if (process.WaitTimedOut(timeout))
                {
                    if (overlappedOut != 0) memory.Write32(overlappedOut, 0);
                    process.LastError = WaitTimeoutError;
                    return 0;
                }
                process.Block();
                return 0;
            }
            var packet = port.Packets.Dequeue();
            if (bytesOut != 0) memory.Write32(bytesOut, packet[0]);
            if (keyOut != 0) memory.Write32(keyOut, packet[1]);
            if (overlappedOut != 0) memory.Write32(overlappedOut, packet[2]);
            // A packet for a failed I/O is still dequeued, but the call reports the failure.
            if (packet[2] != 0)
            {
                var status = memory.Read32(packet[2]);
                if (status != 0)
                {
                    process.LastError = status == StatusEndOfFile ? ErrorHandleEof : status;
                    return 0;
                }
            }
            return 1;
        }

        // OVERLAPPED_ENTRY: lpCompletionKey +0, lpOverlapped +4, Internal +8, bytes +0xC.
        private uint DequeueCompletions(uint portHandle, uint entries, uint count, uint removedOut, uint timeout)
        {
            if (!(Object(portHandle) is CompletionPort port)) { process.LastError = ErrorInvalidHandle; return 0; }
            if (entries == 0 || count == 0) { process.LastError = ErrorInvalidParameter; return 0; }
            if (port.Packets.Count == 0)
            {
                if (process.WaitTimedOut(timeout))
                {
                    if (removedOut != 0) memory.Write32(removedOut, 0);
                    process.LastError = WaitTimeoutError;
                    return 0;
                }
                process.Block();
                return 0;
            }
            uint n = 0;
            while (n < count && port.Packets.Count > 0)
            {
                var packet = port.Packets.Dequeue();
                var at = entries + n * 16;
                memory.Write32(at + 0x0, packet[1]);
                memory.Write32(at + 0x4, packet[2]);
                memory.Write32(at + 0x8, packet[2] != 0 ? memory.Read32(packet[2]) : 0);
                memory.Write32(at + 0xC, packet[0]);
                n++;
            }
            if (removedOut != 0) memory.Write32(removedOut, n);
            return 1;
        }
    }
}
