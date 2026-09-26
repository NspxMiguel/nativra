using System;
using System.Collections.Generic;
using Nativra.X86.Cpu;

namespace Nativra.X86.Loader
{
    /// <summary>
    /// One imported symbol: the DLL it comes from, the function (by name, or by
    /// ordinal when <see cref="Function"/> is null), the guest address of the IAT
    /// slot that holds it, and the value the binder resolved it to.
    /// </summary>
    public sealed class Pe32Import
    {
        public string Module { get; }
        public string Function { get; }   // null when imported by ordinal
        public int Ordinal { get; }       // -1 when imported by name
        public uint SlotAddress { get; }  // guest VA of the IAT entry
        public uint Bound { get; }        // what the binder returned (a sentinel, or 0 = unresolved)

        public Pe32Import(string module, string function, int ordinal, uint slot, uint bound)
        {
            Module = module;
            Function = function;
            Ordinal = ordinal;
            SlotAddress = slot;
            Bound = bound;
        }

        public bool ByOrdinal => Function == null;

        public override string ToString() =>
            ByOrdinal ? $"{Module}#{Ordinal}" : $"{Module}!{Function}";
    }

    /// <summary>Where the guest's thread-local storage directory points, for the TEB/TLS setup.</summary>
    public sealed class Pe32Tls
    {
        public uint RawDataStart { get; }   // guest VA of the initialised TLS template
        public uint RawDataEnd { get; }
        public uint IndexAddress { get; }   // guest VA the loader writes the TLS slot index into
        public uint CallbacksAddress { get; }  // guest VA of a NUL-terminated array of callback VAs
        public uint ZeroFill { get; }

        public Pe32Tls(uint start, uint end, uint index, uint callbacks, uint zeroFill)
        {
            RawDataStart = start;
            RawDataEnd = end;
            IndexAddress = index;
            CallbacksAddress = callbacks;
            ZeroFill = zeroFill;
        }
    }

    /// <summary>
    /// Resolves an imported symbol to the guest address the game should call. The
    /// loader writes whatever this returns into the import address table; the
    /// process layer hands back a sentinel address that traps into the host
    /// import dispatcher, or the real guest export of an already-mapped module.
    /// A return of 0 marks the import unresolved (the slot is left null and the
    /// import is recorded for diagnostics).
    /// </summary>
    /// <param name="module">The importing DLL name, lower-cased with its extension.</param>
    /// <param name="function">The function name, or null when imported by ordinal.</param>
    /// <param name="ordinal">The ordinal when <paramref name="function"/> is null; otherwise -1.</param>
    public delegate uint Pe32Binder(string module, string function, int ordinal);

    /// <summary>
    /// Maps a 32-bit (PE32, machine i386) image into a <see cref="GuestMemory"/>
    /// space and prepares it to run under the x86 core.
    ///
    /// This is the 32-bit counterpart to the app's 64-bit loader, but it does
    /// something different in kind: the 64-bit loader maps a PE into the host
    /// process and lets the CPU run it, whereas a PE32 cannot run on the 64-bit
    /// host, so this maps it into the emulated 4 GB guest space that the JIT and
    /// interpreter execute. Sections are copied to their virtual addresses,
    /// base relocations are applied (HIGHLOW), and each import is bound to a
    /// guest address through a <see cref="Pe32Binder"/>. Nothing here touches
    /// host memory outside the guest reservation.
    /// </summary>
    public sealed class Pe32Image
    {
        public const ushort MachineI386 = 0x014C;
        public const ushort Pe32Magic = 0x010B;

        private const int SizeOfSectionHeader = 40;
        private const int SizeOfImportDescriptor = 20;

        // PE32 optional-header field offsets, from the start of the optional header.
        private const int OptEntryPoint = 16;
        private const int OptImageBase = 28;   // 4 bytes on PE32 (8 on PE32+)
        private const int OptSectionAlignment = 32;
        private const int OptSizeOfImage = 56;
        private const int OptSizeOfHeaders = 60;
        private const int OptNumberOfRvaAndSizes = 92;
        private const int OptDataDirectories = 96;  // 96 on PE32 (112 on PE32+)

        // Data-directory indices.
        private const int DirExport = 0;
        private const int DirImport = 1;
        private const int DirBaseReloc = 5;
        private const int DirTls = 9;
        public const int DirResource = 2;

        private readonly byte[] file;
        private readonly GuestMemory memory;

        public string Name { get; }
        public uint BaseAddress { get; private set; }
        public uint PreferredBase { get; private set; }
        public uint ImageSize { get; private set; }
        public uint EntryPoint { get; private set; }   // guest VA, 0 for a DLL with no entry
        public bool IsDll { get; private set; }
        public Pe32Tls Tls { get; private set; }

        private readonly List<Pe32Import> imports = new List<Pe32Import>();
        private readonly Dictionary<string, uint> exportsByName =
            new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, uint> exportsByOrdinal = new Dictionary<int, uint>();

        public IReadOnlyList<Pe32Import> Imports => imports;
        public IReadOnlyList<Pe32Import> Unresolved
        {
            get
            {
                var list = new List<Pe32Import>();
                foreach (var i in imports) if (i.Bound == 0) list.Add(i);
                return list;
            }
        }

        private Pe32Image(string name, byte[] file, GuestMemory memory)
        {
            Name = name;
            this.file = file;
            this.memory = memory;
        }

        /// <summary>Guest address of a named export, or 0 if the image does not export it.</summary>
        public uint Export(string name) =>
            name != null && exportsByName.TryGetValue(name, out var va) ? va : 0;

        /// <summary>Guest address of an export by ordinal, or 0.</summary>
        public uint ExportByOrdinal(int ordinal) =>
            exportsByOrdinal.TryGetValue(ordinal, out var va) ? va : 0;

        public int ExportCount => exportsByName.Count;

        public bool Contains(uint address) =>
            address >= BaseAddress && address < BaseAddress + ImageSize;

        /// <summary>
        /// Parses, maps, relocates and import-binds <paramref name="file"/> into
        /// <paramref name="memory"/>. <paramref name="binder"/> turns each import
        /// into the guest address the IAT slot receives.
        /// </summary>
        public static Pe32Image Load(string name, byte[] file, GuestMemory memory, Pe32Binder binder)
        {
            if (file == null) throw new ArgumentNullException(nameof(file));
            if (memory == null) throw new ArgumentNullException(nameof(memory));
            var image = new Pe32Image(name ?? "image", file, memory);
            image.Parse();
            image.MapSections();
            image.Relocate();
            image.BindImports(binder);
            image.ReadExports();
            image.ReadTls();
            return image;
        }

        // --- parsing -------------------------------------------------------

        private int peHeader;      // file offset of the PE signature
        private int optHeader;     // file offset of the optional header
        private int sectionTable;  // file offset of the first section header
        private int sectionCount;
        private uint sizeOfHeaders;
        private uint sectionAlignment;
        private int numberOfDirectories;

        private void Parse()
        {
            if (file.Length < 0x40) throw Bad("file shorter than a DOS header");
            if (U16(0) != 0x5A4D) throw Bad("no MZ signature");
            peHeader = (int)U32(0x3C);
            if (peHeader < 0 || peHeader > file.Length - 24) throw Bad("PE offset out of range");
            if (U32(peHeader) != 0x00004550) throw Bad("no PE signature");

            var machine = U16(peHeader + 4);
            sectionCount = U16(peHeader + 6);
            var sizeOfOptional = U16(peHeader + 20);
            optHeader = peHeader + 24;
            if (optHeader + 96 > file.Length) throw Bad("optional header truncated");

            var magic = U16(optHeader);
            if (machine != MachineI386 || magic != Pe32Magic)
                throw Bad($"not a 32-bit i386 PE32 image (machine 0x{machine:X4}, magic 0x{magic:X4})");

            EntryPoint = 0; // filled after base is chosen
            PreferredBase = U32(optHeader + OptImageBase);
            ImageSize = U32(optHeader + OptSizeOfImage);
            sizeOfHeaders = U32(optHeader + OptSizeOfHeaders);
            sectionAlignment = U32(optHeader + OptSectionAlignment);
            numberOfDirectories = (int)U32(optHeader + OptNumberOfRvaAndSizes);
            var characteristics = U16(peHeader + 22);
            IsDll = (characteristics & 0x2000) != 0;

            sectionTable = optHeader + sizeOfOptional;
            if (sectionTable + sectionCount * SizeOfSectionHeader > file.Length)
                throw Bad("section table runs past the file");
            if (ImageSize == 0 || ImageSize > 0xF0000000u) throw Bad("implausible SizeOfImage");
            if (sectionAlignment < GuestMemory.PageSize) sectionAlignment = GuestMemory.PageSize;
        }

        // --- mapping -------------------------------------------------------

        private void MapSections()
        {
            BaseAddress = ChooseBase();
            EntryPoint = IsDll && U32(optHeader + OptEntryPoint) == 0
                ? 0
                : BaseAddress + U32(optHeader + OptEntryPoint);

            // Headers first, so an image that reads its own headers at run time
            // (many do, to walk their own directories) sees them.
            memory.Map(BaseAddress, RoundUp(sizeOfHeaders, sectionAlignment));
            memory.WriteBytes(BaseAddress, file, 0, (int)Math.Min(sizeOfHeaders, (uint)file.Length));

            for (var s = 0; s < sectionCount; s++)
            {
                var header = sectionTable + s * SizeOfSectionHeader;
                var virtualSize = U32(header + 8);
                var virtualAddress = U32(header + 12);
                var rawSize = U32(header + 16);
                var rawPointer = U32(header + 20);

                var span = RoundUp(virtualSize == 0 ? rawSize : virtualSize, sectionAlignment);
                if (span == 0) continue;
                memory.Map(BaseAddress + virtualAddress, span);

                var copy = Math.Min(rawSize, virtualSize == 0 ? rawSize : virtualSize);
                if (copy > 0 && rawPointer < file.Length)
                {
                    copy = Math.Min(copy, (uint)file.Length - rawPointer);
                    memory.WriteBytes(BaseAddress + virtualAddress, file, (int)rawPointer, (int)copy);
                }
            }
        }

        private uint ChooseBase()
        {
            if (PreferredBase != 0 && RegionFree(PreferredBase, ImageSize))
                return PreferredBase;
            var found = memory.FindFree(RoundUp(ImageSize, sectionAlignment), 0x00400000);
            if (found == 0) throw Bad("no free guest region for the image");
            return found;
        }

        private bool RegionFree(uint address, uint size)
        {
            if (address == 0) return false;
            if ((ulong)address + size > 0xFFFFFFFFul) return false;
            var first = address >> GuestMemory.PageShift;
            var last = (uint)(((ulong)address + size - 1) >> GuestMemory.PageShift);
            for (var page = first; page <= last; page++)
                if (memory.IsMapped(page << GuestMemory.PageShift)) return false;
            return true;
        }

        // --- relocation ----------------------------------------------------

        private void Relocate()
        {
            var delta = BaseAddress - PreferredBase;
            if (delta == 0) return;

            uint dirRva = DirectoryRva(DirBaseReloc), dirSize = DirectorySize(DirBaseReloc);
            if (dirRva == 0 || dirSize == 0)
            {
                // A rebased image with no relocation table cannot be fixed up.
                if (BaseAddress != PreferredBase)
                    throw Bad("image had to move but carries no relocation table");
                return;
            }

            uint pos = 0;
            while (pos + 8 <= dirSize)
            {
                var pageRva = ReadImage32(dirRva + pos);
                var blockSize = ReadImage32(dirRva + pos + 4);
                if (blockSize < 8 || pos + blockSize > dirSize) break;

                var entries = (blockSize - 8) / 2;
                for (uint e = 0; e < entries; e++)
                {
                    var entry = ReadImage16(dirRva + pos + 8 + e * 2);
                    var type = entry >> 12;
                    var offset = (uint)(entry & 0x0FFF);
                    if (type == 3) // IMAGE_REL_BASED_HIGHLOW
                    {
                        var target = BaseAddress + pageRva + offset;
                        memory.Write32(target, memory.Read32(target) + delta);
                    }
                    // type 0 (ABSOLUTE) is padding; other types don't occur on i386.
                }
                pos += blockSize;
            }
        }

        // --- imports -------------------------------------------------------

        private void BindImports(Pe32Binder binder)
        {
            uint dirRva = DirectoryRva(DirImport);
            if (dirRva == 0) return;

            for (uint descriptor = 0; ; descriptor += SizeOfImportDescriptor)
            {
                var originalFirstThunk = ReadImage32(dirRva + descriptor);
                var nameRva = ReadImage32(dirRva + descriptor + 12);
                var firstThunk = ReadImage32(dirRva + descriptor + 16);
                if (nameRva == 0 && firstThunk == 0 && originalFirstThunk == 0) break;
                if (nameRva == 0 || firstThunk == 0) continue;

                var module = LowerModule(memory.ReadAnsi(BaseAddress + nameRva));
                // The lookup table (OriginalFirstThunk) names the imports; the
                // IAT (FirstThunk) is what the game calls through and where the
                // resolved address is written. When there is no separate lookup
                // table, the IAT doubles as it before binding.
                var lookup = originalFirstThunk != 0 ? originalFirstThunk : firstThunk;

                for (uint i = 0; ; i += 4)
                {
                    var entry = ReadImage32(lookup + i);
                    if (entry == 0) break;

                    string function;
                    int ordinal;
                    if ((entry & 0x80000000u) != 0)
                    {
                        function = null;
                        ordinal = (int)(entry & 0xFFFF);
                    }
                    else
                    {
                        // entry is an RVA to IMAGE_IMPORT_BY_NAME: hint (2) + name.
                        function = memory.ReadAnsi(BaseAddress + entry + 2);
                        ordinal = -1;
                    }

                    var slot = BaseAddress + firstThunk + i;
                    var bound = binder != null ? binder(module, function, ordinal) : 0;
                    memory.Write32(slot, bound);
                    imports.Add(new Pe32Import(module, function, ordinal, slot, bound));
                }
            }
        }

        // --- exports -------------------------------------------------------

        private void ReadExports()
        {
            uint dirRva = DirectoryRva(DirExport), dirSize = DirectorySize(DirExport);
            if (dirRva == 0) return;

            var ordinalBase = ReadImage32(dirRva + 16);
            var functionCount = ReadImage32(dirRva + 20);
            var nameCount = ReadImage32(dirRva + 24);
            var functionsRva = ReadImage32(dirRva + 28);
            var namesRva = ReadImage32(dirRva + 32);
            var nameOrdinalsRva = ReadImage32(dirRva + 36);

            for (uint f = 0; f < functionCount; f++)
            {
                var funcRva = ReadImage32(functionsRva + f * 4);
                if (funcRva == 0) continue;
                // A forwarder points inside the export directory; those are rare
                // for a game's own DLLs and are recorded by ordinal only.
                exportsByOrdinal[(int)(ordinalBase + f)] = BaseAddress + funcRva;
            }

            for (uint n = 0; n < nameCount; n++)
            {
                var nameRva = ReadImage32(namesRva + n * 4);
                var ordIndex = ReadImage16(nameOrdinalsRva + n * 2);
                var funcRva = ReadImage32(functionsRva + (uint)ordIndex * 4);
                if (funcRva == 0) continue;
                var name = memory.ReadAnsi(BaseAddress + nameRva);
                if (name.Length > 0) exportsByName[name] = BaseAddress + funcRva;
            }
        }

        // --- TLS -----------------------------------------------------------

        private void ReadTls()
        {
            uint dirRva = DirectoryRva(DirTls), dirSize = DirectorySize(DirTls);
            if (dirRva == 0 || dirSize < 24) return;

            // IMAGE_TLS_DIRECTORY32 fields are virtual addresses (already based).
            var start = ReadImage32(dirRva + 0);
            var end = ReadImage32(dirRva + 4);
            var index = ReadImage32(dirRva + 8);
            var callbacks = ReadImage32(dirRva + 12);
            var zeroFill = ReadImage32(dirRva + 16);
            Tls = new Pe32Tls(start, end, index, callbacks, zeroFill);
        }

        // --- helpers -------------------------------------------------------

        /// <summary>A data directory's guest VA (0 when absent) and size.</summary>
        public uint Directory(int index, out uint size)
        {
            var rva = DirectoryRva(index);
            size = rva == 0 ? 0 : DirectorySize(index);
            return rva == 0 ? 0 : BaseAddress + rva;
        }

        private uint DirectoryRva(int index)
        {
            if (index >= numberOfDirectories) return 0;
            return U32(optHeader + OptDataDirectories + index * 8);
        }

        private uint DirectorySize(int index)
        {
            if (index >= numberOfDirectories) return 0;
            return U32(optHeader + OptDataDirectories + index * 8 + 4);
        }

        private static uint RoundUp(uint value, uint alignment) =>
            alignment == 0 ? value : (value + alignment - 1) / alignment * alignment;

        private ushort U16(int offset) => (ushort)(file[offset] | (file[offset + 1] << 8));

        private uint U32(int offset) => (uint)(file[offset] | (file[offset + 1] << 8) |
            (file[offset + 2] << 16) | (file[offset + 3] << 24));

        // Reads from the mapped guest image (after sections are in place), by RVA.
        private uint ReadImage32(uint rva) => memory.Read32(BaseAddress + rva);
        private ushort ReadImage16(uint rva) => memory.Read16(BaseAddress + rva);

        private static string LowerModule(string module) =>
            module == null ? "" : module.ToLowerInvariant();

        private BadImageFormatException Bad(string why) =>
            new BadImageFormatException($"{Name}: {why}");
    }
}
