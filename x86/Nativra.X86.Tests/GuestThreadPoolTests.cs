using System;
using Nativra.X86.Cpu;
using Nativra.X86.Loader;
using Xunit;

namespace Nativra.X86.Tests
{
    /// <summary>
    /// The Vista thread pool, timer queues and registered waits. Callbacks are small machine-code
    /// stubs that count calls in guest memory. Timing assertions are loose on purpose: these run
    /// beside other tests, so they count calls against generous bounds instead of exact waits.
    /// </summary>
    public sealed class GuestThreadPoolTests : IDisposable
    {
        private const uint Code = 0x00600000, Data = 0x00601000;
        // Stubs (offsets in Code); the context is a counter block: +0 calls, +4 last result.
        private const uint TimerCallback = Code + 0x00;     // (instance, context, timer)
        private const uint QueueCallback = Code + 0x10;     // (context, timedOut)
        private const uint WaitCallback = Code + 0x20;      // (instance, context, wait, result)
        private const uint RegisteredCallback = Code + 0x30;
        private const uint FreeLibraryCallback = Code + 0x50;
        private const uint SetEventCallback = Code + 0x70;
        private const uint Slots = Data + 0x100;            // +0 FreeLibraryWhenCallbackReturns, +4 SetEventWhenCallbackReturns

        private readonly GuestProcess p;
        private readonly GuestKernel k;

        public GuestThreadPoolTests()
        {
            p = new GuestProcess(new GuestMemory(native: true), useJit: false);
            k = new GuestKernel(p) { ExePath = "C:\\game\\game.exe" };
            k.Install();
            p.Memory.Map(Code, 0x3000);
            // inc dword [context]; ret
            Stub(TimerCallback, 0x8B, 0x44, 0x24, 0x08, 0xFF, 0x00, 0xC2, 0x0C, 0x00);
            Stub(QueueCallback, 0x8B, 0x44, 0x24, 0x04, 0xFF, 0x00, 0x8B, 0x4C, 0x24, 0x08, 0x89, 0x48, 0x04, 0xC2, 0x08, 0x00);
            // also stores the wait result at [context+4]
            Stub(WaitCallback, 0x8B, 0x44, 0x24, 0x08, 0xFF, 0x00, 0x8B, 0x4C, 0x24, 0x10, 0x89, 0x48, 0x04, 0xC2, 0x10, 0x00);
            Stub(RegisteredCallback, 0x8B, 0x44, 0x24, 0x04, 0xFF, 0x00, 0x8B, 0x4C, 0x24, 0x08, 0x89, 0x48, 0x04, 0xC2, 0x08, 0x00);
            // FreeLibraryWhenCallbackReturns(instance, 0x1234)
            Stub(FreeLibraryCallback, 0x68, 0x34, 0x12, 0x00, 0x00, 0xFF, 0x74, 0x24, 0x08, 0xFF, 0x15, 0x00, 0x11, 0x60, 0x00, 0xC2, 0x0C, 0x00);
            // SetEventWhenCallbackReturns(instance, [context])
            Stub(SetEventCallback, 0x8B, 0x4C, 0x24, 0x08, 0xFF, 0x31, 0xFF, 0x74, 0x24, 0x08, 0xFF, 0x15, 0x04, 0x11, 0x60, 0x00, 0xC2, 0x0C, 0x00);
            p.Memory.Write32(Slots, p.Imports.Bind("kernel32.dll", "FreeLibraryWhenCallbackReturns", -1));
            p.Memory.Write32(Slots + 4, p.Imports.Bind("kernel32.dll", "SetEventWhenCallbackReturns", -1));
        }

        public void Dispose() => p.Dispose();

        private void Stub(uint at, params byte[] bytes) => p.Memory.WriteBytes(at, bytes);

        private uint Call(string function, params uint[] args)
        {
            var result = p.Call(p.Imports.Bind("kernel32.dll", function, -1), out var eax, 50_000_000, args);
            Assert.True(result.Ok, $"{function} stopped as {result}");
            return eax;
        }

        private uint Sleep(uint ms) => Call("Sleep", ms);
        private uint Count(uint block = Data) => p.Memory.Read32(block);

        private uint FileTime(long value)
        {
            var at = k.Heap.Alloc(8, zero: true);
            p.Memory.Write64(at, (ulong)value);
            return at;
        }

        private static long Relative(int milliseconds) => -(long)milliseconds * 10_000;

        private uint Event(bool manual = true) => Call("CreateEventW", 0, manual ? 1u : 0u, 0, 0);

        // Waits until the counter reaches a value (or a generous limit), so a slow machine only slows the test.
        private bool Reaches(uint block, uint count, int limitMs = 3000)
        {
            for (var waited = 0; waited < limitMs; waited += 10)
            {
                if (Count(block) >= count) return true;
                Sleep(10);
            }
            return Count(block) >= count;
        }

        [Fact]
        public void ConcurrencyRuntimeProbeFindsEveryVistaThreadPoolApi()
        {
            var name = k.Heap.Alloc(64, zero: true);
            p.Memory.WriteUnicode(name, "kernel32.dll");
            var module = Call("GetModuleHandleW", name);
            Assert.NotEqual(0u, module);
            var ansi = k.Heap.Alloc(64, zero: true);
            foreach (var function in new[]
            {
                "SetThreadStackGuarantee", "CreateThreadpoolTimer", "SetThreadpoolTimer", "WaitForThreadpoolTimerCallbacks",
                "CloseThreadpoolTimer", "CreateThreadpoolWait", "SetThreadpoolWait", "WaitForThreadpoolWaitCallbacks",
                "CloseThreadpoolWait", "FreeLibraryWhenCallbackReturns", "FlsGetValue2", "CreateThreadpoolWork",
                "SubmitThreadpoolWork", "CloseThreadpoolWork", "GetCurrentProcessorNumber", "FlushProcessWriteBuffers",
                "GetLogicalProcessorInformation", "CreateTimerQueue", "CreateTimerQueueTimer", "ChangeTimerQueueTimer",
                "DeleteTimerQueueTimer", "RegisterWaitForSingleObject", "UnregisterWait", "UnregisterWaitEx",
                "GetNumaHighestNodeNumber", "RtlCaptureStackBackTrace", "DebugBreak", "FatalAppExitA",
            })
            {
                p.Memory.WriteAnsi(ansi, function);
                Assert.NotEqual(0u, Call("GetProcAddress", module, ansi));
            }
        }

        [Fact]
        public void OneShotTimerFiresOnceAndIsNoLongerSet()
        {
            var timer = Call("CreateThreadpoolTimer", TimerCallback, Data, 0);
            Call("SetThreadpoolTimer", timer, FileTime(Relative(20)), 0, 0);
            Assert.Equal(1u, Call("IsThreadpoolTimerSet", timer));
            Assert.True(Reaches(Data, 1));
            Sleep(60);
            Assert.Equal(1u, Count());
            Assert.Equal(0u, Call("IsThreadpoolTimerSet", timer));
            Call("CloseThreadpoolTimer", timer);
        }

        [Fact]
        public void AbsoluteDueTimeIsHonoured()
        {
            var timer = Call("CreateThreadpoolTimer", TimerCallback, Data, 0);
            Call("SetThreadpoolTimer", timer, FileTime(DateTime.UtcNow.ToFileTimeUtc() + 300_000), 0, 0);   // +30 ms
            Assert.Equal(0u, Count());
            Assert.True(Reaches(Data, 1));
            Call("CloseThreadpoolTimer", timer);
        }

        [Fact]
        public void PeriodicTimerRepeatsUntilCancelledWithANullDueTime()
        {
            var timer = Call("CreateThreadpoolTimer", TimerCallback, Data, 0);
            Call("SetThreadpoolTimer", timer, FileTime(Relative(5)), 10, 0);
            Assert.True(Reaches(Data, 4));
            Call("SetThreadpoolTimer", timer, 0, 0, 0);
            Assert.Equal(0u, Call("IsThreadpoolTimerSet", timer));
            Call("WaitForThreadpoolTimerCallbacks", timer, 1);
            var fired = Count();
            Sleep(80);
            Assert.Equal(fired, Count());
            Call("CloseThreadpoolTimer", timer);
        }

        [Fact]
        public void TimersFireInDueOrder()
        {
            // Both stubs count in their own block; the earlier due time must have fired first.
            var early = Call("CreateThreadpoolTimer", TimerCallback, Data, 0);
            var late = Call("CreateThreadpoolTimer", TimerCallback, Data + 0x10, 0);
            Call("SetThreadpoolTimer", late, FileTime(Relative(150)), 0, 0);
            Call("SetThreadpoolTimer", early, FileTime(Relative(10)), 0, 0);
            Assert.True(Reaches(Data, 1));
            Assert.Equal(0u, Count(Data + 0x10));
            Assert.True(Reaches(Data + 0x10, 1));
            Call("CloseThreadpoolTimer", early);
            Call("CloseThreadpoolTimer", late);
        }

        [Fact]
        public void WaitFiresOnSignalOnceAndNeedsSettingAgain()
        {
            var evt = Event(manual: false);
            var wait = Call("CreateThreadpoolWait", WaitCallback, Data, 0);
            Call("SetThreadpoolWait", wait, evt, 0);
            Sleep(30);
            Assert.Equal(0u, Count());
            Call("SetEvent", evt);
            Assert.True(Reaches(Data, 1));
            Assert.Equal(0u, p.Memory.Read32(Data + 4));   // WAIT_OBJECT_0
            Call("SetEvent", evt);
            Sleep(60);
            Assert.Equal(1u, Count());   // one callback per SetThreadpoolWait
            Call("SetThreadpoolWait", wait, evt, 0);
            Assert.True(Reaches(Data, 2));
            Call("WaitForThreadpoolWaitCallbacks", wait, 0);
            Call("CloseThreadpoolWait", wait);
        }

        [Fact]
        public void WaitReportsATimeout()
        {
            var evt = Event();
            var wait = Call("CreateThreadpoolWait", WaitCallback, Data, 0);
            Call("SetThreadpoolWait", wait, evt, FileTime(Relative(20)));
            Assert.True(Reaches(Data, 1));
            Assert.Equal(0x102u, p.Memory.Read32(Data + 4));   // WAIT_TIMEOUT
            Call("CloseThreadpoolWait", wait);
        }

        [Fact]
        public void CancelledWaitDoesNotFire()
        {
            var evt = Event();
            var wait = Call("CreateThreadpoolWait", WaitCallback, Data, 0);
            Call("SetThreadpoolWait", wait, evt, 0);
            Call("SetThreadpoolWait", wait, 0, 0);   // NULL handle cancels
            Call("WaitForThreadpoolWaitCallbacks", wait, 1);
            Call("SetEvent", evt);
            Sleep(60);
            Assert.Equal(0u, Count());
            Call("CloseThreadpoolWait", wait);
        }

        [Fact]
        public void WorkItemsRunAndTheWaitCoversThemAll()
        {
            var work = Call("CreateThreadpoolWork", TimerCallback, Data, 0);
            for (var n = 0; n < 5; n++) Call("SubmitThreadpoolWork", work);
            Call("WaitForThreadpoolWorkCallbacks", work, 0);
            Assert.Equal(5u, Count());
            Call("CloseThreadpoolWork", work);
        }

        [Fact]
        public void WaitingWithCancelSkipsWorkThatHasNotStarted()
        {
            var work = Call("CreateThreadpoolWork", TimerCallback, Data, 0);
            for (var n = 0; n < 3; n++) Call("SubmitThreadpoolWork", work);
            Call("WaitForThreadpoolWorkCallbacks", work, 1);
            Assert.True(Count() < 3, $"{Count()} of 3 ran despite the cancel");
            Call("CloseThreadpoolWork", work);
        }

        [Fact]
        public void CallbackThatBlocksDoesNotStallTheCaller()
        {
            // The callback waits on an event nobody has set yet: the caller keeps going, then releases it.
            var evt = Event();
            p.Memory.Write32(Data + 0x20, evt);
            var wait = p.Imports.Bind("kernel32.dll", "WaitForSingleObject", -1);
            p.Memory.Write32(Slots + 8, wait);
            // work(instance, context, work): WaitForSingleObject([context], INFINITE); inc [context+4]
            Stub(Code + 0x90, 0x8B, 0x44, 0x24, 0x08, 0x6A, 0xFF, 0xFF, 0x30, 0xFF, 0x15, 0x08, 0x11, 0x60, 0x00,
                0x8B, 0x44, 0x24, 0x08, 0xFF, 0x40, 0x04, 0xC2, 0x0C, 0x00);
            var work = Call("CreateThreadpoolWork", Code + 0x90, Data + 0x20, 0);
            Call("SubmitThreadpoolWork", work);
            Sleep(30);
            Assert.Equal(0u, p.Memory.Read32(Data + 0x24));
            Call("SetEvent", evt);
            Call("WaitForThreadpoolWorkCallbacks", work, 0);
            Assert.Equal(1u, p.Memory.Read32(Data + 0x24));
            Call("CloseThreadpoolWork", work);
        }

        [Fact]
        public void FreeLibraryWhenCallbackReturnsIsAppliedAfterTheCallback()
        {
            var timer = Call("CreateThreadpoolTimer", FreeLibraryCallback, 0, 0);
            Call("SetThreadpoolTimer", timer, FileTime(Relative(5)), 0, 0);
            for (var waited = 0; waited < 3000 && k.LibrariesFreedByCallbacks.Count == 0; waited += 10) Sleep(10);
            Assert.Equal(new[] { 0x1234u }, k.LibrariesFreedByCallbacks);
            Call("CloseThreadpoolTimer", timer);
        }

        [Fact]
        public void SetEventWhenCallbackReturnsSignalsTheEvent()
        {
            var evt = Event();
            p.Memory.Write32(Data + 0x30, evt);
            var work = Call("CreateThreadpoolWork", SetEventCallback, Data + 0x30, 0);
            Call("SubmitThreadpoolWork", work);
            Assert.Equal(0u, Call("WaitForSingleObject", evt, 3000));
            Call("CloseThreadpoolWork", work);
        }

        [Fact]
        public void TimerQueueTimerFiresPeriodicallyUntilDeleted()
        {
            var queue = Call("CreateTimerQueue");
            var handle = k.Heap.Alloc(4, zero: true);
            Assert.Equal(1u, Call("CreateTimerQueueTimer", handle, queue, QueueCallback, Data, 5, 10, 0));
            Assert.True(Reaches(Data, 3));
            Assert.Equal(1u, p.Memory.Read32(Data + 4));   // the second argument is TRUE
            Assert.Equal(1u, Call("DeleteTimerQueueTimer", queue, p.Memory.Read32(handle), 0xFFFFFFFF));
            var fired = Count();
            Sleep(60);
            Assert.Equal(fired, Count());
            Assert.Equal(1u, Call("DeleteTimerQueueEx", queue, 0xFFFFFFFF));
        }

        [Fact]
        public void TimerQueueTimerCanBeChangedAndTheQueueDeletedWithItsTimers()
        {
            var queue = Call("CreateTimerQueue");
            var handle = k.Heap.Alloc(4, zero: true);
            Call("CreateTimerQueueTimer", handle, queue, QueueCallback, Data, 0xFFFFFFFF, 0, 0);   // never due
            Sleep(40);
            Assert.Equal(0u, Count());
            Assert.Equal(1u, Call("ChangeTimerQueueTimer", queue, p.Memory.Read32(handle), 5, 0));
            Assert.True(Reaches(Data, 1));
            Sleep(40);
            Assert.Equal(1u, Count());   // one-shot
            Call("CreateTimerQueueTimer", handle, queue, QueueCallback, Data, 5, 10, 0);
            Assert.Equal(1u, Call("DeleteTimerQueueEx", queue, 0xFFFFFFFF));
            var fired = Count();
            Sleep(60);
            Assert.Equal(fired, Count());
        }

        [Fact]
        public void RegisteredWaitWithExecuteOnlyOnceFiresOnce()
        {
            var evt = Event(manual: false);
            var handle = k.Heap.Alloc(4, zero: true);
            Assert.Equal(1u, Call("RegisterWaitForSingleObject", handle, evt, RegisteredCallback, Data, 0xFFFFFFFF, 8));
            Call("SetEvent", evt);
            Assert.True(Reaches(Data, 1));
            Assert.Equal(0u, p.Memory.Read32(Data + 4));   // not timed out
            Call("SetEvent", evt);
            Sleep(60);
            Assert.Equal(1u, Count());
            Call("UnregisterWait", p.Memory.Read32(handle));
        }

        [Fact]
        public void RegisteredWaitWithoutTheFlagRepeatsAndReportsTimeouts()
        {
            var evt = Event(manual: false);
            var handle = k.Heap.Alloc(4, zero: true);
            Call("RegisterWaitForSingleObject", handle, evt, RegisteredCallback, Data, 10, 0);
            Assert.True(Reaches(Data, 3));
            Assert.Equal(1u, p.Memory.Read32(Data + 4));   // timed out
            Assert.Equal(1u, Call("UnregisterWaitEx", p.Memory.Read32(handle), 0xFFFFFFFF));
            var fired = Count();
            Sleep(60);
            Assert.Equal(fired, Count());
        }

        [Fact]
        public void SmallProbedApisAnswer()
        {
            var value = k.Heap.Alloc(4, zero: true);
            p.Memory.Write32(value, 0x3000);
            Assert.Equal(1u, Call("SetThreadStackGuarantee", value));
            Assert.Equal(0u, p.Memory.Read32(value));   // the previous guarantee
            Assert.Equal(1u, Call("GetNumaHighestNodeNumber", value));
            Assert.Equal(0u, p.Memory.Read32(value));
            var slot = Call("FlsAlloc", 0);
            Call("FlsSetValue", slot, 0x4321);
            Assert.Equal(0x4321u, Call("FlsGetValue2", slot));
            var frames = k.Heap.Alloc(64, zero: true);
            Assert.True(Call("RtlCaptureStackBackTrace", 0, 8, frames, 0) >= 1);
        }

        [Fact]
        public void DebugBreakRaisesABreakpointException()
        {
            var result = p.Call(p.Imports.Bind("kernel32.dll", "DebugBreak", -1), out _, 1_000_000);
            Assert.False(result.Ok);
            Assert.Contains(0x80000003u, k.ExceptionsRaised);
        }

        [Fact]
        public void FatalAppExitEndsTheProcess()
        {
            var message = k.Heap.Alloc(16, zero: true);
            p.Memory.WriteAnsi(message, "fatal");
            var result = p.Call(p.Imports.Bind("kernel32.dll", "FatalAppExitA", -1), out _, 1_000_000, 0, message);
            Assert.Equal(GuestStop.Exited, result.Stop);
        }
    }
}
