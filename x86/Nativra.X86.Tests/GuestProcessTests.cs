using System;
using Nativra.X86.Cpu;
using Nativra.X86.Loader;
using Xunit;

namespace Nativra.X86.Tests
{
    /// <summary>
    /// Drives a whole guest process: builds the TEB/PEB/stack, loads a PE32,
    /// and — the heart of the 32-bit layer — proves that a guest call through
    /// the import table traps to a host handler and resumes with the right
    /// result and a balanced stack, under both the interpreter and the JIT.
    /// </summary>
    public sealed class GuestProcessTests
    {
        private const uint Code = 0x00401000;
        private const uint Slot = 0x00402000;

        // A guest "function" that forwards its two arguments to an import called
        // through [Slot] and returns the import's result. Two variants: a stdcall
        // forwarder leaves the pushed arguments for the callee to pop; a cdecl
        // forwarder pops them itself (add esp,8) after the call. Using the wrong
        // one for a convention corrupts the return address, so a passing test
        // proves the dispatcher applied the matching stack cleanup.
        //   mov eax,[esp+8]; push eax; mov eax,[esp+8]; push eax; call [Slot]; [add esp,8;] ret
        private static byte[] Forwarder(bool cdecl)
        {
            var head = new byte[]
            {
                0x8B, 0x44, 0x24, 0x08,   // mov eax, [esp+8]
                0x50,                     // push eax
                0x8B, 0x44, 0x24, 0x08,   // mov eax, [esp+8]
                0x50,                     // push eax
                0xFF, 0x15, 0, 0, 0, 0,   // call dword [Slot]  (absolute; patched below)
            };
            var tail = cdecl
                ? new byte[] { 0x83, 0xC4, 0x08, 0xC3 }  // add esp,8 ; ret
                : new byte[] { 0xC3 };                   // ret
            var code = new byte[head.Length + tail.Length];
            Array.Copy(head, code, head.Length);
            Array.Copy(tail, 0, code, head.Length, tail.Length);
            code[12] = (byte)(Slot & 0xFF); code[13] = (byte)((Slot >> 8) & 0xFF);
            code[14] = (byte)((Slot >> 16) & 0xFF); code[15] = (byte)((Slot >> 24) & 0xFF);
            return code;
        }

        private static void WriteForwarder(GuestProcess p, uint sentinel, bool cdecl = false)
        {
            p.Memory.Map(Code, GuestMemory.PageSize);
            p.Memory.Map(Slot, GuestMemory.PageSize);
            p.Memory.WriteBytes(Code, Forwarder(cdecl));
            p.Memory.Write32(Slot, sentinel);   // the IAT slot holds the import sentinel
        }

        [Fact]
        public void BuildsTebAndPebGamesCanRead()
        {
            using (var memory = new GuestMemory())
            using (var p = new GuestProcess(memory, useJit: false))
            {
                Assert.Equal(p.TebBase, p.Cpu.FsBase);
                Assert.Equal(p.TebBase, memory.Read32(p.TebBase + 0x18));   // TEB self
                Assert.Equal(p.PebBase, memory.Read32(p.TebBase + 0x30));   // TEB -> PEB
                Assert.Equal(0xFFFFFFFFu, memory.Read32(p.TebBase + 0x00)); // SEH chain end
                Assert.Equal(p.StackBase, memory.Read32(p.TebBase + 0x04));
                Assert.Equal(0, memory.Read8(p.PebBase + 0x02));           // not being debugged
            }
        }

        [Fact]
        public void LoadingAnImagePublishesItsBaseIntoThePeb()
        {
            using (var memory = new GuestMemory())
            using (var p = new GuestProcess(memory, useJit: false))
            {
                var image = p.LoadExecutable("test.exe", TestPe32.Minimal());
                Assert.Equal(image.BaseAddress, memory.Read32(p.PebBase + 0x08));
                Assert.Same(image, p.MainImage);
            }
        }

        [Fact]
        public void LinksAGameDllIntoTheGuestAndFallsBackToSentinels()
        {
            using (var memory = new GuestMemory())
            using (var p = new GuestProcess(memory, useJit: false))
            {
                var requested = new System.Collections.Generic.List<string>();
                var dllBytes = TestPe32.Minimal(dll: true);
                p.ModuleSource = name =>
                {
                    requested.Add(name);
                    return name == "game.dll" ? dllBytes : null;   // the game carries game.dll only
                };

                var exe = p.LoadExecutable("waveshaper.exe", TestPe32.Minimal("game.dll", "Start"));
                var dll = p.FindModule("GAME.DLL");

                Assert.NotNull(dll);
                Assert.NotEqual(exe.BaseAddress, dll.BaseAddress);           // one of them had to move
                // By-name import bound straight to the DLL's real export...
                Assert.Equal(dll.Export("Start"), memory.Read32(exe.BaseAddress + TestPe32.IatRva));
                // ...ordinal 5 is not exported, so it falls back to a host sentinel.
                Assert.True(GuestImports.InRegion(memory.Read32(exe.BaseAddress + TestPe32.IatRva + 4)));
                // kernel32 is asked for once (the host said no), not once per import.
                Assert.Single(requested, n => n == "kernel32.dll");
                Assert.Equal(new[] { dll, exe }, p.Images);                  // dependencies first
            }
        }

        [Fact]
        public void InitializeModulesRunsDllMainWithProcessAttach()
        {
            using (var memory = new GuestMemory())
            using (var p = new GuestProcess(memory, useJit: false))
            {
                var dllBytes = TestPe32.Minimal(dll: true, dllMain: true);
                p.ModuleSource = name => name == "game.dll" ? dllBytes : null;
                p.LoadExecutable("waveshaper.exe", TestPe32.Minimal("game.dll", "Start"));
                var dll = p.FindModule("game.dll");

                Assert.Equal(0u, memory.Read32(dll.BaseAddress + TestPe32.DllMainMarkRva));
                var result = p.InitializeModules(100000);
                Assert.True(result.Ok, result.ToString());
                Assert.Equal(TestPe32.DllMainMark, memory.Read32(dll.BaseAddress + TestPe32.DllMainMarkRva));
            }
        }

        [Fact]
        public void InterpreterDispatchesAnImportedStdcallAndBalancesTheStack()
        {
            using (var memory = new GuestMemory())
            using (var p = new GuestProcess(memory, useJit: false))
            {
                RunForwarderAdd(p);
            }
        }

        [SkippableFact]
        public void JitDispatchesAnImportedStdcallAndBalancesTheStack()
        {
            Skip.IfNot(IntPtr.Size == 8, "the JIT needs a 64-bit host");
            using (var memory = new GuestMemory(native: true))
            using (var p = new GuestProcess(memory, useJit: true))
            {
                Skip.IfNot(p.UsesJit, "the host refused executable memory");
                RunForwarderAdd(p);
            }
        }

        private static void RunForwarderAdd(GuestProcess p)
        {
            var called = 0;
            p.Imports.Register("test.dll", "Add", CallConv.Stdcall, 2, call =>
            {
                called++;
                return call.Arg(0) + call.Arg(1);
            });
            var sentinel = p.Imports.Bind("test.dll", "Add", -1);
            WriteForwarder(p, sentinel);

            var espBefore = p.Cpu.Esp;
            var result = p.Call(Code, out var eax, 100000, 3, 5);

            Assert.True(result.Ok, $"stopped as {result.Stop} @ 0x{result.FaultAddress:X8}");
            Assert.Equal(8u, eax);
            Assert.Equal(1, called);
            Assert.Equal(espBefore, p.Cpu.Esp);   // synthetic frame fully unwound
        }

        [Fact]
        public void ReportsAnImportWithNoHandler()
        {
            using (var memory = new GuestMemory())
            using (var p = new GuestProcess(memory, useJit: false))
            {
                var sentinel = p.Imports.Bind("test.dll", "Unhandled", -1);
                WriteForwarder(p, sentinel);

                var result = p.Call(Code, out _, 100000, 1, 2);
                Assert.Equal(GuestStop.MissingImport, result.Stop);
                Assert.NotNull(result.Import);
                Assert.Equal("test.dll!Unhandled", result.Import.ToString());
            }
        }

        [Fact]
        public void CdeclLeavesArgumentCleanupToTheCaller()
        {
            using (var memory = new GuestMemory())
            using (var p = new GuestProcess(memory, useJit: false))
            {
                // A cdecl callee cleans nothing, so the cdecl forwarder pops the
                // 8 bytes itself. If the dispatcher wrongly popped them (stdcall),
                // the forwarder's add esp,8 would over-pop and its ret would fault.
                p.Imports.Register("test.dll", "Add", CallConv.Cdecl, 2, call => call.Arg(0) + call.Arg(1));
                var sentinel = p.Imports.Bind("test.dll", "Add", -1);
                WriteForwarder(p, sentinel, cdecl: true);

                var espBefore = p.Cpu.Esp;
                var result = p.Call(Code, out var eax, 100000, 4, 6);
                Assert.True(result.Ok, $"stopped as {result.Stop} @ 0x{result.FaultAddress:X8}");
                Assert.Equal(10u, eax);
                Assert.Equal(espBefore, p.Cpu.Esp);
            }
        }
    }
}
