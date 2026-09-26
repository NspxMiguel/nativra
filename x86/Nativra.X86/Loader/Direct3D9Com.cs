using System;
using System.Runtime.InteropServices;

namespace Nativra.X86.Loader
{
    /// <summary>
    /// The Direct3D 9 interfaces a 32-bit game calls, in d3d9.h vtable order,
    /// with the signatures the COM bridge needs (see <see cref="ComInterface"/>).
    /// The host side is the 64-bit d3d9.dll (Direct3D 9 on Direct3D 11) the app
    /// already carries; its lock memory comes from the guest's space, so the
    /// pointers LockRect and Lock hand out are ones the game can use.
    /// </summary>
    public static class Direct3D9Com
    {
        public static void Define(GuestCom com)
        {
            const string R = "IDirect3DResource9";
            const string BT = "IDirect3DBaseTexture9";
            const string S = "IDirect3DSurface9";
            const string D = "IDirect3DDevice9";

            com.Define("IDirect3D9", new Guid("81BDCBCA-64D4-426d-AE8D-AD0147F4275C"), null, true,
                "RegisterSoftwareDevice(p)", "GetAdapterCount()", "GetAdapterIdentifier(u,u,p)",
                "GetAdapterModeCount(u,u)", "EnumAdapterModes(u,u,u,p)", "GetAdapterDisplayMode(u,p)",
                "CheckDeviceType(u,u,u,u,u)", "CheckDeviceFormat(u,u,u,u,u,u)",
                "CheckDeviceMultiSampleType(u,u,u,u,u,p)", "CheckDepthStencilMatch(u,u,u,u,u)",
                "CheckDeviceFormatConversion(u,u,u,u)", "GetDeviceCaps(u,u,p)", "GetAdapterMonitor(u)",
                "CreateDevice(u,u,h,u,P,o:" + D + ")");

            // Declared before the device so the device's signatures can name them.
            com.Define(R, new Guid("05EEC05D-8F7D-4362-B999-D1BAF357C704"), null, true,
                "GetDevice(o:" + D + ")", "SetPrivateData(p,p,u,u)", "GetPrivateData(p,p,p)",
                "FreePrivateData(p)", "SetPriority(u)", "GetPriority()", "PreLoad()", "GetType()");
            com.Define(BT, new Guid("580CA87E-1D3C-4d54-991D-B7D3E3C298CE"), R, true,
                "SetLOD(u)", "GetLOD()", "GetLevelCount()", "SetAutoGenFilterType(u)",
                "GetAutoGenFilterType()", "GenerateMipSubLevels()");
            com.Define(S, new Guid("0CFBAF3A-9FF6-429a-99B3-A2796AF8B89B"), R, true,
                "GetContainer(q)", "GetDesc(p)", "LockRect(L,p,u)", "UnlockRect()", "GetDC(V)", "ReleaseDC(h)");
            com.Define("IDirect3DVolume9", new Guid("24F416E6-1F67-4aa7-B88E-D33F6F3128A1"), null, true,
                "GetDevice(o:" + D + ")", "SetPrivateData(p,p,u,u)", "GetPrivateData(p,p,p)", "FreePrivateData(p)",
                "GetContainer(q)", "GetDesc(p)", "LockBox(B,p,u)", "UnlockBox()");
            com.Define("IDirect3DTexture9", new Guid("85C31227-3DE5-4f00-9B3A-F11AC38C18B5"), BT, true,
                "GetLevelDesc(u,p)", "GetSurfaceLevel(u,o:" + S + ")", "LockRect(u,L,p,u)", "UnlockRect(u)",
                "AddDirtyRect(p)");
            com.Define("IDirect3DCubeTexture9", new Guid("FFF32F81-D953-473a-9223-93D652ABA93F"), BT, true,
                "GetLevelDesc(u,p)", "GetCubeMapSurface(u,u,o:" + S + ")", "LockRect(u,u,L,p,u)",
                "UnlockRect(u,u)", "AddDirtyRect(u,p)");
            com.Define("IDirect3DVolumeTexture9", new Guid("2518526C-E789-4111-A7B9-47EF328D13E6"), BT, true,
                "GetLevelDesc(u,p)", "GetVolumeLevel(u,o:IDirect3DVolume9)", "LockBox(u,B,p,u)", "UnlockBox(u)",
                "AddDirtyBox(p)");
            com.Define("IDirect3DVertexBuffer9", new Guid("B64BB1B5-FD70-4df6-BF91-19D0A12455E3"), R, true,
                "Lock(u,u,V,u)", "Unlock()", "GetDesc(p)");
            com.Define("IDirect3DIndexBuffer9", new Guid("7C9DD65E-D3F7-4529-ACEE-785830ACDE35"), R, true,
                "Lock(u,u,V,u)", "Unlock()", "GetDesc(p)");
            com.Define("IDirect3DVertexDeclaration9", new Guid("DD13C59C-36FA-4098-A8FB-C7ED39DC8546"), null, true,
                "GetDevice(o:" + D + ")", "GetDeclaration(p,p)");
            com.Define("IDirect3DVertexShader9", new Guid("EFC5557E-6265-4613-8A94-43857889EB36"), null, true,
                "GetDevice(o:" + D + ")", "GetFunction(p,p)");
            com.Define("IDirect3DPixelShader9", new Guid("6D3BDBDC-5B02-4415-B852-CE5E8BCCB289"), null, true,
                "GetDevice(o:" + D + ")", "GetFunction(p,p)");
            com.Define("IDirect3DStateBlock9", new Guid("B07C4FE5-310D-4ba8-A23C-4F0F206F218B"), null, true,
                "GetDevice(o:" + D + ")", "Capture()", "Apply()");
            com.Define("IDirect3DQuery9", new Guid("d9771460-a695-4f26-bbd3-27b840b541cc"), null, true,
                "GetDevice(o:" + D + ")", "GetType()", "GetDataSize()", "Issue(u)", "GetData(p,u,u)");
            com.Define("IDirect3DSwapChain9", new Guid("794950F2-ADFC-458a-905E-10A10B0B503B"), null, true,
                "Present(p,p,h,p,u)", "GetFrontBufferData(i:" + S + ")", "GetBackBuffer(u,u,o:" + S + ")",
                "GetRasterStatus(p)", "GetDisplayMode(p)", "GetDevice(o:" + D + ")", "GetPresentParameters(P)");

            // The device refers to itself (GetDevice) and everything above.
            com.Define(D, new Guid("D0223B96-BF7A-43fd-92BD-A43B0D82B9EB"), null, true,
                "TestCooperativeLevel()", "GetAvailableTextureMem()", "EvictManagedResources()",
                "GetDirect3D(o:IDirect3D9)", "GetDeviceCaps(p)", "GetDisplayMode(u,p)", "GetCreationParameters(C)",
                "SetCursorProperties(u,u,i:" + S + ")", "SetCursorPosition(s,s,u)", "ShowCursor(u)",
                "CreateAdditionalSwapChain(P,o:IDirect3DSwapChain9)", "GetSwapChain(u,o:IDirect3DSwapChain9)",
                "GetNumberOfSwapChains()", "Reset(P)", "Present(p,p,h,p)", "GetBackBuffer(u,u,u,o:" + S + ")",
                "GetRasterStatus(u,p)", "SetDialogBoxMode(u)", "SetGammaRamp(u,u,p)", "GetGammaRamp(u,p)",
                "CreateTexture(u,u,u,u,u,u,o:IDirect3DTexture9,x)",
                "CreateVolumeTexture(u,u,u,u,u,u,u,o:IDirect3DVolumeTexture9,x)",
                "CreateCubeTexture(u,u,u,u,u,o:IDirect3DCubeTexture9,x)",
                "CreateVertexBuffer(u,u,u,u,o:IDirect3DVertexBuffer9,x)",
                "CreateIndexBuffer(u,u,u,u,o:IDirect3DIndexBuffer9,x)",
                "CreateRenderTarget(u,u,u,u,u,u,o:" + S + ",x)",
                "CreateDepthStencilSurface(u,u,u,u,u,u,o:" + S + ",x)",
                "UpdateSurface(i:" + S + ",p,i:" + S + ",p)", "UpdateTexture(i:" + BT + ",i:" + BT + ")",
                "GetRenderTargetData(i:" + S + ",i:" + S + ")", "GetFrontBufferData(u,i:" + S + ")",
                "StretchRect(i:" + S + ",p,i:" + S + ",p,u)", "ColorFill(i:" + S + ",p,u)",
                "CreateOffscreenPlainSurface(u,u,u,u,o:" + S + ",x)",
                "SetRenderTarget(u,i:" + S + ")", "GetRenderTarget(u,o:" + S + ")",
                "SetDepthStencilSurface(i:" + S + ")", "GetDepthStencilSurface(o:" + S + ")",
                "BeginScene()", "EndScene()", "Clear(u,p,u,u,u,u)",
                "SetTransform(u,p)", "GetTransform(u,p)", "MultiplyTransform(u,p)",
                "SetViewport(p)", "GetViewport(p)", "SetMaterial(p)", "GetMaterial(p)",
                "SetLight(u,p)", "GetLight(u,p)", "LightEnable(u,u)", "GetLightEnable(u,p)",
                "SetClipPlane(u,p)", "GetClipPlane(u,p)", "SetRenderState(u,u)", "GetRenderState(u,p)",
                "CreateStateBlock(u,o:IDirect3DStateBlock9)", "BeginStateBlock()",
                "EndStateBlock(o:IDirect3DStateBlock9)", "SetClipStatus(p)", "GetClipStatus(p)",
                "GetTexture(u,o:" + BT + ")", "SetTexture(u,i:" + BT + ")",
                "GetTextureStageState(u,u,p)", "SetTextureStageState(u,u,u)",
                "GetSamplerState(u,u,p)", "SetSamplerState(u,u,u)", "ValidateDevice(p)",
                "SetPaletteEntries(u,p)", "GetPaletteEntries(u,p)", "SetCurrentTexturePalette(u)",
                "GetCurrentTexturePalette(p)", "SetScissorRect(p)", "GetScissorRect(p)",
                "SetSoftwareVertexProcessing(u)", "GetSoftwareVertexProcessing()",
                "SetNPatchMode(f)", "GetNPatchMode():F",
                "DrawPrimitive(u,u,u)", "DrawIndexedPrimitive(u,s,u,u,u,u)", "DrawPrimitiveUP(u,u,p,u)",
                "DrawIndexedPrimitiveUP(u,u,u,u,p,u,p,u)",
                "ProcessVertices(u,u,u,i:IDirect3DVertexBuffer9,i:IDirect3DVertexDeclaration9,u)",
                "CreateVertexDeclaration(p,o:IDirect3DVertexDeclaration9)",
                "SetVertexDeclaration(i:IDirect3DVertexDeclaration9)",
                "GetVertexDeclaration(o:IDirect3DVertexDeclaration9)", "SetFVF(u)", "GetFVF(p)",
                "CreateVertexShader(p,o:IDirect3DVertexShader9)", "SetVertexShader(i:IDirect3DVertexShader9)",
                "GetVertexShader(o:IDirect3DVertexShader9)",
                "SetVertexShaderConstantF(u,p,u)", "GetVertexShaderConstantF(u,p,u)",
                "SetVertexShaderConstantI(u,p,u)", "GetVertexShaderConstantI(u,p,u)",
                "SetVertexShaderConstantB(u,p,u)", "GetVertexShaderConstantB(u,p,u)",
                "SetStreamSource(u,i:IDirect3DVertexBuffer9,u,u)",
                "GetStreamSource(u,o:IDirect3DVertexBuffer9,p,p)",
                "SetStreamSourceFreq(u,u)", "GetStreamSourceFreq(u,p)",
                "SetIndices(i:IDirect3DIndexBuffer9)", "GetIndices(o:IDirect3DIndexBuffer9)",
                "CreatePixelShader(p,o:IDirect3DPixelShader9)", "SetPixelShader(i:IDirect3DPixelShader9)",
                "GetPixelShader(o:IDirect3DPixelShader9)",
                "SetPixelShaderConstantF(u,p,u)", "GetPixelShaderConstantF(u,p,u)",
                "SetPixelShaderConstantI(u,p,u)", "GetPixelShaderConstantI(u,p,u)",
                "SetPixelShaderConstantB(u,p,u)", "GetPixelShaderConstantB(u,p,u)",
                "DrawRectPatch(u,p,p)", "DrawTriPatch(u,p,p)", "DeletePatch(u)",
                "CreateQuery(u,o:IDirect3DQuery9)");

            // A base texture or resource handed out without its concrete type
            // (GetTexture on a texture the guest has not seen): ask the object.
            var resource = com.Find(R);
            Func<IntPtr, ComInterface> concrete = host =>
            {
                switch (ResourceType(host))
                {
                    case 1: return com.Find(S);
                    case 2: return com.Find("IDirect3DVolume9");
                    case 3: return com.Find("IDirect3DTexture9");
                    case 4: return com.Find("IDirect3DVolumeTexture9");
                    case 5: return com.Find("IDirect3DCubeTexture9");
                    case 6: return com.Find("IDirect3DVertexBuffer9");
                    case 7: return com.Find("IDirect3DIndexBuffer9");
                    default: return null;
                }
            };
            resource.Resolve = concrete;
            com.Find(BT).Resolve = concrete;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint GetTypeFn(IntPtr self);

        /// <summary>IDirect3DResource9::GetType (vtable slot 10) on a host object.</summary>
        private static uint ResourceType(IntPtr host)
        {
            var function = Marshal.ReadIntPtr(Marshal.ReadIntPtr(host), 10 * IntPtr.Size);
            return Marshal.GetDelegateForFunctionPointer<GetTypeFn>(function)(host);
        }
    }
}
