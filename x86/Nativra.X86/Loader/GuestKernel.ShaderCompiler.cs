using System;
using System.Collections.Generic;
using Nativra.X86.Cpu;

namespace Nativra.X86.Loader
{
    public sealed class ShaderMacro
    {
        public string Name { get; set; }
        public string Definition { get; set; }
    }

    public sealed class ShaderCompileRequest
    {
        public byte[] Source { get; set; }
        public string SourceName { get; set; }
        public ShaderMacro[] Macros { get; set; }
        public string Entry { get; set; }
        public string Target { get; set; }
        public uint Flags1 { get; set; }
        public uint Flags2 { get; set; }
    }

    public sealed class ShaderCompileResult
    {
        public uint HResult { get; set; }
        public byte[] Code { get; set; }
        public byte[] Errors { get; set; }
    }

    public sealed partial class GuestKernel
    {
        private const uint InsufficientBuffer = 0x8007007A;
        private const uint InvalidArgument = 0x80070057;
        private const uint CompilerFailure = 0x80004005;
        private const int MaxShaderBytes = 16 * 1024 * 1024;

        /// <summary>The platform compiler, injected before Install; null leaves the host module absent.</summary>
        public Func<ShaderCompileRequest, ShaderCompileResult> ShaderCompiler { get; set; }

        private void InstallShaderCompiler()
        {
            if (ShaderCompiler == null) return;
            const string module = "nativra_host.dll";
            process.HostServed.Add(module);
            process.Imports.Register(module, "HostD3DCompile", CallConv.Stdcall, 14, CompileGuestShader);
        }

        private ulong CompileGuestShader(GuestCall call)
        {
            // src, srcLen, name, macros, entry, target, flags1, flags2,
            // codeOut, codeCap, codeLen, errOut, errCap, errLen.
            if (call.Arg(0) == 0 || call.Arg(1) == 0 || call.Arg(1) > MaxShaderBytes ||
                call.Arg(4) == 0 || call.Arg(5) == 0 || call.Arg(10) == 0 || call.Arg(13) == 0)
            {
                Log?.Invoke("HostD3DCompile: invalid arguments src=0x" + call.Arg(0).ToString("X") + " len=" + call.Arg(1) +
                    " entry=0x" + call.Arg(4).ToString("X") + " target=0x" + call.Arg(5).ToString("X") +
                    " codeLen=0x" + call.Arg(10).ToString("X") + " errLen=0x" + call.Arg(13).ToString("X"));
                return InvalidArgument;
            }
            Log?.Invoke("HostD3DCompile " + call.Arg(1) + " bytes, cap " + call.Arg(9) + "/" + call.Arg(12));
            var macroPointer = call.Arg(3);
            ShaderMacro[] macros = null;
            if (macroPointer != 0)
            {
                var parsed = new List<ShaderMacro>();
                for (var index = 0; index < 256; index++)
                {
                    var name = memory.Read32(macroPointer + (uint)index * 8);
                    var definition = memory.Read32(macroPointer + (uint)index * 8 + 4);
                    if (name == 0) break;
                    if (definition == 0) return InvalidArgument;
                    parsed.Add(new ShaderMacro { Name = memory.ReadAnsi(name), Definition = memory.ReadAnsi(definition) });
                    if (index == 255) return InvalidArgument;
                }
                macros = parsed.ToArray();
            }
            var request = new ShaderCompileRequest
            {
                Source = memory.ReadBytes(call.Arg(0), (int)call.Arg(1)),
                SourceName = call.Arg(2) == 0 ? null : memory.ReadAnsi(call.Arg(2)),
                Macros = macros,
                Entry = memory.ReadAnsi(call.Arg(4)),
                Target = memory.ReadAnsi(call.Arg(5)),
                Flags1 = call.Arg(6),
                Flags2 = call.Arg(7)
            };
            ShaderCompileResult result;
            try { result = ShaderCompiler(request); }
            catch (Exception error) { Log?.Invoke("HostD3DCompile: " + error.Message); return CompilerFailure; }
            if (result == null) return CompilerFailure;
            if (result.HResult != 0)
            {
                // A failed compile is the first thing to read when a game then misbehaves.
                var text = result.Errors == null ? "" : System.Text.Encoding.ASCII.GetString(result.Errors);
                Log?.Invoke("HostD3DCompile " + request.Target + " " + request.Entry + " failed 0x" + result.HResult.ToString("X8") +
                    ": " + (text.Length > 400 ? text.Substring(0, 400) : text).Replace('\n', ' ').Replace('\r', ' '));
            }
            var code = result.Code ?? new byte[0];
            var errors = result.Errors ?? new byte[0];
            memory.Write32(call.Arg(10), (uint)code.Length);
            memory.Write32(call.Arg(13), (uint)errors.Length);
            if (code.Length > MaxShaderBytes || errors.Length > MaxShaderBytes) return CompilerFailure;
            if ((uint)code.Length > call.Arg(9) || (uint)errors.Length > call.Arg(12)) return InsufficientBuffer;
            if (code.Length != 0)
            {
                if (call.Arg(8) == 0) return InvalidArgument;
                memory.WriteBytes(call.Arg(8), code);
            }
            if (errors.Length != 0)
            {
                if (call.Arg(11) == 0) return InvalidArgument;
                memory.WriteBytes(call.Arg(11), errors);
            }
            return result.HResult;
        }
    }
}
