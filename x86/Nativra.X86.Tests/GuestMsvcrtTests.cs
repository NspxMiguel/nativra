using System;
using System.IO;
using System.Text;
using Nativra.X86.Cpu;
using Nativra.X86.Loader;
using Xunit;

namespace Nativra.X86.Tests
{
    /// <summary>
    /// msvcrt.dll as the guest reaches it: every call goes through its import
    /// sentinel with cdecl arguments on the guest stack, and the calls that
    /// run guest code (_initterm, atexit/exit, qsort, setjmp/longjmp) run
    /// real x86 routines.
    /// </summary>
    public sealed class GuestMsvcrtTests : IDisposable
    {
        private const uint Code = 0x00600000, Data = 0x00601000;
        private readonly string work = Path.Combine(Path.GetTempPath(), "nativra-crt-" + Guid.NewGuid().ToString("N"));
        private readonly GuestProcess p;
        private readonly GuestKernel k;
        private readonly StringBuilder log = new StringBuilder();

        public GuestMsvcrtTests()
        {
            Directory.CreateDirectory(work);
            p = new GuestProcess(new GuestMemory(native: true), useJit: false);
            k = new GuestKernel(p)
            {
                ExePath = "C:\\game\\game.exe",
                Files = new HostFolderFiles("C:\\game", work),
                DeterministicTime = true,
                Log = line => log.AppendLine(line),
            };
            k.SetCommandLine("game.exe -window \"C:\\My Saves\\slot 1\" a\\\\\"b");
            k.Install();
            p.Memory.Map(Code, 0x3000);
        }

        public void Dispose()
        {
            p.Dispose();
            try { Directory.Delete(work, true); } catch (IOException) { }
        }

        private uint Crt(string function, params uint[] args)
        {
            var result = p.Call(p.Imports.Bind("msvcrt.dll", function, -1), out var eax, 10_000_000, args);
            Assert.True(result.Ok, $"{function} stopped as {result}");
            return eax;
        }

        private uint Str(string text)
        {
            var bytes = Encoding.GetEncoding(28591).GetBytes(text);
            var ptr = k.Heap.Alloc((uint)bytes.Length + 1, zero: true);
            p.Memory.WriteBytes(ptr, bytes);
            return ptr;
        }

        private uint WStr(string text)
        {
            var ptr = k.Heap.Alloc((uint)(text.Length + 1) * 2, zero: true);
            p.Memory.WriteUnicode(ptr, text);
            return ptr;
        }

        private static uint Lo(double v) => (uint)BitConverter.DoubleToInt64Bits(v);
        private static uint Hi(double v) => (uint)(BitConverter.DoubleToInt64Bits(v) >> 32);

        private string Sprintf(string format, params uint[] args)
        {
            var buffer = k.Heap.Alloc(512);
            var all = new uint[args.Length + 2];
            all[0] = buffer;
            all[1] = Str(format);
            Array.Copy(args, 0, all, 2, args.Length);
            var n = Crt("sprintf", all);
            var text = p.Memory.ReadAnsi(buffer);
            Assert.Equal((uint)text.Length, n);
            return text;
        }

        [Fact]
        public void PrintfFollowsMsvcrtFormatting()
        {
            Assert.Equal("[   42|-7  |+5|002a|0X2A|017]", Sprintf("[%5d|%-4d|%+d|%04x|%#X|%#o]", 42, unchecked((uint)-7), 5, 42, 42, 15));
            Assert.Equal("3.14|1.500000e+002|0.0001|1e+020|  2.50", Sprintf("%.2f|%e|%g|%g|%6.2f",
                Lo(3.14159), Hi(3.14159), Lo(150), Hi(150), Lo(0.0001), Hi(0.0001), Lo(1e20), Hi(1e20), Lo(2.5), Hi(2.5)));
            Assert.Equal("abc|(null)|ab|x|W|%", Sprintf("%s|%s|%.2s|%c|%S|%%", Str("abc"), 0, Str("abc"), 'x', WStr("W")));
            Assert.Equal("-9000000000|0012ABCD", Sprintf("%I64d|%p", unchecked((uint)-9000000000L), unchecked((uint)(-9000000000L >> 32)), 0x12ABCD));
            Assert.Equal("1.#INF00|-1.#INF", Sprintf("%f|%g", Lo(double.PositiveInfinity), Hi(double.PositiveInfinity), Lo(double.NegativeInfinity), Hi(double.NegativeInfinity)));

            // %n stores the count so far.
            var count = k.Heap.Alloc(4);
            Sprintf("abc%n!", count);
            Assert.Equal(3u, p.Memory.Read32(count));

            // _snprintf: -1 and no terminator when cut.
            var buffer = k.Heap.Alloc(16);
            p.Memory.WriteBytes(buffer, Encoding.ASCII.GetBytes("zzzzzzzzzz"));
            Assert.Equal(0xFFFFFFFFu, Crt("_snprintf", buffer, 4, Str("%d"), 123456));
            Assert.Equal("1234zzzzzz", p.Memory.ReadAnsi(buffer));

            // msvcrt's swprintf has no count.
            var wide = k.Heap.Alloc(64);
            Assert.Equal(5u, Crt("swprintf", wide, WStr("%s-%d"), WStr("ab"), 12));
            Assert.Equal("ab-12", p.Memory.ReadUnicode(wide));
        }

        [Fact]
        public void ScanfParsesFieldsAndStopsAtAMismatch()
        {
            var i = k.Heap.Alloc(4);
            var f = k.Heap.Alloc(4);
            var d = k.Heap.Alloc(8);
            var s = k.Heap.Alloc(32);
            var x = k.Heap.Alloc(4);
            var set = k.Heap.Alloc(32);
            Assert.Equal(6u, Crt("sscanf", Str("  -12 2.5 3.25e2 word 0x1f abcDEF"), Str("%d %f %lf %s %x %[a-z]"), i, f, d, s, x, set));
            Assert.Equal(unchecked((uint)-12), p.Memory.Read32(i));
            Assert.Equal(2.5f, BitConverter.Int32BitsToSingle((int)p.Memory.Read32(f)));
            Assert.Equal(325.0, BitConverter.Int64BitsToDouble((long)p.Memory.Read64(d)));
            Assert.Equal("word", p.Memory.ReadAnsi(s));
            Assert.Equal(31u, p.Memory.Read32(x));
            Assert.Equal("abc", p.Memory.ReadAnsi(set));

            Assert.Equal(1u, Crt("sscanf", Str("7 x"), Str("%d %d"), i, x));
            Assert.Equal(0xFFFFFFFFu, Crt("sscanf", Str("   "), Str("%d"), i));
        }

        [Fact]
        public void StringsAndConversionsMatchTheCLibrary()
        {
            Assert.Equal(5u, Crt("strlen", Str("hello")));
            Assert.Equal(0xFFFFFFFFu, Crt("strcmp", Str("abc"), Str("abd")));
            Assert.Equal(0u, Crt("_stricmp", Str("Hello"), Str("hELLO")));
            Assert.Equal(1u, Crt("strcmp", Str("\u00E9"), Str("z")));   // unsigned bytes: 0xE9 > 'z'
            var hay = Str("find the needle here");
            Assert.Equal(hay + 9, Crt("strstr", hay, Str("needle")));

            var padded = k.Heap.Alloc(8);
            p.Memory.WriteBytes(padded, Encoding.ASCII.GetBytes("XXXXXXXX"));
            Crt("strncpy", padded, Str("ab"), 5);
            Assert.Equal(new byte[] { (byte)'a', (byte)'b', 0, 0, 0, (byte)'X' }, p.Memory.ReadBytes(padded, 6));

            var text = Str("a,b,,c");
            var comma = Str(",");
            Assert.Equal("a", p.Memory.ReadAnsi(Crt("strtok", text, comma)));
            Assert.Equal("b", p.Memory.ReadAnsi(Crt("strtok", 0, comma)));
            Assert.Equal("c", p.Memory.ReadAnsi(Crt("strtok", 0, comma)));
            Assert.Equal(0u, Crt("strtok", 0, comma));

            var end = k.Heap.Alloc(4);
            var number = Str("  0x1Fzz");
            Assert.Equal(31u, Crt("strtol", number, end, 0));
            Assert.Equal(number + 6, p.Memory.Read32(end));
            Assert.Equal(0x7FFFFFFFu, Crt("strtol", Str("99999999999"), 0, 10));
            Assert.Equal(34u, p.Memory.Read32(Crt("_errno")));   // ERANGE
            Assert.Equal(0xFFFFFFFFu, Crt("strtoul", Str("-1"), 0, 10));
            Assert.Equal(unchecked((uint)-42), Crt("atoi", Str(" -42abc")));

            var dbl = Str("1.5e3xyz");
            Crt("strtod", dbl, end);
            Assert.Equal(1500.0, p.Interpreter.Fpu.St(0));
            p.Interpreter.Fpu.Pop();
            Assert.Equal(dbl + 5, p.Memory.Read32(end));

            var digits = k.Heap.Alloc(40);
            Crt("_itoa", unchecked((uint)-255), digits, 16);
            Assert.Equal("ffffff01", p.Memory.ReadAnsi(digits));
            Crt("_itoa", unchecked((uint)-255), digits, 10);
            Assert.Equal("-255", p.Memory.ReadAnsi(digits));
        }

        [Fact]
        public void MemoryCallsAllocateFromTheGuestHeap()
        {
            var a = Crt("malloc", 100);
            Assert.NotEqual(0u, a);
            var z = Crt("calloc", 10, 10);
            Assert.All(p.Memory.ReadBytes(z, 100), b => Assert.Equal(0, b));
            p.Memory.Write32(a, 0xCAFEBABE);
            var b2 = Crt("realloc", a, 5000);
            Assert.Equal(0xCAFEBABEu, p.Memory.Read32(b2));
            Assert.True(Crt("_msize", b2) >= 5000);
            Crt("free", b2);
            var aligned = Crt("_aligned_malloc", 100, 64);
            Assert.Equal(0u, aligned % 64);
            Crt("_aligned_free", aligned);
            Assert.NotEqual(0u, Crt("??2@YAPAXI@Z", 16));   // operator new
        }

        [Fact]
        public void TextStreamsTranslateLineEndsAndKeepTheFileStruct()
        {
            var f = Crt("fopen", Str("save.txt"), Str("w"));
            Assert.NotEqual(0u, f);
            Crt("fprintf", f, Str("score=%d\nname=%s\n"), 1200, Str("ada"));
            Crt("fputs", Str("tail"), f);
            Assert.Equal(0u, Crt("fclose", f));
            Assert.Equal("score=1200\r\nname=ada\r\ntail", File.ReadAllText(Path.Combine(work, "save.txt")));

            f = Crt("fopen", Str("C:\\game\\save.txt"), Str("r"));
            var line = k.Heap.Alloc(64);
            Assert.Equal(line, Crt("fgets", line, 64, f));
            Assert.Equal("score=1200\n", p.Memory.ReadAnsi(line));
            var value = k.Heap.Alloc(4);
            var name = k.Heap.Alloc(16);
            Assert.Equal(1u, Crt("fscanf", f, Str("name=%s"), name));
            Assert.Equal("ada", p.Memory.ReadAnsi(name));
            Assert.Equal((uint)'\n', Crt("fgetc", f));
            Assert.Equal((uint)'t', Crt("getc", f));
            Assert.Equal((uint)'t', Crt("ungetc", 't', f));
            var rest = k.Heap.Alloc(16);
            Assert.Equal(4u, Crt("fread", rest, 1, 16, f));
            Assert.Equal("tail", p.Memory.ReadAnsi(rest));
            Assert.NotEqual(0u, Crt("feof", f));
            Assert.NotEqual(0u, p.Memory.Read32(f + 12) & 0x10);   // _IOEOF in the struct, for the feof macro
            Assert.Equal(0u, p.Memory.Read32(f + 4));              // _cnt stays 0 for the getc macro
            Crt("fclose", f);

            // Binary: no translation; seek and tell.
            f = Crt("fopen", Str("save.txt"), Str("rb"));
            Assert.Equal(0u, Crt("fseek", f, 0, 2));
            Assert.Equal(26u, Crt("ftell", f));
            Crt("rewind", f);
            Assert.Equal(12u, Crt("fread", rest, 1, 12, f));
            Assert.Equal((byte)'\r', p.Memory.Read8(rest + 10));
            Crt("fclose", f);

            Assert.Equal(0u, Crt("fopen", Str("missing.txt"), Str("r")));
            Assert.Equal(2u, p.Memory.Read32(Crt("_errno")));   // ENOENT

            // stdout reaches the log a line at a time.
            Crt("printf", Str("hello %s\n"), Str("log"));
            Assert.Contains("hello log", log.ToString());
        }

        [Fact]
        public void StatAndFindFirstSeeTheGameFolder()
        {
            File.WriteAllBytes(Path.Combine(work, "a.pak"), new byte[1234]);
            File.WriteAllBytes(Path.Combine(work, "b.pak"), new byte[1]);
            var st = k.Heap.Alloc(64);
            Assert.Equal(0u, Crt("_stat", Str("a.pak"), st));
            Assert.Equal(1234u, p.Memory.Read32(st + 20));
            Assert.Equal(0x8000u, p.Memory.Read16(st + 6) & 0xF000u);
            Assert.Equal(0xFFFFFFFFu, Crt("_stat", Str("nope.pak"), st));

            var data = k.Heap.Alloc(300);
            var handle = Crt("_findfirst", Str("*.pak"), data);
            Assert.NotEqual(0xFFFFFFFFu, handle);
            var names = p.Memory.ReadAnsi(data + 20);
            Assert.Equal(0u, Crt("_findnext", handle, data));
            names += "," + p.Memory.ReadAnsi(data + 20);
            Assert.Equal(0xFFFFFFFFu, Crt("_findnext", handle, data));
            Assert.Equal(0u, Crt("_findclose", handle));
            Assert.Contains("a.pak", names);
            Assert.Contains("b.pak", names);
        }

        [Fact]
        public void StartupDataAndArgumentsAreThere()
        {
            // A data import's IAT slot holds the variable's address.
            var iob = p.Imports.Bind("msvcrt.dll", "_iob", -1);
            Assert.Equal(1u, p.Memory.Read32(iob + 32 + 16));     // _iob[1]._file == 1
            var pctype = p.Memory.Read32(p.Imports.Bind("msvcrt.dll", "_pctype", -1));
            Assert.Equal(0x181, p.Memory.Read16(pctype + 'A' * 2));   // _UPPER | _HEX | _ALPHA
            Assert.Equal(0x84, p.Memory.Read16(pctype + '7' * 2));
            Assert.Equal(0u, p.Memory.Read16(pctype - 2));           // [-1]: EOF

            uint argc = k.Heap.Alloc(4), argv = k.Heap.Alloc(4), env = k.Heap.Alloc(4), info = k.Heap.Alloc(4);
            Crt("__getmainargs", argc, argv, env, 0, info);
            Assert.Equal(4u, p.Memory.Read32(argc));
            var args = p.Memory.Read32(argv);
            Assert.Equal("-window", p.Memory.ReadAnsi(p.Memory.Read32(args + 4)));
            Assert.Equal("C:\\My Saves\\slot 1", p.Memory.ReadAnsi(p.Memory.Read32(args + 8)));
            Assert.Equal("a\\b", p.Memory.ReadAnsi(p.Memory.Read32(args + 12)));   // two backslashes and a quote: one backslash, and the quote opens
            Assert.Equal(0u, p.Memory.Read32(args + 16));
            Assert.Equal("C:\\Windows", p.Memory.ReadAnsi(Crt("getenv", Str("windir"))));
            Assert.Equal(0u, Crt("_putenv", Str("GAME_MODE=fast")));
            Assert.Equal("fast", p.Memory.ReadAnsi(Crt("getenv", Str("game_mode"))));
        }

        // f1: inc dword [0x601000]; ret      f2: add dword [0x601000], 10; ret
        private static readonly byte[] Counters =
        {
            0xFF, 0x05, 0x00, 0x10, 0x60, 0x00, 0xC3,
            0x83, 0x05, 0x00, 0x10, 0x60, 0x00, 0x0A, 0xC3,
        };

        [Fact]
        public void InittermAndExitRunGuestFunctionsInOrder()
        {
            p.Memory.WriteBytes(Code, Counters);
            var table = Data + 0x10;
            p.Memory.Write32(table, Code);
            p.Memory.Write32(table + 4, 0);          // empty slots are skipped
            p.Memory.Write32(table + 8, Code + 7);
            Crt("_initterm", table, table + 12);
            Assert.Equal(11u, p.Memory.Read32(Data));

            // exit runs the atexit functions, last first, then ends the process.
            Crt("atexit", Code + 7);
            Crt("atexit", Code);
            var result = p.Call(p.Imports.Bind("msvcrt.dll", "exit", -1), out _, 1_000_000, 5);
            Assert.Equal(GuestStop.Exited, result.Stop);
            Assert.Equal(5u, result.ExitCode);
            Assert.Equal(22u, p.Memory.Read32(Data));
        }

        [Fact]
        public void LongjmpReturnsToSetjmpWithTheValue()
        {
            p.Memory.Write32(Data, p.Imports.Bind("msvcrt.dll", "_setjmp", -1));
            p.Memory.Write32(Data + 4, p.Imports.Bind("msvcrt.dll", "longjmp", -1));
            p.Memory.WriteBytes(Code, new byte[]
            {
                0x68, 0x00, 0x11, 0x60, 0x00,             // push jmp_buf
                0xFF, 0x15, 0x00, 0x10, 0x60, 0x00,       // call [_setjmp]
                0x83, 0xC4, 0x04,                         // add esp, 4
                0x85, 0xC0,                               // test eax, eax
                0x75, 0x0D,                               // jnz done
                0x6A, 0x07,                               // push 7
                0x68, 0x00, 0x11, 0x60, 0x00,             // push jmp_buf
                0xFF, 0x15, 0x04, 0x10, 0x60, 0x00,       // call [longjmp]
                0xC3,                                     // done: ret
            });
            var esp = p.Cpu.Esp;
            Assert.True(p.Call(Code, out var eax, 1_000_000).Ok);
            Assert.Equal(7u, eax);
            Assert.Equal(esp, p.Cpu.Esp);
        }

        [Fact]
        public void ExceptHandler3RunsTheFilterAndTheExceptBlock()
        {
            // A VC-style __try/__except frame registered with _except_handler3;
            // the __try body raises, the filter answers EXCEPTION_EXECUTE_HANDLER,
            // and the __except block returns 42.
            p.Memory.Write32(Data, p.Imports.Bind("msvcrt.dll", "_except_handler3", -1));
            p.Memory.Write32(Data + 4, p.Imports.Bind("kernel32.dll", "RaiseException", -1));
            p.Memory.Write32(Data + 0x40, 0xFFFFFFFF);    // scope 0: enclosing level -1,
            p.Memory.Write32(Data + 0x44, Code + 86);     //   filter,
            p.Memory.Write32(Data + 0x48, Code + 64);     //   __except block
            p.Memory.WriteBytes(Code, new byte[]
            {
                0x55, 0x8B, 0xEC,                               // push ebp; mov ebp, esp
                0x6A, 0xFF,                                     // push -1 (try level)
                0x68, 0x40, 0x10, 0x60, 0x00,                   // push scope table
                0xFF, 0x35, 0x00, 0x10, 0x60, 0x00,             // push [_except_handler3]
                0x64, 0xFF, 0x35, 0x00, 0x00, 0x00, 0x00,       // push fs:[0]
                0x64, 0x89, 0x25, 0x00, 0x00, 0x00, 0x00,       // mov fs:[0], esp
                0x83, 0xEC, 0x08,                               // sub esp, 8
                0x89, 0x65, 0xE8,                               // mov [ebp-18h], esp
                0xC7, 0x45, 0xFC, 0x00, 0x00, 0x00, 0x00,       // mov dword [ebp-4], 0: enter __try
                0x6A, 0x00, 0x6A, 0x00, 0x6A, 0x00,             // RaiseException(0xE0000001, 0, 0, 0)
                0x68, 0x01, 0x00, 0x00, 0xE0,
                0xFF, 0x15, 0x04, 0x10, 0x60, 0x00,
                0x33, 0xC0,                                     // xor eax, eax (not reached)
                0xEB, 0x08,                                     // jmp out
                0x8B, 0x65, 0xE8,                               // __except: mov esp, [ebp-18h]
                0xB8, 0x2A, 0x00, 0x00, 0x00,                   // mov eax, 42
                0x8B, 0x4D, 0xF0,                               // out: mov ecx, [ebp-10h]
                0x64, 0x89, 0x0D, 0x00, 0x00, 0x00, 0x00,       // mov fs:[0], ecx
                0x8B, 0xE5, 0x5D, 0xC3,                         // mov esp, ebp; pop ebp; ret
                0xB8, 0x01, 0x00, 0x00, 0x00, 0xC3,             // filter: return EXCEPTION_EXECUTE_HANDLER
            });
            var chain = p.Memory.Read32(p.TebBase);
            var esp = p.Cpu.Esp;
            var result = p.Call(Code, out var eax, 1_000_000);
            Assert.True(result.Ok, result.ToString());
            Assert.Equal(42u, eax);
            Assert.Equal(chain, p.Memory.Read32(p.TebBase));
            Assert.Equal(esp, p.Cpu.Esp);
        }

        [Fact]
        public void QsortCallsTheGuestComparator()
        {
            // cmp(a, b): return *a - *b
            p.Memory.WriteBytes(Code, new byte[] { 0x8B, 0x44, 0x24, 0x04, 0x8B, 0x00, 0x8B, 0x4C, 0x24, 0x08, 0x2B, 0x01, 0xC3 });
            uint[] values = { 5, 3, 9, 1, 7, 3 };
            var array = k.Heap.Alloc(24);
            for (var n = 0; n < values.Length; n++) p.Memory.Write32(array + (uint)n * 4, values[n]);
            Crt("qsort", array, 6, 4, Code);
            var sorted = new uint[6];
            for (var n = 0; n < 6; n++) sorted[n] = p.Memory.Read32(array + (uint)n * 4);
            Assert.Equal(new uint[] { 1, 3, 3, 5, 7, 9 }, sorted);
            var key = k.Heap.Alloc(4);
            p.Memory.Write32(key, 7);
            Assert.Equal(array + 16, Crt("bsearch", key, array, 6, 4, Code));
        }

        [Fact]
        public void X87IntrinsicsTakeTheirOperandsFromTheFpuStack()
        {
            var fpu = p.Interpreter.Fpu;
            fpu.Push(2);
            fpu.Push(10);
            Crt("_CIpow");
            Assert.Equal(1024.0, fpu.St(0));
            fpu.Pop();

            fpu.Push(-3.7);
            Assert.Equal(unchecked((uint)-3), Crt("_ftol"));
            fpu.Push(5e9);
            Crt("_ftol2");
            Assert.Equal(1u, p.Cpu.Edx);   // 5e9 needs the high half

            Crt("sqrt", Lo(81), Hi(81));
            Assert.Equal(9.0, fpu.St(0));
            fpu.Pop();

            // _controlfp reports and sets the rounding mode.
            Assert.Equal(0x9001Fu, Crt("_controlfp", 0, 0));
            Assert.Equal(0x9031Fu, Crt("_controlfp", 0x300, 0x300));   // _RC_CHOP
            Assert.Equal(0xC00, fpu.Control & 0xC00);
        }
    }
}
