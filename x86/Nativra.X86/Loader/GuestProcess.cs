using System;
using System.Runtime.InteropServices;
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
        /// <summary>The guest raised a software exception (RaiseException; a C++ throw) that nothing handled.</summary>
        Raised,
        /// <summary>Every guest thread waits on something nothing can signal.</summary>
        Deadlocked,
        /// <summary>A host handler failed with an exception of its own (a bug or a host refusal, not the guest's doing).</summary>
        HostError,
    }

    /// <summary>The outcome of a run, with the fault, missing-import or exit detail when relevant.</summary>
    public sealed class GuestRunResult
    {
        public GuestStop Stop { get; }
        public uint FaultAddress { get; }
        public GuestImport Import { get; }
        public uint ExitCode { get; }

        /// <summary>For <see cref="GuestStop.HostError"/>: the host exception, in full.</summary>
        public string Detail { get; }

        public GuestRunResult(GuestStop stop, uint faultAddress = 0, GuestImport import = null, uint exitCode = 0, string detail = null)
        {
            Stop = stop;
            FaultAddress = faultAddress;
            Import = import;
            ExitCode = exitCode;
            Detail = detail;
        }

        public bool Ok => Stop == GuestStop.Returned;

        public override string ToString()
        {
            switch (Stop)
            {
                case GuestStop.Fault: return $"fault at 0x{FaultAddress:X8}";
                case GuestStop.MissingImport: return $"missing import {Import}";
                case GuestStop.Exited: return $"exited with code {ExitCode}";
                case GuestStop.Raised:
                    return $"raised exception 0x{ExitCode:X8} at 0x{FaultAddress:X8}" + (Detail != null ? $" ({Detail})" : "");
                case GuestStop.HostError:
                {
                    var first = Detail ?? "";
                    var line = first.IndexOf('\n');
                    return $"host error in {Import}: {(line > 0 ? first.Substring(0, line).TrimEnd() : first)}";
                }
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
    public sealed partial class GuestProcess : IDisposable
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

        private const uint PebLdrData = 0x800;   // PEB_LDR_DATA, inside the PEB page past the PEB itself
        private const uint PebParameters = 0x900;   // RTL_USER_PROCESS_PARAMETERS, likewise
        private const uint ParametersSize = 0x2A0;

        /// <summary>Guest VA of RTL_USER_PROCESS_PARAMETERS (PEB+0x10).</summary>
        public uint ProcessParameters { get; private set; }

        private const uint BlockSize = 0x1000;
        private const uint MaxTlsModules = BlockSize / 4;
        private uint tlsModules;
        // Every module's TLS directory by slot: a new thread gets its own copy
        // of each template.
        private readonly List<Pe32Tls> tlsTemplates = new List<Pe32Tls>();
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

        /// <summary>The running thread's TEB (what FS points at).</summary>
        public uint TebBase => CurrentThread.TebBase;
        public uint PebBase { get; private set; }
        public uint StackBase => CurrentThread.StackBase;     // high end of the running thread's stack
        public uint StackLimit => CurrentThread.StackLimit;   // low end

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
            // The JIT emits x64 code: an arm64 host (a Mac running the tests) interprets.
            if (useJit && memory.IsNative && IntPtr.Size == 8 && RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
            {
                try { jit = new JitEngine(Cpu, memory); }
                catch (InvalidOperationException) { jit = null; }   // executable memory refused
                catch (ArgumentException) { jit = null; }
            }
            Jit = jit;
            Interpreter = jit != null ? jit.Interpreter : new Interpreter(Cpu, memory);

            var main = new GuestThread(MainThreadId);
            threads.Add(main);
            CurrentThread = main;
            main.Attached = true;   // the loader attaches the program's modules itself
            BuildStack(main, DefaultStack);
            BuildTebAndPeb();
            Cpu.Esp = (main.StackBase - 16) & ~0xFu;
            InstallThreadSentinels();
        }

        // --- process setup -------------------------------------------------

        private void BuildStack(GuestThread thread, uint size)
        {
            var low = Memory.FindFree(size, 0x00110000);
            if (low == 0) throw new InvalidOperationException("no room for a guest stack");
            Memory.Map(low, size);
            thread.StackLimit = low;
            thread.StackBase = low + size;
        }

        private void BuildTebAndPeb()
        {
            PebBase = Alloc(BlockSize, 0x7E000000);
            BuildTeb(CurrentThread);

            Memory.Write8(PebBase + PebBeingDebugged, 0);
            Memory.Write32(PebBase + PebImageBase, 0);   // filled by the main image
            // PEB_LDR_DATA with its three module lists empty (each head points
            // at itself): code that walks them finds nothing instead of a null.
            var ldr = PebBase + PebLdrData;
            Memory.Write32(ldr + 0x00, 0x30);   // Length
            Memory.Write32(ldr + 0x04, 1);      // Initialized
            for (uint head = 0x0C; head <= 0x1C; head += 8)
            {
                Memory.Write32(ldr + head, ldr + head);
                Memory.Write32(ldr + head + 4, ldr + head);
            }
            Memory.Write32(PebBase + PebLdr, ldr);
            // RTL_USER_PROCESS_PARAMETERS: normalised, no console; the kernel
            // fills in the image path and command line strings.
            ProcessParameters = PebBase + PebParameters;
            Memory.Write32(ProcessParameters + 0x00, ParametersSize);   // MaximumLength
            Memory.Write32(ProcessParameters + 0x04, ParametersSize);   // Length
            Memory.Write32(ProcessParameters + 0x08, 1);                // Flags: RTL_USER_PROC_PARAMS_NORMALIZED
            Memory.Write32(PebBase + PebProcessParameters, ProcessParameters);
            Memory.Write32(PebBase + PebProcessHeap, 0);
            Memory.Write32(PebBase + PebNumberOfProcessors, 4);
            Memory.Write32(PebBase + PebOsMajorVersion, 10);
            Memory.Write32(PebBase + PebOsMinorVersion, 0);
            Memory.Write16(PebBase + PebOsBuildNumber, 26100);

            Cpu.FsBase = TebBase;
        }

        /// <summary>A thread's TEB and its static-TLS array, both one page.</summary>
        private void BuildTeb(GuestThread thread)
        {
            var teb = Alloc(BlockSize, PebBase + BlockSize);
            thread.TebBase = teb;
            Memory.Write32(teb + TebExceptionList, 0xFFFFFFFF); // end of the SEH chain
            Memory.Write32(teb + TebStackBase, thread.StackBase);
            Memory.Write32(teb + TebStackLimit, thread.StackLimit);
            Memory.Write32(teb + TebSelf, teb);
            Memory.Write32(teb + TebClientId, ProcessId);
            Memory.Write32(teb + TebClientId + 4, thread.Id);
            thread.TlsArray = Alloc(BlockSize, teb + BlockSize);   // static TLS: one block pointer per module
            Memory.Write32(teb + TebTlsPointer, thread.TlsArray);
            Memory.Write32(teb + TebPeb, PebBase);
            Memory.Write32(teb + TebLastError, 0);
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
                SetUpStaticTls(image);
                images.Add(image);
                modulesByName[key] = image;
                if (cyclic.Remove(key)) BindCycle(key, image);
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
            if (loading.Contains(key)) return null;
            // The program's own folders first (the guest kernel's search), then
            // what the host carries (redistributables, shims).
            var bytes = ModuleSearch?.Invoke(key);
            if (bytes == null)
            {
                if (ModuleSource == null || notCarried.Contains(key)) return null;
                bytes = ModuleSource(key);
                if (bytes == null) { notCarried.Add(key); return null; }
            }
            return LoadImage(key, bytes);
        }

        /// <summary>
        /// Looks for a DLL by file name in the guest's own search path (set by the
        /// guest kernel); null when it is not there.
        /// </summary>
        public Func<string, byte[]> ModuleSearch { get; set; }

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
            // A DLL that imports from one still being mapped (FreeType and
            // HarfBuzz import each other): bound for now, put right once that
            // one is mapped and its exports are known, as Windows does.
            if (image == null && loading.Contains(ModuleKey(module))) cyclic.Add(ModuleKey(module));
            return Imports.Bind(module, function, ordinal);
        }

        private readonly HashSet<string> cyclic = new HashSet<string>();

        /// <summary>Points every import of <paramref name="key"/> bound before it was mapped at its real export.</summary>
        private void BindCycle(string key, Pe32Image target)
        {
            foreach (var image in images)
            {
                foreach (var import in image.Imports)
                {
                    if (!GuestImports.InRegion(import.Bound) || ModuleKey(import.Module) != key) continue;
                    var va = import.Function != null ? target.Export(import.Function) : target.ExportByOrdinal(import.Ordinal);
                    if (va == 0) continue;
                    Memory.Write32(import.SlotAddress, va);
                    import.Bound = va;
                }
            }
        }

        /// <summary>
        /// Runs each loaded DLL's entry point with DLL_PROCESS_ATTACH, in load
        /// order (dependencies first), as the Windows loader would before the
        /// program's own entry. Returns the first failure, or a clean result.
        /// </summary>
        public GuestRunResult InitializeModules(long maxBlocks = 50_000_000)
        {
            var result = AttachModulesFrom(0, maxBlocks);
            if (!result.Ok) return result;
            // The program's own TLS callbacks run last, just before its entry point.
            return MainImage != null ? RunTlsCallbacks(MainImage, maxBlocks) : new GuestRunResult(GuestStop.Returned);
        }

        private readonly HashSet<Pe32Image> attached = new HashSet<Pe32Image>();

        /// <summary>An address as module+offset when it lies in a mapped image.</summary>
        public string Describe(uint address)
        {
            foreach (var image in images)
                if (address >= image.BaseAddress && address - image.BaseAddress < image.ImageSize)
                    return image.Name + "+0x" + (address - image.BaseAddress).ToString("X");
            return "0x" + address.ToString("X8");
        }

        /// <summary>The last DLL whose initialisation did not finish, and how it stopped.</summary>
        public string LastAttachFailure { get; private set; }

        /// <summary>
        /// DLL_PROCESS_ATTACH for every image mapped from position <paramref name="first"/>
        /// of <see cref="Images"/> on that has not had it yet, in list order — which is
        /// dependencies before their dependents. LoadLibrary needs this: a DLL loaded at
        /// run time brings its own imports, and Windows initialises those before it
        /// (Source's dedicated.dll runs straight into tier0's allocator otherwise).
        /// </summary>
        public GuestRunResult AttachModulesFrom(int first, long maxBlocks = 50_000_000)
        {
            var list = images.ToArray();
            for (var n = Math.Max(0, first); n < list.Length; n++)
            {
                var image = list[n];
                if (image == MainImage || attached.Contains(image)) continue;
                attached.Add(image);
                var result = AttachModule(image, maxBlocks);
                if (!result.Ok)
                {
                    LastAttachFailure = image.Name + ": " + result + " at " + Describe(Cpu.Eip);
                    return result;
                }
            }
            return new GuestRunResult(GuestStop.Returned);
        }

        /// <summary>
        /// DLL_PROCESS_ATTACH for one DLL: its TLS callbacks, then DllMain,
        /// as the Windows loader runs them.
        /// </summary>
        public GuestRunResult AttachModule(Pe32Image image, long maxBlocks = 50_000_000)
        {
            var result = RunTlsCallbacks(image, maxBlocks);
            if (!result.Ok || !image.IsDll || image.EntryPoint == 0) return result;
            return Call(image.EntryPoint, out _, maxBlocks, image.BaseAddress, DllProcessAttach, 0);
        }

        private GuestRunResult RunTlsCallbacks(Pe32Image image, long maxBlocks)
        {
            var tls = image.Tls;
            if (tls != null && tls.CallbacksAddress != 0)
            {
                for (var at = tls.CallbacksAddress; ; at += 4)
                {
                    var callback = Memory.Read32(at);
                    if (callback == 0) break;
                    var result = Call(callback, out _, maxBlocks, image.BaseAddress, DllProcessAttach, 0);
                    if (!result.Ok) return result;
                }
            }
            return new GuestRunResult(GuestStop.Returned);
        }

        /// <summary>
        /// Gives a module with a TLS directory its slot in the static TLS
        /// array (written to its _tls_index) and a block initialised from its
        /// template, which is what __declspec(thread) and thread_local read
        /// through fs:[2Ch].
        /// </summary>
        private void SetUpStaticTls(Pe32Image image)
        {
            var tls = image.Tls;
            if (tls == null) return;
            if (tlsModules >= MaxTlsModules) throw new InvalidOperationException("too many modules with static TLS");

            var index = tlsModules++;
            tlsTemplates.Add(tls);
            if (tls.IndexAddress != 0) Memory.Write32(tls.IndexAddress, index);
            // Every live thread gets the module's block, as the Windows loader
            // does for a DLL with TLS loaded while threads run.
            foreach (var thread in threads)
                if (!thread.IsDone) GiveTlsBlock(thread, index, tls);
        }

        private void GiveTlsBlock(GuestThread thread, uint index, Pe32Tls tls)
        {
            var template = tls.RawDataEnd > tls.RawDataStart ? tls.RawDataEnd - tls.RawDataStart : 0;
            var size = template + tls.ZeroFill;
            var block = Alloc(Math.Max(size, 16u), 0x00300000);
            if (template > 0) Memory.WriteBytes(block, Memory.ReadBytes(tls.RawDataStart, (int)template));
            Memory.Write32(thread.TlsArray + index * 4, block);
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
            var owner = CurrentThread;
            depth++;
            try
            {
                return RunLoop(owner, stopEip, maxBlocks);
            }
            finally
            {
                depth--;
            }
        }

        private GuestRunResult RunLoop(GuestThread owner, uint stopEip, long maxBlocks)
        {
            for (long i = 0; i < maxBlocks; i++)
            {
                if (switchWanted || ++slice >= SliceBlocks)
                {
                    var stop = Schedule(owner);
                    if (stop != null) return stop;
                }
                var eip = Cpu.Eip;
                if (eip == stopEip && CurrentThread == owner) return new GuestRunResult(GuestStop.Returned);

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
                        var where = raised.Address != 0 ? raised.Address : Memory.Read32(Cpu.Esp);
                        var detail = raised.Code == 0xC0000005 && raised.Address != 0
                            ? $"{(raised.Write ? "write to" : "read of")} 0x{raised.Target:X8}"
                            : null;
                        return new GuestRunResult(GuestStop.Raised, where, import, raised.Code, detail);
                    }
                    catch (GuestFaultException fe)
                    {
                        // A handler read or wrote a bad guest pointer the game passed it.
                        return new GuestRunResult(GuestStop.Fault, fe.Address, import);
                    }
                    catch (Exception e) when (!(e is OutOfMemoryException) && !(e is StackOverflowException))
                    {
                        // The host's own failure: stop with the import named, never take the layer down.
                        return new GuestRunResult(GuestStop.HostError, eip, import, detail: e.ToString());
                    }
                    continue;
                }

                blockedStreak = 0;
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
                    // A processor exception: the guest's own SEH handlers get it
                    // first (a game that probes memory under __try expects that).
                    if (HardwareException != null && HardwareException(ge)) continue;
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
            blocking = false;
            hostWake = false;
            var result = import.Handler.Body(call);
            var thread = CurrentThread;
            if (blocking)
            {
                // The call waits: EIP stays on the import, so the thread asks
                // again each time it is scheduled, until the answer is ready.
                thread.Blocked = true;
                switchWanted = true;
                blockedStreak++;
                if (hostWake) allWaitingSince = 0;   // the host can still wake it: this is not a deadlock
                return;
            }
            thread.Blocked = false;
            thread.WaitStarted = false;
            blockedStreak = 0;
            if (jumped) return;   // the handler set the whole CPU state itself

            Cpu.Eax = (uint)result;
            Cpu.Edx = (uint)(result >> 32);
            Cpu.Esp = esp + 4 + (uint)import.Handler.CleanupBytes;   // pop return + callee-cleaned args
            Cpu.Eip = returnAddress;
            CallTrace?.Invoke(import + "(" + TraceArg(esp + 4) + "," + TraceArg(esp + 8) + "," + TraceArg(esp + 12) + "," +
                TraceArg(esp + 16) + ") = 0x" + ((uint)result).ToString("X") + " ret 0x" + returnAddress.ToString("X8") +
                " tid " + CurrentThread.Id.ToString("X"));
        }

        private string TraceArg(uint at) => Memory.IsMapped(at) ? "0x" + Memory.Read32(at).ToString("X") : "?";

        /// <summary>
        /// Called after each served import with its first four stack arguments and
        /// its result, when set (a desktop run's --trace); null costs nothing.
        /// </summary>
        public Action<string> CallTrace { get; set; }

        private bool jumped;

        /// <summary>
        /// Offered each processor exception (access violation, divide by zero,
        /// int3, …) before it stops the run; true when it set the guest up to
        /// handle it (SEH dispatch), false to stop with a fault.
        /// </summary>
        public Func<GuestException, bool> HardwareException { get; set; }
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
