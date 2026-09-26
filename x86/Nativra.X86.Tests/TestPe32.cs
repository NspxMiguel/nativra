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
        public const uint DllMainRva = 0x1010;
        public const uint DllMainMarkRva = 0x1100;   // where the test DllMain writes its mark
        public const uint DllMainMark = 0x600DF00D;

        // DllMain(hinst, reason, reserved), position-independent so it needs no
        // relocation: writes DllMainMark at hinst+DllMainMarkRva, returns TRUE.
        //   mov ecx,[esp+4]; mov dword [ecx+0x1100],0x600DF00D; mov eax,1; ret 12
        private static readonly byte[] DllMainCode =
        {
            0x8B, 0x4C, 0x24, 0x04,
            0xC7, 0x81, 0x00, 0x11, 0x00, 0x00, 0x0D, 0xF0, 0x0D, 0x60,
            0xB8, 0x01, 0x00, 0x00, 0x00,
            0xC2, 0x0C, 0x00,
        };

        /// <param name="importModule">The DLL the image imports from (at most 15 characters).</param>
        /// <param name="importName">The by-name import (at most 17 characters); the second import is ordinal 5 of the same DLL.</param>
        /// <param name="dll">Mark the image a DLL.</param>
        /// <param name="dllMain">Give the DLL a real entry point (<see cref="DllMainCode"/>); otherwise it has none.</param>
        public static byte[] Minimal(string importModule = "kernel32.dll", string importName = "GetProcAddress",
            bool dll = false, bool dllMain = false)
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
            // Characteristics (COFF + 18): EXECUTABLE_IMAGE | 32BIT_MACHINE, plus DLL.
            P16(peOff + 22, (ushort)(dll ? 0x2102 : 0x0102));

            P16(optOff + 0, 0x010B); // Magic = PE32
            P32(optOff + 16, dll ? (dllMain ? DllMainRva : 0) : EntryRva);
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
            if (dllMain)
                for (var i = 0; i < DllMainCode.Length; i++)
                    b[0x400 + (int)(DllMainRva - 0x1000) + i] = DllMainCode[i];

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
            Ascii(R(0x2040) + 2, importName);

            Ascii(R(0x2054), importModule);

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
