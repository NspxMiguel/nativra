using System;
using System.Text;
using Nativra.X86.Cpu;
using Nativra.X86.Loader;
using Xunit;

namespace Nativra.X86.Tests
{
    public sealed class GuestShaderCompilerTests : IDisposable
    {
        private readonly GuestProcess process;
        private readonly GuestKernel kernel;
        private ShaderCompileRequest received;
        private ShaderCompileResult answer;

        public GuestShaderCompilerTests()
        {
            process = new GuestProcess(new GuestMemory(native: true), useJit: false);
            kernel = new GuestKernel(process);
            kernel.ShaderCompiler = request => { received = request; return answer; };
            kernel.Install();
        }

        public void Dispose() => process.Dispose();

        private uint Allocate(uint size = 128) => kernel.Heap.Alloc(size, zero: true);

        private uint Text(string value)
        {
            var bytes = Encoding.ASCII.GetBytes(value + "\0");
            var pointer = Allocate((uint)bytes.Length);
            process.Memory.WriteBytes(pointer, bytes);
            return pointer;
        }

        private uint Call(string module, string function, params uint[] args)
        {
            var run = process.Call(process.Imports.Bind(module, function, -1), out var eax, 1000000, args);
            Assert.True(run.Ok, run.ToString());
            return eax;
        }

        private uint Compile(uint source, uint length, uint macros, uint code, uint codeCap, uint codeLength,
            uint errors, uint errorCap, uint errorLength) =>
            Call("nativra_host.dll", "HostD3DCompile", source, length, Text("shader.frag"), macros,
                Text("main"), Text("ps_3_0"), 3, 7, code, codeCap, codeLength, errors, errorCap, errorLength);

        [Fact]
        public void DynamicLookupAndCapacityRetryCopyBytecode()
        {
            var module = Call("kernel32.dll", "LoadLibraryW", Wide("nativra_host.dll"));
            Assert.NotEqual(0u, module);
            Assert.NotEqual(0u, Call("kernel32.dll", "GetProcAddress", module, Text("HostD3DCompile")));
            var source = Allocate();
            process.Memory.WriteBytes(source, new byte[] { 1, 0, 255, 4 });
            answer = new ShaderCompileResult { HResult = 0, Code = new byte[] { 0x44, 0x58, 0x42, 0x43 } };
            var codeLength = Allocate(4);
            var errorLength = Allocate(4);
            Assert.Equal(0x8007007Au, Compile(source, 4, 0, 0, 0, codeLength, 0, 0, errorLength));
            Assert.Equal(4u, process.Memory.Read32(codeLength));
            Assert.Equal(0u, process.Memory.Read32(errorLength));
            var code = Allocate(4);
            Assert.Equal(0u, Compile(source, 4, 0, code, 4, codeLength, 0, 0, errorLength));
            Assert.Equal(answer.Code, process.Memory.ReadBytes(code, 4));
            Assert.Equal(new byte[] { 1, 0, 255, 4 }, received.Source);
            Assert.Null(received.Macros);
            Assert.Equal("ps_3_0", received.Target);
            Assert.Equal(3u, received.Flags1);
            Assert.Equal(7u, received.Flags2);
        }

        [Fact]
        public void CompilerErrorsAndGuestMacrosCrossTheBoundary()
        {
            var source = Text("bad shader");
            var macros = Allocate(24);
            process.Memory.Write32(macros, Text("LIGHTS"));
            process.Memory.Write32(macros + 4, Text("2"));
            process.Memory.Write32(macros + 8, Text("QUALITY"));
            process.Memory.Write32(macros + 12, Text("high"));
            answer = new ShaderCompileResult
            {
                HResult = 0x80004005,
                Errors = Encoding.ASCII.GetBytes("syntax error\0")
            };
            var codeLength = Allocate(4);
            var errorLength = Allocate(4);
            Assert.Equal(0x8007007Au, Compile(source, 10, macros, 0, 0, codeLength, 0, 0, errorLength));
            Assert.Equal((uint)answer.Errors.Length, process.Memory.Read32(errorLength));
            var errorBuffer = Allocate((uint)answer.Errors.Length);
            Assert.Equal(0x80004005u, Compile(source, 10, macros, 0, 0, codeLength,
                errorBuffer, (uint)answer.Errors.Length, errorLength));
            Assert.Equal(answer.Errors, process.Memory.ReadBytes(errorBuffer, answer.Errors.Length));
            Assert.Equal("LIGHTS", received.Macros[0].Name);
            Assert.Equal("2", received.Macros[0].Definition);
            Assert.Equal("QUALITY", received.Macros[1].Name);
            Assert.Equal("high", received.Macros[1].Definition);
        }

        private uint Wide(string value)
        {
            var bytes = Encoding.Unicode.GetBytes(value + "\0");
            var pointer = Allocate((uint)bytes.Length);
            process.Memory.WriteBytes(pointer, bytes);
            return pointer;
        }
    }
}
