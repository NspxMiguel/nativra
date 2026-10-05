namespace Nativra.X86.Loader
{
    public sealed partial class GuestKernel
    {
        private void InstallHeapFunctions(GuestImports imports)
        {
            HostCall allocate = c =>
            {
                if (c.Arg(0) != ProcessHeapHandle) return 0;
                var block = heap.Alloc(c.Arg(2), (c.Arg(1) & HeapZeroMemory) != 0);
                if (block == 0) Log?.Invoke("Heap allocation of " + c.Arg(2) + " bytes failed: " + heap.LastFailure);
                return block;
            };
            HostCall release = c => c.Arg(0) == ProcessHeapHandle && heap.Free(c.Arg(2)) ? 1u : 0u;
            HostCall resize = c => c.Arg(0) != ProcessHeapHandle || c.Arg(2) == 0 ? 0 :
                heap.ReAlloc(c.Arg(2), c.Arg(3), (c.Arg(1) & HeapZeroMemory) != 0, (c.Arg(1) & 0x10) != 0);
            HostCall size = c => c.Arg(0) == ProcessHeapHandle ? heap.RequestedSizeOf(c.Arg(2)) : uint.MaxValue;
            HostCall validate = c => c.Arg(0) == ProcessHeapHandle &&
                (c.Arg(2) == 0 || heap.RequestedSizeOf(c.Arg(2)) != uint.MaxValue) ? 1u : 0u;
            HostCall enumerate = c =>
            {
                if (c.Arg(0) != 0 && c.Arg(1) != 0) memory.Write32(c.Arg(1), ProcessHeapHandle);
                return 1;
            };
            imports.Register("kernel32.dll", "HeapAlloc", CallConv.Stdcall, 3, allocate);
            imports.Register("kernel32.dll", "HeapFree", CallConv.Stdcall, 3, release);
            imports.Register("kernel32.dll", "HeapReAlloc", CallConv.Stdcall, 4, resize);
            imports.Register("kernel32.dll", "HeapSize", CallConv.Stdcall, 3, size);
            imports.Register("kernel32.dll", "HeapValidate", CallConv.Stdcall, 3, validate);
            imports.Register("kernel32.dll", "GetProcessHeaps", CallConv.Stdcall, 2, enumerate);
            imports.Register("ntdll.dll", "RtlAllocateHeap", CallConv.Stdcall, 3, allocate);
            imports.Register("ntdll.dll", "RtlFreeHeap", CallConv.Stdcall, 3, release);
            imports.Register("ntdll.dll", "RtlReAllocateHeap", CallConv.Stdcall, 4, resize);
            imports.Register("ntdll.dll", "RtlSizeHeap", CallConv.Stdcall, 3, size);
            imports.Register("ntdll.dll", "RtlValidateHeap", CallConv.Stdcall, 3, validate);
            imports.Register("ntdll.dll", "RtlGetProcessHeaps", CallConv.Stdcall, 2, enumerate);
        }
    }
}
