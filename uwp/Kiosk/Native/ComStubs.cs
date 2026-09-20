using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Kiosk.Native
{
    /// <summary>
    /// COM, answered truthfully.
    ///
    /// A stand-in that returns zero is claiming success, and zero is what
    /// success means here. For a function that is supposed to hand back an
    /// object, claiming success without handing one back is the worst possible
    /// answer: the caller uses whatever was already in that variable, decides
    /// the object is broken, and tries again — which is how a game ends up
    /// calling the same function a hundred thousand times and never finishing
    /// starting up. Measured on this console, exactly that.
    ///
    /// Saying "that class is not registered" is both true and useful: an audio
    /// layer told so falls back to silence and the game carries on.
    /// </summary>
    public static class ComStubs
    {
        private const int S_OK = 0;
        private const int REGDB_E_CLASSNOTREG = unchecked((int)0x80040154);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CreateDelegate(
            IntPtr clsid, IntPtr outer, uint context, IntPtr riid, IntPtr result);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr AllocDelegate(UIntPtr size);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void FreeDelegate(IntPtr block);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GuidDelegate(IntPtr target);

        private static CreateDelegate create;
        private static AllocDelegate alloc;
        private static FreeDelegate free;
        private static GuidDelegate guid;

        /// <summary>Which classes were asked for, so the list is measured not guessed.</summary>
        public static readonly List<string> Wanted = new List<string>();

        public static void Install(SystemImports imports)
        {
            create = (clsid, outer, context, riid, result) =>
            {
                try
                {
                    if (clsid != IntPtr.Zero)
                    {
                        var name = Marshal.PtrToStructure<Guid>(clsid).ToString();
                        lock (Wanted)
                        {
                            if (!Wanted.Contains(name) && Wanted.Count < 40) Wanted.Add(name);
                        }
                    }
                    // Whatever was in the caller's variable is not an object.
                    if (result != IntPtr.Zero) Marshal.WriteIntPtr(result, IntPtr.Zero);
                }
                catch
                {
                    // Recording is optional; the answer is not.
                }
                return REGDB_E_CLASSNOTREG;
            };

            // Memory a game allocates through COM is memory it will read from,
            // and a null pointer answered as success faults at the first write.
            alloc = size =>
            {
                try
                {
                    return Marshal.AllocHGlobal((IntPtr)(long)size);
                }
                catch
                {
                    return IntPtr.Zero;
                }
            };
            free = block =>
            {
                if (block == IntPtr.Zero) return;
                try
                {
                    Marshal.FreeHGlobal(block);
                }
                catch
                {
                    // Freeing something we did not allocate is not worth dying for.
                }
            };
            guid = target =>
            {
                if (target == IntPtr.Zero) return REGDB_E_CLASSNOTREG;
                Marshal.StructureToPtr(Guid.NewGuid(), target, false);
                return S_OK;
            };

            var ours = new Dictionary<string, IntPtr>
            {
                { "CoCreateInstance", Marshal.GetFunctionPointerForDelegate(create) },
                { "CoTaskMemAlloc", Marshal.GetFunctionPointerForDelegate(alloc) },
                { "CoTaskMemFree", Marshal.GetFunctionPointerForDelegate(free) },
                { "CoCreateGuid", Marshal.GetFunctionPointerForDelegate(guid) },
            };

            var answers = new Dictionary<string, long>
            {
                { "CoInitialize", S_OK },
                { "CoInitializeEx", S_OK },
                { "CoUninitialize", 0 },
                { "CoCreateInstanceEx", REGDB_E_CLASSNOTREG },
                { "CoGetClassObject", REGDB_E_CLASSNOTREG },
                { "PropVariantClear", S_OK },
                { "CoInitializeSecurity", S_OK },
                { "CoSetProxyBlanket", S_OK },
            };

            foreach (var module in new[]
            {
                "ole32.dll", "OLE32.dll", "Ole32.dll", "combase.dll", "COMBASE.dll",
            })
            {
                foreach (var pair in ours) imports.Overrides[module + "!" + pair.Key] = pair.Value;
                foreach (var pair in answers) imports.Answers[module + "!" + pair.Key] = pair.Value;
            }
        }
    }
}
