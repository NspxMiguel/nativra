using System;
using System.Collections.Generic;

namespace Nativra.X86.Loader
{
    // Threads and synchronisation. Guest threads are green threads (see
    // GuestProcess.Threads): a call that has to wait blocks its thread with
    // GuestProcess.Block and is asked again when the thread next runs, so
    // every handler here is written to be called repeatedly until it can
    // answer. Nothing here waits on the host.
    public sealed partial class GuestKernel
    {
        private const uint WaitAbandoned0 = 0x80;
        private const uint WaitFailed = 0xFFFFFFFF;
        private const uint ErrorNotOwner = 288;
        private const uint ErrorTooManyPosts = 298;
        private const uint CreateSuspended = 0x4;
        private const uint PseudoThread = 0xFFFFFFFE;

        private abstract class Waitable
        {
            /// <summary>Signalled for this thread; consuming takes the signal (auto-reset, a count, ownership).</summary>
            public abstract bool Ready(uint thread);
            public abstract void Consume(uint thread);
        }

        private sealed class GuestEvent : Waitable
        {
            public bool ManualReset;
            public bool Signaled;
            public override bool Ready(uint thread) => Signaled;
            public override void Consume(uint thread) { if (!ManualReset) Signaled = false; }
        }

        private sealed class GuestSemaphore : Waitable
        {
            public int Count, Maximum;
            public override bool Ready(uint thread) => Count > 0;
            public override void Consume(uint thread) => Count--;
        }

        private sealed class GuestMutex : Waitable
        {
            public uint Owner;
            public int Recursion;
            public override bool Ready(uint thread) => Owner == 0 || Owner == thread;
            public override void Consume(uint thread) { Owner = thread; Recursion++; }
        }

        private sealed class ThreadObject : Waitable
        {
            public GuestThread Thread;
            public override bool Ready(uint thread) => Thread.IsDone;
            public override void Consume(uint thread) { }
        }

        private readonly Dictionary<uint, Waitable> waitables = new Dictionary<uint, Waitable>();
        private readonly HashSet<uint> noThreadCalls = new HashSet<uint>();   // DisableThreadLibraryCalls

        // A thread inside SleepConditionVariable*: the lock it gave up and the
        // wake count it saw, kept across the calls it makes while it waits.
        private sealed class ConditionWait
        {
            public uint Generation;
            public int Recursion;
            public bool Shared;
        }

        private readonly Dictionary<uint, ConditionWait> conditionWaits = new Dictionary<uint, ConditionWait>();

        private uint Me => process.CurrentThread.Id;

        private void InstallThreads(GuestImports i)
        {
            const string k = "kernel32.dll";
            process.ThreadStarting = AttachThread;

            i.Register(k, "CreateThread", CallConv.Stdcall, 6, c =>
            {
                var thread = process.CreateThread(c.Arg(2), c.Arg(3), c.Arg(1), (c.Arg(4) & CreateSuspended) != 0);
                var handle = NewHandle();
                thread.Handle = handle;
                waitables[handle] = new ThreadObject { Thread = thread };
                if (c.Arg(5) != 0) memory.Write32(c.Arg(5), thread.Id);
                return handle;
            });
            i.Register(k, "ExitThread", CallConv.Stdcall, 1, c => { process.ExitCurrentThread(c.Arg(0)); return 0; });
            // _endthreadex on a thread started by a DLL: modules stay loaded, so only the exit is real.
            i.Register(k, "FreeLibraryAndExitThread", CallConv.Stdcall, 2, c => { process.ExitCurrentThread(c.Arg(1)); return 0; });
            i.Register(k, "TerminateThread", CallConv.Stdcall, 2, c =>
            {
                var t = ThreadFor(c.Arg(0));
                if (t == null) { process.LastError = ErrorInvalidHandle; return 0; }
                if (t == process.CurrentThread) { process.ExitCurrentThread(c.Arg(1)); return 0; }
                t.ExitCode = c.Arg(1);
                t.IsDone = true;
                return 1;
            });
            i.Register(k, "GetExitCodeThread", CallConv.Stdcall, 2, c =>
            {
                var t = ThreadFor(c.Arg(0));
                if (t == null) { process.LastError = ErrorInvalidHandle; return 0; }
                memory.Write32(c.Arg(1), t.ExitCode);
                return 1;
            });
            i.Register(k, "ResumeThread", CallConv.Stdcall, 1, c =>
            {
                var t = ThreadFor(c.Arg(0));
                if (t == null) return WaitFailed;
                var previous = (uint)t.SuspendCount;
                if (t.SuspendCount > 0) t.SuspendCount--;
                return previous;
            });
            i.Register(k, "SuspendThread", CallConv.Stdcall, 1, c =>
            {
                var t = ThreadFor(c.Arg(0));
                if (t == null) return WaitFailed;
                var previous = (uint)t.SuspendCount;
                t.SuspendCount++;
                if (t == process.CurrentThread) process.Yield();
                return previous;
            });
            i.Register(k, "GetCurrentThreadId", CallConv.Stdcall, 0, c => Me);
            i.Register(k, "GetThreadId", CallConv.Stdcall, 1, c => ThreadFor(c.Arg(0))?.Id ?? 0);
            i.Register(k, "SetThreadPriority", CallConv.Stdcall, 2, c => 1);
            i.Register(k, "GetThreadPriority", CallConv.Stdcall, 1, c => 0);
            i.Register(k, "SetThreadPriorityBoost", CallConv.Stdcall, 2, c => 1);
            i.Register(k, "SetThreadAffinityMask", CallConv.Stdcall, 2, c => 0xF);
            i.Register(k, "SetThreadIdealProcessor", CallConv.Stdcall, 2, c => 0);
            i.Register(k, "SetThreadDescription", CallConv.Stdcall, 2, c => 0);   // S_OK
            i.Register(k, "GetProcessAffinityMask", CallConv.Stdcall, 3, c =>
            {
                if (c.Arg(1) != 0) memory.Write32(c.Arg(1), 0xF);
                if (c.Arg(2) != 0) memory.Write32(c.Arg(2), 0xF);
                return 1;
            });
            i.Register(k, "DisableThreadLibraryCalls", CallConv.Stdcall, 1, c => { noThreadCalls.Add(c.Arg(0)); return 1; });

            i.Register(k, "Sleep", CallConv.Stdcall, 1, c => { Sleep(c.Arg(0)); return 0; });
            i.Register(k, "SleepEx", CallConv.Stdcall, 2, c => { Sleep(c.Arg(0)); return 0; });
            i.Register(k, "SwitchToThread", CallConv.Stdcall, 0, c => { process.Yield(); return 1; });

            // Critical sections: RTL_CRITICAL_SECTION's own fields hold the
            // state, so a section the program zeroed itself works too.
            i.Register(k, "InitializeCriticalSection", CallConv.Stdcall, 1, c => { InitSection(c.Arg(0)); return 0; });
            i.Register(k, "InitializeCriticalSectionAndSpinCount", CallConv.Stdcall, 2, c => { InitSection(c.Arg(0)); return 1; });
            i.Register(k, "InitializeCriticalSectionEx", CallConv.Stdcall, 3, c => { InitSection(c.Arg(0)); return 1; });
            i.Register(k, "DeleteCriticalSection", CallConv.Stdcall, 1, c => 0);
            i.Register(k, "SetCriticalSectionSpinCount", CallConv.Stdcall, 2, c => 0);
            i.Register(k, "EnterCriticalSection", CallConv.Stdcall, 1, c =>
            {
                if (!TryEnterSection(c.Arg(0))) process.Block();
                return 0;
            });
            i.Register(k, "TryEnterCriticalSection", CallConv.Stdcall, 1, c => TryEnterSection(c.Arg(0)) ? 1u : 0u);
            i.Register(k, "LeaveCriticalSection", CallConv.Stdcall, 1, c => { LeaveSection(c.Arg(0)); return 0; });

            // Slim reader/writer locks: 0 free, 1 held exclusively, 2n held by n readers.
            i.Register(k, "InitializeSRWLock", CallConv.Stdcall, 1, c => { memory.Write32(c.Arg(0), 0); return 0; });
            i.Register(k, "AcquireSRWLockExclusive", CallConv.Stdcall, 1, c => { if (!TryExclusive(c.Arg(0))) process.Block(); return 0; });
            i.Register(k, "AcquireSRWLockShared", CallConv.Stdcall, 1, c => { if (!TryShared(c.Arg(0))) process.Block(); return 0; });
            i.Register(k, "TryAcquireSRWLockExclusive", CallConv.Stdcall, 1, c => TryExclusive(c.Arg(0)) ? 1u : 0u);
            i.Register(k, "TryAcquireSRWLockShared", CallConv.Stdcall, 1, c => TryShared(c.Arg(0)) ? 1u : 0u);
            i.Register(k, "ReleaseSRWLockExclusive", CallConv.Stdcall, 1, c => { memory.Write32(c.Arg(0), 0); return 0; });
            i.Register(k, "ReleaseSRWLockShared", CallConv.Stdcall, 1, c =>
            {
                var v = memory.Read32(c.Arg(0));
                memory.Write32(c.Arg(0), v >= 2 ? v - 2 : 0);
                return 0;
            });

            // Condition variables: the variable holds a wake count; a sleeper
            // returns once it changes (spurious wakes are allowed by the API).
            i.Register(k, "InitializeConditionVariable", CallConv.Stdcall, 1, c => { memory.Write32(c.Arg(0), 0); return 0; });
            i.Register(k, "WakeConditionVariable", CallConv.Stdcall, 1, c => { Wake(c.Arg(0)); return 0; });
            i.Register(k, "WakeAllConditionVariable", CallConv.Stdcall, 1, c => { Wake(c.Arg(0)); return 0; });
            i.Register(k, "SleepConditionVariableCS", CallConv.Stdcall, 3, c => SleepCondition(c.Arg(0), c.Arg(1), c.Arg(2), false, false));
            i.Register(k, "SleepConditionVariableSRW", CallConv.Stdcall, 4, c =>
                SleepCondition(c.Arg(0), c.Arg(1), c.Arg(2), true, (c.Arg(3) & 1) != 0));

            // Events, semaphores, mutexes (names are ignored: one process).
            i.Register(k, "CreateEventA", CallConv.Stdcall, 4, c => CreateEvent(c.Arg(1) != 0, c.Arg(2) != 0));
            i.Register(k, "CreateEventW", CallConv.Stdcall, 4, c => CreateEvent(c.Arg(1) != 0, c.Arg(2) != 0));
            i.Register(k, "CreateEventExA", CallConv.Stdcall, 4, c => CreateEvent((c.Arg(2) & 1) != 0, (c.Arg(2) & 2) != 0));
            i.Register(k, "CreateEventExW", CallConv.Stdcall, 4, c => CreateEvent((c.Arg(2) & 1) != 0, (c.Arg(2) & 2) != 0));
            i.Register(k, "SetEvent", CallConv.Stdcall, 1, c => SignalEvent(c.Arg(0), true));
            i.Register(k, "ResetEvent", CallConv.Stdcall, 1, c => SignalEvent(c.Arg(0), false));
            i.Register(k, "PulseEvent", CallConv.Stdcall, 1, c => SignalEvent(c.Arg(0), false));
            i.Register(k, "CreateSemaphoreA", CallConv.Stdcall, 4, c => CreateSemaphore((int)c.Arg(1), (int)c.Arg(2)));
            i.Register(k, "CreateSemaphoreW", CallConv.Stdcall, 4, c => CreateSemaphore((int)c.Arg(1), (int)c.Arg(2)));
            i.Register(k, "CreateSemaphoreExW", CallConv.Stdcall, 6, c => CreateSemaphore((int)c.Arg(1), (int)c.Arg(2)));
            i.Register(k, "ReleaseSemaphore", CallConv.Stdcall, 3, c =>
            {
                if (!(Object(c.Arg(0)) is GuestSemaphore s)) { process.LastError = ErrorInvalidHandle; return 0; }
                var add = (int)c.Arg(1);
                if (add <= 0 || s.Count + add > s.Maximum) { process.LastError = ErrorTooManyPosts; return 0; }
                if (c.Arg(2) != 0) memory.Write32(c.Arg(2), (uint)s.Count);
                s.Count += add;
                return 1;
            });
            i.Register(k, "CreateMutexA", CallConv.Stdcall, 3, c => CreateMutex(c.Arg(1) != 0));
            i.Register(k, "CreateMutexW", CallConv.Stdcall, 3, c => CreateMutex(c.Arg(1) != 0));
            i.Register(k, "CreateMutexExW", CallConv.Stdcall, 4, c => CreateMutex((c.Arg(2) & 1) != 0));
            i.Register(k, "ReleaseMutex", CallConv.Stdcall, 1, c =>
            {
                if (!(Object(c.Arg(0)) is GuestMutex m) || m.Owner != Me) { process.LastError = ErrorNotOwner; return 0; }
                if (--m.Recursion == 0) m.Owner = 0;
                return 1;
            });

            i.Register(k, "WaitForSingleObject", CallConv.Stdcall, 2, c => WaitAny(new[] { c.Arg(0) }, false, c.Arg(1)));
            i.Register(k, "WaitForSingleObjectEx", CallConv.Stdcall, 3, c => WaitAny(new[] { c.Arg(0) }, false, c.Arg(1)));
            i.Register(k, "WaitForMultipleObjects", CallConv.Stdcall, 4, c => WaitAny(Handles(c.Arg(1), c.Arg(0)), c.Arg(2) != 0, c.Arg(3)));
            i.Register(k, "WaitForMultipleObjectsEx", CallConv.Stdcall, 5, c => WaitAny(Handles(c.Arg(1), c.Arg(0)), c.Arg(2) != 0, c.Arg(3)));
            i.Register(k, "SignalObjectAndWait", CallConv.Stdcall, 4, c =>
            {
                // Signal once: on a repeat call (the thread asking again) the
                // signal has already been given.
                if (!process.CurrentThread.Blocked) Signal(c.Arg(0));
                return WaitAny(new[] { c.Arg(1) }, false, c.Arg(2));
            });
        }

        // --- threads --------------------------------------------------------

        private GuestThread ThreadFor(uint handle)
        {
            if (handle == PseudoThread) return process.CurrentThread;
            return Object(handle) is ThreadObject t ? t.Thread : null;
        }

        /// <summary>DLL_THREAD_ATTACH for every module that wants it, on the new thread.</summary>
        private void AttachThread(GuestThread thread)
        {
            const uint DllThreadAttach = 2;
            foreach (var image in process.Images)
            {
                if (image == process.MainImage || !image.IsDll || image.EntryPoint == 0) continue;
                if (noThreadCalls.Contains(image.BaseAddress)) continue;
                var result = process.Call(image.EntryPoint, out _, 50_000_000, image.BaseAddress, DllThreadAttach, 0);
                if (!result.Ok) Say($"x86: DLL_THREAD_ATTACH of {image.Name} on thread 0x{thread.Id:X}: {result}");
            }
        }

        private void Sleep(uint milliseconds)
        {
            if (milliseconds == 0) { process.Yield(); return; }
            if (!process.WaitTimedOut(milliseconds)) process.Block();
        }

        // --- critical sections ------------------------------------------------

        // RTL_CRITICAL_SECTION (x86): DebugInfo +0, LockCount +4,
        // RecursionCount +8, OwningThread +0xC, LockSemaphore +0x10, SpinCount +0x14.
        private void InitSection(uint cs)
        {
            memory.Write32(cs + 0x00, 0);
            memory.Write32(cs + 0x04, 0xFFFFFFFF);
            memory.Write32(cs + 0x08, 0);
            memory.Write32(cs + 0x0C, 0);
            memory.Write32(cs + 0x10, 0);
            memory.Write32(cs + 0x14, 0);
        }

        private bool TryEnterSection(uint cs, int recursion = 1)
        {
            var owner = memory.Read32(cs + 0x0C);
            if (owner != 0 && owner != Me) return false;
            memory.Write32(cs + 0x0C, Me);
            memory.Write32(cs + 0x08, memory.Read32(cs + 0x08) + (uint)recursion);
            memory.Write32(cs + 0x04, memory.Read32(cs + 0x04) + 1);
            return true;
        }

        private void LeaveSection(uint cs)
        {
            if (memory.Read32(cs + 0x0C) != Me) return;
            var recursion = (int)memory.Read32(cs + 0x08) - 1;
            memory.Write32(cs + 0x04, memory.Read32(cs + 0x04) - 1);
            if (recursion <= 0)
            {
                memory.Write32(cs + 0x08, 0);
                memory.Write32(cs + 0x0C, 0);
                memory.Write32(cs + 0x04, 0xFFFFFFFF);
            }
            else memory.Write32(cs + 0x08, (uint)recursion);
        }

        // --- SRW locks and condition variables --------------------------------

        private bool TryExclusive(uint srw)
        {
            if (memory.Read32(srw) != 0) return false;
            memory.Write32(srw, 1);
            return true;
        }

        private bool TryShared(uint srw)
        {
            var v = memory.Read32(srw);
            if ((v & 1) != 0) return false;
            memory.Write32(srw, v + 2);
            return true;
        }

        private void Wake(uint cv) => memory.Write32(cv, memory.Read32(cv) + 1);

        private uint SleepCondition(uint cv, uint lockAddress, uint timeout, bool srw, bool shared)
        {
            var me = Me;
            if (!conditionWaits.TryGetValue(me, out var wait))
            {
                // First call: give up the lock and note the wake count.
                wait = new ConditionWait { Generation = memory.Read32(cv), Shared = shared };
                if (srw)
                {
                    var v = memory.Read32(lockAddress);
                    memory.Write32(lockAddress, shared ? (v >= 2 ? v - 2 : 0) : 0);
                }
                else
                {
                    wait.Recursion = (int)memory.Read32(lockAddress + 0x08);
                    memory.Write32(lockAddress + 0x08, 0);
                    memory.Write32(lockAddress + 0x0C, 0);
                    memory.Write32(lockAddress + 0x04, 0xFFFFFFFF);
                }
                conditionWaits[me] = wait;
            }

            var woken = memory.Read32(cv) != wait.Generation;
            var timedOut = !woken && process.WaitTimedOut(timeout);
            if (!woken && !timedOut) { process.Block(); return 0; }

            // Take the lock back before returning, however long that takes.
            var retaken = srw
                ? (wait.Shared ? TryShared(lockAddress) : TryExclusive(lockAddress))
                : TryEnterSection(lockAddress, Math.Max(1, wait.Recursion));
            if (!retaken) { process.Block(); return 0; }

            conditionWaits.Remove(me);
            if (!woken) { process.LastError = ErrorTimeout; return 0; }
            return 1;
        }

        // --- waitable objects ---------------------------------------------------

        private Waitable Object(uint handle) => waitables.TryGetValue(handle, out var w) ? w : null;

        private uint CreateEvent(bool manualReset, bool signaled)
        {
            var handle = NewHandle();
            waitables[handle] = new GuestEvent { ManualReset = manualReset, Signaled = signaled };
            return handle;
        }

        private uint SignalEvent(uint handle, bool signaled)
        {
            if (!(Object(handle) is GuestEvent e)) { process.LastError = ErrorInvalidHandle; return 0; }
            e.Signaled = signaled;
            return 1;
        }

        private void Signal(uint handle)
        {
            switch (Object(handle))
            {
                case GuestEvent e: e.Signaled = true; break;
                case GuestSemaphore s: if (s.Count < s.Maximum) s.Count++; break;
                case GuestMutex m: if (m.Owner == Me && --m.Recursion == 0) m.Owner = 0; break;
            }
        }

        private uint CreateSemaphore(int initial, int maximum)
        {
            var handle = NewHandle();
            waitables[handle] = new GuestSemaphore { Count = Math.Max(0, initial), Maximum = Math.Max(1, maximum) };
            return handle;
        }

        private uint CreateMutex(bool owned)
        {
            var handle = NewHandle();
            var m = new GuestMutex();
            if (owned) m.Consume(Me);
            waitables[handle] = m;
            return handle;
        }

        private uint[] Handles(uint array, uint count)
        {
            count = Math.Min(count, 64);
            var handles = new uint[count];
            for (uint n = 0; n < count; n++) handles[n] = memory.Read32(array + n * 4);
            return handles;
        }

        /// <summary>
        /// WaitForSingle/MultipleObjects. Handles that are not waitable
        /// objects (files, the process) count as signalled, as they are once
        /// any I/O on them has finished.
        /// </summary>
        private uint WaitAny(uint[] handles, bool all, uint timeout)
        {
            var me = Me;
            if (handles.Length == 0) { process.LastError = ErrorInvalidParameter; return WaitFailed; }

            if (all)
            {
                var every = true;
                foreach (var h in handles)
                {
                    var w = Object(h);
                    if (w != null && !w.Ready(me)) { every = false; break; }
                }
                if (every)
                {
                    foreach (var h in handles) Object(h)?.Consume(me);
                    return WaitObject0;
                }
            }
            else
            {
                for (var n = 0; n < handles.Length; n++)
                {
                    var w = Object(handles[n]);
                    if (w == null) return WaitObject0 + (uint)n;
                    if (!w.Ready(me)) continue;
                    w.Consume(me);
                    return WaitObject0 + (uint)n;
                }
            }

            if (process.WaitTimedOut(timeout)) return WaitTimeout;
            process.Block();
            return 0;
        }

        private bool CloseRuntimeHandle(uint handle) => waitables.Remove(handle);
    }
}
