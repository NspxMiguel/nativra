using System;
using System.Collections.Generic;
using Nativra.X86.Cpu;
using Nativra.X86.Jit;

namespace Nativra.X86.Loader
{
    /// <summary>Why a run of guest code stopped.</summary>
    public enum GuestStop
    {
        /// <summary>Reached the requested stop address (a normal return).</summary>
        Returned,
        /// <summary>Ran past the block/step budget without stopping.</summary>
        Budget,
        /// <summary>Touched memory nothing is mapped at (a guest access violation).</summary>
        Fault,
        /// <summary>Called an import that has no host handler.</summary>
        MissingImport,
    }

    /// <summary>The outcome of a run, with the fault or missing-import detail when relevant.</summary>
    public sealed class GuestRunResult
    {
        public GuestStop Stop { get; }
        public uint FaultAddress { get; }
        public GuestImport Import { get; }

        public GuestRunResult(GuestStop stop, uint faultAddress = 0, GuestImport import = null)
        {
            Stop = stop;
            FaultAddress = faultAddress;
            Import = import;
        }

        public bool Ok => Stop == GuestStop.Returned;
    }

    /// <summary>
    /// A 32-bit guest process running under the x86 core: the guest address
    /// space, its CPU state, a block-JIT engine (with the interpreter as the
    /// fallback for any host that refuses executable memory), the loaded PE32
    /// images, and the import table that traps calls into host code.
    ///
    /// This is the piece that turns the loose primitives — memory, CPU, JIT,
    /// PE loader — into something that can start a program: it builds a 32-bit
    /// TEB and PEB so FS-relative and PEB-walking code works, lays down a stack,
    /// and drives execution with a loop that services each imported call the
    /// moment the guest reaches its sentinel.
    /// </summary>
    public sealed class GuestProcess : IDisposable
    {
        // 32-bit TEB field offsets (NT_TIB and the fields games actually read).
        private const uint TebExceptionList = 0x00;
        private const uint TebStackBase = 0x04;
        private const uint TebStackLimit = 0x08;
        private const uint TebSelf = 0x18;
        private const uint TebTlsPointer = 0x2C;
        private const uint TebPeb = 0x30;
        private const uint TebLastError = 0x34;
        private const uint TebTlsSlots = 0xE10;   // 64 TLS slots for Tls{Get,Set}Value

        // 32-bit PEB field offsets.
        private const uint PebBeingDebugged = 0x02;
        private const uint PebImageBase = 0x08;
        private const uint PebLdr = 0x0C;
        private const uint PebProcessParameters = 0x10;
        private const uint PebProcessHeap = 0x18;
        private const uint PebNumberOfProcessors = 0x64;
        private const uint PebOsMajorVersion = 0xA4;
        private const uint PebOsMinorVersion = 0xA8;
        private const uint PebOsBuildNumber = 0xAC;   // 16-bit

        private const uint BlockSize = 0x1000;
        private const uint DefaultStack = 0x00100000;   // 1 MB

        // A high, unmapped address a called guest function returns to when we
        // invoked it ourselves; reaching it ends the run cleanly.
        public const uint HaltAddress = 0xDEAD0000;

        public GuestMemory Memory { get; }
        public CpuState Cpu { get; }
        public GuestImports Imports { get; } = new GuestImports();
        public JitEngine Jit { get; }
        public Interpreter Interpreter { get; }
        public bool UsesJit => Jit != null;

        public uint TebBase { get; private set; }
        public uint PebBase { get; private set; }
        public uint StackBase { get; private set; }   // high end of the stack
        public uint StackLimit { get; private set; }  // low end

        private readonly List<Pe32Image> images = new List<Pe32Image>();
        public IReadOnlyList<Pe32Image> Images => images;

        public GuestProcess(GuestMemory memory, bool useJit = true)
        {
            Memory = memory ?? throw new ArgumentNullException(nameof(memory));
            Cpu = new CpuState();

            JitEngine jit = null;
            if (useJit && memory.IsNative && IntPtr.Size == 8)
            {
                try { jit = new JitEngine(Cpu, memory); }
                catch (InvalidOperationException) { jit = null; }   // executable memory refused
                catch (ArgumentException) { jit = null; }
            }
            Jit = jit;
            Interpreter = jit != null ? jit.Interpreter : new Interpreter(Cpu, memory);

            BuildStack(DefaultStack);
            BuildTebAndPeb();
        }

        // --- process setup -------------------------------------------------

        private void BuildStack(uint size)
        {
            var low = Memory.FindFree(size, 0x00110000);
            if (low == 0) throw new InvalidOperationException("no room for the guest stack");
            Memory.Map(low, size);
            StackLimit = low;
            StackBase = low + size;
            Cpu.Esp = (StackBase - 16) & ~0xFu;
        }

        private void BuildTebAndPeb()
        {
            PebBase = Alloc(BlockSize, 0x7E000000);
            TebBase = Alloc(BlockSize, PebBase + BlockSize);

            Memory.Write32(TebBase + TebExceptionList, 0xFFFFFFFF); // end of the SEH chain
            Memory.Write32(TebBase + TebStackBase, StackBase);
            Memory.Write32(TebBase + TebStackLimit, StackLimit);
            Memory.Write32(TebBase + TebSelf, TebBase);
            Memory.Write32(TebBase + TebTlsPointer, 0);
            Memory.Write32(TebBase + TebPeb, PebBase);
            Memory.Write32(TebBase + TebLastError, 0);

            Memory.Write8(PebBase + PebBeingDebugged, 0);
            Memory.Write32(PebBase + PebImageBase, 0);   // filled by the first image
            Memory.Write32(PebBase + PebLdr, 0);
            Memory.Write32(PebBase + PebProcessParameters, 0);
            Memory.Write32(PebBase + PebProcessHeap, 0);
            Memory.Write32(PebBase + PebNumberOfProcessors, 4);
            Memory.Write32(PebBase + PebOsMajorVersion, 10);
            Memory.Write32(PebBase + PebOsMinorVersion, 0);
            Memory.Write16(PebBase + PebOsBuildNumber, 26100);

            Cpu.FsBase = TebBase;
        }

        private uint Alloc(uint size, uint hint)
        {
            var at = Memory.FindFree(size, hint);
            if (at == 0) throw new InvalidOperationException("out of guest address space");
            Memory.Map(at, size);
            return at;
        }

        /// <summary>Loads a PE32 image into the process and binds its imports to sentinels.</summary>
        public Pe32Image LoadImage(string name, byte[] bytes)
        {
            var image = Pe32Image.Load(name, bytes, Memory, Imports.Bind);
            images.Add(image);
            if (images.Count == 1 && image.BaseAddress != 0)
                Memory.Write32(PebBase + PebImageBase, image.BaseAddress);
            return image;
        }

        /// <summary>The one TLS slot index the loader would hand out; kept minimal for now.</summary>
        public uint TlsGetValue(uint slot) =>
            slot < 64 ? Memory.Read32(TebBase + TebTlsSlots + slot * 4) : 0;

        public void TlsSetValue(uint slot, uint value)
        {
            if (slot < 64) Memory.Write32(TebBase + TebTlsSlots + slot * 4, value);
        }

        // --- calling and running -------------------------------------------

        private void Push(uint value)
        {
            Cpu.Esp -= 4;
            Memory.Write32(Cpu.Esp, value);
        }

        /// <summary>
        /// Calls a guest function with the stdcall/cdecl argument order (arguments
        /// pushed right-to-left) and runs until it returns, giving back EAX. The
        /// return address is <see cref="HaltAddress"/>, so the function's own
        /// <c>ret</c> ends the run.
        /// </summary>
        public GuestRunResult Call(uint function, out uint eax, long maxBlocks = 50_000_000, params uint[] args)
        {
            var saved = Cpu.Esp;
            for (var i = args.Length - 1; i >= 0; i--) Push(args[i]);
            Push(HaltAddress);
            Cpu.Eip = function;
            var result = Run(HaltAddress, maxBlocks);
            eax = Cpu.Eax;
            // We own this synthetic host->guest frame, so unwind it regardless of
            // the callee's own convention; the caller sees only the result.
            if (result.Ok) Cpu.Esp = saved;
            return result;
        }

        /// <summary>
        /// Runs guest code until EIP reaches <paramref name="stopEip"/>, servicing
        /// each imported call along the way. Stops early on a fault, a missing
        /// import, or the block budget.
        /// </summary>
        public GuestRunResult Run(uint stopEip, long maxBlocks = 50_000_000)
        {
            for (long i = 0; i < maxBlocks; i++)
            {
                var eip = Cpu.Eip;
                if (eip == stopEip) return new GuestRunResult(GuestStop.Returned);

                if (Imports.TryResolve(eip, out var import))
                {
                    if (import.Handler == null)
                        return new GuestRunResult(GuestStop.MissingImport, eip, import);
                    Dispatch(import);
                    continue;
                }

                try
                {
                    if (Jit != null) Jit.RunBlock();
                    else Interpreter.Step();
                }
                catch (GuestFaultException fe)
                {
                    return new GuestRunResult(GuestStop.Fault, fe.Address);
                }
                catch (GuestException ge)
                {
                    var addr = ge.Information != null && ge.Information.Length > 1
                        ? ge.Information[ge.Information.Length - 1] : ge.Eip;
                    return new GuestRunResult(GuestStop.Fault, addr);
                }
            }
            return new GuestRunResult(GuestStop.Budget);
        }

        private void Dispatch(GuestImport import)
        {
            var esp = Cpu.Esp;
            var returnAddress = Memory.Read32(esp);
            var call = new GuestCall(this, returnAddress, esp + 4, Cpu.Ecx);
            var result = import.Handler.Body(call);

            Cpu.Eax = (uint)result;
            Cpu.Edx = (uint)(result >> 32);
            Cpu.Esp = esp + 4 + (uint)import.Handler.CleanupBytes;   // pop return + callee-cleaned args
            Cpu.Eip = returnAddress;
        }

        public void Dispose()
        {
            Jit?.Dispose();
        }
    }
}
