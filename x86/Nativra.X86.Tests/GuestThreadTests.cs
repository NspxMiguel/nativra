using Nativra.X86.Cpu;
using Nativra.X86.Loader;
using Xunit;

namespace Nativra.X86.Tests
{
    /// <summary>
    /// Guest threads (green threads on the host thread) driven by assembled
    /// guest code (nasm, org 0x600000; listings in the comments).
    /// </summary>
    public sealed class GuestThreadTests
    {
        private const uint Code = 0x00600000;
        private const uint Data = 0x00601000;   // +0 CreateThread, +4 WaitForSingleObject, +8 GetExitCodeThread

        private static GuestProcess Load(byte[] program, bool jit, out GuestKernel kernel)
        {
            var p = new GuestProcess(new GuestMemory(native: jit), useJit: jit);
            Assert.Equal(jit, p.UsesJit);   // a JIT case must not fall back to the interpreter unnoticed
            kernel = new GuestKernel(p);
            kernel.Install();
            p.Memory.Map(Code, 0x2000);
            p.Memory.WriteBytes(Code, program);
            p.Memory.Write32(Data + 0, p.Imports.Bind("kernel32.dll", "CreateThread", -1));
            p.Memory.Write32(Data + 4, p.Imports.Bind("kernel32.dll", "WaitForSingleObject", -1));
            p.Memory.Write32(Data + 8, p.Imports.Bind("kernel32.dll", "GetExitCodeThread", -1));
            return p;
        }

        // main: h = CreateThread(worker, 0x1234); WaitForSingleObject(h, INFINITE);
        //       GetExitCodeThread(h, &code); return code + [0x601010]
        // worker(p): spin 100000; [0x601010] = p; return 7
        private static readonly byte[] WaitForWorker =
        {
            0x56, 0x6A, 0x00, 0x6A, 0x00, 0x68, 0x34, 0x12, 0x00, 0x00, 0x68, 0x3D, 0x00, 0x60, 0x00, 0x6A,
            0x00, 0x6A, 0x00, 0xFF, 0x15, 0x00, 0x10, 0x60, 0x00, 0x89, 0xC6, 0x6A, 0xFF, 0x56, 0xFF, 0x15,
            0x04, 0x10, 0x60, 0x00, 0x68, 0x20, 0x10, 0x60, 0x00, 0x56, 0xFF, 0x15, 0x08, 0x10, 0x60, 0x00,
            0xA1, 0x20, 0x10, 0x60, 0x00, 0x03, 0x05, 0x10, 0x10, 0x60, 0x00, 0x5E, 0xC3, 0x8B, 0x44, 0x24,
            0x04, 0xB9, 0xA0, 0x86, 0x01, 0x00, 0x49, 0x75, 0xFD, 0xA3, 0x10, 0x10, 0x60, 0x00, 0xB8, 0x07,
            0x00, 0x00, 0x00, 0xC2, 0x04, 0x00,
        };

        // main: CreateThread(worker); while ([0x601030] == 0) {} return [0x601030]
        // worker: [0x601030] = 5; return 0
        // Main never calls into the kernel while it spins: only preemption lets the worker run.
        private static readonly byte[] SpinOnFlag =
        {
            0x6A, 0x00, 0x6A, 0x00, 0x6A, 0x00, 0x68, 0x24, 0x00, 0x60, 0x00, 0x6A, 0x00, 0x6A, 0x00, 0xFF,
            0x15, 0x00, 0x10, 0x60, 0x00, 0x83, 0x3D, 0x30, 0x10, 0x60, 0x00, 0x00, 0x74, 0xF7, 0xA1, 0x30,
            0x10, 0x60, 0x00, 0xC3, 0xC7, 0x05, 0x30, 0x10, 0x60, 0x00, 0x05, 0x00, 0x00, 0x00, 0x31, 0xC0,
            0xC2, 0x04, 0x00,
        };

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void WaitingThreadResumesWhenTheWorkerExits(bool jit)
        {
            var p = Load(WaitForWorker, jit, out _);
            var result = p.Call(Code, out var eax, 10_000_000);
            Assert.True(result.Ok, result.ToString());
            Assert.Equal(0x1234u + 7u, eax);
            Assert.Equal(2, p.Threads.Count);
            Assert.True(p.Threads[1].IsDone);
            Assert.Equal(7u, p.Threads[1].ExitCode);
            Assert.Equal(GuestProcess.MainThreadId, p.CurrentThread.Id);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void SpinningThreadIsPreemptedSoTheOtherCanRun(bool jit)
        {
            var p = Load(SpinOnFlag, jit, out _);
            var result = p.Call(Code, out var eax, 10_000_000);
            Assert.True(result.Ok, result.ToString());
            Assert.Equal(5u, eax);
        }

        [Fact]
        public void EachThreadHasItsOwnTebAndLastError()
        {
            var p = Load(WaitForWorker, false, out _);
            var worker = p.CreateThread(Code + 0x3D, 0, 0, suspended: true);
            Assert.NotEqual(p.Threads[0].TebBase, worker.TebBase);
            Assert.Equal(worker.TebBase, p.Memory.Read32(worker.TebBase + 0x18));   // NT_TIB.Self
            Assert.Equal(worker.Id, p.Memory.Read32(worker.TebBase + 0x24));
            Assert.Equal(p.PebBase, p.Memory.Read32(worker.TebBase + 0x30));
            Assert.True(worker.StackBase - worker.StackLimit >= 256 * 1024);
        }

        [Fact]
        public void InfiniteWaitOnAnEventNobodySetsIsReportedAsADeadlock()
        {
            var p = new GuestProcess(new GuestMemory(), useJit: false) { DeadlockMilliseconds = 50 };
            var kernel = new GuestKernel(p);
            kernel.Install();
            var create = p.Imports.Bind("kernel32.dll", "CreateEventW", -1);
            Assert.True(p.Call(create, out var handle, 1000, 0, 0, 0, 0).Ok);
            var wait = p.Imports.Bind("kernel32.dll", "WaitForSingleObject", -1);
            var result = p.Call(wait, out _, 1_000_000, handle, 0xFFFFFFFF);
            Assert.Equal(GuestStop.Deadlocked, result.Stop);
        }

        [Fact]
        public void TimedWaitAndSleepTakeRealTime()
        {
            var p = new GuestProcess(new GuestMemory(), useJit: false);
            var kernel = new GuestKernel(p);
            kernel.Install();
            var create = p.Imports.Bind("kernel32.dll", "CreateEventW", -1);
            Assert.True(p.Call(create, out var handle, 1000, 0, 0, 0, 0).Ok);

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var wait = p.Imports.Bind("kernel32.dll", "WaitForSingleObject", -1);
            Assert.True(p.Call(wait, out var answer, 1_000_000, handle, 30).Ok);
            Assert.Equal(0x102u, answer);   // WAIT_TIMEOUT
            Assert.True(clock.ElapsedMilliseconds >= 25, clock.ElapsedMilliseconds + " ms");

            clock.Restart();
            var sleep = p.Imports.Bind("kernel32.dll", "Sleep", -1);
            Assert.True(p.Call(sleep, out _, 1_000_000, 20).Ok);
            Assert.True(clock.ElapsedMilliseconds >= 15, clock.ElapsedMilliseconds + " ms");
        }
    }
}
