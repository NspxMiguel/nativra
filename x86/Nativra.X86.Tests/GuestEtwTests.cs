using Nativra.X86.Cpu;
using Nativra.X86.Loader;
using Xunit;

namespace Nativra.X86.Tests
{
    public sealed class GuestEtwTests
    {
        [Fact]
        public void LegacyEtwExportsResolveDynamicallyAndCanBeCalled()
        {
            using (var process = new GuestProcess(new GuestMemory(), useJit: false))
            {
                var kernel = new GuestKernel(process);
                kernel.Install();
                var libraryName = kernel.Heap.Alloc(32);
                process.Memory.WriteAnsi(libraryName, "advapi32.dll");
                var load = process.Imports.Bind("kernel32.dll", "LoadLibraryA", -1);
                Assert.True(process.Call(load, out var module, 1_000_000, libraryName).Ok);
                Assert.NotEqual(0u, module);

                uint Resolve(string name, int argumentDwords)
                {
                    var nameAddress = kernel.Heap.Alloc((uint)name.Length + 1);
                    process.Memory.WriteAnsi(nameAddress, name);
                    var lookup = process.Imports.Bind("kernel32.dll", "GetProcAddress", -1);
                    Assert.True(process.Call(lookup, out var address, 1_000_000, module, nameAddress).Ok);
                    Assert.True(GuestImports.InRegion(address), name);
                    Assert.True(process.Imports.TryResolve(address, out var import));
                    Assert.Equal(CallConv.Stdcall, import.Handler.Conv);
                    Assert.Equal(argumentDwords, import.Handler.ArgDwords);
                    return address;
                }

                var register = Resolve("RegisterTraceGuidsW", 8);
                var unregister = Resolve("UnregisterTraceGuids", 2);
                var trace = Resolve("TraceEvent", 3);
                var logger = Resolve("GetTraceLoggerHandle", 1);
                var level = Resolve("GetTraceEnableLevel", 2);
                var flags = Resolve("GetTraceEnableFlags", 2);
                var handle = kernel.Heap.Alloc(8, zero: true);

                Assert.True(process.Call(register, out var status, 1_000_000, 0, 0, 0, 0, 0, 0, 0, handle).Ok);
                Assert.Equal(0u, status);
                Assert.Equal(1ul, process.Memory.Read64(handle));
                Assert.True(process.Call(logger, out var session, 1_000_000, 0).Ok);
                Assert.Equal(0u, session);
                Assert.True(process.Call(level, out var enabledLevel, 1_000_000, 0, 0).Ok);
                Assert.Equal(0u, enabledLevel);
                Assert.True(process.Call(flags, out var enabledFlags, 1_000_000, 0, 0).Ok);
                Assert.Equal(0u, enabledFlags);
                Assert.True(process.Call(trace, out status, 1_000_000, 0, 0, 0).Ok);
                Assert.Equal(0u, status);
                Assert.True(process.Call(unregister, out status, 1_000_000, 1, 0).Ok);
                Assert.Equal(0u, status);
                Assert.Empty(kernel.ProbedAbsent);
            }
        }
    }
}
