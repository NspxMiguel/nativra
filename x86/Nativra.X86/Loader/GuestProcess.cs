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
        /// <summary>The guest ended the process (ExitProcess and friends).</summary>
        Exited,
        /// <summary>The guest raised a software exception (RaiseException; a C++ throw) that nothing dispatches yet.</summary>
        Raised,
    }

    /// <summary>The outcome of a run, with the fault, missing-import or exit detail when relevant.</summary>
    public sealed class GuestRunResult
    {
        public GuestStop Stop { get; }
        public uint FaultAddress { get; }
        public GuestImport Import { get; }
        public uint ExitCode { get; }

        public GuestRunResult(GuestStop stop, uint faultAddress = 0, GuestImport import = null, uint exitCode = 0)
        {
            Stop = stop;
            FaultAddress = faultAddress;
            Import = import;
            ExitCode = exitCode;
        }

        public bool Ok => Stop == GuestStop.Returned;

        public override string ToString()
        {
            switch (Stop)
            {
                case GuestStop.Fault: return $"fault at 0x{FaultAddress:X8}";
                case GuestStop.MissingImport: return $"missing import {Import}";
                case GuestStop.Exited: return $"exited with code {ExitCode}";
                case GuestStop.Raised: return $"raised exception 0x{ExitCode:X8} from 0x{FaultAddress:X8}";
                default: return Stop.ToString().ToLowerInvariant();
            }
        }
    }

    /// <summary>
    /// Thrown by a host handler to end the guest process (ExitProcess). The run
    /// loop turns it into <see cref="GuestStop.Exited"/> instead of letting it
    /// escape to the host.
    /// </summary>
    public sealed class GuestExitException : Exception
    {
        public uint Code { get; }

        public GuestExitException(uint code) : base($"guest exited with code {code}")
        {
            Code = code;
        }
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
    /// links a game's own DLLs into the guest (asking the host for their bytes),
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
        private const uint TebClientId = 0x20;     // process id, then thread id
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

        private const uint DllProcessAttach = 1;

        // A high, unmapped address a called guest function returns to when we
        // invoked it ourselves; reaching it ends the run cleanly.
        public const uint HaltAddress = 0xDEAD0000;

        public GuestMemory Memory { get; }
        public CpuState Cpu { get; }
        public GuestImports Imports { get; } = new GuestImports();
        public JitEngine Jit { get; }
        public Interpreter Interpreter { get; }
        public bool UsesJit => Jit != null && JitRefusal == null;

        /// <summary>
        /// Why the JIT stopped being used mid-run (the host refused to publish an
        /// executable block), or null. The run carries on in the interpreter.
        /// </summary>
        public string JitRefusal { get; private set; }

        public uint TebBase { get; private set; }
        public uint PebBase { get; private set; }
        public uint StackBase { get; private set; }   // high end of the stack
        public uint StackLimit { get; private set; }  // low end

        /// <summary>
        /// Where a game's own DLLs come from: given a module name (lower-cased,
        /// with extension), the host returns its bytes, or null when the game
        /// does not carry it (a system DLL, served by host handlers instead).
        /// </summary>
        public Func<string, byte[]> ModuleSource { get; set; }

        /// <summary>The program itself, once <see cref="LoadExecutable"/> has run.</summary>
        public Pe32Image MainImage { get; private set; }

        private readonly List<Pe32Image> images = new List<Pe32Image>();
        private readonly Dictionary<string, Pe32Image> modulesByName =
            new Dictionary<string, Pe32Image>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> loading = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Modules the host said the game does not carry; asked once, since each
        // question can mean a storage lookup on the console.
        private readonly HashSet<string> notCarried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Every image mapped into the guest, dependencies before their dependents.</summary>
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
            Memory.Write32(TebBase + TebClientId, 0x1234);        // process id
            Memory.Write32(TebBase + TebClientId + 4, 0x1000);    // thread id
            Memory.Write32(TebBase + TebTlsPointer, 0);
            Memory.Write32(TebBase + TebPeb, PebBase);
            Memory.Write32(TebBase + TebLastError, 0);

            Memory.Write8(PebBase + PebBeingDebugged, 0);
            Memory.Write32(PebBase + PebImageBase, 0);   // filled by the main image
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

        /// <summary>The thread's last-error slot, which GetLastError/SetLastError use.</summary>
        public uint LastError
        {
            get => Memory.Read32(TebBase + TebLastError);
            set => Memory.Write32(TebBase + TebLastError, value);
        }

        public uint TlsGetValue(uint slot) =>
            slot < 64 ? Memory.Read32(TebBase + TebTlsSlots + slot * 4) : 0;

        public void TlsSetValue(uint slot, uint value)
        {
            if (slot < 64) Memory.Write32(TebBase + TebTlsSlots + slot * 4, value);
        }

        // --- images and linking --------------------------------------------

        /// <summary>
        /// Loads the program: maps it, links every DLL it needs that the game
        /// carries, and publishes its base in the PEB as the process image.
        /// </summary>
        public Pe32Image LoadExecutable(string name, byte[] bytes)
        {
            var image = LoadImage(name, bytes);
            MainImage = image;
            Memory.Write32(PebBase + PebImageBase, image.BaseAddress);
            return image;
        }

        /// <summary>
        /// Maps one PE32 image and binds its imports: to a game DLL's real
        /// export when the game carries that DLL (loading it first if needed),
        /// otherwise to a sentinel serviced by a host handler.
        /// </summary>
        public Pe32Image LoadImage(string name, byte[] bytes)
        {
            var key = ModuleKey(name);
            loading.Add(key);
            try
            {
                var image = Pe32Image.Load(name, bytes, Memory, ResolveImport);
                images.Add(image);
                modulesByName[key] = image;
                return image;
            }
            finally
            {
                loading.Remove(key);
            }
        }

        /// <summary>The mapped image for a module name, or null if it is not a guest image.</summary>
        public Pe32Image FindModule(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            return modulesByName.TryGetValue(ModuleKey(name), out var image) ? image : null;
        }

        /// <summary>
        /// Maps a game DLL on request (LoadLibrary): returns the image if it is
        /// already mapped or the host can supply it, otherwise null.
        /// </summary>
        public Pe32Image LoadModule(string name)
        {
            var existing = FindModule(name);
            if (existing != null) return existing;
            var key = ModuleKey(name);
            if (ModuleSource == null || loading.Contains(key) || notCarried.Contains(key)) return null;
            var bytes = ModuleSource(key);
            if (bytes == null) { notCarried.Add(key); return null; }
            return LoadImage(key, bytes);
        }

        /// <summary>The address a guest call to module!function should reach.</summary>
        public uint ResolveImport(string module, string function, int ordinal)
        {
            module = GuestImports.Canonical(module);
            var image = LoadModule(module);
            if (image != null)
            {
                var va = function != null ? image.Export(function) : image.ExportByOrdinal(ordinal);
                if (va != 0) return va;
            }
            return Imports.Bind(module, function, ordinal);
        }

        /// <summary>
        /// Runs each loaded DLL's entry point with DLL_PROCESS_ATTACH, in load
        /// order (dependencies first), as the Windows loader would before the
        /// program's own entry. Returns the first failure, or a clean result.
        /// </summary>
        public GuestRunResult InitializeModules(long maxBlocks = 50_000_000)
        {
            foreach (var image in images.ToArray())
            {
                if (image == MainImage || !image.IsDll || image.EntryPoint == 0) continue;
                var result = Call(image.EntryPoint, out _, maxBlocks, image.BaseAddress, DllProcessAttach, 0);
                if (!result.Ok) return result;
            }
            return new GuestRunResult(GuestStop.Returned);
        }

        private static string ModuleKey(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var slash = name.LastIndexOfAny(new[] { '\\', '/' });
            if (slash >= 0) name = name.Substring(slash + 1);
            name = name.ToLowerInvariant();
            return GuestImports.Canonical(name.IndexOf('.') < 0 ? name + ".dll" : name);
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
        /// import, a process exit, or the block budget.
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
                    try
                    {
                        Dispatch(import);
                    }
                    catch (GuestExitException exit)
                    {
                        return new GuestRunResult(GuestStop.Exited, exitCode: exit.Code);
                    }
                    catch (GuestRaisedException raised)
                    {
                        return new GuestRunResult(GuestStop.Raised, Memory.Read32(Cpu.Esp), import, raised.Code);
                    }
                    catch (GuestFaultException fe)
                    {
                        // A handler read or wrote a bad guest pointer the game passed it.
                        return new GuestRunResult(GuestStop.Fault, fe.Address, import);
                    }
                    continue;
                }

                try
                {
                    if (UsesJit) Jit.RunBlock();
                    else Interpreter.Step();
                }
                catch (Exception e) when (UsesJit && (e is InvalidOperationException || e is OutOfMemoryException))
                {
                    // The code cache could not publish a block. Nothing ran (the
                    // block is published before it executes), so the interpreter
                    // picks up from the very same state.
                    JitRefusal = e.Message;
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
            recent.Enqueue(import.ToString());
            if (recent.Count > RecentImportCount) recent.Dequeue();
            jumped = false;
            var result = import.Handler.Body(call);
            if (jumped) return;   // the handler set the whole CPU state itself

            Cpu.Eax = (uint)result;
            Cpu.Edx = (uint)(result >> 32);
            Cpu.Esp = esp + 4 + (uint)import.Handler.CleanupBytes;   // pop return + callee-cleaned args
            Cpu.Eip = returnAddress;
        }

        private bool jumped;
        private const int RecentImportCount = 24;
        private readonly Queue<string> recent = new Queue<string>();

        /// <summary>The last imports the guest called, oldest first: where a stuck or crashed run was.</summary>
        public IReadOnlyCollection<string> RecentImports => recent;

        /// <summary>
        /// Called by a host handler that has set EIP, ESP and the registers
        /// itself (exception dispatch, unwinding): the import returns nowhere,
        /// the guest continues from that state.
        /// </summary>
        public void Jumped() => jumped = true;

        public void Dispose()
        {
            Jit?.Dispose();
        }
    }
}
