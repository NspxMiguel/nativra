namespace Nativra.X86.Tests
{
    /// <summary>
    /// A minimal but complete PE32 image, assembled byte-for-byte so the loader
    /// tests need no compiler or platform. It has a .text and a .rdata section,
    /// one entry point, an import of kernel32.dll!GetProcAddress (by name) and
    /// one by ordinal, a HIGHLOW relocation, and a single named export "Start".
    /// </summary>
    internal static class TestPe32
    {
        public const uint PreferredBase = 0x10000000;
        public const uint EntryRva = 0x1000;
        public const uint MarkerRva = 0x1000;
        public const uint IatRva = 0x2034;

        public static byte[] Minimal()
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
            int R(uint rva) => rdataRawPtr + (int)(rva - rdataRva);

            P16(0, 0x5A4D);          // MZ
            P32(0x3C, peOff);        // e_lfanew

            P32(peOff, 0x00004550);  // "PE\0\0"
            P16(peOff + 4, 0x014C);  // Machine = i386
            P16(peOff + 6, 2);       // NumberOfSections
            P16(peOff + 20, 0xE0);   // SizeOfOptionalHeader (COFF + 16)
            P16(peOff + 22, 0x0102); // Characteristics: EXECUTABLE_IMAGE | 32BIT_MACHINE (COFF + 18)

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

            Ascii(sectTable, ".text");
            P32(sectTable + 8, 0x1000);
            P32(sectTable + 12, 0x1000);
            P32(sectTable + 16, 0x200);
            P32(sectTable + 20, 0x400);
            P32(sectTable + 36, 0x60000020u);

            Ascii(sectTable + 40, ".rdata");
            P32(sectTable + 40 + 8, 0x1000);
            P32(sectTable + 40 + 12, 0x2000);
            P32(sectTable + 40 + 16, 0x400);
            P32(sectTable + 40 + 20, 0x600);
            P32(sectTable + 40 + 36, 0x40000040u);

            P32(0x400, PreferredBase + MarkerRva); // self-referential pointer (needs relocation)

            P32(R(0x2000) + 0, 0x2028);  // OriginalFirstThunk
            P32(R(0x2000) + 12, 0x2054); // Name
            P32(R(0x2000) + 16, 0x2034); // FirstThunk

            P32(R(0x2028) + 0, 0x2040);       // by name
            P32(R(0x2028) + 4, 0x80000005u);  // by ordinal 5
            P32(R(0x2028) + 8, 0);

            P32(R(0x2034) + 0, 0xDEADBEEF);
            P32(R(0x2034) + 4, 0xDEADBEEF);
            P32(R(0x2034) + 8, 0);

            P16(R(0x2040), 0);
            Ascii(R(0x2040) + 2, "GetProcAddress");

            Ascii(R(0x2054), "kernel32.dll");

            P32(R(0x2064) + 12, 0x209C); // Name -> "test.dll"
            P32(R(0x2064) + 16, 1);      // ordinal base
            P32(R(0x2064) + 20, 1);      // NumberOfFunctions
            P32(R(0x2064) + 24, 1);      // NumberOfNames
            P32(R(0x2064) + 28, 0x208C);
            P32(R(0x2064) + 32, 0x2090);
            P32(R(0x2064) + 36, 0x2094);
            P32(R(0x208C), 0x1000);
            P32(R(0x2090), 0x2096);
            P16(R(0x2094), 0);
            Ascii(R(0x2096), "Start");
            Ascii(R(0x209C), "test.dll");

            P32(R(0x20A8) + 0, 0x1000);
            P32(R(0x20A8) + 4, 12);
            P16(R(0x20A8) + 8, (ushort)((3 << 12) | 0));
            P16(R(0x20A8) + 10, 0);

            return b;
        }
    }
}
