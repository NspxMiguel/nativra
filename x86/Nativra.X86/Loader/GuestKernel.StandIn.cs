using System;
using System.Collections.Generic;
using System.Text;
using Nativra.X86.Cpu;

namespace Nativra.X86.Loader
{
    // A system DLL the host serves has no image of its own: its handle used to
    // point at nothing. Plenty of code reads a module through its handle —
    // walking the export table instead of calling GetProcAddress (DRM wrappers,
    // Steam's own DLLs), checking the PE header, finding the image size — and
    // faulted there. Each stand-in handle now holds a small real PE image whose
    // export table lists what the host serves, pointing at the same entry points
    // GetProcAddress hands out.
    public sealed partial class GuestKernel
    {
        private const uint StandInSize = FakeModuleStride;
        private const uint StandInExports = 0x400;

        private void BuildStandIn(uint handle, string name)
        {
            if (!memory.IsFree(handle, StandInSize)) return;   // something else already lives there
            var names = process.Imports.NamesOf(name);
            names.Sort(string.CompareOrdinal);     // export names are binary-searched
            var functions = new uint[names.Count];
            for (var n = 0; n < names.Count; n++) functions[n] = process.Imports.Bind(name, names[n], -1);

            var image = new byte[StandInSize];
            void Put32(uint at, uint v) { image[at] = (byte)v; image[at + 1] = (byte)(v >> 8); image[at + 2] = (byte)(v >> 16); image[at + 3] = (byte)(v >> 24); }
            void Put16(uint at, uint v) { image[at] = (byte)v; image[at + 1] = (byte)(v >> 8); }

            // DOS and NT headers: an i386 DLL with one data directory, its exports.
            image[0] = (byte)'M'; image[1] = (byte)'Z';
            Put32(0x3C, 0x40);
            Put32(0x40, 0x00004550);                   // "PE\0\0"
            Put16(0x44, 0x014C);                       // Machine: i386
            Put16(0x46, 0);
            Put32(0x48, 0x5A000000);                   // TimeDateStamp
            Put16(0x54, 0xE0);                         // SizeOfOptionalHeader
            Put16(0x56, 0x2102);                       // DLL | 32-bit | executable
            const uint opt = 0x58;
            Put16(opt + 0, 0x010B);                    // PE32
            Put32(opt + 28, handle);                   // ImageBase
            Put32(opt + 32, 0x1000);                   // SectionAlignment
            Put32(opt + 36, 0x200);                    // FileAlignment
            Put16(opt + 40, 6);                        // OS version 6.x
            Put16(opt + 48, 6);                        // subsystem version 6.x
            Put32(opt + 56, StandInSize);              // SizeOfImage
            Put32(opt + 60, StandInExports);           // SizeOfHeaders
            Put16(opt + 68, 3);                        // Subsystem: console
            Put32(opt + 92, 16);                       // NumberOfRvaAndSizes

            // IMAGE_EXPORT_DIRECTORY, then the three arrays, then the strings.
            uint dir = StandInExports, functionsAt = dir + 40, namesAt = functionsAt + (uint)names.Count * 4;
            uint ordinalsAt = namesAt + (uint)names.Count * 4, text = ordinalsAt + (uint)names.Count * 2;
            uint Text(string s)
            {
                var bytes = Encoding.ASCII.GetBytes(s);
                if (text + bytes.Length + 1 > StandInSize) return 0;
                var at = text;
                Array.Copy(bytes, 0, image, at, bytes.Length);
                text += (uint)bytes.Length + 1;
                return at;
            }
            Put32(dir + 12, Text(name));               // Name
            Put32(dir + 16, 1);                        // Base ordinal
            var count = 0;
            for (var n = 0; n < names.Count; n++)
            {
                var nameAt = Text(names[n]);
                if (nameAt == 0) break;                // no room for more names
                Put32(functionsAt + (uint)n * 4, functions[n] - handle);   // wraps for addresses below the image
                Put32(namesAt + (uint)n * 4, nameAt);
                Put16(ordinalsAt + (uint)n * 2, (uint)n);
                count++;
            }
            Put32(dir + 20, (uint)count);              // NumberOfFunctions
            Put32(dir + 24, (uint)count);              // NumberOfNames
            Put32(dir + 28, functionsAt);
            Put32(dir + 32, namesAt);
            Put32(dir + 36, ordinalsAt);
            Put32(opt + 96, dir);                      // DataDirectory[EXPORT].VirtualAddress
            Put32(opt + 100, text - dir);              // .Size

            // An image like any other to VirtualQuery: the handle is its allocation base.
            memory.ReserveImage(handle, StandInSize);
            memory.Map(handle, StandInSize);
            memory.WriteBytes(handle, image);
            memory.Protect(handle, StandInSize, Win32Memory.PageReadOnly, out _);
        }
    }
}
