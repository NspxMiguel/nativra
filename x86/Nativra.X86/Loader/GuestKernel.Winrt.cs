using System;
using System.Collections.Generic;
using System.Text;

namespace Nativra.X86.Loader
{
    public sealed partial class GuestKernel
    {
        private const uint WinrtInvalidArgument = 0x80070057, WinrtPointer = 0x80004003, WinrtOutOfMemory = 0x8007000E;
        private sealed class GuestString
        {
            public uint Buffer, Length, References = 1;
            public bool FastPass;
        }
        private readonly Dictionary<uint, GuestString> winrtStrings = new Dictionary<uint, GuestString>();
        private uint emptyWinrtBuffer;

        private uint CreateWinrtString(uint source, uint length, uint result, uint header = 0, bool reference = false)
        {
            if (result == 0 || (reference && header == 0)) return WinrtInvalidArgument;
            memory.Write32(result, 0);
            if (source == 0 && length != 0) return WinrtPointer;
            if (length > (uint)(int.MaxValue / 2 - 1)) return WinrtOutOfMemory;
            if ((ulong)source + length * 2UL + (reference ? 2UL : 0UL) > (1UL << 32)) return WinrtInvalidArgument;
            if (reference && source != 0 && memory.Read16(source + length * 2) != 0) return WinrtInvalidArgument;
            if (length == 0) return 0;
            var buffer = source;
            var handle = header;
            if (!reference)
            {
                buffer = heap.Alloc((length + 1) * 2);
                if (buffer == 0) return WinrtOutOfMemory;
                memory.WriteBytes(buffer, memory.ReadBytes(source, (int)length * 2));
                memory.Write16(buffer + length * 2, 0);
                handle = heap.Alloc(8);
                if (handle == 0) { heap.Free(buffer); return WinrtOutOfMemory; }
            }
            memory.Write32(handle, length);
            memory.Write32(handle + 4, buffer);
            winrtStrings[handle] = new GuestString { Buffer = buffer, Length = length, FastPass = reference };
            memory.Write32(result, handle);
            return 0;
        }

        private uint DeleteWinrtString(uint handle)
        {
            if (handle == 0) return 0;
            if (!winrtStrings.TryGetValue(handle, out var value)) return WinrtInvalidArgument;
            if (value.FastPass) return 0; // Caller owns the header and backing buffer.
            if (--value.References == 0)
            {
                winrtStrings.Remove(handle);
                heap.Free(value.Buffer);
                heap.Free(handle);
            }
            return 0;
        }

        private uint DuplicateWinrtString(uint handle, uint result)
        {
            if (result == 0) return WinrtInvalidArgument;
            memory.Write32(result, 0);
            if (handle == 0) return 0;
            if (!winrtStrings.TryGetValue(handle, out var value)) return WinrtInvalidArgument;
            if (value.FastPass) return CreateWinrtString(value.Buffer, value.Length, result);
            value.References++;
            memory.Write32(result, handle);
            return 0;
        }

        private uint RawWinrtString(uint handle, uint length)
        {
            if (handle != 0 && winrtStrings.TryGetValue(handle, out var value))
            {
                if (length != 0) memory.Write32(length, value.Length);
                return value.Buffer;
            }
            if (length != 0) memory.Write32(length, 0);
            if (emptyWinrtBuffer == 0)
            {
                emptyWinrtBuffer = heap.Alloc(2);
                memory.Write16(emptyWinrtBuffer, 0);
            }
            return emptyWinrtBuffer;
        }

        private string WinrtText(uint handle) => handle != 0 && winrtStrings.TryGetValue(handle, out var value)
            ? Encoding.Unicode.GetString(memory.ReadBytes(value.Buffer, (int)value.Length * 2)) : "";

        private uint RefuseWinrtActivation(uint result)
        {
            if (result == 0) return WinrtPointer;
            memory.Write32(result, 0);
            return 0x80040154; // REGDB_E_CLASSNOTREG: no guest WinRT class factory.
        }

        private void InstallWinrt(GuestImports imports)
        {
            const string module = "combase.dll";
            foreach (var owner in new[] { module, "kernel32.dll" })
            {
                imports.Register(owner, "RoInitialize", CallConv.Stdcall, 1, c => c.Arg(0) > 1 ? WinrtInvalidArgument : InitializeApartment(c.Arg(0)));
                imports.Register(owner, "RoUninitialize", CallConv.Stdcall, 0, c => UninitializeApartment());
            }
            imports.Register(module, "RoGetActivationFactory", CallConv.Stdcall, 3, c => RefuseWinrtActivation(c.Arg(2)));
            imports.Register(module, "RoActivateInstance", CallConv.Stdcall, 2, c => RefuseWinrtActivation(c.Arg(1)));
            imports.Register(module, "WindowsCreateString", CallConv.Stdcall, 3, c => CreateWinrtString(c.Arg(0), c.Arg(1), c.Arg(2)));
            imports.Register(module, "WindowsCreateStringReference", CallConv.Stdcall, 4, c => CreateWinrtString(c.Arg(0), c.Arg(1), c.Arg(3), c.Arg(2), true));
            imports.Register(module, "WindowsDeleteString", CallConv.Stdcall, 1, c => DeleteWinrtString(c.Arg(0)));
            imports.Register(module, "WindowsDuplicateString", CallConv.Stdcall, 2, c => DuplicateWinrtString(c.Arg(0), c.Arg(1)));
            imports.Register(module, "WindowsGetStringRawBuffer", CallConv.Stdcall, 2, c => RawWinrtString(c.Arg(0), c.Arg(1)));
            imports.Register(module, "WindowsGetStringLen", CallConv.Stdcall, 1, c => winrtStrings.TryGetValue(c.Arg(0), out var value) ? value.Length : 0u);
            imports.Register(module, "WindowsIsStringEmpty", CallConv.Stdcall, 1, c => c.Arg(0) == 0 ? 1u : 0u);
            imports.Register(module, "WindowsCompareStringOrdinal", CallConv.Stdcall, 3, c =>
            {
                if (c.Arg(2) == 0) return WinrtInvalidArgument;
                if ((c.Arg(0) != 0 && !winrtStrings.ContainsKey(c.Arg(0))) || (c.Arg(1) != 0 && !winrtStrings.ContainsKey(c.Arg(1)))) return WinrtInvalidArgument;
                memory.Write32(c.Arg(2), unchecked((uint)Math.Sign(string.CompareOrdinal(WinrtText(c.Arg(0)), WinrtText(c.Arg(1))))));
                return 0;
            });
        }
    }
}
