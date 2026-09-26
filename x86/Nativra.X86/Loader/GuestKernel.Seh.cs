using System;
using System.Collections.Generic;
using Nativra.X86.Cpu;

namespace Nativra.X86.Loader
{
    // x86 structured exception handling: RaiseException walks the frame chain
    // at FS:[0] calling each handler, RtlUnwind calls them again with the
    // unwinding flag and pops them. This is what a C++ throw (vcruntime's
    // _CxxThrowException), __try/__except in the C runtime and the /GS
    // failure path run on.
    //
    // Handlers are guest code, and a handler that catches never returns to the
    // dispatcher: __CxxFrameHandler3 unwinds, runs the catch block and jumps to
    // the continuation. So dispatch is not a nested run; it is a state machine.
    // Each handler is entered with a sentinel as its return address, and the
    // sentinel's host handler takes the next step. A dispatch whose handler
    // never came back is simply abandoned, as on Windows.
    public sealed partial class GuestKernel
    {
        private const uint ExceptionNoncontinuable = 0x1;
        private const uint ExceptionUnwinding = 0x2;
        private const uint ExceptionExitUnwind = 0x4;
        private const uint StatusUnwind = 0xC0000027;
        private const uint StatusNoncontinuableException = 0xC0000025;
        private const uint StatusInvalidDisposition = 0xC0000026;
        private const uint EndOfChain = 0xFFFFFFFF;

        private const uint RecordSize = 80;
        private const uint ContextSize = 0x2CC;
        private const uint ContextFull = 0x10007;   // CONTEXT_i386 | CONTROL | INTEGER | SEGMENTS
        private const uint FrameMagic = 0x5E4D15A7;

        // Handler-call area, below the stack pointer at the raise:
        //   +0 return address (sentinel)   +4 record  +8 frame  +12 context
        //   +16 dispatcher context ptr     +20 dispatch id      +24 magic
        //   +28 dispatcher context slot    +32 record (80)      +112 context
        private const uint AreaSize = 112 + ContextSize + 4;

        private readonly Dictionary<uint, SehDispatch> dispatches = new Dictionary<uint, SehDispatch>();
        private uint nextDispatch = 1;
        private uint sehReturn;

        private sealed class SehDispatch
        {
            public uint Id;
            public uint Top;          // the handler-call area
            public uint Record;
            public uint Context;
            public uint Frame;        // the registration whose handler is running
            public bool Unwind;
            public uint TargetFrame;
            public uint ResumeEip, ResumeEsp, ReturnValue;
            public uint Ebx, Esi, Edi, Ebp;
        }

        /// <summary>Exceptions raised in the guest, by code, in order (the probe reports them).</summary>
        public List<uint> ExceptionsRaised { get; } = new List<uint>();

        private void InstallSeh(GuestImports i)
        {
            i.Register("nativra.dll", "SehReturn", CallConv.Cdecl, 0, c => { HandlerReturned(); return 0; });
            sehReturn = i.Bind("nativra.dll", "SehReturn", -1);

            i.Register("kernel32.dll", "RaiseException", CallConv.Stdcall, 4, c =>
            {
                RaiseException(c);
                return 0;
            });
            process.HardwareException = DispatchHardware;
            HostCall unwind = c => { RtlUnwind(c); return 0; };
            i.Register("kernel32.dll", "RtlUnwind", CallConv.Stdcall, 4, unwind);
            i.Register("ntdll.dll", "RtlUnwind", CallConv.Stdcall, 4, unwind);
        }

        private uint ChainHead
        {
            get => memory.Read32(process.TebBase);
            set => memory.Write32(process.TebBase, value);
        }

        private SehDispatch NewDispatch(uint esp)
        {
            // Dispatches whose area the stack has since returned above were
            // abandoned by a handler that caught; forget them.
            var stale = new List<uint>();
            foreach (var d in dispatches.Values) if (d.Top < esp) stale.Add(d.Id);
            foreach (var id in stale) dispatches.Remove(id);

            var top = (esp - AreaSize - 64) & ~0xFu;
            var dispatch = new SehDispatch
            {
                Id = nextDispatch++,
                Top = top,
                Record = top + 32,
                Context = top + 112,
            };
            dispatches[dispatch.Id] = dispatch;
            return dispatch;
        }

        private void WriteContext(uint p, uint eip, uint esp)
        {
            var cpu = process.Cpu;
            memory.WriteBytes(p, new byte[ContextSize]);
            memory.Write32(p + 0x00, ContextFull);
            memory.Write32(p + 0x8C, 0x2B);   // gs
            memory.Write32(p + 0x90, 0x53);   // fs
            memory.Write32(p + 0x94, 0x2B);   // es
            memory.Write32(p + 0x98, 0x2B);   // ds
            memory.Write32(p + 0x9C, cpu.Edi);
            memory.Write32(p + 0xA0, cpu.Esi);
            memory.Write32(p + 0xA4, cpu.Ebx);
            memory.Write32(p + 0xA8, cpu.Edx);
            memory.Write32(p + 0xAC, cpu.Ecx);
            memory.Write32(p + 0xB0, cpu.Eax);
            memory.Write32(p + 0xB4, cpu.Ebp);
            memory.Write32(p + 0xB8, eip);
            memory.Write32(p + 0xBC, 0x23);   // cs
            memory.Write32(p + 0xC0, cpu.EFlags);
            memory.Write32(p + 0xC4, esp);
            memory.Write32(p + 0xC8, 0x2B);   // ss
        }

        private void RestoreContext(uint p)
        {
            var cpu = process.Cpu;
            cpu.Edi = memory.Read32(p + 0x9C);
            cpu.Esi = memory.Read32(p + 0xA0);
            cpu.Ebx = memory.Read32(p + 0xA4);
            cpu.Edx = memory.Read32(p + 0xA8);
            cpu.Ecx = memory.Read32(p + 0xAC);
            cpu.Eax = memory.Read32(p + 0xB0);
            cpu.Ebp = memory.Read32(p + 0xB4);
            cpu.Eip = memory.Read32(p + 0xB8);
            cpu.EFlags = (memory.Read32(p + 0xC0) & 0x00000FD5) | Cpu.Flag.Fixed;
            cpu.Esp = memory.Read32(p + 0xC4);
        }

        private void RaiseException(GuestCall c)
        {
            uint code = c.Arg(0), flags = c.Arg(1), count = c.Arg(2), args = c.Arg(3);
            ExceptionsRaised.Add(code);
            var d = NewDispatch(c.ArgBase - 4);

            memory.WriteBytes(d.Record, new byte[RecordSize]);
            memory.Write32(d.Record + 0, code);
            memory.Write32(d.Record + 4, flags & ExceptionNoncontinuable);
            memory.Write32(d.Record + 12, c.ReturnAddress);
            if (count > 15) count = 15;
            if (args == 0) count = 0;
            memory.Write32(d.Record + 16, count);
            for (uint n = 0; n < count; n++) memory.Write32(d.Record + 20 + n * 4, memory.Read32(args + n * 4));

            // Continuing resumes as if RaiseException had returned.
            WriteContext(d.Context, c.ReturnAddress, c.ArgBase + 16);
            EnterHandler(d, ChainHead);
        }

        /// <summary>
        /// A processor exception at <see cref="GuestException.Eip"/>: the
        /// same dispatch as RaiseException, with the context of the faulting
        /// instruction (continuing re-executes it, as on Windows). False when
        /// no frame is there to take it, so the run stops with the fault.
        /// </summary>
        private bool DispatchHardware(GuestException fault)
        {
            var cpu = process.Cpu;
            if (IsEnd(ChainHead)) return false;
            ExceptionsRaised.Add(fault.Code);
            var d = NewDispatch(cpu.Esp);

            memory.WriteBytes(d.Record, new byte[RecordSize]);
            memory.Write32(d.Record + 0, fault.Code);
            memory.Write32(d.Record + 12, fault.Eip);
            var info = fault.Information ?? new uint[0];
            var count = (uint)Math.Min(info.Length, 15);
            memory.Write32(d.Record + 16, count);
            for (uint n = 0; n < count; n++) memory.Write32(d.Record + 20 + n * 4, info[n]);

            WriteContext(d.Context, fault.Eip, cpu.Esp);
            try
            {
                EnterHandler(d, ChainHead);
            }
            catch (GuestRaisedException)
            {
                return false;
            }
            return true;
        }

        private void RtlUnwind(GuestCall c)
        {
            uint target = c.Arg(0), targetIp = c.Arg(1), record = c.Arg(2), returnValue = c.Arg(3);
            var cpu = process.Cpu;
            var d = NewDispatch(c.ArgBase - 4);
            d.Unwind = true;
            d.TargetFrame = target;
            d.ResumeEip = targetIp != 0 ? targetIp : c.ReturnAddress;
            d.ResumeEsp = c.ArgBase + 16;
            d.ReturnValue = returnValue;
            d.Ebx = cpu.Ebx; d.Esi = cpu.Esi; d.Edi = cpu.Edi; d.Ebp = cpu.Ebp;

            if (record == 0)
            {
                memory.WriteBytes(d.Record, new byte[RecordSize]);
                memory.Write32(d.Record + 0, StatusUnwind);
                memory.Write32(d.Record + 12, c.ReturnAddress);
            }
            else d.Record = record;
            var flags = memory.Read32(d.Record + 4) | ExceptionUnwinding;
            if (target == 0) flags |= ExceptionExitUnwind;
            memory.Write32(d.Record + 4, flags);
            WriteContext(d.Context, d.ResumeEip, d.ResumeEsp);

            var head = ChainHead;
            if (head == target || IsEnd(head)) FinishUnwind(d);
            else EnterHandler(d, head);
        }

        private static bool IsEnd(uint frame) => frame == EndOfChain || frame == 0;

        /// <summary>Calls the handler of <paramref name="frame"/>, or ends the dispatch at the chain's end.</summary>
        private void EnterHandler(SehDispatch d, uint frame)
        {
            if (IsEnd(frame))
            {
                if (d.Unwind) { FinishUnwind(d); return; }
                dispatches.Remove(d.Id);
                throw new GuestRaisedException(memory.Read32(d.Record));   // unhandled
            }

            d.Frame = frame;
            var t = d.Top;
            memory.Write32(t + 0, sehReturn);
            memory.Write32(t + 4, d.Record);
            memory.Write32(t + 8, frame);
            memory.Write32(t + 12, d.Context);
            memory.Write32(t + 16, t + 28);
            memory.Write32(t + 20, d.Id);
            memory.Write32(t + 24, FrameMagic);
            memory.Write32(t + 28, 0);

            process.Cpu.Esp = t;
            process.Cpu.Eip = memory.Read32(frame + 4);
            process.Jumped();
        }

        /// <summary>A handler returned to the sentinel: act on its disposition.</summary>
        private void HandlerReturned()
        {
            var d = FindReturning();
            if (d == null) throw new GuestRaisedException(StatusInvalidDisposition);
            var disposition = process.Cpu.Eax;

            if (d.Unwind)
            {
                // The frame is done with: take it off the chain, go to the next.
                var next = memory.Read32(d.Frame);
                ChainHead = next;
                if (next == d.TargetFrame || IsEnd(next)) FinishUnwind(d);
                else EnterHandler(d, next);
                return;
            }

            switch (disposition)
            {
                case 0: // ExceptionContinueExecution
                    dispatches.Remove(d.Id);
                    if ((memory.Read32(d.Record + 4) & ExceptionNoncontinuable) != 0)
                        throw new GuestRaisedException(StatusNoncontinuableException);
                    RestoreContext(d.Context);
                    process.Jumped();
                    return;
                case 1: // ExceptionContinueSearch
                case 2: // ExceptionNestedException
                    EnterHandler(d, memory.Read32(d.Frame));
                    return;
                default:
                    dispatches.Remove(d.Id);
                    throw new GuestRaisedException(StatusInvalidDisposition);
            }
        }

        /// <summary>
        /// The dispatch a returning handler belongs to. Handlers are cdecl, so
        /// ESP is back at the arguments; one that popped them (ret 16) is
        /// accepted too.
        /// </summary>
        private SehDispatch FindReturning()
        {
            var esp = process.Cpu.Esp;
            foreach (var top in new[] { esp - 4, esp - 20 })
            {
                if (memory.Read32(top + 24) != FrameMagic) continue;
                if (dispatches.TryGetValue(memory.Read32(top + 20), out var d) && d.Top == top) return d;
            }
            return null;
        }

        private void FinishUnwind(SehDispatch d)
        {
            dispatches.Remove(d.Id);
            if (d.TargetFrame != 0 && !IsEnd(d.TargetFrame)) ChainHead = d.TargetFrame;
            var cpu = process.Cpu;
            cpu.Ebx = d.Ebx; cpu.Esi = d.Esi; cpu.Edi = d.Edi; cpu.Ebp = d.Ebp;
            cpu.Eax = d.ReturnValue;
            cpu.Esp = d.ResumeEsp;
            cpu.Eip = d.ResumeEip;
            process.Jumped();
        }
    }
}
