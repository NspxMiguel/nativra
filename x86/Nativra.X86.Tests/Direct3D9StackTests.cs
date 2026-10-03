using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nativra.X86.Cpu;
using Nativra.X86.Loader;
using Xunit;

namespace Nativra.X86.Tests
{
    public sealed class Direct3D9StackTests
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr Stub(IntPtr self);

        // Argument counts transcribed from the Microsoft DirectX SDK d3d9.h interface declarations.
        // Include inherited slots because the bridge expands each complete guest vtable.
        private static readonly Dictionary<string, string> Counts = new Dictionary<string, string>
        {
            { "IDirect3D9", "2,0,0,1,0,3,2,4,2,5,6,6,5,4,3,1,6" },
            { "IDirect3DDevice9", "2,0,0,0,0,0,1,1,2,1,3,3,1,2,2,0,1,4,4,2,1,3,2,8,9,7,6,6,8,8,4,2,2,2,5,3,6,2,2,1,1,0,0,6,2,2,2,1,1,1,1,2,2,2,2,2,2,2,2,2,0,1,1,1,2,2,3,3,3,3,1,2,2,1,1,1,1,1,0,1,0,3,6,4,8,6,2,1,1,1,1,2,1,1,3,3,3,3,3,3,4,4,2,2,1,1,2,1,1,3,3,3,3,3,3,3,3,1,2" },
            { "IDirect3DStateBlock9", "2,0,0,1,0,0" },
            { "IDirect3DSwapChain9", "2,0,0,5,1,3,1,1,1,1" },
            { "IDirect3DResource9", "2,0,0,1,4,3,1,1,0,0,0" },
            { "IDirect3DVertexDeclaration9", "2,0,0,1,2" },
            { "IDirect3DVertexShader9", "2,0,0,1,2" },
            { "IDirect3DPixelShader9", "2,0,0,1,2" },
            { "IDirect3DBaseTexture9", "2,0,0,1,4,3,1,1,0,0,0,1,0,0,1,0,0" },
            { "IDirect3DTexture9", "2,0,0,1,4,3,1,1,0,0,0,1,0,0,1,0,0,2,2,4,1,1" },
            { "IDirect3DVolumeTexture9", "2,0,0,1,4,3,1,1,0,0,0,1,0,0,1,0,0,2,2,4,1,1" },
            { "IDirect3DCubeTexture9", "2,0,0,1,4,3,1,1,0,0,0,1,0,0,1,0,0,2,3,5,2,2" },
            { "IDirect3DVertexBuffer9", "2,0,0,1,4,3,1,1,0,0,0,4,0,1" },
            { "IDirect3DIndexBuffer9", "2,0,0,1,4,3,1,1,0,0,0,4,0,1" },
            { "IDirect3DSurface9", "2,0,0,1,4,3,1,1,0,0,0,2,1,3,0,1,1" },
            { "IDirect3DVolume9", "2,0,0,1,4,3,1,2,1,3,0" },
            { "IDirect3DQuery9", "2,0,0,1,0,0,1,3" },
        };

        [Fact]
        public void EveryD3D9MethodPopsTheArgumentsDeclaredByD3D9H()
        {
            using (var process = new GuestProcess(new GuestMemory(native: true), useJit: false))
            {
                var kernel = new GuestKernel(process);
                kernel.Install();
                var com = new GuestCom(process, kernel);
                Direct3D9Com.Define(com);
                Stub stub = self => new IntPtr(1);
                var function = Marshal.GetFunctionPointerForDelegate(stub);
                var vtable = Marshal.AllocHGlobal(119 * IntPtr.Size);
                var hosts = new List<IntPtr>();
                try
                {
                    for (var n = 0; n < 119; n++) Marshal.WriteIntPtr(vtable, n * IntPtr.Size, function);
                    var argument = kernel.Heap.Alloc(2048, zero: true);
                    var mismatches = new List<string>();
                    foreach (var entry in Counts)
                    {
                        var host = Marshal.AllocHGlobal(IntPtr.Size);
                        hosts.Add(host);
                        Marshal.WriteIntPtr(host, vtable);
                        var face = com.Find(entry.Key);
                        var counts = entry.Value.Split(',');
                        Assert.Equal(counts.Length, face.Methods.Count);
                        var wrappedFace = entry.Key == "IDirect3DResource9" || entry.Key == "IDirect3DBaseTexture9"
                            ? com.Find("IDirect3DTexture9") : face;
                        var obj = com.Wrap(host, wrappedFace);
                        for (var slot = 0; slot < counts.Length; slot++)
                        {
                            var count = int.Parse(counts[slot]);
                            var args = new uint[count + 1];
                            args[0] = obj;
                            for (var n = 1; n < args.Length; n++) args[n] = argument;
                            var method = process.Memory.Read32(process.Memory.Read32(obj) + (uint)slot * 4);
                            Assert.True(process.Imports.TryResolve(method, out var import));
                            if (import.Handler.ArgDwords != count + 1)
                            {
                                mismatches.Add(entry.Key + "::" + face.Methods[slot].Name + " expects " + count + " arguments in d3d9.h but bridge pops " + (import.Handler.ArgDwords - 1));
                                continue;
                            }
                            Assert.True(StackLeftOver(process, kernel, method, args) == 0,
                                entry.Key + "::" + face.Methods[slot].Name + " left ESP unbalanced");
                        }
                    }
                    Assert.True(mismatches.Count == 0, string.Join("; ", mismatches));
                }
                finally
                {
                    GC.KeepAlive(stub);
                    foreach (var host in hosts) Marshal.FreeHGlobal(host);
                    Marshal.FreeHGlobal(vtable);
                }
            }
        }

        private static int StackLeftOver(GuestProcess process, GuestKernel kernel, uint function, uint[] args)
        {
            var code = new List<byte> { 0x89, 0xE3 }; // mov ebx, esp
            for (var n = args.Length - 1; n >= 0; n--)
            {
                code.Add(0x68); // push imm32
                code.AddRange(BitConverter.GetBytes(args[n]));
            }
            code.Add(0xB8); // mov eax, function
            code.AddRange(BitConverter.GetBytes(function));
            code.AddRange(new byte[] { 0xFF, 0xD0, 0x29, 0xE3, 0x89, 0xD8, 0xC3 });
            var at = kernel.Heap.Alloc((uint)code.Count);
            process.Memory.WriteBytes(at, code.ToArray());
            var result = process.Call(at, out var difference, 100000);
            Assert.True(result.Ok, result.ToString());
            return (int)difference;
        }
    }
}
