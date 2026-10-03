using System;
using System.Runtime.InteropServices;
using Nativra.X86.Cpu;
using Nativra.X86.Jit;
using Xunit;

namespace Nativra.X86.Tests
{
    /// <summary>Real host page faults must only run under the Windows vectored handler.</summary>
    public sealed class JitNativeFaultTests
    {
        private const uint Code = 0x00400000;
        private const uint Data = 0x00500000;

        [SkippableFact]
        public void MissingBlockRangeRecoversThroughOwnedPageAndActiveFaultExit()
        {
            Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
                RuntimeInformation.ProcessArchitecture == Architecture.X64,
                "real JIT page faults require Windows x64");
            using (var memory = new GuestMemory(native: true))
            {
                memory.Map(Code, 0x1000);
                memory.Map(Data, 0x1000);
                memory.WriteBytes(Code, new byte[] { 0x8B, 0x03, 0xEB, 0x00 });
                var cpu = new CpuState { Eip = Code, Ebx = Data };
                using (var jit = new JitEngine(cpu, memory))
                {
                    jit.RunBlock(1);
                    jit.UnregisterFaultRangeForTesting(Code);
                    cpu.Eip = Code;
                    cpu.Ebx = 0;
                    var fault = Assert.Throws<GuestException>(() => jit.RunBlock(1));
                    Assert.Equal(Code, fault.Eip);
                    Assert.Equal(0u, fault.Information[1]);
                }
            }
        }

        [SkippableTheory]
        [InlineData(0, 0)] [InlineData(0, 1)] [InlineData(0, 2)] [InlineData(0, 3)]
        [InlineData(1, 0)] [InlineData(1, 1)] [InlineData(1, 2)] [InlineData(1, 3)]
        [InlineData(2, 0)] [InlineData(2, 1)] [InlineData(2, 2)] [InlineData(2, 3)]
        public void FaultsFromSingleChainedPackedAndInvalidatedBlocks(int access, int layout)
        {
            Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
                RuntimeInformation.ProcessArchitecture == Architecture.X64,
                "real JIT page faults require Windows x64");

            var target = layout == 1 ? Code + 0x300u : layout == 3 ? Code + 0x1000u : Code;
            var badAddress = access == 0 ? 0u : access == 1 ? 0x00600000u : Data;
            using (var memory = new GuestMemory(native: true))
            {
                memory.Map(Code, 0x2000);
                memory.Map(Data, 0x1000);
                memory.Write32(Data, 0x12345678);
                // mov eax,[ebx] / mov [ebx],eax; jmp to a mapped landing pad.
                memory.WriteBytes(target, access == 2
                    ? new byte[] { 0x89, 0x03, 0xEB, 0x00 }
                    : new byte[] { 0x8B, 0x03, 0xEB, 0x00 });
                if (layout == 1)
                {
                    WriteJump(memory, Code, Code + 0x100);
                    WriteJump(memory, Code + 0x100, Code + 0x200);
                    WriteJump(memory, Code + 0x200, target);
                }
                if (layout >= 2)
                    for (var i = 1; i <= 4; i++)
                        memory.WriteBytes(Code + (uint)(i * 0x20), new byte[] { 0xEB, 0xFE });

                var expected = new CpuState { Eip = target, Ebx = badAddress };
                var actual = new CpuState { Eip = target, Ebx = badAddress };
                using (var jit = new JitEngine(actual, memory))
                {
                    if (layout >= 2)
                    {
                        for (var i = 1; i <= 4; i++)
                        {
                            actual.Eip = Code + (uint)(i * 0x20);
                            jit.RunBlock(1);
                        }
                        if (layout == 3)
                        {
                            // Publish the target before dropping its packed neighbour.
                            actual.Eip = target;
                            actual.Ebx = Data;
                            jit.RunBlock(1);
                            jit.Invalidate(Code, 1);
                        }
                    }
                    actual.Eip = layout == 1 ? Code : target;
                    actual.Ebx = badAddress;
                    var protectedPage = access == 2;
                    if (protectedPage)
                    {
                        Assert.Equal(0u, memory.Protect(Data, 0x1000, Win32Memory.PageReadOnly, out _));
                        Assert.True(HostPages.Current.Protect(memory.HostBase + (int)Data, 0x1000,
                            write: false, execute: false));
                    }
                    try
                    {
                        var fault = Assert.Throws<GuestException>(() => jit.RunBlock(layout == 1 ? 4 : 1));
                        Assert.Equal(GuestException.AccessViolation, fault.Code);
                        Assert.Equal(target, fault.Eip);
                        Assert.Equal((uint)(access == 2 ? 1 : 0), fault.Information[0]);
                        Assert.Equal(badAddress, fault.Information[1]);
                        Assert.Equal(target, actual.Eip);
                    }
                    finally
                    {
                        if (protectedPage)
                        {
                            Assert.True(HostPages.Current.Protect(memory.HostBase + (int)Data, 0x1000,
                                write: true, execute: false));
                            Assert.Equal(0u, memory.Protect(Data, 0x1000, Win32Memory.PageReadWrite, out _));
                        }
                    }
                }

                if (access != 2)
                {
                    var reference = new Interpreter(expected, memory);
                    var fault = Assert.Throws<GuestException>(() => reference.Step());
                    Assert.Equal(GuestException.AccessViolation, fault.Code);
                    Assert.Equal(target, fault.Eip);
                    Assert.Equal(badAddress, fault.Information[1]);
                }
            }
        }

        private static void WriteJump(GuestMemory memory, uint from, uint to)
        {
            var displacement = unchecked((int)(to - from - 5));
            memory.WriteBytes(from, new byte[]
            {
                0xE9, (byte)displacement, (byte)(displacement >> 8),
                (byte)(displacement >> 16), (byte)(displacement >> 24),
            });
        }
    }
}
