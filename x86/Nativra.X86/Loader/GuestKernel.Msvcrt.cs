using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Nativra.X86.Cpu;

namespace Nativra.X86.Loader
{
    // msvcrt.dll, the Windows C runtime that MinGW-built and older MSVC games
    // import, implemented on the host the way Wine implements it: over the
    // guest heap, the kernel's file handles and its thread scheduler. It is
    // only the fallback: a game that carries its own msvcrXX.dll maps that
    // real DLL instead, since game DLLs take precedence over handlers.
    //
    // This part: the exported variables (_iob, _pctype, __argc, _environ…),
    // start-up (__getmainargs, _initterm), exit and atexit, memory, the
    // environment, time, threads, setjmp/longjmp, signals and the odds and
    // ends. GuestKernel.MsvcrtText.cs has strings, ctype, conversions and
    // math; GuestKernel.MsvcrtStdio.cs has file descriptors, FILE streams
    // and the printf/scanf engines.
    public sealed partial class GuestKernel
    {
        private const string Crt = "msvcrt.dll";
        private const int IobCount = 20, FileSize = 32;
        private const uint Enoent = 2, Ebadf = 9, Enomem = 12, Eacces = 13, Eexist = 17, Einval = 22, Emfile = 24, Erange = 34;

        private uint crtData;          // start of the block holding the exported variables
        private uint iob;              // FILE _iob[20]
        private uint ctypeTable;       // unsigned short[257], index 0 is EOF
        private uint wctypeTable;      // unsigned short[256]
        private uint mbctypeTable;     // unsigned char[257]
        private uint pctypeVar, pwctypeVar, argcVar, argvVar, wargvVar, environVar, wenvironVar;
        private uint acmdlnVar, wcmdlnVar, fmodeVar, commodeVar, mbCurMaxVar, initenvVar, winitenvVar;
        private uint osverVar, winverVar, winmajorVar, winminorVar, timezoneVar, daylightVar, dstbiasVar, tznameVar;
        private uint hugeVar, lconv, tmBuffer, asctimeBuffer, localeName, localeNameWide, crtScratch;
        private uint controlWord = 0x0009001F;   // _controlfp's view: _PC_53 | _RC_NEAR | every exception masked
        private int randSeedDefault = 1;
        private readonly Dictionary<uint, uint> errnoSlots = new Dictionary<uint, uint>();
        private readonly Dictionary<uint, int> randSeeds = new Dictionary<uint, int>();
        private readonly Dictionary<uint, uint> strtokNext = new Dictionary<uint, uint>();
        private readonly List<uint> atexitFunctions = new List<uint>();
        private readonly Dictionary<uint, Stack<CallChain>> chains = new Dictionary<uint, Stack<CallChain>>();
        private readonly Dictionary<string, uint> envCopies = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, uint> signalHandlers = new Dictionary<int, uint>();
        private readonly System.Diagnostics.Stopwatch crtClock = System.Diagnostics.Stopwatch.StartNew();
        private uint chainSentinel;

        /// <summary>
        /// A run of guest calls a single CRT call makes (the _initterm table,
        /// the atexit list at exit), driven from sentinel to sentinel so no
        /// guest code runs nested inside a host call.
        /// </summary>
        private sealed class CallChain
        {
            public Queue<uint> Functions = new Queue<uint>();
            public uint ReturnAddress;
            public uint Esp;               // the caller's ESP after the call (cdecl: arguments still there)
            public bool StopOnNonZero;     // _initterm_e
            public Action<uint> Finish;    // instead of returning (exit)
        }

        private void C(GuestImports i, string name, int args, HostCall body) => i.Register(Crt, name, CallConv.Cdecl, args, body);

        private void CrtData(GuestImports i, string name, uint address) => i.RegisterData(Crt, name, address);

        private ulong ReturnDouble(double value)
        {
            process.Interpreter.Fpu.Push(value);
            return 0;
        }

        private static double D(GuestCall c, int index) => BitConverter.Int64BitsToDouble((long)c.Arg64(index));

        private uint ErrnoSlot()
        {
            var id = process.CurrentThread.Id;
            if (!errnoSlots.TryGetValue(id, out var slot)) errnoSlots[id] = slot = heap.Alloc(8, zero: true);
            return slot;
        }

        internal void SetErrno(uint value) => memory.Write32(ErrnoSlot(), value);

        private void InstallMsvcrt(GuestImports i)
        {
            InstallCrtData(i);
            InstallCrtStartup(i);
            InstallCrtMemory(i);
            InstallCrtEnvironment(i);
            InstallCrtTime(i);
            InstallCrtThreads(i);
            InstallCrtText(i);
            InstallCrtStdio(i);
            InstallCrtEh(i);
        }

        // --- exported variables ---------------------------------------------

        private void InstallCrtData(GuestImports i)
        {
            crtData = heap.Alloc(4096, zero: true);
            uint cursor = crtData;
            uint Take(uint size) { var at = cursor; cursor += (size + 7) & ~7u; return at; }

            ctypeTable = Take(257 * 2);
            for (var ch = 0; ch < 256; ch++) memory.Write16(ctypeTable + 2 + (uint)ch * 2, ClassicCtype(ch));
            wctypeTable = Take(256 * 2);
            for (var ch = 0; ch < 256; ch++) memory.Write16(wctypeTable + (uint)ch * 2, ch < 128 ? ClassicCtype(ch) : (ushort)(CharType1((char)ch) & 0x1FF));
            mbctypeTable = Take(257);

            pctypeVar = Take(4); memory.Write32(pctypeVar, ctypeTable + 2);
            pwctypeVar = Take(4); memory.Write32(pwctypeVar, wctypeTable);
            argcVar = Take(4); argvVar = Take(4); wargvVar = Take(4);
            environVar = Take(4); wenvironVar = Take(4); initenvVar = Take(4); winitenvVar = Take(4);
            acmdlnVar = Take(4); wcmdlnVar = Take(4);
            fmodeVar = Take(4); commodeVar = Take(4);
            mbCurMaxVar = Take(4); memory.Write32(mbCurMaxVar, 1);
            osverVar = Take(4); memory.Write32(osverVar, 26100);
            winverVar = Take(4); memory.Write32(winverVar, 0x0A00);
            winmajorVar = Take(4); memory.Write32(winmajorVar, 10);
            winminorVar = Take(4);
            timezoneVar = Take(4); daylightVar = Take(4); dstbiasVar = Take(4); memory.Write32(dstbiasVar, unchecked((uint)-3600));
            tznameVar = Take(8);
            var utc = Take(8); memory.WriteAnsi(utc, "UTC");
            memory.Write32(tznameVar, utc); memory.Write32(tznameVar + 4, utc);
            hugeVar = Take(8); memory.Write64(hugeVar, (ulong)BitConverter.DoubleToInt64Bits(double.PositiveInfinity));
            tmBuffer = Take(36);
            asctimeBuffer = Take(32);
            localeName = Take(4); memory.WriteAnsi(localeName, "C");
            localeNameWide = Take(4); memory.WriteUnicode(localeNameWide, "C");
            crtScratch = Take(64);

            // struct lconv: ten char* then eight chars; the strings are "." and "".
            lconv = Take(56);
            var dot = Take(2); memory.WriteAnsi(dot, ".");
            var empty = Take(2);
            memory.Write32(lconv, dot);
            for (uint n = 1; n < 10; n++) memory.Write32(lconv + n * 4, empty);
            for (uint n = 0; n < 8; n++) memory.Write8(lconv + 40 + n, 0x7F);   // CHAR_MAX: not available

            iob = heap.Alloc(IobCount * FileSize, zero: true);
            for (var fd = 0; fd < 3; fd++)
            {
                var f = iob + (uint)fd * FileSize;
                memory.Write32(f + 12, fd == 0 ? 1u : 2u);   // _IOREAD / _IOWRT
                memory.Write32(f + 16, (uint)fd);            // _file
            }

            chainSentinel = i.Bind(Crt, "__nativra_chain", -1);
            BuildEnvironment();
            BuildArguments();

            CrtData(i, "_iob", iob);
            CrtData(i, "_pctype", pctypeVar);
            CrtData(i, "_pwctype", pwctypeVar);
            CrtData(i, "_ctype", ctypeTable);
            CrtData(i, "_mbctype", mbctypeTable);
            CrtData(i, "__argc", argcVar);
            CrtData(i, "__argv", argvVar);
            CrtData(i, "__wargv", wargvVar);
            CrtData(i, "_environ", environVar);
            CrtData(i, "_wenviron", wenvironVar);
            CrtData(i, "__initenv", initenvVar);
            CrtData(i, "__winitenv", winitenvVar);
            CrtData(i, "_acmdln", acmdlnVar);
            CrtData(i, "_wcmdln", wcmdlnVar);
            CrtData(i, "_fmode", fmodeVar);
            CrtData(i, "_commode", commodeVar);
            CrtData(i, "__mb_cur_max", mbCurMaxVar);
            CrtData(i, "_osver", osverVar);
            CrtData(i, "_winver", winverVar);
            CrtData(i, "_winmajor", winmajorVar);
            CrtData(i, "_winminor", winminorVar);
            CrtData(i, "_timezone", timezoneVar);
            CrtData(i, "_daylight", daylightVar);
            CrtData(i, "_dstbias", dstbiasVar);
            CrtData(i, "_tzname", tznameVar);
            CrtData(i, "_HUGE", hugeVar);
            CrtData(i, "_adjust_fdiv", Take(4));
            CrtData(i, "_osplatform", Take(4));
            memory.Write32(i.DataAddress(Crt, "_osplatform"), 2);   // VER_PLATFORM_WIN32_NT

            // The accessors that return the variables' addresses.
            void Address(string name, uint at) => C(i, name, 0, c => at);
            Address("__p___argc", argcVar);
            Address("__p___argv", argvVar);
            Address("__p___wargv", wargvVar);
            Address("__p__environ", environVar);
            Address("__p__wenviron", wenvironVar);
            Address("__p___initenv", initenvVar);
            Address("__p___winitenv", winitenvVar);
            Address("__p__acmdln", acmdlnVar);
            Address("__p__wcmdln", wcmdlnVar);
            Address("__p__fmode", fmodeVar);
            Address("__p__commode", commodeVar);
            Address("__p___mb_cur_max", mbCurMaxVar);
            Address("__p__pctype", pctypeVar);
            Address("__p__pwctype", pwctypeVar);
            Address("__p__mbctype", mbctypeTable);
            Address("__p__osver", osverVar);
            Address("__p__winver", winverVar);
            Address("__p__winmajor", winmajorVar);
            Address("__p__winminor", winminorVar);
            Address("__p__timezone", timezoneVar);
            Address("__p__daylight", daylightVar);
            Address("__p__dstbias", dstbiasVar);
            Address("__p__tzname", tznameVar);
            Address("__p__iob", iob);
            Address("__iob_func", iob);
            Address("__pctype_func", ctypeTable + 2);
            Address("__pwctype_func", wctypeTable);
            C(i, "___mb_cur_max_func", 0, c => 1);
            C(i, "___lc_codepage_func", 0, c => 0);   // the "C" locale has no code page
            C(i, "___lc_collate_cp_func", 0, c => 0);
            C(i, "___lc_handle_func", 0, c => crtScratch);   // six zero LCIDs: the "C" locale
        }

        /// <summary>The "C" locale's classification bits (_UPPER 1, _LOWER 2, _DIGIT 4, _SPACE 8, _PUNCT 0x10, _CONTROL 0x20, _BLANK 0x40, _HEX 0x80, _ALPHA 0x100).</summary>
        private static ushort ClassicCtype(int ch)
        {
            if (ch >= 128) return 0;
            if (ch == ' ') return 0x48;
            if (ch == '\t') return 0x68;
            if (ch >= 0x0A && ch <= 0x0D) return 0x28;
            if (ch < 0x20 || ch == 0x7F) return 0x20;
            if (ch >= '0' && ch <= '9') return 0x84;
            if (ch >= 'A' && ch <= 'Z') return (ushort)(0x101 | (ch <= 'F' ? 0x80 : 0));
            if (ch >= 'a' && ch <= 'z') return (ushort)(0x102 | (ch <= 'f' ? 0x80 : 0));
            return 0x10;
        }

        private void BuildEnvironment()
        {
            var entries = new List<string>();
            foreach (var pair in environment) entries.Add(pair.Key + "=" + pair.Value);
            uint Table(bool wide)
            {
                var table = heap.Alloc((uint)(entries.Count + 1) * 4, zero: true);
                for (var n = 0; n < entries.Count; n++)
                {
                    var s = heap.Alloc((uint)(entries[n].Length + 1) * (wide ? 2u : 1u));
                    WriteText(s, entries[n], wide);
                    memory.Write32(table + (uint)n * 4, s);
                }
                return table;
            }
            var narrow = Table(false);
            var wideTable = Table(true);
            memory.Write32(environVar, narrow);
            memory.Write32(initenvVar, narrow);
            memory.Write32(wenvironVar, wideTable);
            memory.Write32(winitenvVar, wideTable);
            envCopies.Clear();
        }

        /// <summary>argc/argv from the command line, split by the Microsoft C rules.</summary>
        private void BuildArguments()
        {
            var line = commandLineAnsi != 0 ? ReadText(commandLineAnsi, false) : "";
            var args = SplitCommandLine(line);
            uint Table(bool wide)
            {
                var table = heap.Alloc((uint)(args.Count + 1) * 4, zero: true);
                for (var n = 0; n < args.Count; n++)
                {
                    var s = heap.Alloc((uint)(args[n].Length + 1) * (wide ? 2u : 1u));
                    WriteText(s, args[n], wide);
                    memory.Write32(table + (uint)n * 4, s);
                }
                return table;
            }
            memory.Write32(argcVar, (uint)args.Count);
            memory.Write32(argvVar, Table(false));
            memory.Write32(wargvVar, Table(true));
            memory.Write32(acmdlnVar, commandLineAnsi);
            memory.Write32(wcmdlnVar, commandLineWide);
        }

        internal static List<string> SplitCommandLine(string line)
        {
            var args = new List<string>();
            var n = 0;
            while (true)
            {
                while (n < line.Length && (line[n] == ' ' || line[n] == '\t')) n++;
                if (n >= line.Length) break;
                var arg = new StringBuilder();
                var quoted = false;
                while (n < line.Length && (quoted || (line[n] != ' ' && line[n] != '\t')))
                {
                    if (line[n] == '\\')
                    {
                        var slashes = 0;
                        while (n < line.Length && line[n] == '\\') { slashes++; n++; }
                        if (n < line.Length && line[n] == '"')
                        {
                            arg.Append('\\', slashes / 2);
                            if (slashes % 2 == 1) { arg.Append('"'); n++; }
                        }
                        else arg.Append('\\', slashes);
                        continue;
                    }
                    if (line[n] == '"')
                    {
                        // "" inside quotes is a literal quote.
                        if (quoted && n + 1 < line.Length && line[n + 1] == '"') { arg.Append('"'); n += 2; continue; }
                        quoted = !quoted;
                        n++;
                        continue;
                    }
                    arg.Append(line[n++]);
                }
                args.Add(arg.ToString());
            }
            return args;
        }

        // --- start-up and exit ------------------------------------------------

        private void InstallCrtStartup(GuestImports i)
        {
            C(i, "__getmainargs", 5, c => MainArgs(c, false));
            C(i, "__wgetmainargs", 5, c => MainArgs(c, true));
            C(i, "__set_app_type", 1, c => 0);
            C(i, "__setusermatherr", 1, c => 0);
            C(i, "_set_error_mode", 1, c => 0);
            C(i, "_set_app_type", 1, c => 0);
            C(i, "_configthreadlocale", 1, c => 2);   // _DISABLE_PER_THREAD_LOCALE
            C(i, "_set_invalid_parameter_handler", 1, c => 0);
            C(i, "_get_invalid_parameter_handler", 0, c => 0);
            C(i, "_set_purecall_handler", 1, c => 0);
            C(i, "_set_abort_behavior", 2, c => 0);
            C(i, "_set_SSE2_enable", 1, c => c.Arg(0));
            C(i, "_lock", 1, c => 0);      // guest threads never run in parallel with a host call
            C(i, "_unlock", 1, c => 0);
            C(i, "_mlock", 1, c => 0);
            C(i, "_munlock", 1, c => 0);
            C(i, "_initterm", 2, c => StartChain(c, Table(c.Arg(0), c.Arg(1)), false, null));
            C(i, "_initterm_e", 2, c => StartChain(c, Table(c.Arg(0), c.Arg(1)), true, null));
            C(i, "__nativra_chain", 0, c => ContinueChain());

            C(i, "atexit", 1, c => { atexitFunctions.Add(c.Arg(0)); return 0; });
            C(i, "_onexit", 1, c => { atexitFunctions.Add(c.Arg(0)); return c.Arg(0); });
            C(i, "__dllonexit", 3, c => { atexitFunctions.Add(c.Arg(0)); return c.Arg(0); });
            C(i, "exit", 1, c => RunExit(c, c.Arg(0), true));
            C(i, "_cexit", 0, c => RunExit(c, 0, false));
            C(i, "_exit", 1, c => { FlushAllStreams(); throw new GuestExitException(c.Arg(0)); });
            C(i, "_c_exit", 0, c => 0);
            C(i, "abort", 0, c => Abort("abort()"));
            C(i, "_amsg_exit", 1, c => Abort($"runtime error R60{c.Arg(0):D2}"));
            C(i, "_purecall", 0, c => Abort("pure virtual function call"));
            C(i, "?terminate@@YAXXZ", 0, c => Abort("terminate()"));
            C(i, "_assert", 3, c => Abort($"Assertion failed: {ReadText(c.Arg(0), false)}, file {ReadText(c.Arg(1), false)}, line {c.Arg(2)}"));
            C(i, "_wassert", 3, c => Abort($"Assertion failed: {ReadText(c.Arg(0), true)}, file {ReadText(c.Arg(1), true)}, line {c.Arg(2)}"));
            C(i, "_XcptFilter", 2, c => 0);   // EXCEPTION_CONTINUE_SEARCH
            C(i, "__CppXcptFilter", 2, c => 0);
            C(i, "_CxxThrowException", 2, c =>
            {
                // RaiseException(0xE06D7363, EXCEPTION_NONCONTINUABLE, 3, { magic, object, throw info }).
                var info = heap.Alloc(12);
                memory.Write32(info, 0x19930520);
                memory.Write32(info + 4, c.Arg(0));
                memory.Write32(info + 8, c.Arg(1));
                TailCall(c, 0, process.Imports.Bind("kernel32.dll", "RaiseException", -1), 0xE06D7363, 1, 3, info);
                return 0;
            });
            C(i, "signal", 2, c =>
            {
                var sig = (int)c.Arg(0);
                signalHandlers.TryGetValue(sig, out var previous);
                signalHandlers[sig] = c.Arg(1);
                return previous;
            });
            C(i, "raise", 1, c =>
            {
                if (signalHandlers.TryGetValue((int)c.Arg(0), out var handler) && handler > 1)
                {
                    TailCall(c, 0, handler, c.Arg(0));
                    return 0;
                }
                if (handler == 1) return 0;   // SIG_IGN
                return Abort($"signal {c.Arg(0)}");
            });
        }

        private ulong Abort(string why)
        {
            Say("x86: " + why);
            FlushAllStreams();
            throw new GuestExitException(3);
        }

        private uint MainArgs(GuestCall c, bool wide)
        {
            BuildArguments();
            memory.Write32(c.Arg(0), memory.Read32(argcVar));
            memory.Write32(c.Arg(1), memory.Read32(wide ? wargvVar : argvVar));
            memory.Write32(c.Arg(2), memory.Read32(wide ? wenvironVar : environVar));
            return 0;
        }

        private List<uint> Table(uint begin, uint end)
        {
            var list = new List<uint>();
            for (var at = begin; at < end; at += 4)
            {
                var f = memory.Read32(at);
                if (f != 0) list.Add(f);
            }
            return list;
        }

        /// <summary>
        /// Calls each function in turn, cdecl with no arguments: each returns
        /// to the chain sentinel, which starts the next; the last returns to
        /// the CRT call's own caller (or finishes it, as exit does).
        /// </summary>
        private ulong StartChain(GuestCall c, List<uint> functions, bool stopOnNonZero, Action<uint> finish)
        {
            var chain = new CallChain
            {
                ReturnAddress = c.ReturnAddress,
                Esp = c.ArgBase,
                StopOnNonZero = stopOnNonZero,
                Finish = finish,
            };
            foreach (var f in functions) chain.Functions.Enqueue(f);
            var id = process.CurrentThread.Id;
            if (!chains.TryGetValue(id, out var stack)) chains[id] = stack = new Stack<CallChain>();
            stack.Push(chain);
            NextInChain(chain, stack, 0);
            return 0;
        }

        private ulong ContinueChain()
        {
            if (!chains.TryGetValue(process.CurrentThread.Id, out var stack) || stack.Count == 0)
                throw new InvalidOperationException("CRT call chain sentinel reached with no chain running");
            NextInChain(stack.Peek(), stack, process.Cpu.Eax);
            return 0;
        }

        private void NextInChain(CallChain chain, Stack<CallChain> stack, uint lastResult)
        {
            var cpu = process.Cpu;
            if (chain.Functions.Count > 0 && !(chain.StopOnNonZero && lastResult != 0))
            {
                var esp = chain.Esp - 4;
                memory.Write32(esp, chainSentinel);
                cpu.Esp = esp;
                cpu.Eip = chain.Functions.Dequeue();
                process.Jumped();
                return;
            }
            stack.Pop();
            if (chain.Finish != null) { chain.Finish(lastResult); return; }
            cpu.Esp = chain.Esp;
            cpu.Eip = chain.ReturnAddress;
            cpu.Eax = chain.StopOnNonZero ? lastResult : 0;
            process.Jumped();
        }

        /// <summary>exit / _cexit: the atexit functions, last registered first, then (exit) the process ends.</summary>
        private ulong RunExit(GuestCall c, uint code, bool terminate)
        {
            var functions = new List<uint>(atexitFunctions);
            functions.Reverse();
            atexitFunctions.Clear();
            StartChain(c, functions, false, terminate ? (Action<uint>)(_ =>
            {
                FlushAllStreams();
                throw new GuestExitException(code);
            }) : null);
            return 0;
        }

        // --- memory ---------------------------------------------------------------

        private void InstallCrtMemory(GuestImports i)
        {
            HostCall malloc = c => CrtAlloc(c.Arg(0), false);
            HostCall free = c => { if (c.Arg(0) != 0) heap.Free(c.Arg(0)); return 0; };
            C(i, "malloc", 1, malloc);
            C(i, "free", 1, free);
            C(i, "calloc", 2, c =>
            {
                var total = (ulong)c.Arg(0) * c.Arg(1);
                if (total > 0x7FFFFFFF) { SetErrno(Enomem); return 0; }
                return CrtAlloc((uint)total, true);
            });
            C(i, "realloc", 2, c => CrtRealloc(c.Arg(0), c.Arg(1)));
            C(i, "_recalloc", 3, c =>
            {
                var total = (ulong)c.Arg(1) * c.Arg(2);
                if (total > 0x7FFFFFFF) { SetErrno(Enomem); return 0; }
                var old = c.Arg(0) != 0 ? heap.SizeOf(c.Arg(0)) : 0;
                var p = CrtRealloc(c.Arg(0), (uint)total);
                if (p != 0 && total > old) FillMemory(p + old, (uint)total - old, 0);
                return p;
            });
            C(i, "_msize", 1, c => heap.SizeOf(c.Arg(0)));
            C(i, "_expand", 2, c => c.Arg(1) <= heap.SizeOf(c.Arg(0)) ? c.Arg(0) : 0);
            C(i, "_heapmin", 0, c => 0);
            C(i, "_heapchk", 0, c => unchecked((uint)-2));   // _HEAPOK
            C(i, "_heapset", 1, c => unchecked((uint)-2));
            C(i, "_get_heap_handle", 0, c => ProcessHeapHandle);
            C(i, "_set_new_handler", 1, c => 0);
            C(i, "_query_new_handler", 0, c => 0);
            C(i, "_set_new_mode", 1, c => 0);
            C(i, "_query_new_mode", 0, c => 0);
            C(i, "_set_sbh_threshold", 1, c => 1);
            C(i, "_get_sbh_threshold", 0, c => 0);
            // operator new / delete (and the array forms), as msvcrt exports them.
            C(i, "??2@YAPAXI@Z", 1, malloc);
            C(i, "??_U@YAPAXI@Z", 1, malloc);
            C(i, "??3@YAXPAX@Z", 1, free);
            C(i, "??_V@YAXPAX@Z", 1, free);
            C(i, "_aligned_malloc", 2, c => AlignedAlloc(c.Arg(0), c.Arg(1), 0));
            C(i, "_aligned_offset_malloc", 3, c => AlignedAlloc(c.Arg(0), c.Arg(1), c.Arg(2)));
            C(i, "_aligned_free", 1, c => { if (c.Arg(0) != 0) heap.Free(memory.Read32((c.Arg(0) & ~3u) - 4)); return 0; });
            C(i, "_aligned_realloc", 3, c =>
            {
                if (c.Arg(0) == 0) return AlignedAlloc(c.Arg(1), c.Arg(2), 0);
                var original = memory.Read32((c.Arg(0) & ~3u) - 4);
                if (c.Arg(1) == 0) { heap.Free(original); return 0; }
                var fresh = AlignedAlloc(c.Arg(1), c.Arg(2), 0);
                if (fresh == 0) return 0;
                var available = heap.SizeOf(original) - (c.Arg(0) - original);
                MoveMemory(fresh, c.Arg(0), Math.Min(available, c.Arg(1)));
                heap.Free(original);
                return fresh;
            });
        }

        private uint CrtAlloc(uint size, bool zero)
        {
            var p = heap.Alloc(size == 0 ? 1 : size, zero);
            if (p == 0) SetErrno(Enomem);
            return p;
        }

        private uint CrtRealloc(uint p, uint size)
        {
            if (p == 0) return CrtAlloc(size, false);
            if (size == 0) { heap.Free(p); return 0; }
            var q = heap.ReAlloc(p, size);
            if (q == 0) SetErrno(Enomem);
            return q;
        }

        /// <summary>The block's own start is kept in the four bytes before the aligned pointer.</summary>
        private uint AlignedAlloc(uint size, uint alignment, uint offset)
        {
            if (alignment == 0 || (alignment & (alignment - 1)) != 0) { SetErrno(Einval); return 0; }
            if (alignment < 4) alignment = 4;
            var raw = heap.Alloc(size + alignment + 4 + offset);
            if (raw == 0) { SetErrno(Enomem); return 0; }
            var aligned = ((raw + 4 + offset + alignment - 1) & ~(alignment - 1)) - offset;
            memory.Write32((aligned & ~3u) - 4, raw);
            return aligned;
        }

        // --- environment and locale ---------------------------------------------------

        private void InstallCrtEnvironment(GuestImports i)
        {
            C(i, "getenv", 1, c => GetEnv(ReadText(c.Arg(0), false), false));
            C(i, "_wgetenv", 1, c => GetEnv(ReadText(c.Arg(0), true), true));
            C(i, "_putenv", 1, c => PutEnv(ReadText(c.Arg(0), false)));
            C(i, "_wputenv", 1, c => PutEnv(ReadText(c.Arg(0), true)));
            C(i, "_putenv_s", 2, c => PutEnv(ReadText(c.Arg(0), false) + "=" + ReadText(c.Arg(1), false)));
            C(i, "getenv_s", 4, c =>
            {
                var found = environment.TryGetValue(ReadText(c.Arg(3), false), out var value);
                if (c.Arg(0) != 0) memory.Write32(c.Arg(0), found ? (uint)value.Length + 1 : 0);
                if (!found) { if (c.Arg(1) != 0 && c.Arg(2) != 0) memory.Write8(c.Arg(1), 0); return 0; }
                if (c.Arg(2) < value.Length + 1) return Erange;
                WriteText(c.Arg(1), value, false);
                return 0;
            });
            C(i, "setlocale", 2, c => localeName);
            C(i, "_wsetlocale", 2, c => localeNameWide);
            C(i, "localeconv", 0, c => lconv);
            C(i, "_create_locale", 2, c => crtScratch);
            C(i, "_free_locale", 1, c => 0);
            C(i, "_get_current_locale", 0, c => crtScratch);
            C(i, "_getmbcp", 0, c => 0);
            C(i, "_setmbcp", 1, c => 0);
            C(i, "system", 1, c => c.Arg(0) == 0 ? 0u : unchecked((uint)-1));
            C(i, "_getpid", 0, c => GuestProcess.ProcessId);
        }

        private uint GetEnv(string name, bool wide)
        {
            if (!environment.TryGetValue(name, out var value)) return 0;
            var key = (wide ? "W:" : "A:") + name;
            if (envCopies.TryGetValue(key, out var copy) && ReadText(copy, wide) == value) return copy;
            copy = heap.Alloc((uint)(value.Length + 1) * (wide ? 2u : 1u));
            WriteText(copy, value, wide);
            envCopies[key] = copy;
            return copy;
        }

        private uint PutEnv(string assignment)
        {
            var eq = assignment.IndexOf('=');
            if (eq <= 0) { SetErrno(Einval); return unchecked((uint)-1); }
            var name = assignment.Substring(0, eq);
            var value = assignment.Substring(eq + 1);
            SetEnvironment(name, value.Length == 0 ? null : value);
            BuildEnvironment();
            return 0;
        }

        // --- time -------------------------------------------------------------------------

        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private long UnixNow => (long)(UtcNow - Epoch).TotalSeconds;

        private void InstallCrtTime(GuestImports i)
        {
            C(i, "time", 1, c => { var t = (uint)UnixNow; if (c.Arg(0) != 0) memory.Write32(c.Arg(0), t); return t; });
            C(i, "_time32", 1, c => { var t = (uint)UnixNow; if (c.Arg(0) != 0) memory.Write32(c.Arg(0), t); return t; });
            C(i, "_time64", 1, c => { var t = (ulong)UnixNow; if (c.Arg(0) != 0) memory.Write64(c.Arg(0), t); return t; });
            C(i, "clock", 0, c => DeterministicTime ? 1000u : (uint)crtClock.ElapsedMilliseconds);
            C(i, "difftime", 2, c => ReturnDouble((int)c.Arg(0) - (double)(int)c.Arg(1)));
            C(i, "_difftime32", 2, c => ReturnDouble((int)c.Arg(0) - (double)(int)c.Arg(1)));
            C(i, "_difftime64", 4, c => ReturnDouble((long)c.Arg64(0) - (double)(long)c.Arg64(2)));
            C(i, "gmtime", 1, c => WriteTm((int)memory.Read32(c.Arg(0))));
            C(i, "localtime", 1, c => WriteTm((int)memory.Read32(c.Arg(0))));
            C(i, "_gmtime32", 1, c => WriteTm((int)memory.Read32(c.Arg(0))));
            C(i, "_localtime32", 1, c => WriteTm((int)memory.Read32(c.Arg(0))));
            C(i, "_gmtime64", 1, c => WriteTm((long)memory.Read64(c.Arg(0))));
            C(i, "_localtime64", 1, c => WriteTm((long)memory.Read64(c.Arg(0))));
            C(i, "mktime", 1, c => (uint)MakeTime(c.Arg(0)));
            C(i, "_mktime32", 1, c => (uint)MakeTime(c.Arg(0)));
            C(i, "_mkgmtime", 1, c => (uint)MakeTime(c.Arg(0)));
            C(i, "_mktime64", 1, c => (ulong)MakeTime(c.Arg(0)));
            C(i, "_mkgmtime64", 1, c => (ulong)MakeTime(c.Arg(0)));
            C(i, "asctime", 1, c => AscTime(ReadTm(c.Arg(0))));
            C(i, "ctime", 1, c => AscTime(Epoch.AddSeconds((int)memory.Read32(c.Arg(0)))));
            C(i, "_ctime64", 1, c => AscTime(Epoch.AddSeconds((long)memory.Read64(c.Arg(0)))));
            C(i, "_ftime", 1, c => { FTime(c.Arg(0), false); return 0; });
            C(i, "_ftime32", 1, c => { FTime(c.Arg(0), false); return 0; });
            C(i, "_ftime64", 1, c => { FTime(c.Arg(0), true); return 0; });
            C(i, "_tzset", 0, c => 0);
            C(i, "_strdate", 1, c => { WriteText(c.Arg(0), UtcNow.ToString("MM/dd/yy", CultureInfo.InvariantCulture), false); return c.Arg(0); });
            C(i, "_strtime", 1, c => { WriteText(c.Arg(0), UtcNow.ToString("HH:mm:ss", CultureInfo.InvariantCulture), false); return c.Arg(0); });
            C(i, "strftime", 4, c => StrFTime(c.Arg(0), c.Arg(1), ReadText(c.Arg(2), false), c.Arg(3), false));
            C(i, "wcsftime", 4, c => StrFTime(c.Arg(0), c.Arg(1), ReadText(c.Arg(2), true), c.Arg(3), true));
            C(i, "_getsystime", 1, c => { WriteTm(UnixNow, c.Arg(0)); return (uint)UtcNow.Millisecond; });
        }

        /// <summary>A struct tm in the shared buffer (as gmtime/localtime return); the guest's time zone is UTC.</summary>
        private uint WriteTm(long seconds, uint into = 0)
        {
            DateTime t;
            try { t = Epoch.AddSeconds(seconds); }
            catch (ArgumentOutOfRangeException) { SetErrno(Einval); return 0; }
            if (seconds < 0) { SetErrno(Einval); return 0; }
            var p = into != 0 ? into : tmBuffer;
            memory.Write32(p, (uint)t.Second);
            memory.Write32(p + 4, (uint)t.Minute);
            memory.Write32(p + 8, (uint)t.Hour);
            memory.Write32(p + 12, (uint)t.Day);
            memory.Write32(p + 16, (uint)(t.Month - 1));
            memory.Write32(p + 20, (uint)(t.Year - 1900));
            memory.Write32(p + 24, (uint)t.DayOfWeek);
            memory.Write32(p + 28, (uint)(t.DayOfYear - 1));
            memory.Write32(p + 32, 0);
            return p;
        }

        private DateTime ReadTm(uint p)
        {
            var t = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            try
            {
                t = t.AddYears((int)memory.Read32(p + 20)).AddMonths((int)memory.Read32(p + 16))
                     .AddDays((int)memory.Read32(p + 12) - 1).AddHours((int)memory.Read32(p + 8))
                     .AddMinutes((int)memory.Read32(p + 4)).AddSeconds((int)memory.Read32(p));
            }
            catch (ArgumentOutOfRangeException) { }
            return t;
        }

        /// <summary>mktime: normalises the struct in place (as C requires) and returns seconds since 1970, or -1.</summary>
        private long MakeTime(uint p)
        {
            var t = ReadTm(p);
            if (t < Epoch) return -1;
            var seconds = (long)(t - Epoch).TotalSeconds;
            WriteTm(seconds, p);
            return seconds;
        }

        private uint AscTime(DateTime t)
        {
            var text = t.ToString("ddd MMM dd HH:mm:ss yyyy", CultureInfo.InvariantCulture) + "\n";
            WriteText(asctimeBuffer, text, false);
            return asctimeBuffer;
        }

        private void FTime(uint p, bool wide64)
        {
            var now = UtcNow;
            var seconds = (long)(now - Epoch).TotalSeconds;
            if (wide64)
            {
                memory.Write64(p, (ulong)seconds);
                memory.Write16(p + 8, (ushort)now.Millisecond);
                memory.Write16(p + 10, 0);
                memory.Write16(p + 12, 0);
            }
            else
            {
                memory.Write32(p, (uint)seconds);
                memory.Write16(p + 4, (ushort)now.Millisecond);
                memory.Write16(p + 6, 0);
                memory.Write16(p + 8, 0);
            }
        }

        private uint StrFTime(uint buffer, uint max, string format, uint tm, bool wide)
        {
            var t = ReadTm(tm);
            var s = new StringBuilder();
            for (var n = 0; n < format.Length; n++)
            {
                if (format[n] != '%' || n + 1 >= format.Length) { s.Append(format[n]); continue; }
                var ch = format[++n];
                var alt = ch == '#';
                if (alt && n + 1 < format.Length) ch = format[++n];
                string two(int v) => alt ? v.ToString(CultureInfo.InvariantCulture) : v.ToString("00", CultureInfo.InvariantCulture);
                switch (ch)
                {
                    case 'a': s.Append(t.ToString("ddd", CultureInfo.InvariantCulture)); break;
                    case 'A': s.Append(t.ToString("dddd", CultureInfo.InvariantCulture)); break;
                    case 'b': case 'h': s.Append(t.ToString("MMM", CultureInfo.InvariantCulture)); break;
                    case 'B': s.Append(t.ToString("MMMM", CultureInfo.InvariantCulture)); break;
                    case 'c': s.Append(t.ToString(alt ? "dddd, MMMM dd, yyyy HH:mm:ss" : "MM/dd/yy HH:mm:ss", CultureInfo.InvariantCulture)); break;
                    case 'd': s.Append(two(t.Day)); break;
                    case 'H': s.Append(two(t.Hour)); break;
                    case 'I': s.Append(two(t.Hour % 12 == 0 ? 12 : t.Hour % 12)); break;
                    case 'j': s.Append(alt ? t.DayOfYear.ToString(CultureInfo.InvariantCulture) : t.DayOfYear.ToString("000", CultureInfo.InvariantCulture)); break;
                    case 'm': s.Append(two(t.Month)); break;
                    case 'M': s.Append(two(t.Minute)); break;
                    case 'p': s.Append(t.Hour < 12 ? "AM" : "PM"); break;
                    case 'S': s.Append(two(t.Second)); break;
                    case 'U': s.Append(two((t.DayOfYear + 6 - (int)t.DayOfWeek) / 7)); break;
                    case 'w': s.Append((int)t.DayOfWeek); break;
                    case 'W': s.Append(two((t.DayOfYear + 6 - ((int)t.DayOfWeek + 6) % 7) / 7)); break;
                    case 'x': s.Append(t.ToString(alt ? "dddd, MMMM dd, yyyy" : "MM/dd/yy", CultureInfo.InvariantCulture)); break;
                    case 'X': s.Append(t.ToString("HH:mm:ss", CultureInfo.InvariantCulture)); break;
                    case 'y': s.Append(two(t.Year % 100)); break;
                    case 'Y': s.Append(t.Year); break;
                    case 'z': case 'Z': s.Append("UTC"); break;
                    case '%': s.Append('%'); break;
                    default: s.Append('%').Append(ch); break;
                }
            }
            var text = s.ToString();
            if (text.Length + 1 > max) { SetErrno(Erange); return 0; }
            WriteText(buffer, text, wide);
            return (uint)text.Length;
        }

        // --- threads, errno, setjmp -----------------------------------------------------------

        private void InstallCrtThreads(GuestImports i)
        {
            var createThread = process.Imports.Bind("kernel32.dll", "CreateThread", -1);
            // _beginthreadex has CreateThread's arguments in CreateThread's order.
            C(i, "_beginthreadex", 6, c =>
            {
                TailCall(c, 0, createThread, c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3), c.Arg(4), c.Arg(5));
                return 0;
            });
            C(i, "_beginthread", 3, c =>
            {
                TailCall(c, 0, createThread, 0, c.Arg(1), c.Arg(0), c.Arg(2), 0, 0);
                return 0;
            });
            C(i, "_endthreadex", 1, c => { process.ExitCurrentThread(c.Arg(0)); return 0; });
            C(i, "_endthread", 0, c => { process.ExitCurrentThread(0); return 0; });
            C(i, "__threadid", 0, c => process.CurrentThread.Id);
            C(i, "__threadhandle", 0, c => PseudoCurrentThread);
            C(i, "_errno", 0, c => ErrnoSlot());
            C(i, "__doserrno", 0, c => ErrnoSlot() + 4);
            C(i, "_get_errno", 1, c => { memory.Write32(c.Arg(0), memory.Read32(ErrnoSlot())); return 0; });
            C(i, "_set_errno", 1, c => { SetErrno(c.Arg(0)); return 0; });
            C(i, "_get_doserrno", 1, c => { memory.Write32(c.Arg(0), memory.Read32(ErrnoSlot() + 4)); return 0; });
            C(i, "_set_doserrno", 1, c => { memory.Write32(ErrnoSlot() + 4, c.Arg(0)); return 0; });
            var sleep = process.Imports.Bind("kernel32.dll", "Sleep", -1);
            C(i, "_sleep", 1, c => { TailCall(c, 0, sleep, c.Arg(0)); return 0; });

            C(i, "_setjmp", 1, c => SetJmp(c));
            C(i, "_setjmp3", 2, c => SetJmp(c));
            C(i, "longjmp", 2, c => LongJmp(c.Arg(0), c.Arg(1)));
            C(i, "_longjmpex", 2, c => LongJmp(c.Arg(0), c.Arg(1)));
        }

        // jmp_buf (x86): Ebp, Ebx, Edi, Esi, Esp, Eip, Registration, TryLevel, Cookie, UnwindFunc, UnwindData[6].
        private uint SetJmp(GuestCall c)
        {
            var cpu = process.Cpu;
            var buf = c.Arg(0);
            memory.Write32(buf, cpu.Ebp);
            memory.Write32(buf + 4, cpu.Ebx);
            memory.Write32(buf + 8, cpu.Edi);
            memory.Write32(buf + 12, cpu.Esi);
            memory.Write32(buf + 16, c.ArgBase);            // ESP once the call has returned (cdecl)
            memory.Write32(buf + 20, c.ReturnAddress);
            memory.Write32(buf + 24, memory.Read32(process.TebBase));   // FS:[0], the SEH chain head
            memory.Write32(buf + 28, 0xFFFFFFFF);
            memory.Write32(buf + 32, 0x56433230);           // "VC20": the setjmp3 cookie
            return 0;
        }

        private ulong LongJmp(uint buf, uint value)
        {
            var cpu = process.Cpu;
            cpu.Ebp = memory.Read32(buf);
            cpu.Ebx = memory.Read32(buf + 4);
            cpu.Edi = memory.Read32(buf + 8);
            cpu.Esi = memory.Read32(buf + 12);
            cpu.Esp = memory.Read32(buf + 16);
            cpu.Eip = memory.Read32(buf + 20);
            memory.Write32(process.TebBase, memory.Read32(buf + 24));
            cpu.Eax = value == 0 ? 1 : value;
            process.Jumped();
            return 0;
        }
    }
}
