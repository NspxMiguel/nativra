using System;
using System.Collections.Generic;
using Nativra.X86.Cpu;
using Nativra.X86.Loader;
using Xunit;

namespace Nativra.X86.Tests
{
    /// <summary>
    /// Structured exception handling driven by real guest code: frames pushed
    /// on FS:[0], handlers called by RaiseException, a catch that unwinds with
    /// RtlUnwind and continues at its own label. The programs are assembled
    /// (nasm, org 0x600000) from the listings in the comments.
    /// </summary>
    public sealed class GuestSehTests
    {
        private const uint Code = 0x00600000;
        private const uint Data = 0x00601000;   // +0 RaiseException, +4 RtlUnwind, then scratch

        private static GuestProcess Load(byte[] program, bool jit, out GuestKernel kernel)
        {
            var p = new GuestProcess(new GuestMemory(native: jit), useJit: jit);
            Skip.If(jit && !TestHost.CanJit, "JIT needs an x64 host");
            Assert.Equal(jit, p.UsesJit);   // a JIT case must not fall back to the interpreter unnoticed
            kernel = new GuestKernel(p);
            kernel.Install();
            p.Memory.Map(Code, 0x2000);
            p.Memory.WriteBytes(Code, program);
            p.Memory.Write32(Data + 0, p.Imports.Bind("kernel32.dll", "RaiseException", -1));
            p.Memory.Write32(Data + 4, p.Imports.Bind("kernel32.dll", "RtlUnwind", -1));
            return p;
        }

        // AddVectoredExceptionHandler(1, handler) at Data+8, then RaiseException(0xE0001234,
        // flags, 0, 0) with no frame on the chain at all. The handler (at Code+0x40) stores
        // the code at Data+0x10 and returns EXCEPTION_CONTINUE_EXECUTION; the program
        // returns what was stored. Byte 18 is the flags argument.
        private static byte[] VectoredProgram(bool noncontinuable)
        {
            var b = new byte[0x60];
            for (var n = 0; n < b.Length; n++) b[n] = 0xCC;
            byte[] main =
            {
                0x68, 0x40, 0x00, 0x60, 0x00,             // push handler
                0x6A, 0x01,                               // push 1 (first)
                0xFF, 0x15, 0x08, 0x10, 0x60, 0x00,       // call [AddVectoredExceptionHandler]
                0x6A, 0x00, 0x6A, 0x00,                   // push 0 (arguments), push 0 (count)
                0x6A, (byte)(noncontinuable ? 1 : 0),     // push flags
                0x68, 0x34, 0x12, 0x00, 0xE0,             // push 0xE0001234
                0xFF, 0x15, 0x00, 0x10, 0x60, 0x00,       // call [RaiseException]
                0xA1, 0x10, 0x10, 0x60, 0x00,             // mov eax, [Data+0x10]
                0xC3,                                     // ret
            };
            byte[] handler =
            {
                0x8B, 0x44, 0x24, 0x04,                   // mov eax, [esp+4]   EXCEPTION_POINTERS*
                0x8B, 0x00,                               // mov eax, [eax]     ExceptionRecord
                0x8B, 0x00,                               // mov eax, [eax]     ExceptionCode
                0xA3, 0x10, 0x10, 0x60, 0x00,             // mov [Data+0x10], eax
                0xB8, 0xFF, 0xFF, 0xFF, 0xFF,             // mov eax, -1        CONTINUE_EXECUTION
                0xC2, 0x04, 0x00,                         // ret 4
            };
            Array.Copy(main, b, main.Length);
            Array.Copy(handler, 0, b, 0x40, handler.Length);
            return b;
        }

        [SkippableTheory]
        [InlineData(false)]
        [InlineData(true)]
        public void AVectoredHandlerSeesTheExceptionFirstAndCanContinueIt(bool jit)
        {
            var p = Load(VectoredProgram(noncontinuable: false), jit, out var kernel);
            p.Memory.Write32(Data + 8, p.Imports.Bind("kernel32.dll", "AddVectoredExceptionHandler", -1));
            var result = p.Call(Code, out var eax, 100_000);
            Assert.True(result.Ok, result.ToString());
            Assert.Equal(0xE0001234u, eax);
        }

        [SkippableTheory]
        [InlineData(false)]
        [InlineData(true)]
        public void AVectoredHandlerCannotContinueANoncontinuableException(bool jit)
        {
            var p = Load(VectoredProgram(noncontinuable: true), jit, out var kernel);
            p.Memory.Write32(Data + 8, p.Imports.Bind("kernel32.dll", "AddVectoredExceptionHandler", -1));
            var result = p.Call(Code, out _, 100_000);
            Assert.False(result.Ok, "a noncontinuable exception must not resume after RaiseException");
            Assert.Equal(0xE0001234u, p.Memory.Read32(Data + 0x10));   // the handler still saw it first
        }

        // A frame whose handler records the code, clobbers EBX and returns
        // ExceptionContinueExecution: execution resumes after the RaiseException
        // call with the registers of the raise (EBX 0x11111111) restored.
        // Returns code + EBX.
        private static readonly byte[] ContinueProgram =
        {
            0x53, 0x56, 0xBB, 0x11, 0x11, 0x11, 0x11, 0xBE, 0x22, 0x22, 0x22, 0x22, 0x68, 0x44, 0x00, 0x60,
            0x00, 0x64, 0xFF, 0x35, 0x00, 0x00, 0x00, 0x00, 0x64, 0x89, 0x25, 0x00, 0x00, 0x00, 0x00, 0x6A,
            0x00, 0x6A, 0x00, 0x6A, 0x00, 0x68, 0x34, 0x12, 0x00, 0xE0, 0xFF, 0x15, 0x00, 0x10, 0x60, 0x00,
            0xA1, 0x10, 0x10, 0x60, 0x00, 0x01, 0xD8, 0x64, 0x8F, 0x05, 0x00, 0x00, 0x00, 0x00, 0x83, 0xC4,
            0x04, 0x5E, 0x5B, 0xC3, 0x8B, 0x44, 0x24, 0x04, 0x8B, 0x00, 0xA3, 0x10, 0x10, 0x60, 0x00, 0xBB,
            0x99, 0x99, 0x99, 0x99, 0x31, 0xC0, 0xC3,
        };

        // outer frame (catches) -> inner frame (searches) -> RaiseException.
        // The search phase asks inner (count +0x100), then outer, which calls
        // RtlUnwind(outer, after_unwind, record, 0x77): inner runs again with
        // EXCEPTION_UNWINDING (count +0x10000) and is popped; after_unwind pops
        // outer and returns 0x77 + the counts.
        private static readonly byte[] UnwindProgram =
        {
            0x53, 0x56, 0x57, 0x55, 0x89, 0x25, 0x20, 0x10, 0x60, 0x00, 0x68, 0x80, 0x00, 0x60, 0x00, 0x64,
            0xFF, 0x35, 0x00, 0x00, 0x00, 0x00, 0x64, 0x89, 0x25, 0x00, 0x00, 0x00, 0x00, 0x89, 0x25, 0x24,
            0x10, 0x60, 0x00, 0xE8, 0x0A, 0x00, 0x00, 0x00, 0xB8, 0xAD, 0x0B, 0x00, 0x00, 0xE9, 0xA8, 0x00,
            0x00, 0x00, 0x55, 0x89, 0xE5, 0x68, 0x5B, 0x00, 0x60, 0x00, 0x64, 0xFF, 0x35, 0x00, 0x00, 0x00,
            0x00, 0x64, 0x89, 0x25, 0x00, 0x00, 0x00, 0x00, 0x6A, 0x00, 0x6A, 0x00, 0x6A, 0x00, 0x68, 0x63,
            0x73, 0x6D, 0xE0, 0xFF, 0x15, 0x00, 0x10, 0x60, 0x00, 0x0F, 0x0B, 0x8B, 0x44, 0x24, 0x04, 0xF7,
            0x40, 0x04, 0x02, 0x00, 0x00, 0x00, 0x75, 0x0C, 0xFF, 0x05, 0x30, 0x10, 0x60, 0x00, 0xB8, 0x01,
            0x00, 0x00, 0x00, 0xC3, 0xFF, 0x05, 0x34, 0x10, 0x60, 0x00, 0xB8, 0x01, 0x00, 0x00, 0x00, 0xC3,
            0x8B, 0x44, 0x24, 0x04, 0xF7, 0x40, 0x04, 0x06, 0x00, 0x00, 0x00, 0x75, 0x14, 0x6A, 0x77, 0x50,
            0x68, 0xA7, 0x00, 0x60, 0x00, 0xFF, 0x74, 0x24, 0x14, 0xFF, 0x15, 0x04, 0x10, 0x60, 0x00, 0x0F,
            0x0B, 0xB8, 0x01, 0x00, 0x00, 0x00, 0xC3, 0xA3, 0x38, 0x10, 0x60, 0x00, 0xA1, 0x24, 0x10, 0x60,
            0x00, 0x8B, 0x00, 0x64, 0xA3, 0x00, 0x00, 0x00, 0x00, 0x8B, 0x25, 0x20, 0x10, 0x60, 0x00, 0xA1,
            0x38, 0x10, 0x60, 0x00, 0x8B, 0x0D, 0x30, 0x10, 0x60, 0x00, 0xC1, 0xE1, 0x08, 0x01, 0xC8, 0x8B,
            0x0D, 0x34, 0x10, 0x60, 0x00, 0xC1, 0xE1, 0x10, 0x01, 0xC8, 0x5D, 0x5F, 0x5E, 0x5B, 0xC3,
        };

        [SkippableTheory]
        [InlineData(false)]
        [InlineData(true)]
        public void HandlerThatContinuesResumesAfterTheRaiseWithItsRegisters(bool jit)
        {
            var p = Load(ContinueProgram, jit, out var kernel);
            var result = p.Call(Code, out var eax, 100_000);
            Assert.True(result.Ok, result.ToString());
            Assert.Equal(0xE0001234u + 0x11111111u, eax);
            Assert.Equal(0xFFFFFFFFu, p.Memory.Read32(p.TebBase));   // chain restored
            Assert.Equal(new[] { 0xE0001234u }, kernel.ExceptionsRaised);
        }

        [SkippableTheory]
        [InlineData(false)]
        [InlineData(true)]
        public void CatchUnwindsInnerFramesAndContinuesAtItsLabel(bool jit)
        {
            var p = Load(UnwindProgram, jit, out _);
            var result = p.Call(Code, out var eax, 100_000);
            Assert.True(result.Ok, result.ToString());
            Assert.Equal(0x00010177u, eax);   // 0x77, one search call, one unwind call
            Assert.Equal(0xFFFFFFFFu, p.Memory.Read32(p.TebBase));
        }

        // The game's first C++ throw came from a failed GetProcAddress for the
        // processor-group APIs. Its catch rethrew and the CRT then terminated.
        // Resolve the same names from guest code under an SEH frame; a missing
        // export raises an exception instead of silently passing the test.
        [SkippableTheory]
        [InlineData(false)]
        [InlineData(true)]
        public void ProcessorGroupLookupsDoNotEnterTheExceptionPath(bool jit)
        {
            var code = new List<byte>();
            void Emit(params byte[] bytes) => code.AddRange(bytes);
            void Push(uint value)
            {
                Emit(0x68);
                Emit(BitConverter.GetBytes(value));
            }
            int Lookup(uint name)
            {
                Push(name);
                Emit(0x53);                                  // push ebx: kernel32 handle
                Emit(0xFF, 0x15, 0x08, 0x10, 0x60, 0x00);    // call [GetProcAddress]
                Emit(0x85, 0xC0, 0x74, 0x00);                // test eax,eax; je fail
                return code.Count - 1;
            }

            Emit(0x53);                                      // save ebx
            Push(Code + 0x80);                               // SEH handler
            Emit(0x64, 0xFF, 0x35, 0, 0, 0, 0);             // push fs:[0]
            Emit(0x64, 0x89, 0x25, 0, 0, 0, 0);             // mov fs:[0],esp
            Emit(0x8B, 0x1D, 0x20, 0x10, 0x60, 0x00);      // mov ebx,[kernel32 handle]
            var first = Lookup(Data + 0x80);
            var second = Lookup(Data + 0xA0);
            Emit(0x64, 0x8F, 0x05, 0, 0, 0, 0);             // pop fs:[0]
            Emit(0x83, 0xC4, 0x04, 0x5B);                   // discard handler; restore ebx
            Emit(0xB8, 0x01, 0, 0, 0, 0xC3);                // return 1
            var fail = code.Count;
            Emit(0x6A, 0, 0x6A, 0, 0x6A, 0);              // exception arguments
            Push(0xE0001234);
            Emit(0xFF, 0x15, 0, 0x10, 0x60, 0, 0x0F, 0x0B); // RaiseException; ud2
            code[first] = checked((byte)(fail - first - 1));
            code[second] = checked((byte)(fail - second - 1));
            while (code.Count < 0x80) Emit(0xCC);
            Emit(0xB8, 0x01, 0, 0, 0, 0xC3);               // ContinueSearch

            var p = Load(code.ToArray(), jit, out var kernel);
            p.Memory.Write32(Data + 8, p.Imports.Bind("kernel32.dll", "GetProcAddress", -1));
            var moduleName = kernel.Heap.Alloc(32);
            p.Memory.WriteAnsi(moduleName, "kernel32.dll");
            var load = p.Imports.Bind("kernel32.dll", "LoadLibraryA", -1);
            var loaded = p.Call(load, out var module, 1000, moduleName);
            Assert.True(loaded.Ok, loaded.ToString());
            p.Memory.Write32(Data + 0x20, module);
            p.Memory.WriteAnsi(Data + 0x80, "SetThreadGroupAffinity");
            p.Memory.WriteAnsi(Data + 0xA0, "GetThreadGroupAffinity");

            var result = p.Call(Code, out var eax, 100_000);
            Assert.True(result.Ok, result.ToString());
            Assert.Equal(1u, eax);
            Assert.Empty(kernel.ExceptionsRaised);

            var currentThread = p.Imports.Bind("kernel32.dll", "GetCurrentThread", -1);
            Assert.True(p.Call(currentThread, out var thread, 1000).Ok);
            var setAffinity = p.Imports.Bind("kernel32.dll", "SetThreadGroupAffinity", -1);
            var getAffinity = p.Imports.Bind("kernel32.dll", "GetThreadGroupAffinity", -1);
            p.Memory.Write32(Data + 0x40, 2);
            Assert.True(p.Call(setAffinity, out var changed, 1000, thread, Data + 0x40, Data + 0x50).Ok);
            Assert.Equal(1u, changed);
            Assert.Equal(0xFu, p.Memory.Read32(Data + 0x50));
            Assert.True(p.Call(getAffinity, out var queried, 1000, thread, Data + 0x60).Ok);
            Assert.Equal(1u, queried);
            Assert.Equal(2u, p.Memory.Read32(Data + 0x60));
        }

        // A frame whose handler records the code and the faulting address,
        // then skips the 5-byte `mov eax,[0x12345678]` in the CONTEXT and
        // continues. Returns code + EBX (7).
        private static readonly byte[] AccessViolationProgram =
        {
            0x53, 0x68, 0x31, 0x00, 0x60, 0x00, 0x64, 0xFF, 0x35, 0x00, 0x00, 0x00, 0x00, 0x64, 0x89, 0x25,
            0x00, 0x00, 0x00, 0x00, 0xBB, 0x07, 0x00, 0x00, 0x00, 0xA1, 0x78, 0x56, 0x34, 0x12, 0xA1, 0x10,
            0x10, 0x60, 0x00, 0x01, 0xD8, 0x64, 0x8F, 0x05, 0x00, 0x00, 0x00, 0x00, 0x83, 0xC4, 0x04, 0x5B,
            0xC3, 0x8B, 0x44, 0x24, 0x04, 0x8B, 0x08, 0x89, 0x0D, 0x10, 0x10, 0x60, 0x00, 0x8B, 0x48, 0x18,
            0x89, 0x0D, 0x14, 0x10, 0x60, 0x00, 0x8B, 0x44, 0x24, 0x0C, 0x83, 0x80, 0xB8, 0x00, 0x00, 0x00,
            0x05, 0x31, 0xC0, 0xC3,
        };

        // mov ebx,7 / inc ebx / mov eax,[0x12345678] (at +6) / ret — no handler.
        private static readonly byte[] PlainFaultProgram =
        {
            0xBB, 0x07, 0x00, 0x00, 0x00, 0x43, 0xA1, 0x78, 0x56, 0x34, 0x12, 0xC3,
        };

        // Under the JIT a guest fault is a host access violation, which only
        // the Windows vectored handler turns back into a guest fault;
        // elsewhere it would end the test host.
        private static bool CanRun(bool jit) =>
            !jit || System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);

        [SkippableTheory]
        [InlineData(false)]
        [InlineData(true)]
        public void AccessViolationReachesTheGuestHandlerWhichSkipsTheInstruction(bool jit)
        {
            if (!CanRun(jit)) return;
            var p = Load(AccessViolationProgram, jit, out var kernel);
            var result = p.Call(Code, out var eax, 100_000);
            Assert.True(result.Ok, result.ToString());
            Assert.Equal(0xC0000005u + 7u, eax);
            Assert.Equal(0x12345678u, p.Memory.Read32(Data + 0x14));   // ExceptionInformation[1]
            Assert.Equal(new[] { 0xC0000005u }, kernel.ExceptionsRaised);
        }

        [SkippableTheory]
        [InlineData(false)]
        [InlineData(true)]
        public void UnhandledFaultStopsAtTheFaultingInstructionWithItsState(bool jit)
        {
            if (!CanRun(jit)) return;
            var p = Load(PlainFaultProgram, jit, out _);
            var result = p.Call(Code, out _, 100_000);
            Assert.Equal(GuestStop.Fault, result.Stop);
            Assert.Equal(0x12345678u, result.FaultAddress);
            Assert.Equal(Code + 6, p.Cpu.Eip);
            Assert.Equal(8u, p.Cpu.Ebx);

            // The process is intact: it runs guest code again afterwards.
            var again = p.Call(Code + 11, out _, 1000);   // just the ret
            Assert.True(again.Ok, again.ToString());
        }

        [Fact]
        public void UnhandledExceptionStopsTheRun()
        {
            // Only the inner frame, which always searches on: nobody catches.
            var p = new GuestProcess(new GuestMemory(), useJit: false);
            var kernel = new GuestKernel(p);
            kernel.Install();
            var raise = p.Imports.Bind("kernel32.dll", "RaiseException", -1);
            var result = p.Call(raise, out _, 1000, 0xC0000094, 0, 0, 0);
            Assert.Equal(GuestStop.Raised, result.Stop);
            Assert.Equal(0xC0000094u, result.ExitCode);
        }
    }
}
