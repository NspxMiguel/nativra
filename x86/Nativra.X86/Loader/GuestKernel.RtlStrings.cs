using System;
using System.Text;

namespace Nativra.X86.Loader
{
    public sealed partial class GuestKernel
    {
        private void InstallRtlStringFunctions(GuestImports imports)
        {
            foreach (var wide in new[] { false, true })
            {
                var w = wide;
                var kind = wide ? "Unicode" : "Ansi";
                imports.Register("ntdll.dll", "RtlInit" + kind + "String", CallConv.Stdcall, 2,
                    c => InitRtlString(c.Arg(0), c.Arg(1), w, false));
                imports.Register("ntdll.dll", "RtlInit" + kind + "StringEx", CallConv.Stdcall, 2,
                    c => InitRtlString(c.Arg(0), c.Arg(1), w, true));
                imports.Register("ntdll.dll", "RtlFree" + kind + "String", CallConv.Stdcall, 1, c =>
                {
                    var buffer = memory.Read32(c.Arg(0) + 4);
                    if (buffer != 0)
                    {
                        heap.Free(buffer);
                        memory.Write64(c.Arg(0), 0);
                    }
                    return 0;
                });
            }
            imports.Register("ntdll.dll", "RtlAnsiStringToUnicodeString", CallConv.Stdcall, 3,
                c => ConvertRtlString(c.Arg(0), c.Arg(1), (c.Arg(2) & 0xFF) != 0, true));
            imports.Register("ntdll.dll", "RtlUnicodeStringToAnsiString", CallConv.Stdcall, 3,
                c => ConvertRtlString(c.Arg(0), c.Arg(1), (c.Arg(2) & 0xFF) != 0, false));
        }

        private uint InitRtlString(uint descriptor, uint source, bool wide, bool extended)
        {
            var unit = wide ? 2u : 1u;
            uint length = 0;
            if (source != 0)
            {
                while ((wide ? memory.Read16(source + length) : memory.Read8(source + length)) != 0)
                {
                    length += unit;
                    if (extended && length > (wide ? 0xFFFCu : 0xFFFEu)) return 0xC0000106; // STATUS_NAME_TOO_LONG
                    if (wide && !extended && length >= 0xFFFC) break;
                }
            }
            memory.Write16(descriptor, unchecked((ushort)length));
            memory.Write16(descriptor + 2, source == 0 ? (ushort)0 : unchecked((ushort)(length + unit)));
            memory.Write32(descriptor + 4, source);
            return 0;
        }

        private uint ConvertRtlString(uint destination, uint source, bool allocate, bool toUnicode)
        {
            var sourceLength = memory.Read16(source);
            var sourceBuffer = memory.Read32(source + 4);
            var raw = memory.ReadBytes(sourceBuffer, toUnicode ? sourceLength : sourceLength & ~1);
            var converted = toUnicode ? Encoding.Unicode.GetBytes(Ansi.Decode(raw)) : Ansi.Encode(Encoding.Unicode.GetString(raw));
            var terminator = toUnicode ? 2 : 1;
            var required = converted.Length + terminator;
            if (required > ushort.MaxValue) return 0xC00000F0; // STATUS_INVALID_PARAMETER_2
            memory.Write16(destination, (ushort)converted.Length);
            var capacity = memory.Read16(destination + 2);
            var buffer = memory.Read32(destination + 4);
            if (allocate)
            {
                capacity = (ushort)required;
                buffer = heap.Alloc((uint)required);
                memory.Write16(destination + 2, capacity);
                memory.Write32(destination + 4, buffer);
                if (buffer == 0) return 0xC0000017; // STATUS_NO_MEMORY
            }
            var count = converted.Length;
            uint status = 0;
            if (capacity < required)
            {
                // ANSI conversion copies a terminated prefix; Unicode conversion
                // reports its required length without touching a short buffer.
                status = 0x80000005; // STATUS_BUFFER_OVERFLOW
                if (toUnicode || capacity == 0) return status;
                count = capacity - 1;
                memory.Write16(destination, (ushort)count);
            }
            if (count != 0)
            {
                if (count != converted.Length) Array.Resize(ref converted, count);
                memory.WriteBytes(buffer, converted);
            }
            if (toUnicode) memory.Write16(buffer + (uint)count, 0);
            else memory.Write8(buffer + (uint)count, 0);
            return status;
        }
    }
}
