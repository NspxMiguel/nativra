using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nativra.X86.Cpu;
using Nativra.X86.Loader;

namespace Nativra.X86.Run
{
    internal static unsafe class Microbenchmarks
    {
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr Tick(IntPtr self, IntPtr value) => value;

        public static int Run()
        {
            const int memoryIterations = 2_000_000;
            using (var memory = new GuestMemory(native: true))
            {
                memory.Map(0x10000, 4096);
                uint sum = 0;
                var watch = Stopwatch.StartNew();
                for (var n = 0; n < memoryIterations; n++)
                {
                    memory.Write32(0x10000, (uint)n);
                    sum += memory.Read32(0x10000);
                }
                watch.Stop();
                Console.WriteLine("bench.memory.ns=" + (watch.Elapsed.TotalMilliseconds * 1_000_000 / memoryIterations).ToString("F1") + " checksum=" + sum);
            }

            const int comIterations = 100_000;
            using (var memory = new GuestMemory(native: true))
            using (var process = new GuestProcess(memory, useJit: false))
            {
                var kernel = new GuestKernel(process);
                kernel.Install();
                var com = new GuestCom(process, kernel);
                var face = com.Define("IBenchmark", Guid.Empty, null, true, "Tick(u)");
                var vtable = Marshal.AllocHGlobal(4 * IntPtr.Size);
                var host = Marshal.AllocHGlobal(IntPtr.Size);
                try
                {
                    for (var n = 0; n < 4; n++) Marshal.WriteIntPtr(vtable, n * IntPtr.Size, (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr>)&Tick);
                    Marshal.WriteIntPtr(host, vtable);
                    var obj = com.Wrap(host, face);
                    var entry = memory.Read32(memory.Read32(obj) + 12);
                    for (var n = 0; n < 1000; n++) process.Call(entry, out _, 100, obj, (uint)n);
                    GC.Collect();
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    var watch = Stopwatch.StartNew();
                    for (var n = 0; n < comIterations; n++) process.Call(entry, out _, 100, obj, (uint)n);
                    watch.Stop();
                    Console.WriteLine("bench.com.ns=" + (watch.Elapsed.TotalMilliseconds * 1_000_000 / comIterations).ToString("F1"));
                    Console.WriteLine("bench.com.bytes=" + ((GC.GetAllocatedBytesForCurrentThread() - before) / (double)comIterations).ToString("F1"));
                }
                finally
                {
                    Marshal.FreeHGlobal(host);
                    Marshal.FreeHGlobal(vtable);
                }
            }
            return 0;
        }
    }
}
