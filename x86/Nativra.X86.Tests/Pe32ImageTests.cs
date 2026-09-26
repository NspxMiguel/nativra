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
        private const uint PreferredBase = TestPe32.PreferredBase;
        private const uint EntryRva = TestPe32.EntryRva;
        private const uint MarkerRva = TestPe32.MarkerRva;   // a self-referential dword to prove relocation
        private const uint IatRva = TestPe32.IatRva;

        private static byte[] BuildPe32() => TestPe32.Minimal();

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
    }
}
