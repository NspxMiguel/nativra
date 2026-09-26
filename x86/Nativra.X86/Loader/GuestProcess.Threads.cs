using System;
using System.Collections.Generic;
using System.Diagnostics;
using Nativra.X86.Cpu;

namespace Nativra.X86.Loader
{
    /// <summary>One guest thread: its TEB, stack, static TLS and saved registers.</summary>
    public sealed class GuestThread
    {
        public uint Id { get; }
        /// <summary>The handle CreateThread gave out (0 for the main thread).</summary>
        public uint Handle { get; set; }
        public uint TebBase { get; internal set; }
        public uint StackBase { get; internal set; }
        public uint StackLimit { get; internal set; }
        public uint TlsArray { get; internal set; }

        public bool IsDone { get; internal set; }
        public uint ExitCode { get; internal set; } = StillActive;
        public int SuspendCount { get; internal set; }

        /// <summary>Waiting inside its last import call (it re-asks when scheduled).</summary>
        public bool Blocked { get; internal set; }

        internal const uint StillActive = 259;
        internal readonly CpuState Saved = new CpuState();
        internal readonly FpuUnit.Snapshot Fpu = new FpuUnit.Snapshot();
        internal bool Attached;        // DLL_THREAD_ATTACH has run
        internal bool WaitStarted;     // a timed wait has its deadline
        internal long WaitDeadline;    // Stopwatch milliseconds

        internal GuestThread(uint id)
        {
            Id = id;
        }

        public override string ToString() =>
            $"thread 0x{Id:X}" + (IsDone ? " done" : Blocked ? " waiting" : SuspendCount > 0 ? " suspended" : "");
    }

    // Guest threads are green threads on the one host thread that runs the
    // guest. A thread gives way when it waits (the import it called cannot
    // answer yet: its EIP stays on the import and it asks again when it next
    // runs) and after a fixed number of blocks, so a thread spinning on a flag
    // does not starve the one that sets it. One host thread means the guest's
    // interlocked instructions stay atomic without any host locking, and
    // critical sections, events and condition variables are exact.
    public sealed partial class GuestProcess
    {
        public const uint ProcessId = 0x1234;
        public const uint MainThreadId = 0x1000;

        /// <summary>Blocks a thread runs before the next one gets a turn.</summary>
        public const int SliceBlocks = 20_000;

        /// <summary>How long every thread may wait with nothing able to wake it before the run stops.</summary>
        public int DeadlockMilliseconds { get; set; } = 30_000;

        private readonly List<GuestThread> threads = new List<GuestThread>();
        private uint nextThreadId = MainThreadId + 4;
        private int depth;            // nested Run calls on the host stack
        private int slice;
        private bool switchWanted;
        private bool blocking;
        private int blockedStreak;    // consecutive turns that ended in a wait with nothing run between
        private long allWaitingSince;
        private uint threadExitSentinel;
        private readonly Stopwatch clock = Stopwatch.StartNew();

        public GuestThread CurrentThread { get; private set; }
        public IReadOnlyList<GuestThread> Threads => threads;

        /// <summary>Called with a new thread before its start routine runs (DLL_THREAD_ATTACH).</summary>
        public Action<GuestThread> ThreadStarting { get; set; }

        /// <summary>
        /// Makes the calling import wait: EIP stays on it, the thread gives
        /// way, and the handler runs again when the thread is next scheduled.
        /// </summary>
        public void Block() => blocking = true;

        /// <summary>Lets other threads run once this import has returned (Sleep(0), SwitchToThread).</summary>
        public void Yield() => switchWanted = true;

        /// <summary>
        /// For a handler that waits up to <paramref name="milliseconds"/>:
        /// the first call starts the clock, later calls (the thread asking
        /// again) say whether it has run out. INFINITE never does.
        /// </summary>
        public bool WaitTimedOut(uint milliseconds)
        {
            if (milliseconds == 0xFFFFFFFF) return false;
            var thread = CurrentThread;
            if (!thread.WaitStarted)
            {
                thread.WaitStarted = true;
                thread.WaitDeadline = clock.ElapsedMilliseconds + milliseconds;
                return milliseconds == 0;
            }
            return clock.ElapsedMilliseconds >= thread.WaitDeadline;
        }

        public GuestThread FindThread(uint id)
        {
            foreach (var t in threads) if (t.Id == id) return t;
            return null;
        }

        /// <summary>
        /// A new thread that will call <paramref name="start"/>(<paramref name="parameter"/>)
        /// as a stdcall thread routine; its return value is its exit code.
        /// </summary>
        public GuestThread CreateThread(uint start, uint parameter, uint stackSize, bool suspended)
        {
            var thread = new GuestThread(nextThreadId);
            nextThreadId += 4;
            // 256 KB unless the program asks for more: guest threads share the
            // app's 5 GB with everything else, and most worker threads need
            // far less than the 1 MB default a PE header asks for.
            var size = Math.Max(stackSize, 256u * 1024);
            size = (size + 0xFFFF) & ~0xFFFFu;
            BuildStack(thread, size);
            BuildTeb(thread);
            for (var i = 0; i < tlsTemplates.Count; i++) GiveTlsBlock(thread, (uint)i, tlsTemplates[i]);

            var esp = (thread.StackBase - 16) & ~0xFu;
            esp -= 4; Memory.Write32(esp, parameter);
            esp -= 4; Memory.Write32(esp, threadExitSentinel);   // the routine returns here
            var cpu = thread.Saved;
            cpu.Esp = esp;
            cpu.Eip = start;
            cpu.FsBase = thread.TebBase;
            cpu.EFlags = Flag.Fixed;
            if (suspended) thread.SuspendCount = 1;
            threads.Add(thread);
            return thread;
        }

        /// <summary>Ends the running thread (its routine returned, or ExitThread).</summary>
        public void ExitCurrentThread(uint code)
        {
            var thread = CurrentThread;
            if (thread.Id == MainThreadId) throw new GuestExitException(code);   // as ExitProcess
            thread.ExitCode = code;
            thread.IsDone = true;
            thread.Blocked = false;
            switchWanted = true;
            Jumped();
        }

        private void InstallThreadSentinels()
        {
            Imports.Register("nativra.dll", "ThreadExit", CallConv.Cdecl, 0, c =>
            {
                ExitCurrentThread(Cpu.Eax);
                return 0;
            });
            threadExitSentinel = Imports.Bind("nativra.dll", "ThreadExit", -1);
        }

        private bool Runnable(GuestThread t) => !t.IsDone && t.SuspendCount == 0;

        /// <summary>
        /// Picks who runs next. Null to carry on; a result when the run must
        /// stop (the thread that owns this run finished, or every thread
        /// waits on something nothing can signal).
        /// </summary>
        private GuestRunResult Schedule(GuestThread owner)
        {
            switchWanted = false;
            slice = 0;
            var current = CurrentThread;

            // A nested run (DllMain from LoadLibrary, a callback) holds this
            // thread's host frame: another thread could not unwind past it,
            // so nested runs keep to their own thread.
            if (depth > 1)
            {
                if (current.Blocked) PauseForTime();
                return null;
            }

            GuestThread next = null;
            var start = threads.IndexOf(current);
            for (var k = 1; k <= threads.Count; k++)
            {
                var candidate = threads[(start + k) % threads.Count];
                if (Runnable(candidate)) { next = candidate; break; }
            }

            if (next == null)
                return owner.IsDone ? new GuestRunResult(GuestStop.Exited, exitCode: owner.ExitCode)
                                    : new GuestRunResult(GuestStop.Deadlocked);

            // Everyone waited in turn with nothing run in between: let time
            // pass for timeouts, and give up when nothing can ever wake them.
            var runnable = 0;
            foreach (var t in threads) if (Runnable(t)) runnable++;
            if (blockedStreak >= runnable)
            {
                if (allWaitingSince == 0) allWaitingSince = clock.ElapsedMilliseconds;
                if (clock.ElapsedMilliseconds - allWaitingSince > DeadlockMilliseconds)
                    return new GuestRunResult(GuestStop.Deadlocked);
                PauseForTime();
                blockedStreak = 0;
            }
            else
            {
                allWaitingSince = 0;
            }

            if (next != current) SwitchTo(next);
            return null;
        }

        private void PauseForTime() => System.Threading.Thread.Sleep(1);

        private void SwitchTo(GuestThread next)
        {
            var current = CurrentThread;
            Save(current);
            if (current.IsDone) Retire(current);
            Load(next);
            CurrentThread = next;
            if (!next.Attached)
            {
                next.Attached = true;
                // The attach calls run on the new thread's own stack, then it
                // starts from exactly where it was set up to.
                ThreadStarting?.Invoke(next);
                Load(next);
            }
        }

        private void Save(GuestThread t)
        {
            Array.Copy(Cpu.R, t.Saved.R, 8);
            t.Saved.Eip = Cpu.Eip;
            t.Saved.EFlags = Cpu.EFlags;
            t.Saved.FsBase = Cpu.FsBase;
            Interpreter.Fpu.SaveTo(t.Fpu);
        }

        private void Load(GuestThread t)
        {
            Array.Copy(t.Saved.R, Cpu.R, 8);
            Cpu.Eip = t.Saved.Eip;
            Cpu.EFlags = t.Saved.EFlags;
            Cpu.FsBase = t.Saved.FsBase;
            Interpreter.Fpu.LoadFrom(t.Fpu);
        }

        /// <summary>A finished thread's stack goes back; its TEB stays for GetExitCodeThread-style questions.</summary>
        private void Retire(GuestThread t)
        {
            if (t.StackBase == 0) return;
            Memory.Unmap(t.StackLimit, t.StackBase - t.StackLimit);
            t.StackBase = t.StackLimit = 0;
        }
    }
}
