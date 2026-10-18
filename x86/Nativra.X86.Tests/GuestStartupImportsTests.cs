using System;
using System.Linq;
using Nativra.X86.Cpu;
using Nativra.X86.Loader;
using Xunit;

namespace Nativra.X86.Tests
{
    public sealed class GuestStartupImportsTests
    {
        private static uint Call(GuestProcess process, string module, string name, params uint[] args)
        {
            var result = process.Call(process.Imports.Bind(module, name, -1), out var eax, 10000, args);
            Assert.True(result.Ok, result.ToString());
            return eax;
        }

        [Theory]
        [InlineData("", "\\")]
        [InlineData("C:", "C:\\")]
        [InlineData("C:\\..", "C:\\")]
        [InlineData("C:\\one\\.\\..\\two\\three\\..", "C:\\two")]
        [InlineData("C:\\one/.\\..\\two/three\\..\\four/.five", "C:\\four/.five")]
        [InlineData("../../one/two/", "../../one/two/")]
        [InlineData("one\\two", "one\\two")]
        [InlineData("one\\..\\two", "\\two")]
        [InlineData("\\\\server\\share\\one\\..\\..", "\\\\server\\share")]
        public void PathCanonicalizationPreservesWindowsRootsAndLiteralForwardSlashes(string source, string expected)
        {
            using (var process = new GuestProcess(new GuestMemory(), useJit: false))
            {
                var kernel = new GuestKernel(process);
                kernel.Install();
                var input = kernel.Heap.Alloc(600);
                foreach (var wide in new[] { false, true })
                {
                    if (wide) process.Memory.WriteUnicode(input, source); else process.Memory.WriteAnsi(input, source);
                    process.LastError = 0x1234;
                    Assert.Equal(1u, Call(process, "shlwapi.dll", "PathCanonicalize" + (wide ? "W" : "A"), input, input));
                    Assert.Equal(expected, wide ? process.Memory.ReadUnicode(input) : process.Memory.ReadAnsi(input));
                    Assert.Equal(0x1234u, process.LastError);
                }
            }
        }

        [Fact]
        public void PathCanonicalizationBoundsOutputAndRejectsNullInputs()
        {
            using (var process = new GuestProcess(new GuestMemory(), useJit: false))
            {
                var kernel = new GuestKernel(process);
                kernel.Install();
                var output = kernel.Heap.Alloc(524);
                var input = kernel.Heap.Alloc(600);
                process.Memory.WriteUnicode(input, new string('a', 259));
                process.Memory.Write32(output + 520, 0xC0FFEE);
                Assert.Equal(1u, Call(process, "shlwapi.dll", "PathCanonicalizeW", output, input));
                Assert.Equal(0xC0FFEEu, process.Memory.Read32(output + 520));
                process.Memory.WriteUnicode(input, new string('a', 260));
                Assert.Equal(0u, Call(process, "shlwapi.dll", "PathCanonicalizeW", output, input));
                Assert.Equal(206u, process.LastError);
                Assert.Equal(0u, Call(process, "shlwapi.dll", "PathCanonicalizeW", output, 0));
                Assert.Equal(87u, process.LastError);
                Assert.Equal("", process.Memory.ReadUnicode(output));
                Assert.Equal(0u, Call(process, "shlwapi.dll", "PathCanonicalizeW", 0, input));
            }
        }

        [Fact]
        public void BcryptSystemRandomWritesOnlyTheRequestedBufferAcrossChunks()
        {
            using (var process = new GuestProcess(new GuestMemory(), useJit: false))
            {
                var kernel = new GuestKernel(process);
                kernel.Install();
                var buffer = kernel.Heap.Alloc(9012);
                process.Memory.WriteBytes(buffer, Enumerable.Repeat((byte)0xA5, 9012).ToArray());
                Assert.Equal(0u, Call(process, "bcrypt.dll", "BCryptGenRandom", 0, buffer + 4, 9000, 2));
                Assert.Equal(0xA5A5A5A5u, process.Memory.Read32(buffer));
                Assert.Equal(0xA5A5A5A5u, process.Memory.Read32(buffer + 9004));
                Assert.Contains(process.Memory.ReadBytes(buffer + 4, 9000), b => b != 0xA5);
                Assert.Equal(0u, Call(process, "bcrypt.dll", "BCryptGenRandom", 0, 1, 0, 2));
                Assert.Equal(0xC000000Du, Call(process, "bcrypt.dll", "BCryptGenRandom", 0, 0, 0, 2));
                Assert.Equal(0xC0000008u, Call(process, "bcrypt.dll", "BCryptGenRandom", 0, buffer, 4, 0));
                Assert.Equal(0xC000000Du, Call(process, "bcrypt.dll", "BCryptGenRandom", 0, buffer, 4, 6));
            }
        }

        [Fact]
        public void BcryptRngProvidersHaveAnExplicitLifetimeAndRejectUnsupportedAlgorithms()
        {
            using (var process = new GuestProcess(new GuestMemory(), useJit: false))
            {
                var kernel = new GuestKernel(process);
                kernel.Install();
                var algorithm = kernel.Heap.Alloc(32);
                var output = kernel.Heap.Alloc(64);
                process.Memory.WriteUnicode(algorithm, "RNG");
                Assert.Equal(0u, Call(process, "bcrypt.dll", "BCryptOpenAlgorithmProvider", output, algorithm, 0, 0));
                var handle = process.Memory.Read32(output);
                Assert.NotEqual(0u, handle);
                Assert.Equal(0u, Call(process, "bcrypt.dll", "BCryptGenRandom", handle, output + 4, 32, 0));
                Assert.Equal(0u, Call(process, "bcrypt.dll", "BCryptCloseAlgorithmProvider", handle, 0));
                Assert.Equal(0xC0000008u, Call(process, "bcrypt.dll", "BCryptGenRandom", handle, output + 4, 32, 0));
                Assert.Equal(0u, Call(process, "bcrypt.dll", "BCryptGenRandom", 0x81, output + 4, 32, 0));
                process.Memory.WriteUnicode(algorithm, "AES");
                Assert.Equal(0xC00000BBu, Call(process, "bcrypt.dll", "BCryptOpenAlgorithmProvider", output, algorithm, 0, 0));
                Assert.Equal(0u, process.Memory.Read32(output));
            }
        }
    }
}
