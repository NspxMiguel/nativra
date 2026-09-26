using System.Collections.Generic;
using Nativra.X86.Cpu;
using Nativra.X86.Loader;
using Xunit;

namespace Nativra.X86.Tests
{
    /// <summary>
    /// Loads a hand-built PE32 into guest memory and checks every step the
    /// loader owns: header parsing, section placement, HIGHLOW relocation,
    /// import binding by name and by ordinal, and the export table. The image
    /// is assembled byte-for-byte in <see cref="BuildPe32"/> so the test needs
    /// no compiler or platform — it runs the same on any host.
    /// </summary>
    public sealed class Pe32ImageTests
    {
        private const uint PreferredBase = 0x10000000;
        private const uint EntryRva = 0x1000;
        private const uint MarkerRva = 0x1000;   // a self-referential dword to prove relocation
        private const uint IatRva = 0x2034;

        [Fact]
        public void LoadsAtPreferredBaseWhenFree()
        {
            var memory = new GuestMemory();
            var image = Pe32Image.Load("waveshaper.exe", BuildPe32(), memory, (m, f, o) => 0xF0000000);

            Assert.Equal(PreferredBase, image.BaseAddress);
            Assert.Equal(PreferredBase + EntryRva, image.EntryPoint);
            Assert.False(image.IsDll);
            // Not relocated: the self-referential marker is untouched.
            Assert.Equal(PreferredBase + MarkerRva, memory.Read32(PreferredBase + MarkerRva));
        }

        [Fact]
        public void RelocatesWhenPreferredBaseIsTaken()
        {
            var memory = new GuestMemory();
            // Occupy the first page of the preferred region so the loader must move.
            memory.Map(PreferredBase, GuestMemory.PageSize);

            var image = Pe32Image.Load("waveshaper.exe", BuildPe32(), memory, (m, f, o) => 0xF0000000);

            Assert.NotEqual(PreferredBase, image.BaseAddress);
            Assert.Equal(image.BaseAddress + EntryRva, image.EntryPoint);
            // The marker was PreferredBase+0x1000; after a HIGHLOW fixup by the
            // move delta it must now read newBase+0x1000.
            Assert.Equal(image.BaseAddress + MarkerRva, memory.Read32(image.BaseAddress + MarkerRva));
        }

        [Fact]
        public void BindsImportsByNameAndOrdinalIntoTheIat()
        {
            var memory = new GuestMemory();
            var seen = new List<string>();
            uint next = 0xF0000000;
            Pe32Binder binder = (module, function, ordinal) =>
            {
                seen.Add(function != null ? $"{module}!{function}" : $"{module}#{ordinal}");
                return next += 0x10;
            };

            var image = Pe32Image.Load("waveshaper.exe", BuildPe32(), memory, binder);

            Assert.Contains("kernel32.dll!GetProcAddress", seen);
            Assert.Contains("kernel32.dll#5", seen);

            // The IAT slots hold what the binder returned, in order.
            var slot0 = memory.Read32(image.BaseAddress + IatRva);
            var slot1 = memory.Read32(image.BaseAddress + IatRva + 4);
            Assert.Equal(0xF0000010u, slot0);
            Assert.Equal(0xF0000020u, slot1);
            Assert.Empty(image.Unresolved);
        }

        [Fact]
        public void RecordsUnresolvedImportsWhenTheBinderReturnsZero()
        {
            var memory = new GuestMemory();
            // Resolve the named import, leave the ordinal one unresolved.
            var image = Pe32Image.Load("waveshaper.exe", BuildPe32(), memory,
                (module, function, ordinal) => function != null ? 0xF0000010u : 0u);

            var unresolved = image.Unresolved;
            Assert.Single(unresolved);
            Assert.True(unresolved[0].ByOrdinal);
            Assert.Equal(5, unresolved[0].Ordinal);
            Assert.Equal(0u, memory.Read32(image.BaseAddress + IatRva + 4));
        }

        [Fact]
        public void ReadsTheExportTable()
        {
            var memory = new GuestMemory();
            var image = Pe32Image.Load("waveshaper.exe", BuildPe32(), memory, (m, f, o) => 0xF0000000);

            Assert.Equal(image.BaseAddress + 0x1000, image.Export("Start"));
            Assert.Equal(image.BaseAddress + 0x1000, image.ExportByOrdinal(1));
            Assert.Equal(0u, image.Export("Missing"));
            Assert.Equal(1, image.ExportCount);
        }

        // --- a minimal but complete PE32 -----------------------------------

        private static byte[] BuildPe32()
        {
            const int fileLen = 0xA00;
            const int peOff = 0x80;
            const int optOff = peOff + 24;          // 0x98
            const int sectTable = optOff + 0xE0;    // 0x178
            const int rdataRawPtr = 0x600;
            const uint rdataRva = 0x2000;

            var b = new byte[fileLen];

            void P16(int off, ushort v) { b[off] = (byte)v; b[off + 1] = (byte)(v >> 8); }
            void P32(int off, uint v)
            {
                b[off] = (byte)v; b[off + 1] = (byte)(v >> 8);
                b[off + 2] = (byte)(v >> 16); b[off + 3] = (byte)(v >> 24);
            }
            void Ascii(int off, string s) { for (var i = 0; i < s.Length; i++) b[off + i] = (byte)s[i]; }
            int R(uint rva) => rdataRawPtr + (int)(rva - rdataRva);   // rdata RVA -> file offset

            // DOS header.
            P16(0, 0x5A4D);          // MZ
            P32(0x3C, peOff);        // e_lfanew

            // PE signature + COFF header.
            P32(peOff, 0x00004550);  // "PE\0\0"
            P16(peOff + 4, 0x014C);  // Machine = i386
            P16(peOff + 6, 2);       // NumberOfSections
            P16(peOff + 20, 0xE0);   // SizeOfOptionalHeader (COFF + 16)
            P16(peOff + 22, 0x0102); // Characteristics: EXECUTABLE_IMAGE | 32BIT_MACHINE (COFF + 18)

            // Optional header (PE32).
            P16(optOff + 0, 0x010B); // Magic = PE32
            P32(optOff + 16, EntryRva);
            P32(optOff + 28, PreferredBase);
            P32(optOff + 32, 0x1000); // SectionAlignment
            P32(optOff + 36, 0x200);  // FileAlignment
            P32(optOff + 56, 0x3000); // SizeOfImage
            P32(optOff + 60, 0x400);  // SizeOfHeaders
            P32(optOff + 92, 16);     // NumberOfRvaAndSizes
            P32(optOff + 96 + 0 * 8, 0x2064); P32(optOff + 96 + 0 * 8 + 4, 40); // Export
            P32(optOff + 96 + 1 * 8, 0x2000); P32(optOff + 96 + 1 * 8 + 4, 40); // Import
            P32(optOff + 96 + 5 * 8, 0x20A8); P32(optOff + 96 + 5 * 8 + 4, 12); // BaseReloc

            // Section headers.
            Ascii(sectTable, ".text");
            P32(sectTable + 8, 0x1000);   // VirtualSize
            P32(sectTable + 12, 0x1000);  // VirtualAddress
            P32(sectTable + 16, 0x200);   // SizeOfRawData
            P32(sectTable + 20, 0x400);   // PointerToRawData
            P32(sectTable + 36, 0x60000020u);

            Ascii(sectTable + 40, ".rdata");
            P32(sectTable + 40 + 8, 0x1000);
            P32(sectTable + 40 + 12, 0x2000);
            P32(sectTable + 40 + 16, 0x400);
            P32(sectTable + 40 + 20, 0x600);
            P32(sectTable + 40 + 36, 0x40000040u);

            // .text: a self-referential absolute pointer that relocation must fix.
            P32(0x400, PreferredBase + MarkerRva);

            // .rdata: import descriptor.
            P32(R(0x2000) + 0, 0x2028);  // OriginalFirstThunk (ILT)
            P32(R(0x2000) + 12, 0x2054); // Name -> "kernel32.dll"
            P32(R(0x2000) + 16, 0x2034); // FirstThunk (IAT)
            // (null descriptor at 0x2014 stays zero)

            // ILT: import by name, then by ordinal, then terminator.
            P32(R(0x2028) + 0, 0x2040);       // RVA of hint/name
            P32(R(0x2028) + 4, 0x80000005u);  // ordinal 5
            P32(R(0x2028) + 8, 0);

            // IAT: pre-filled with garbage so the test proves the loader wrote it.
            P32(R(0x2034) + 0, 0xDEADBEEF);
            P32(R(0x2034) + 4, 0xDEADBEEF);
            P32(R(0x2034) + 8, 0);

            // Hint/name for GetProcAddress.
            P16(R(0x2040), 0);
            Ascii(R(0x2040) + 2, "GetProcAddress");

            // Imported DLL name.
            Ascii(R(0x2054), "kernel32.dll");

            // Export directory.
            P32(R(0x2064) + 12, 0x209C); // Name -> "test.dll"
            P32(R(0x2064) + 16, 1);      // ordinal base
            P32(R(0x2064) + 20, 1);      // NumberOfFunctions
            P32(R(0x2064) + 24, 1);      // NumberOfNames
            P32(R(0x2064) + 28, 0x208C); // AddressOfFunctions
            P32(R(0x2064) + 32, 0x2090); // AddressOfNames
            P32(R(0x2064) + 36, 0x2094); // AddressOfNameOrdinals
            P32(R(0x208C), 0x1000);      // function[0] RVA
            P32(R(0x2090), 0x2096);      // name[0] RVA
            P16(R(0x2094), 0);           // nameOrdinal[0]
            Ascii(R(0x2096), "Start");
            Ascii(R(0x209C), "test.dll");

            // Base relocation block for page 0x1000: one HIGHLOW at offset 0.
            P32(R(0x20A8) + 0, 0x1000);  // PageRVA
            P32(R(0x20A8) + 4, 12);      // BlockSize (8 header + 2 entries)
            P16(R(0x20A8) + 8, (ushort)((3 << 12) | 0)); // HIGHLOW at offset 0
            P16(R(0x20A8) + 10, 0);      // ABSOLUTE padding

            return b;
        }
    }
}
