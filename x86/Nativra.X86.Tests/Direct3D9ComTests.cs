using Nativra.X86.Cpu;
using Nativra.X86.Loader;
using Xunit;

namespace Nativra.X86.Tests
{
    /// <summary>The D3D9 tables must match d3d9.h slot for slot, or every call after a mistake lands on the wrong method.</summary>
    public sealed class Direct3D9ComTests
    {
        [Theory]
        [InlineData("IDirect3D9", 17)]
        [InlineData("IDirect3DDevice9", 119)]
        [InlineData("IDirect3DSwapChain9", 10)]
        [InlineData("IDirect3DResource9", 11)]
        [InlineData("IDirect3DBaseTexture9", 17)]
        [InlineData("IDirect3DTexture9", 22)]
        [InlineData("IDirect3DCubeTexture9", 22)]
        [InlineData("IDirect3DVolumeTexture9", 22)]
        [InlineData("IDirect3DSurface9", 17)]
        [InlineData("IDirect3DVolume9", 11)]
        [InlineData("IDirect3DVertexBuffer9", 14)]
        [InlineData("IDirect3DIndexBuffer9", 14)]
        [InlineData("IDirect3DVertexDeclaration9", 5)]
        [InlineData("IDirect3DVertexShader9", 5)]
        [InlineData("IDirect3DPixelShader9", 5)]
        [InlineData("IDirect3DStateBlock9", 6)]
        [InlineData("IDirect3DQuery9", 8)]
        public void VtableHasTheRightNumberOfMethods(string name, int count)
        {
            using (var p = new GuestProcess(new GuestMemory(native: true), useJit: false))
            {
                var kernel = new GuestKernel(p);
                kernel.Install();
                var com = new GuestCom(p, kernel);
                Direct3D9Com.Define(com);
                Assert.Equal(count, com.Find(name).Methods.Count);
            }
        }

        [Theory]
        [InlineData("IDirect3DDevice9", 16, "Reset")]
        [InlineData("IDirect3DDevice9", 17, "Present")]
        [InlineData("IDirect3DDevice9", 41, "BeginScene")]
        [InlineData("IDirect3DDevice9", 57, "SetRenderState")]
        [InlineData("IDirect3DDevice9", 65, "SetTexture")]
        [InlineData("IDirect3DDevice9", 81, "DrawPrimitive")]
        [InlineData("IDirect3DDevice9", 100, "SetStreamSource")]
        [InlineData("IDirect3DDevice9", 118, "CreateQuery")]
        [InlineData("IDirect3DTexture9", 19, "LockRect")]
        [InlineData("IDirect3DSurface9", 13, "LockRect")]
        [InlineData("IDirect3DVertexBuffer9", 11, "Lock")]
        [InlineData("IDirect3D9", 16, "CreateDevice")]
        public void MethodSitsAtItsD3D9Slot(string name, int slot, string method)
        {
            using (var p = new GuestProcess(new GuestMemory(native: true), useJit: false))
            {
                var kernel = new GuestKernel(p);
                kernel.Install();
                var com = new GuestCom(p, kernel);
                Direct3D9Com.Define(com);
                Assert.Equal(method, com.Find(name).Methods[slot].Name);
            }
        }
    }
}
