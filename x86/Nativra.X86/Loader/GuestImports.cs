using System;
using System.Collections.Generic;

namespace Nativra.X86.Loader
{
    /// <summary>The x86 stack-cleanup conventions a 32-bit import can use.</summary>
    public enum CallConv
    {
        /// <summary>Callee pops its arguments (the Win32 default).</summary>
        Stdcall,
        /// <summary>Caller pops the arguments (the C runtime).</summary>
        Cdecl,
        /// <summary><c>this</c> in ECX, callee pops the stack arguments (MSVC member functions).</summary>
        Thiscall,
    }

    /// <summary>
    /// The guest state visible to a host implementation of an imported function
    /// at the moment the guest calls it: its stack arguments, the <c>this</c>
    /// pointer for a thiscall, and the process whose memory it reads and writes.
    /// </summary>
    public readonly struct GuestCall
    {
        public GuestProcess Process { get; }
        /// <summary>Where the guest will resume; also the address the call was made through.</summary>
        public uint ReturnAddress { get; }
        /// <summary>Guest VA of the first stack argument (ESP+4 at entry).</summary>
        public uint ArgBase { get; }
        /// <summary>ECX at entry — the <c>this</c> pointer on a thiscall.</summary>
        public uint This { get; }

        public GuestCall(GuestProcess process, uint returnAddress, uint argBase, uint thisPtr)
        {
            Process = process;
            ReturnAddress = returnAddress;
            ArgBase = argBase;
            This = thisPtr;
        }

        /// <summary>The i-th 4-byte stack argument.</summary>
        public uint Arg(int i) => Process.Memory.Read32(ArgBase + (uint)i * 4);

        /// <summary>Two consecutive stack slots as a 64-bit argument (i is the low slot index).</summary>
        public ulong Arg64(int i) => Process.Memory.Read32(ArgBase + (uint)i * 4) |
            ((ulong)Process.Memory.Read32(ArgBase + (uint)(i + 1) * 4) << 32);
    }

    /// <summary>
    /// A host-side implementation of a 32-bit imported function. The body reads
    /// its arguments from the <see cref="GuestCall"/> and returns the result as
    /// EAX in the low 32 bits and EDX in the high 32 bits (so a 64-bit return,
    /// or a single 32-bit/HRESULT value, both fit).
    /// </summary>
    public delegate ulong HostCall(GuestCall call);

    /// <summary>A registered host function: its convention, argument count and body.</summary>
    public sealed class HostFunction
    {
        public CallConv Conv { get; }
        public int ArgDwords { get; }
        public HostCall Body { get; }

        public HostFunction(CallConv conv, int argDwords, HostCall body)
        {
            Conv = conv;
            ArgDwords = argDwords;
            Body = body ?? throw new ArgumentNullException(nameof(body));
        }

        /// <summary>How many bytes the callee removes from the stack on return.</summary>
        public int CleanupBytes => Conv == CallConv.Cdecl ? 0 : ArgDwords * 4;
    }

    /// <summary>One imported symbol resolved to a sentinel plus its host handler (if any).</summary>
    public sealed class GuestImport
    {
        public string Module { get; }
        public string Function { get; }
        public int Ordinal { get; }
        public uint Sentinel { get; }
        public HostFunction Handler { get; internal set; }

        public GuestImport(string module, string function, int ordinal, uint sentinel)
        {
            Module = module;
            Function = function;
            Ordinal = ordinal;
            Sentinel = sentinel;
        }

        public bool ByOrdinal => Function == null;

        public override string ToString() =>
            ByOrdinal ? $"{Module}#{Ordinal}" : $"{Module}!{Function}";
    }

    /// <summary>
    /// The import table for a guest process: it hands every imported symbol a
    /// unique sentinel address in an unmapped region, so that when the guest
    /// calls through its IAT the run loop faults on the sentinel and dispatches
    /// to the registered host implementation. Handlers can be registered before
    /// or after the symbol is bound; dispatch looks them up by name each time a
    /// binding is created, so order does not matter.
    /// </summary>
    public sealed class GuestImports
    {
        // A page-unaligned, deliberately unmapped strip high in the guest space.
        // Sentinels are 16 bytes apart so a mis-decoded call near one still lands
        // on unmapped memory rather than a neighbour.
        private const uint Region = 0x7EF00000;
        private const uint Stride = 16;
        private const uint RegionEnd = 0x7EFFFFF0;

        private uint cursor = Region;
        private readonly Dictionary<uint, GuestImport> bySentinel = new Dictionary<uint, GuestImport>();
        private readonly Dictionary<string, GuestImport> byKey = new Dictionary<string, GuestImport>();
        private readonly Dictionary<string, HostFunction> handlers =
            new Dictionary<string, HostFunction>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, uint> data =
            new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Imports that were called but had no registered handler.</summary>
        public IReadOnlyCollection<GuestImport> Missing
        {
            get
            {
                var list = new List<GuestImport>();
                foreach (var i in bySentinel.Values) if (i.Handler == null) list.Add(i);
                return list;
            }
        }

        private static string Key(string module, string function, int ordinal) =>
            function != null ? module + "!" + function : module + "#" + ordinal;

        /// <summary>
        /// Registers a host implementation. Any binding that already exists, or
        /// is created later, for this module/function picks it up.
        /// </summary>
        public void Register(string module, string function, CallConv conv, int argDwords, HostCall body)
        {
            var key = Key(Norm(module), function, -1);
            var fn = new HostFunction(conv, argDwords, body);
            handlers[key] = fn;
            if (byKey.TryGetValue(key, out var existing)) existing.Handler = fn;
        }

        /// <summary>
        /// Registers an exported variable (msvcrt's _iob, _pctype, __argc...):
        /// the IAT slot of an import of it receives the variable's guest
        /// address instead of a sentinel, as the Windows loader would write.
        /// </summary>
        public void RegisterData(string module, string name, uint address) =>
            data[Key(Norm(module), name, -1)] = address;

        /// <summary>The guest address of an exported variable, or 0.</summary>
        public uint DataAddress(string module, string name) =>
            name != null && data.TryGetValue(Key(Norm(module), name, -1), out var at) ? at : 0;

        /// <summary>Registers a handler matched by ordinal instead of name.</summary>
        public void RegisterOrdinal(string module, int ordinal, CallConv conv, int argDwords, HostCall body)
        {
            var key = Key(Norm(module), null, ordinal);
            var fn = new HostFunction(conv, argDwords, body);
            handlers[key] = fn;
            if (byKey.TryGetValue(key, out var existing)) existing.Handler = fn;
        }

        /// <summary>
        /// The <see cref="Pe32Binder"/> the loader calls per import: returns a
        /// stable sentinel address for the symbol, attaching a handler if one is
        /// already registered.
        /// </summary>
        public uint Bind(string module, string function, int ordinal)
        {
            module = Norm(module);
            var key = Key(module, function, ordinal);
            if (data.TryGetValue(key, out var variable)) return variable;
            if (byKey.TryGetValue(key, out var existing)) return existing.Sentinel;

            if (cursor >= RegionEnd) throw new InvalidOperationException("out of import sentinels");
            var sentinel = cursor;
            cursor += Stride;

            var import = new GuestImport(module, function, ordinal, sentinel);
            if (handlers.TryGetValue(key, out var fn)) import.Handler = fn;
            byKey[key] = import;
            bySentinel[sentinel] = import;
            return sentinel;
        }

        /// <summary>True when <paramref name="address"/> is one of our sentinels.</summary>
        public bool TryResolve(uint address, out GuestImport import) =>
            bySentinel.TryGetValue(address, out import);

        /// <summary>True when a host implementation is registered for module!function.</summary>
        public bool HasHandler(string module, string function) =>
            function != null && (handlers.ContainsKey(Key(Norm(module), function, -1)) ||
                                 data.ContainsKey(Key(Norm(module), function, -1)));

        /// <summary>
        /// True when the program's static imports reference <paramref name="module"/>
        /// or a handler is registered for it — i.e. the real loader would have it
        /// loaded, so GetModuleHandle should find it.
        /// </summary>
        public bool KnowsModule(string module)
        {
            var prefix = Norm(module) + "!";
            var ordinalPrefix = Norm(module) + "#";
            foreach (var key in byKey.Keys)
                if (key.StartsWith(prefix, StringComparison.Ordinal) ||
                    key.StartsWith(ordinalPrefix, StringComparison.Ordinal)) return true;
            foreach (var key in handlers.Keys)
                if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>True when an address falls inside the sentinel region at all (used to spot strays).</summary>
        public static bool InRegion(uint address) => address >= Region && address < RegionEnd + Stride;

        private static string Norm(string module) => Canonical(module);

        /// <summary>
        /// The DLL that really implements <paramref name="module"/>. Windows
        /// resolves API sets through a schema; the guest has no schema, so the
        /// families a game or its C runtime import are folded here: the core
        /// sets and kernelbase onto kernel32 (served by host handlers), the C
        /// runtime sets onto ucrtbase (a guest DLL, when one is available).
        /// </summary>
        public static string Canonical(string module)
        {
            if (string.IsNullOrEmpty(module)) return "";
            var m = module.ToLowerInvariant();
            if (m.StartsWith("api-ms-win-crt-", StringComparison.Ordinal)) return "ucrtbase.dll";
            if (m.StartsWith("api-ms-win-core-", StringComparison.Ordinal) ||
                m.StartsWith("api-ms-win-eventing-", StringComparison.Ordinal) ||
                m.StartsWith("api-ms-win-security-base", StringComparison.Ordinal) ||
                m == "kernelbase.dll")
                return "kernel32.dll";
            return m;
        }
    }
}
