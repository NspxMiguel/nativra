using System.Collections.Generic;
using Nativra.X86.Cpu;

namespace Nativra.X86.Loader
{
    // msvcrt: the structured-exception handlers MSVC code registers for its
    // __try blocks (_except_handler3, and _except_handler4_common behind the
    // static __except_handler4 stub), and the helpers an MSVC-built DLL that
    // links the system msvcrt (d3dx9_43.dll among them) calls while it starts.
    //
    // Frame layout both handlers share, at the registration R:
    //   R-8 saved ESP   R-4 EXCEPTION_POINTERS*   R+0 next   R+4 handler
    //   R+8 scope table (EH4: XORed with the security cookie)   R+12 try level
    //   R+16 the function's EBP
    // A scope entry is { enclosing level, filter, handler }; a null filter
    // marks a __finally. EH4's table starts after 16 bytes of cookie offsets
    // and ends at level -2, EH3's at level -1.
    public sealed partial class GuestKernel
    {
        private sealed class PendingTransfer
        {
            public uint Frame, Entries, Ebp, Handler;
            public int TargetLevel, Enclosing, End;
        }

        private readonly Dictionary<uint, PendingTransfer> pendingTransfers = new Dictionary<uint, PendingTransfer>();
        private uint ehContinue;

        private void InstallCrtEh(GuestImports i)
        {
            ehContinue = i.Bind(Crt, "__nativra_eh_continue", -1);
            C(i, "__nativra_eh_continue", 0, c => ContinueTransfer());
            C(i, "_except_handler3", 4, c => ExceptHandler(c, c.Arg(0), c.Arg(1), false, 0));
            C(i, "_except_handler4_common", 6, c => ExceptHandler(c, c.Arg(2), c.Arg(3), true, memory.Read32(c.Arg(0))));
            C(i, "_local_unwind2", 2, c => { LocalUnwind(c.Arg(0), ScopeEntries(c.Arg(0), false, 0), c.Arg(0) + 16, (int)c.Arg(1), -1); return 0; });
            C(i, "_local_unwind4", 3, c =>
            {
                var cookie = memory.Read32(c.Arg(0));
                LocalUnwind(c.Arg(1), ScopeEntries(c.Arg(1), true, cookie), c.Arg(1) + 16, (int)c.Arg(2), -2);
                return 0;
            });
            C(i, "_abnormal_termination", 0, c => 0);

            // Start-up helpers of MSVC DLLs built against the system msvcrt.
            C(i, "_encoded_null", 0, c => 0);   // EncodePointer(NULL): pointers are not scrambled here
            C(i, "_malloc_crt", 1, c => CrtAlloc(c.Arg(0), false));
            C(i, "_calloc_crt", 2, c => CrtAlloc(c.Arg(0) * c.Arg(1), true));
            C(i, "_realloc_crt", 2, c => CrtRealloc(c.Arg(0), c.Arg(1)));
            C(i, "_crt_debugger_hook", 1, c => 0);
            C(i, "__clean_type_info_names_internal", 1, c => 0);
            C(i, "__crtGetShowWindowMode", 0, c => 10);   // SW_SHOWDEFAULT
            C(i, "__crtSetUnhandledExceptionFilter", 1, c => 0);
            C(i, "__uncaught_exception", 0, c => 0);
            C(i, "_invalid_parameter_noinfo", 0, c => { Say("x86: CRT invalid parameter"); return 0; });
            C(i, "_invalid_parameter", 5, c => { Say("x86: CRT invalid parameter"); return 0; });
            C(i, "_invoke_watson", 5, c => Abort("_invoke_watson"));
            C(i, "__security_error_handler", 2, c => Abort("buffer overrun detected"));

            // type_info: its vftable is a data import; the scalar deleting destructor is its one entry.
            var typeInfoTable = heap.Alloc(8, zero: true);
            memory.Write32(typeInfoTable, i.Bind(Crt, "??_Etype_info@@UAEPAXI@Z", -1));
            i.Register(Crt, "??_Etype_info@@UAEPAXI@Z", CallConv.Thiscall, 1, c => { if ((c.Arg(0) & 1) != 0) heap.Free(c.This); return c.This; });
            i.Register(Crt, "??1type_info@@UAE@XZ", CallConv.Thiscall, 0, c => 0);
            i.Register(Crt, "?name@type_info@@QBEPBDXZ", CallConv.Thiscall, 0, c => memory.Read32(c.This + 4) != 0 ? memory.Read32(c.This + 4) : c.This + 8);
            i.Register(Crt, "?raw_name@type_info@@QBEPBDXZ", CallConv.Thiscall, 0, c => c.This + 8);
            i.Register(Crt, "??8type_info@@QBEHABV0@@Z", CallConv.Thiscall, 1, c => CompareBytes(c.This + 8, c.Arg(0) + 8, uint.MaxValue, false) == 0 ? 1u : 0u);
            i.Register(Crt, "??9type_info@@QBEHABV0@@Z", CallConv.Thiscall, 1, c => CompareBytes(c.This + 8, c.Arg(0) + 8, uint.MaxValue, false) != 0 ? 1u : 0u);
            i.Register(Crt, "?before@type_info@@QBEHABV1@@Z", CallConv.Thiscall, 1, c => CompareBytes(c.This + 8, c.Arg(0) + 8, uint.MaxValue, false) < 0 ? 1u : 0u);
            CrtData(i, "??_7type_info@@6B@", typeInfoTable);
        }

        private uint ScopeEntries(uint frame, bool eh4, uint cookie)
        {
            var table = memory.Read32(frame + 8);
            if (eh4) table ^= cookie;
            return eh4 ? table + 16 : table;
        }

        private uint CallGuestWithEbp(uint function, uint ebp)
        {
            var saved = SaveRegisters();
            process.Cpu.Ebp = ebp;
            var result = process.Call(function, out var eax, 50_000_000);
            RestoreRegisters(saved);
            if (!result.Ok) Say($"x86: SEH funclet 0x{function:X8} stopped: {result}");
            return result.Ok ? eax : 0;
        }

        /// <summary>The frame-based handler: unwinding runs this frame's __finally blocks; dispatch asks the filters in turn.</summary>
        private ulong ExceptHandler(GuestCall c, uint record, uint frame, bool eh4, uint cookie)
        {
            var entries = ScopeEntries(frame, eh4, cookie);
            var ebp = frame + 16;
            var end = eh4 ? -2 : -1;
            if ((memory.Read32(record + 4) & (ExceptionUnwinding | ExceptionExitUnwind)) != 0)
            {
                LocalUnwind(frame, entries, ebp, end, end);
                return 1;   // ExceptionContinueSearch
            }

            // GetExceptionInformation() reads the pointers at [ebp-14h].
            var pointers = heap.Alloc(8);
            memory.Write32(pointers, record);
            memory.Write32(pointers + 4, c.Arg(2 + (eh4 ? 2 : 0)));
            memory.Write32(frame - 4, pointers);

            for (var level = (int)memory.Read32(frame + 12); level != end && level >= 0;)
            {
                var entry = entries + (uint)level * 12;
                var enclosing = (int)memory.Read32(entry);
                var filter = memory.Read32(entry + 4);
                if (filter != 0)
                {
                    var verdict = (int)CallGuestWithEbp(filter, ebp);
                    if (verdict < 0) return 0;   // EXCEPTION_CONTINUE_EXECUTION
                    if (verdict > 0)
                    {
                        // Unwind every frame above this one, then this frame's
                        // inner __finally blocks, then run the __except block.
                        pendingTransfers[process.CurrentThread.Id] = new PendingTransfer
                        {
                            Frame = frame, Entries = entries, Ebp = ebp, Handler = memory.Read32(entry + 8),
                            TargetLevel = level, Enclosing = enclosing, End = end,
                        };
                        TailCall(c, 0, process.Imports.Bind("kernel32.dll", "RtlUnwind", -1), frame, ehContinue, record, 0);
                        return 0;
                    }
                }
                level = enclosing;
            }
            return 1;
        }

        private ulong ContinueTransfer()
        {
            var id = process.CurrentThread.Id;
            if (!pendingTransfers.TryGetValue(id, out var t))
                throw new System.InvalidOperationException("SEH transfer sentinel reached with nothing pending");
            pendingTransfers.Remove(id);
            LocalUnwind(t.Frame, t.Entries, t.Ebp, t.TargetLevel, t.End);
            memory.Write32(t.Frame + 12, (uint)t.Enclosing);
            var cpu = process.Cpu;
            cpu.Ebp = t.Ebp;
            cpu.Esp = memory.Read32(t.Frame - 8);   // the __except block reloads it from [ebp-18h] as well
            cpu.Eip = t.Handler;
            process.Jumped();
            return 0;
        }

        /// <summary>Runs the __finally blocks from the current try level down to <paramref name="stop"/>.</summary>
        private void LocalUnwind(uint frame, uint entries, uint ebp, int stop, int end)
        {
            for (var level = (int)memory.Read32(frame + 12); level != stop && level != end && level >= 0;)
            {
                var entry = entries + (uint)level * 12;
                var enclosing = (int)memory.Read32(entry);
                memory.Write32(frame + 12, (uint)enclosing);   // before the call, as _local_unwind does
                if (memory.Read32(entry + 4) == 0) CallGuestWithEbp(memory.Read32(entry + 8), ebp);
                level = enclosing;
            }
        }
    }
}
