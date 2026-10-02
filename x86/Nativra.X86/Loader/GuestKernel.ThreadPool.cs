using System;
using System.Collections.Generic;

namespace Nativra.X86.Loader
{
    // The Vista thread pool (CreateThreadpool*), timer queues and the wait
    // registration APIs. Every callback runs on a guest thread of its own (see
    // the service threads in GuestKernel.Libraries): a timer or wait object
    // keeps one while it is armed, a work item gets one per submission. So a
    // callback that blocks stalls nothing but itself, and nothing here runs
    // guest code or touches a guest object from the host thread. Pointers
    // handed to the guest (PTP_TIMER, the callback instance, ...) are opaque
    // handles: the programs only pass them back.
    public sealed partial class GuestKernel
    {
        private enum PoolKind { Work, Timer, Wait, QueueTimer, RegisteredWait, Callback }

        /// <summary>What a callback asked to have done when it returns (the *WhenCallbackReturns calls).</summary>
        private sealed class PoolInstance
        {
            public uint Handle, Event, Semaphore, SemaphoreCount, Mutex, Section, Library;
        }

        private sealed class PoolObject
        {
            public PoolKind Kind;
            public uint Handle, Callback, Context, Queue;
            public long Due = -1;               // Milliseconds clock; -1 when not set
            public uint Period;
            public bool Armed, Once;            // waits: a handle is being waited on; fire only once
            public uint WaitHandle, TimeoutMs = 0xFFFFFFFF;
            public bool Running, Closed, HasThread;
            public int Outstanding, Queued, Cancel;   // work: submitted, not yet started, to be skipped
            public uint CompletionEvent;        // signalled once a deleted timer's callback has returned
            public PoolInstance Instance;
        }

        private const uint WaitTimeoutResult = 0x102;
        private const uint ErrorIoPending = 997;

        private readonly Dictionary<uint, PoolObject> poolObjects = new Dictionary<uint, PoolObject>();
        private readonly Dictionary<uint, PoolInstance> poolInstances = new Dictionary<uint, PoolInstance>();
        private readonly HashSet<uint> timerQueues = new HashSet<uint>();
        private readonly List<uint> freedByCallbacks = new List<uint>();
        private uint stackGuarantee;

        /// <summary>Modules a callback asked FreeLibraryWhenCallbackReturns for (modules stay loaded; this records the request).</summary>
        public IReadOnlyList<uint> LibrariesFreedByCallbacks => freedByCallbacks;

        private void InstallThreadPool(GuestImports i)
        {
            const string k = "kernel32.dll";

            // Work items.
            i.Register(k, "CreateThreadpoolWork", CallConv.Stdcall, 3, c => NewPoolObject(PoolKind.Work, c.Arg(0), c.Arg(1)));
            i.Register(k, "SubmitThreadpoolWork", CallConv.Stdcall, 1, c => { SubmitWork(c.Arg(0)); return 0; });
            i.Register(k, "WaitForThreadpoolWorkCallbacks", CallConv.Stdcall, 2, c => WaitForCallbacks(c.Arg(0), c.Arg(1) != 0));
            i.Register(k, "CloseThreadpoolWork", CallConv.Stdcall, 1, c => { ClosePoolObject(c.Arg(0)); return 0; });
            i.Register(k, "TrySubmitThreadpoolCallback", CallConv.Stdcall, 3, c =>
            {
                var handle = NewPoolObject(PoolKind.Callback, c.Arg(0), c.Arg(1));
                var o = poolObjects[handle];
                o.Closed = true;   // nobody holds it: it goes with its one callback
                poolObjects.Remove(handle);
                StartWork(o);
                return 1;
            });

            // Timers: FILETIME due time, negative relative (100 ns), positive absolute.
            i.Register(k, "CreateThreadpoolTimer", CallConv.Stdcall, 3, c => NewPoolObject(PoolKind.Timer, c.Arg(0), c.Arg(1)));
            i.Register(k, "SetThreadpoolTimer", CallConv.Stdcall, 4, c =>
            {
                if (!poolObjects.TryGetValue(c.Arg(0), out var o)) return 0;
                var due = FileTimeToDelay(c.Arg(1));
                o.Period = c.Arg(2);
                o.Due = due < 0 ? -1 : Milliseconds + due;   // NULL cancels
                if (o.Due >= 0) StartPoolThread(o);
                return 0;
            });
            i.Register(k, "IsThreadpoolTimerSet", CallConv.Stdcall, 1, c => poolObjects.TryGetValue(c.Arg(0), out var o) && o.Due >= 0 ? 1u : 0u);
            i.Register(k, "WaitForThreadpoolTimerCallbacks", CallConv.Stdcall, 2, c => WaitForCallbacks(c.Arg(0), c.Arg(1) != 0));
            i.Register(k, "CloseThreadpoolTimer", CallConv.Stdcall, 1, c => { ClosePoolObject(c.Arg(0)); return 0; });

            // Waits: one callback per SetThreadpoolWait, as on Windows.
            i.Register(k, "CreateThreadpoolWait", CallConv.Stdcall, 3, c => NewPoolObject(PoolKind.Wait, c.Arg(0), c.Arg(1)));
            i.Register(k, "SetThreadpoolWait", CallConv.Stdcall, 3, c =>
            {
                if (!poolObjects.TryGetValue(c.Arg(0), out var o)) return 0;
                if (c.Arg(1) == 0) { o.Armed = false; return 0; }   // NULL handle cancels
                var timeout = FileTimeToDelay(c.Arg(2));
                o.WaitHandle = c.Arg(1);
                o.Due = timeout < 0 ? -1 : Milliseconds + timeout;
                o.Armed = true;
                StartPoolThread(o);
                return 0;
            });
            i.Register(k, "WaitForThreadpoolWaitCallbacks", CallConv.Stdcall, 2, c => WaitForCallbacks(c.Arg(0), c.Arg(1) != 0));
            i.Register(k, "CloseThreadpoolWait", CallConv.Stdcall, 1, c => { ClosePoolObject(c.Arg(0)); return 0; });

            // What a callback asks for as it returns.
            i.Register(k, "SetEventWhenCallbackReturns", CallConv.Stdcall, 2, c => { With(c.Arg(0), n => n.Event = c.Arg(1)); return 0; });
            i.Register(k, "ReleaseSemaphoreWhenCallbackReturns", CallConv.Stdcall, 3, c =>
            {
                With(c.Arg(0), n => { n.Semaphore = c.Arg(1); n.SemaphoreCount = c.Arg(2); });
                return 0;
            });
            i.Register(k, "ReleaseMutexWhenCallbackReturns", CallConv.Stdcall, 2, c => { With(c.Arg(0), n => n.Mutex = c.Arg(1)); return 0; });
            i.Register(k, "LeaveCriticalSectionWhenCallbackReturns", CallConv.Stdcall, 2, c => { With(c.Arg(0), n => n.Section = c.Arg(1)); return 0; });
            i.Register(k, "FreeLibraryWhenCallbackReturns", CallConv.Stdcall, 2, c => { With(c.Arg(0), n => n.Library = c.Arg(1)); return 0; });
            i.Register(k, "CallbackMayRunLong", CallConv.Stdcall, 1, c => 1);
            i.Register(k, "DisassociateCurrentThreadFromCallback", CallConv.Stdcall, 1, c => 0);

            // Pools and cleanup groups exist only as handles: every pool is the one set of guest threads.
            i.Register(k, "CreateThreadpool", CallConv.Stdcall, 1, c => NewHandle());
            i.Register(k, "CloseThreadpool", CallConv.Stdcall, 1, c => 0);
            i.Register(k, "SetThreadpoolThreadMaximum", CallConv.Stdcall, 2, c => 0);
            i.Register(k, "SetThreadpoolThreadMinimum", CallConv.Stdcall, 2, c => 1);
            i.Register(k, "CreateThreadpoolCleanupGroup", CallConv.Stdcall, 0, c => NewHandle());
            i.Register(k, "CloseThreadpoolCleanupGroup", CallConv.Stdcall, 1, c => 0);
            i.Register(k, "CloseThreadpoolCleanupGroupMembers", CallConv.Stdcall, 3, c => 0);

            // Timer queues: WAITORTIMERCALLBACK(context, TRUE).
            i.Register(k, "CreateTimerQueue", CallConv.Stdcall, 0, c => { var q = NewHandle(); timerQueues.Add(q); return q; });
            i.Register(k, "CreateTimerQueueTimer", CallConv.Stdcall, 7, c =>
            {
                var o = NewPoolObject(PoolKind.QueueTimer, c.Arg(2), c.Arg(3));
                var t = poolObjects[o];
                t.Queue = c.Arg(1);
                t.Period = c.Arg(5);
                t.Due = c.Arg(4) == InvalidHandleValue ? -1 : Milliseconds + c.Arg(4);
                if (c.Arg(0) != 0) memory.Write32(c.Arg(0), o);
                if (t.Due >= 0) StartPoolThread(t);
                return 1;
            });
            i.Register(k, "ChangeTimerQueueTimer", CallConv.Stdcall, 4, c =>
            {
                if (!poolObjects.TryGetValue(c.Arg(1), out var t) || t.Kind != PoolKind.QueueTimer) { process.LastError = ErrorInvalidParameter; return 0; }
                t.Period = c.Arg(3);
                t.Due = c.Arg(2) == InvalidHandleValue ? -1 : Milliseconds + c.Arg(2);
                if (t.Due >= 0) StartPoolThread(t);
                return 1;
            });
            i.Register(k, "DeleteTimerQueueTimer", CallConv.Stdcall, 3, c => DeleteQueueTimer(c.Arg(1), c.Arg(2)));
            i.Register(k, "DeleteTimerQueueEx", CallConv.Stdcall, 2, c => DeleteTimerQueue(c.Arg(0), c.Arg(1)));
            i.Register(k, "DeleteTimerQueue", CallConv.Stdcall, 1, c => DeleteTimerQueue(c.Arg(0), 0));

            // RegisterWaitForSingleObject: WAITORTIMERCALLBACK(context, timedOut).
            i.Register(k, "RegisterWaitForSingleObject", CallConv.Stdcall, 6, c =>
            {
                var handle = RegisterWait(c.Arg(1), c.Arg(2), c.Arg(3), c.Arg(4), c.Arg(5));
                if (c.Arg(0) != 0) memory.Write32(c.Arg(0), handle);
                return 1;
            });
            i.Register(k, "RegisterWaitForSingleObjectEx", CallConv.Stdcall, 5, c => RegisterWait(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3), c.Arg(4)));
            i.Register(k, "UnregisterWait", CallConv.Stdcall, 1, c => UnregisterWait(c.Arg(0), 0));
            i.Register(k, "UnregisterWaitEx", CallConv.Stdcall, 2, c => UnregisterWait(c.Arg(0), c.Arg(1)));

            // Small things the Concurrency Runtime and friends probe for.
            i.Register(k, "SetThreadStackGuarantee", CallConv.Stdcall, 1, c =>
            {
                if (c.Arg(0) != 0)
                {
                    var previous = stackGuarantee;
                    if (memory.Read32(c.Arg(0)) != 0) stackGuarantee = memory.Read32(c.Arg(0));
                    memory.Write32(c.Arg(0), previous);
                }
                return 1;
            });
            i.Register(k, "GetNumaHighestNodeNumber", CallConv.Stdcall, 1, c =>
            {
                if (c.Arg(0) != 0) memory.Write32(c.Arg(0), 0);
                return 1;
            });
            HostCall capture = c => CaptureStackBackTrace(c);
            i.Register(k, "RtlCaptureStackBackTrace", CallConv.Stdcall, 4, capture);
            i.Register("ntdll.dll", "RtlCaptureStackBackTrace", CallConv.Stdcall, 4, capture);
            // A breakpoint is an exception like any other: whoever handles it sees EXCEPTION_BREAKPOINT.
            HostCall breakpoint = c => { TailCall(c, 0, process.Imports.Bind("kernel32.dll", "RaiseException", -1), 0x80000003, 0, 0, 0); return 0; };
            i.Register(k, "DebugBreak", CallConv.Stdcall, 0, breakpoint);
            i.Register("ntdll.dll", "DbgBreakPoint", CallConv.Stdcall, 0, breakpoint);
            i.Register(k, "FatalAppExitA", CallConv.Stdcall, 2, c => FatalAppExit(c.Arg(1), false));
            i.Register(k, "FatalAppExitW", CallConv.Stdcall, 2, c => FatalAppExit(c.Arg(1), true));
        }

        // --- objects ------------------------------------------------------------

        private uint NewPoolObject(PoolKind kind, uint callback, uint context)
        {
            var o = new PoolObject { Kind = kind, Handle = NewHandle(), Callback = callback, Context = context };
            poolObjects[o.Handle] = o;
            return o.Handle;
        }

        private void With(uint instance, Action<PoolInstance> change)
        {
            if (poolInstances.TryGetValue(instance, out var n)) change(n);
        }

        /// <summary>
        /// A FILETIME timeout (a pointer) as milliseconds from now: negative is relative, positive
        /// absolute, 0 for a time already past. -1 for no time at all (a NULL pointer).
        /// </summary>
        private long FileTimeToDelay(uint pointer)
        {
            if (pointer == 0) return -1;
            var value = (long)memory.Read64(pointer);
            if (value == 0) return 0;
            var hundredNs = value < 0 ? -value : value - UtcNow.ToFileTimeUtc();
            return Math.Max(0, (hundredNs + 9999) / 10000);
        }

        private void ClosePoolObject(uint handle)
        {
            if (!poolObjects.TryGetValue(handle, out var o)) return;
            // A callback still running finishes on its own thread, which then ends.
            o.Closed = true;
            o.Due = -1;
            o.Armed = false;
            poolObjects.Remove(handle);
        }

        private uint WaitForCallbacks(uint handle, bool cancelPending)
        {
            if (!poolObjects.TryGetValue(handle, out var o)) return 0;
            if (cancelPending)
            {
                o.Cancel = o.Queued;
                o.Due = -1;
                o.Armed = false;
            }
            if (o.Running || o.Outstanding > 0) process.Block();
            return 0;
        }

        // --- threads ----------------------------------------------------------------

        private void StartPoolThread(PoolObject o)
        {
            if (o.HasThread) return;
            o.HasThread = true;
            StartService(() => PoolStep(o));
        }

        private void SubmitWork(uint handle)
        {
            if (!poolObjects.TryGetValue(handle, out var o)) return;
            o.Outstanding++;
            o.Queued++;
            StartWork(o);
        }

        // One submission, one thread: two callbacks that wait for each other still both run.
        private void StartWork(PoolObject o)
        {
            if (o.Kind == PoolKind.Callback) { o.Outstanding++; o.Queued++; }
            var started = false;
            StartService(() =>
            {
                if (started)
                {
                    EndCallback(o);
                    o.Outstanding--;
                    return ServiceStep.Exit;
                }
                started = true;
                o.Queued--;
                if (o.Cancel > 0)
                {
                    o.Cancel--;
                    o.Outstanding--;
                    return ServiceStep.Exit;
                }
                var instance = BeginCallback(o);
                return o.Kind == PoolKind.Callback
                    ? ServiceStep.Call(o.Callback, instance, o.Context)
                    : ServiceStep.Call(o.Callback, instance, o.Context, o.Handle);
            });
        }

        private uint BeginCallback(PoolObject o)
        {
            var instance = new PoolInstance { Handle = NewHandle() };
            poolInstances[instance.Handle] = instance;
            o.Instance = instance;
            return instance.Handle;
        }

        /// <summary>The callback has returned: do what it asked for through the *WhenCallbackReturns calls.</summary>
        private void EndCallback(PoolObject o)
        {
            var n = o.Instance;
            o.Instance = null;
            if (n == null) return;
            poolInstances.Remove(n.Handle);
            if (n.Event != 0) SignalEvent(n.Event, true);
            if (n.Semaphore != 0 && Object(n.Semaphore) is GuestSemaphore s)
                s.Count = Math.Min(s.Maximum, s.Count + (int)n.SemaphoreCount);
            if (n.Mutex != 0 && Object(n.Mutex) is GuestMutex m && m.Owner == Me && --m.Recursion <= 0) { m.Recursion = 0; m.Owner = 0; }
            if (n.Section != 0) LeaveSection(n.Section);
            if (n.Library != 0) freedByCallbacks.Add(n.Library);
        }

        /// <summary>One step of a timer's or wait's thread: it ends when nothing is left to wait for.</summary>
        private ServiceStep PoolStep(PoolObject o)
        {
            if (o.Running)
            {
                o.Running = false;
                EndCallback(o);
            }
            if (o.Closed) { o.HasThread = false; RetirePoolObject(o); return ServiceStep.Exit; }

            var now = Milliseconds;
            switch (o.Kind)
            {
                case PoolKind.Timer:
                case PoolKind.QueueTimer:
                {
                    if (o.Due < 0) { o.HasThread = false; return ServiceStep.Exit; }
                    // A short wait at most, so a timer set again for sooner is noticed.
                    if (now < o.Due) return ServiceStep.Wait((uint)Math.Min(o.Due - now, 5));
                    o.Due = o.Period == 0 ? -1 : Math.Max(o.Due + o.Period, now + 1);
                    o.Running = true;
                    if (o.Kind == PoolKind.QueueTimer) return ServiceStep.Call(o.Callback, o.Context, 1);
                    return ServiceStep.Call(o.Callback, BeginCallback(o), o.Context, o.Handle);
                }
                default:
                {
                    if (!o.Armed) { o.HasThread = false; return ServiceStep.Exit; }
                    // A handle that is no waitable object (a file, say) counts as signalled, as in WaitAny.
                    var w = Object(o.WaitHandle);
                    uint result;
                    if (w == null || w.Ready(Me)) { w?.Consume(Me); result = WaitObject0; }
                    else if (o.Due >= 0 && now >= o.Due) result = WaitTimeoutResult;
                    else return ServiceStep.Wait(1);
                    if (o.Kind == PoolKind.Wait || o.Once) o.Armed = false;
                    else o.Due = o.TimeoutMs == 0xFFFFFFFF ? -1 : now + o.TimeoutMs;
                    o.Running = true;
                    if (o.Kind == PoolKind.RegisteredWait) return ServiceStep.Call(o.Callback, o.Context, result == WaitTimeoutResult ? 1u : 0u);
                    return ServiceStep.Call(o.Callback, BeginCallback(o), o.Context, o.Handle, result);
                }
            }
        }

        private void RetirePoolObject(PoolObject o)
        {
            poolObjects.Remove(o.Handle);
            var done = o.CompletionEvent;
            o.CompletionEvent = 0;
            if (done != 0 && done != InvalidHandleValue) SignalEvent(done, true);
        }

        // --- timer queues and registered waits -------------------------------------------

        private uint DeleteQueueTimer(uint handle, uint completion)
        {
            if (!poolObjects.TryGetValue(handle, out var t) || t.Kind != PoolKind.QueueTimer)
            {
                if (process.CurrentThread.Blocked) return 1;   // asked again after waiting: it has gone
                process.LastError = ErrorInvalidParameter;
                return 0;
            }
            t.Closed = true;
            t.Due = -1;
            if (!t.Running)
            {
                if (completion != 0 && completion != InvalidHandleValue) t.CompletionEvent = completion;
                RetirePoolObject(t);
                return 1;
            }
            // The callback is running: INVALID_HANDLE_VALUE waits for it, an event is signalled when it returns.
            if (completion == InvalidHandleValue) { process.Block(); return 0; }
            t.CompletionEvent = completion;
            process.LastError = ErrorIoPending;
            return 0;
        }

        private uint DeleteTimerQueue(uint queue, uint completion)
        {
            if (!timerQueues.Contains(queue))
            {
                if (process.CurrentThread.Blocked) return 1;
                process.LastError = ErrorInvalidHandle;
                return 0;
            }
            var running = false;
            foreach (var t in new List<PoolObject>(poolObjects.Values))
            {
                if (t.Kind != PoolKind.QueueTimer || t.Queue != queue) continue;
                t.Closed = true;
                t.Due = -1;
                if (t.Running) running = true; else RetirePoolObject(t);
            }
            if (running && completion == InvalidHandleValue) { process.Block(); return 0; }
            timerQueues.Remove(queue);
            if (completion != 0 && completion != InvalidHandleValue) SignalEvent(completion, true);
            return 1;
        }

        private uint RegisterWait(uint handle, uint callback, uint context, uint milliseconds, uint flags)
        {
            const uint ExecuteOnlyOnce = 0x8;
            var id = NewPoolObject(PoolKind.RegisteredWait, callback, context);
            var o = poolObjects[id];
            o.WaitHandle = handle;
            o.TimeoutMs = milliseconds;
            o.Due = milliseconds == InvalidHandleValue ? -1 : Milliseconds + milliseconds;
            o.Once = (flags & ExecuteOnlyOnce) != 0;
            o.Armed = true;
            StartPoolThread(o);
            return id;
        }

        private uint UnregisterWait(uint handle, uint completion)
        {
            if (!poolObjects.TryGetValue(handle, out var o) || o.Kind != PoolKind.RegisteredWait)
            {
                if (process.CurrentThread.Blocked) return 1;
                process.LastError = ErrorInvalidHandle;
                return 0;
            }
            o.Armed = false;
            o.Closed = true;
            if (!o.Running)
            {
                RetirePoolObject(o);
                if (completion != 0 && completion != InvalidHandleValue) SignalEvent(completion, true);
                return 1;
            }
            if (completion == InvalidHandleValue) { process.Block(); return 0; }
            o.CompletionEvent = completion;
            process.LastError = ErrorIoPending;
            return 0;
        }

        // --- odds and ends -----------------------------------------------------------------

        /// <summary>RtlCaptureStackBackTrace: the caller's return address, then the EBP chain while it stays on this thread's stack.</summary>
        private uint CaptureStackBackTrace(GuestCall c)
        {
            uint skip = c.Arg(0), capacity = Math.Min(c.Arg(1), 64), frames = c.Arg(2);
            if (frames == 0) return 0;
            var thread = process.CurrentThread;
            uint low = thread.StackLimit, high = thread.StackBase, count = 0, seen = 0;
            void Add(uint address)
            {
                if (seen++ < skip || count >= capacity) return;
                memory.Write32(frames + count * 4, address);
                count++;
            }
            Add(c.ReturnAddress);
            var ebp = process.Cpu.Ebp;
            while (count < capacity && ebp >= low && ebp + 8 <= high && (ebp & 3) == 0)
            {
                var back = memory.Read32(ebp + 4);
                if (back == 0) break;
                Add(back);
                var next = memory.Read32(ebp);
                if (next <= ebp) break;
                ebp = next;
            }
            if (c.Arg(3) != 0) memory.Write32(c.Arg(3), 0);
            return count;
        }

        private uint FatalAppExit(uint message, bool wide)
        {
            Say("x86: FatalAppExit: " + ReadText(message, wide));
            throw new GuestExitException(1);
        }
    }
}
