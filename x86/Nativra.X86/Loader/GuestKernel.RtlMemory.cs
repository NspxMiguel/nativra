namespace Nativra.X86.Loader
{
    public sealed partial class GuestKernel
    {
        private void InstallRtlMemoryFunctions(GuestImports imports)
        {
            imports.Register("ntdll.dll", "RtlCompareMemory", CallConv.Stdcall, 3, c =>
            {
                uint matched = 0;
                while (matched < c.Arg(2) && memory.Read8(c.Arg(0) + matched) == memory.Read8(c.Arg(1) + matched))
                    matched++;
                return matched;
            });
            imports.Register("ntdll.dll", "RtlCompareMemoryUlong", CallConv.Stdcall, 3, c =>
            {
                // The x86 contract compares whole ULONGs and returns a byte count.
                var length = c.Arg(1) & ~3u;
                uint matched = 0;
                while (matched < length && memory.Read32(c.Arg(0) + matched) == c.Arg(2))
                    matched += 4;
                return matched;
            });
            imports.Register("ntdll.dll", "RtlFillMemoryUlong", CallConv.Stdcall, 3, c =>
            {
                var length = c.Arg(1) & ~3u;
                for (uint offset = 0; offset < length; offset += 4)
                    memory.Write32(c.Arg(0) + offset, c.Arg(2));
                return 0;
            });
        }
    }
}
