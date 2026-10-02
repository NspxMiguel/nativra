using System;
using Nativra.X86.Cpu;

namespace Nativra.X86.Loader
{
    // ole32's GUID text conversions, the part the rest of COM (the registry
    // of classes, the apartments in GuestKernel.Runtime.cs, the class
    // factories in GuestCom.cs) is keyed by.
    public sealed partial class GuestKernel
    {
        private const uint CoEClassString = 0x800401F3, CoEIidString = 0x800401F4;

        /// <summary>A GUID in the registry form "{xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx}" and nothing looser.</summary>
        private static bool TryParseGuidText(string text, out Guid guid) =>
            Guid.TryParseExact(text, "B", out guid);

        private uint WriteGuidString(string text, uint result)
        {
            var at = heap.Alloc((uint)(text.Length + 1) * 2);   // CoTaskMem: the caller frees it with CoTaskMemFree
            memory.WriteUnicode(at, text);
            memory.Write32(result, at);
            return 0;
        }

        private void InstallOle(GuestImports i)
        {
            const string o = "ole32.dll";
            i.Register(o, "CLSIDFromString", CallConv.Stdcall, 2, c =>
            {
                if (c.Arg(0) == 0) { memory.WriteBytes(c.Arg(1), new byte[16]); return 0; }   // NULL names CLSID_NULL
                if (!TryParseGuidText(ReadText(c.Arg(0), true), out var guid)) return CoEClassString;
                memory.WriteBytes(c.Arg(1), guid.ToByteArray());
                return 0;
            });
            i.Register(o, "IIDFromString", CallConv.Stdcall, 2, c =>
            {
                if (c.Arg(0) == 0 || !TryParseGuidText(ReadText(c.Arg(0), true), out var guid)) return CoEIidString;
                memory.WriteBytes(c.Arg(1), guid.ToByteArray());
                return 0;
            });
            foreach (var name in new[] { "StringFromCLSID", "StringFromIID" })
                i.Register(o, name, CallConv.Stdcall, 2, c =>
                    WriteGuidString(new Guid(memory.ReadBytes(c.Arg(0), 16)).ToString("B").ToUpperInvariant(), c.Arg(1)));
        }
    }
}
