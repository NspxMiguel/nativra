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
        private uint filterReturn;
        // The one filter call in flight, if any: UnhandledExceptionFilter is
        // WINAPI (stdcall, one argument, the callee pops it), a different
        // stack shape from the SEH handlers FindReturning's two offsets are
        // built for, so this is tracked directly instead of guessed at.
        private SehDispatch pendingFilter;

        // Vectored handlers (AddVectoredExceptionHandler) run before the frame
        // chain, in list order; each registration's cookie is what Remove takes.
        private uint vectoredReturn;
        private readonly List<KeyValuePair<uint, uint>> vectored = new List<KeyValuePair<uint, uint>>();   // cookie, handler
        private readonly Stack<SehDispatch> pendingVectored = new Stack<SehDispatch>();

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
            public uint[] Vectored;   // the handlers this dispatch still has to offer it to
            public int NextVectored;
        }

        /// <summary>Exceptions raised in the guest, by code, in order (the probe reports them).</summary>
        public List<uint> ExceptionsRaised { get; } = new List<uint>();

        private void InstallSeh(GuestImports i)
        {
            i.Register("nativra.dll", "SehReturn", CallConv.Cdecl, 0, c => { HandlerReturned(); return 0; });
            sehReturn = i.Bind("nativra.dll", "SehReturn", -1);
            i.Register("nativra.dll", "SehFilterReturn", CallConv.Cdecl, 0, c => { FilterReturned(); return 0; });
            filterReturn = i.Bind("nativra.dll", "SehFilterReturn", -1);
            i.Register("nativra.dll", "SehVectoredReturn", CallConv.Cdecl, 0, c => { VectoredReturned(); return 0; });
            vectoredReturn = i.Bind("nativra.dll", "SehVectoredReturn", -1);

            HostCall add = c => AddVectored(c.Arg(0) != 0, c.Arg(1));
            HostCall remove = c => RemoveVectored(c.Arg(0));
            i.Register("kernel32.dll", "AddVectoredExceptionHandler", CallConv.Stdcall, 2, add);
            i.Register("kernel32.dll", "RemoveVectoredExceptionHandler", CallConv.Stdcall, 1, remove);
            i.Register("ntdll.dll", "RtlAddVectoredExceptionHandler", CallConv.Stdcall, 2, add);
            i.Register("ntdll.dll", "RtlRemoveVectoredExceptionHandler", CallConv.Stdcall, 1, remove);

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
            StartDispatch(d);
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
            // With no frame, no vectored handler and no top-level filter, nothing
            // in the guest can take it: the fault is reported as it stands.
            if (IsEnd(ChainHead) && vectored.Count == 0 && unhandledFilter == 0) return false;
            ExceptionsRaised.Add(fault.Code);
            // The record and context go below the faulting thread's stack
            // pointer; when that is not writable (a stack overflow, a wild
            // ESP) there is no dispatching it, and the fault is reported.
            var below = (cpu.Esp - AreaSize - 64) & ~0xFu;
            if (!memory.IsMapped(below) || !memory.IsMapped(cpu.Esp - 4)) return false;
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
                StartDispatch(d);
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

        private uint AddVectored(bool first, uint handler)
        {
            if (handler == 0) { process.LastError = ErrorInvalidParameter; return 0; }
            var cookie = NewHandle();
            var entry = new KeyValuePair<uint, uint>(cookie, handler);
            if (first) vectored.Insert(0, entry); else vectored.Add(entry);
            return cookie;
        }

        private uint RemoveVectored(uint cookie)
        {
            var at = vectored.FindIndex(e => e.Key == cookie);
            if (at < 0) return 0;
            vectored.RemoveAt(at);
            return 1;
        }

        /// <summary>A new exception goes to the vectored handlers first, then the frame chain.</summary>
        private int firstChanceLogged;

        private void StartDispatch(SehDispatch d)
        {
            // The first few, as they are raised: a handled one still says where
            // a program went wrong before it chose to give up.
            if (firstChanceLogged < 20 && Log != null)
            {
                firstChanceLogged++;
                Log("exception " + DescribeRecord(d.Record) + DescribeContext(d.Context));
            }
            d.Vectored = vectored.Count == 0 ? null : vectored.ConvertAll(e => e.Value).ToArray();
            d.NextVectored = 0;
            if (d.Vectored != null) CallVectored(d);
            else EnterHandler(d, ChainHead);
        }

        /// <summary>
        /// Calls the next vectored handler, LONG CALLBACK handler(EXCEPTION_POINTERS*),
        /// laid out like the top-level filter's call; <see cref="VectoredReturned"/>
        /// takes the next step.
        /// </summary>
        private void CallVectored(SehDispatch d)
        {
            var handler = d.Vectored[d.NextVectored++];
            var t = d.Top;
            memory.Write32(t + 8, d.Record);
            memory.Write32(t + 12, d.Context);
            memory.Write32(t + 0, vectoredReturn);
            memory.Write32(t + 4, t + 8);
            pendingVectored.Push(d);
            process.Cpu.Esp = t;
            process.Cpu.Eip = handler;
            process.Jumped();
        }

        /// <summary>
        /// A vectored handler returned: -1 (EXCEPTION_CONTINUE_EXECUTION) resumes
        /// from the context it may have changed; anything else offers the
        /// exception to the next handler, and after the last one to the frames.
        /// </summary>
        private void VectoredReturned()
        {
            if (pendingVectored.Count == 0) throw new GuestRaisedException(StatusInvalidDisposition);
            var d = pendingVectored.Pop();
            if (process.Cpu.Eax == 0xFFFFFFFF)
            {
                if ((memory.Read32(d.Record + 4) & ExceptionNoncontinuable) != 0)
                    throw new GuestRaisedException(StatusNoncontinuableException);
                dispatches.Remove(d.Id);
                RestoreContext(d.Context);
                process.Jumped();
                return;
            }
            if (d.NextVectored < d.Vectored.Length) CallVectored(d);
            else EnterHandler(d, ChainHead);
        }

        /// <summary>Calls the handler of <paramref name="frame"/>, or ends the dispatch at the chain's end.</summary>
        private void EnterHandler(SehDispatch d, uint frame)
        {
            if (IsEnd(frame))
            {
                if (d.Unwind) { FinishUnwind(d); return; }
                // A program that installed its own top-level filter (SetUnhandledExceptionFilter
                // — common in a DRM/anti-tamper wrapper, which routinely raises an exception on
                // purpose and expects its own filter to fix up the context and resume) gets a real
                // call to it before this counts as fatal, exactly as Windows would.
                if (unhandledFilter != 0) { CallUnhandledFilter(d); return; }
                dispatches.Remove(d.Id);
                // Unhandled: the record says where it happened (and, for an access
                // violation, what was touched), which is what the report needs.
                var code = memory.Read32(d.Record);
                var parameters = memory.Read32(d.Record + 16);
                Log?.Invoke("unhandled exception " + DescribeRecord(d.Record) + DescribeContext(d.Context));
                throw new GuestRaisedException(code, memory.Read32(d.Record + 12),
                    parameters >= 2 ? memory.Read32(d.Record + 24) : 0, parameters >= 1 && memory.Read32(d.Record + 20) != 0);
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

        /// <summary>" (module+0xoffset)" for an address inside a mapped image, else empty.</summary>
        private string Where(uint address)
        {
            foreach (var image in process.Images)
                if (address >= image.BaseAddress && address - image.BaseAddress < image.ImageSize)
                    return " (" + image.Name + "+0x" + (address - image.BaseAddress).ToString("X") + ")";
            return "";
        }

        /// <summary>
        /// The faulting registers and the stack words above ESP that point into a
        /// mapped image: return-address candidates, which name the callers.
        /// </summary>
        private string DescribeContext(uint context)
        {
            if (context == 0 || !memory.IsMapped(context)) return "";
            uint esp = memory.Read32(context + 0xC4), ebp = memory.Read32(context + 0xB4), ecx = memory.Read32(context + 0xAC);
            var frames = new List<string>();
            for (uint n = 0; n < 96 && frames.Count < 12; n++)
            {
                var at = esp + n * 4;
                if (!memory.IsMapped(at)) break;
                var word = memory.Read32(at);
                var where = Where(word);
                if (where.Length > 0) frames.Add(where.Trim().Trim('(', ')'));
            }
            return " esp=0x" + esp.ToString("X8") + " ebp=0x" + ebp.ToString("X8") + " ecx=0x" + ecx.ToString("X8") +
                " stack=[" + string.Join(" < ", frames) + "]";
        }

        /// <summary>An exception record on one line: code, flags, where, and every parameter.</summary>
        private string DescribeRecord(uint record)
        {
            var count = Math.Min(memory.Read32(record + 16), 15u);
            var args = new List<string>();
            for (uint n = 0; n < count; n++) args.Add("0x" + memory.Read32(record + 20 + n * 4).ToString("X8"));
            var eip = memory.Read32(record + 12);
            return "code=0x" + memory.Read32(record).ToString("X8") + " flags=0x" + memory.Read32(record + 4).ToString("X8") +
                " eip=0x" + eip.ToString("X8") + Where(eip) +
                " eax=0x" + process.Cpu.Eax.ToString("X8") + " params=[" + string.Join(",", args) + "]";
        }

        /// <summary>
        /// Calls the program's own top-level filter with a pointer to an
        /// EXCEPTION_POINTERS {ExceptionRecord, ContextRecord} — the same call
        /// Windows makes when structured handling finds nothing on the chain.
        /// The filter is guest code; like a __except handler, this sets up its
        /// call and returns, and <see cref="FilterReturned"/> takes the next
        /// step once it comes back to the sentinel.
        /// </summary>
        private void CallUnhandledFilter(SehDispatch d)
        {
            // t+8/t+12 are the normal handler call's frame/context argument
            // slots (unused here — the filter takes one argument, not four),
            // safely inside the reserved call area and clear of d.Record
            // (t+32) and d.Context (t+112) that a wider struct would clobber.
            var t = d.Top;
            memory.Write32(t + 8, d.Record);            // EXCEPTION_POINTERS.ExceptionRecord
            memory.Write32(t + 12, d.Context);           // EXCEPTION_POINTERS.ContextRecord
            memory.Write32(t + 0, filterReturn);
            memory.Write32(t + 4, t + 8);                 // the one argument: &EXCEPTION_POINTERS
            pendingFilter = d;
            Log?.Invoke("calling unhandledFilter 0x" + unhandledFilter.ToString("X8") +
                " for " + DescribeRecord(d.Record) + DescribeContext(d.Context));

            process.Cpu.Esp = t;
            process.Cpu.Eip = unhandledFilter;
            process.Jumped();
        }

        /// <summary>
        /// The top-level filter returned: -1 (EXCEPTION_CONTINUE_EXECUTION)
        /// resumes the guest exactly where the exception happened, same as a
        /// __except handler's disposition 0 — anything else (0 CONTINUE_SEARCH,
        /// 1 EXECUTE_HANDLER, or garbage) is still unhandled, same as Windows
        /// with no debugger attached: the process would not survive it either.
        /// </summary>
        private void FilterReturned()
        {
            var d = pendingFilter;
            pendingFilter = null;
            if (d == null) throw new GuestRaisedException(StatusInvalidDisposition);
            var result = process.Cpu.Eax;
            Log?.Invoke("unhandledFilter returned 0x" + result.ToString("X8"));
            dispatches.Remove(d.Id);

            if (result == 0xFFFFFFFF)
            {
                RestoreContext(d.Context);
                process.Jumped();
                return;
            }

            var code = memory.Read32(d.Record);
            var parameters = memory.Read32(d.Record + 16);
            throw new GuestRaisedException(code, memory.Read32(d.Record + 12),
                parameters >= 2 ? memory.Read32(d.Record + 24) : 0, parameters >= 1 && memory.Read32(d.Record + 20) != 0);
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
