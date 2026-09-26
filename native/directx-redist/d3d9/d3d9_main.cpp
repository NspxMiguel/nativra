// IDirect3D9Ex, device capabilities and the DLL's exports.

#include "d3d9_objects.h"

namespace d3d9 {

namespace {

struct Mode { UINT width, height; };
const Mode kModes[] = {
    { 640, 480 }, { 800, 600 }, { 1024, 768 }, { 1280, 720 }, { 1280, 1024 }, { 1366, 768 },
    { 1600, 900 }, { 1920, 1080 }, { 2560, 1440 }, { 3840, 2160 },
};

bool IsDisplayFormat(D3DFORMAT f)
{
    return f == D3DFMT_X8R8G8B8 || f == D3DFMT_A8R8G8B8 || f == D3DFMT_R5G6B5 || f == D3DFMT_X1R5G5B5 ||
           f == D3DFMT_A2R10G10B10;
}

bool IsRenderable(D3DFORMAT f)
{
    switch (f) {
    case D3DFMT_A8R8G8B8: case D3DFMT_X8R8G8B8: case D3DFMT_A8B8G8R8: case D3DFMT_X8B8G8R8: case D3DFMT_R5G6B5:
    case D3DFMT_A1R5G5B5: case D3DFMT_X1R5G5B5: case D3DFMT_A2B10G10R10: case D3DFMT_G16R16: case D3DFMT_A16B16G16R16:
    case D3DFMT_R16F: case D3DFMT_G16R16F: case D3DFMT_A16B16G16R16F: case D3DFMT_R32F: case D3DFMT_G32R32F:
    case D3DFMT_A32B32G32R32F: case D3DFMT_A8:
        return true;
    default:
        return false;
    }
}

} // namespace

HRESULT Direct3D9::QueryInterface(REFIID riid, void** out)
{
    if (!out) return E_POINTER;
    *out = nullptr;
    if (riid == __uuidof(IUnknown) || riid == __uuidof(IDirect3D9) || (ex && riid == __uuidof(IDirect3D9Ex))) {
        AddRef();
        *out = static_cast<IDirect3D9Ex*>(this);
        return S_OK;
    }
    return E_NOINTERFACE;
}

HRESULT Direct3D9::GetAdapterIdentifier(UINT adapter, DWORD, D3DADAPTER_IDENTIFIER9* id)
{
    if (adapter != 0 || !id) return D3DERR_INVALIDCALL;
    std::memset(id, 0, sizeof *id);
    std::strcpy(id->Driver, "nativra_d3d9.dll");
    std::strcpy(id->Description, "Nativra Direct3D 9 on Direct3D 11");
    std::strcpy(id->DeviceName, "\\\\.\\DISPLAY1");
    id->DriverVersion.QuadPart = 0x001E0000000D0000LL;   // 30.0.13.0: new enough for driver checks
    id->VendorId = 0x1002;                               // the console's GPU is AMD
    id->DeviceId = 0x73DF;
    id->SubSysId = 0;
    id->Revision = 0;
    id->DeviceIdentifier = { 0x6e617469, 0x7672, 0x6139, { 'd', '3', 'd', '9', 'o', 'n', '1', '1' } };
    id->WHQLLevel = 1;
    return D3D_OK;
}

UINT Direct3D9::GetAdapterModeCount(UINT adapter, D3DFORMAT format)
{
    if (adapter != 0 || format != D3DFMT_X8R8G8B8) return 0;
    return static_cast<UINT>(sizeof kModes / sizeof kModes[0]);
}

HRESULT Direct3D9::EnumAdapterModes(UINT adapter, D3DFORMAT format, UINT mode, D3DDISPLAYMODE* out)
{
    if (adapter != 0 || !out || mode >= GetAdapterModeCount(adapter, format)) return D3DERR_INVALIDCALL;
    out->Width = kModes[mode].width;
    out->Height = kModes[mode].height;
    out->RefreshRate = 60;
    out->Format = D3DFMT_X8R8G8B8;
    return D3D_OK;
}

HRESULT Direct3D9::GetAdapterDisplayMode(UINT adapter, D3DDISPLAYMODE* mode)
{
    if (adapter != 0 || !mode) return D3DERR_INVALIDCALL;
    mode->Width = GetHost().width;
    mode->Height = GetHost().height;
    mode->RefreshRate = 60;
    mode->Format = D3DFMT_X8R8G8B8;
    return D3D_OK;
}

HRESULT Direct3D9::CheckDeviceType(UINT adapter, D3DDEVTYPE, D3DFORMAT display, D3DFORMAT backbuffer, BOOL windowed)
{
    if (adapter != 0) return D3DERR_INVALIDCALL;
    if (!IsDisplayFormat(display)) return D3DERR_NOTAVAILABLE;
    if (backbuffer == D3DFMT_UNKNOWN) return windowed ? D3D_OK : D3DERR_INVALIDCALL;
    return IsDisplayFormat(backbuffer) || backbuffer == D3DFMT_A8R8G8B8 ? D3D_OK : D3DERR_NOTAVAILABLE;
}

HRESULT Direct3D9::CheckDeviceFormat(UINT adapter, D3DDEVTYPE, D3DFORMAT, DWORD usage, D3DRESOURCETYPE resource,
                                     D3DFORMAT format)
{
    if (adapter != 0) return D3DERR_INVALIDCALL;
    const FormatInfo* f = GetFormat(format);
    if (!f || resource == D3DRTYPE_VOLUMETEXTURE || resource == D3DRTYPE_VOLUME) return D3DERR_NOTAVAILABLE;
    const bool depth = f->depth != DXGI_FORMAT_UNKNOWN;
    if ((usage & D3DUSAGE_DEPTHSTENCIL) && !depth) return D3DERR_NOTAVAILABLE;
    if ((usage & D3DUSAGE_RENDERTARGET) && !IsRenderable(format)) return D3DERR_NOTAVAILABLE;
    if (depth && !(usage & D3DUSAGE_DEPTHSTENCIL) && resource != D3DRTYPE_TEXTURE && resource != D3DRTYPE_SURFACE)
        return D3DERR_NOTAVAILABLE;
    if (depth && resource == D3DRTYPE_CUBETEXTURE) return D3DERR_NOTAVAILABLE;
    if ((usage & (D3DUSAGE_QUERY_SRGBREAD | D3DUSAGE_QUERY_SRGBWRITE)) && f->srgb == DXGI_FORMAT_UNKNOWN)
        return D3DERR_NOTAVAILABLE;
    if ((usage & D3DUSAGE_AUTOGENMIPMAP) && (f->block || depth || !IsRenderable(format))) return D3DOK_NOAUTOGEN;
    return D3D_OK;
}

HRESULT Direct3D9::CheckDeviceMultiSampleType(UINT adapter, D3DDEVTYPE, D3DFORMAT, BOOL, D3DMULTISAMPLE_TYPE ms,
                                              DWORD* quality)
{
    if (adapter != 0) return D3DERR_INVALIDCALL;
    // Multisampling is not implemented yet; games fall back to none.
    if (ms == D3DMULTISAMPLE_NONE || ms == D3DMULTISAMPLE_NONMASKABLE) {
        if (quality) *quality = 1;
        return D3D_OK;
    }
    if (quality) *quality = 0;
    return D3DERR_NOTAVAILABLE;
}

HRESULT Direct3D9::CheckDepthStencilMatch(UINT adapter, D3DDEVTYPE, D3DFORMAT, D3DFORMAT, D3DFORMAT ds)
{
    if (adapter != 0) return D3DERR_INVALIDCALL;
    const FormatInfo* f = GetFormat(ds);
    return f && f->depth != DXGI_FORMAT_UNKNOWN ? D3D_OK : D3DERR_NOTAVAILABLE;
}

HRESULT Direct3D9::CheckDeviceFormatConversion(UINT adapter, D3DDEVTYPE, D3DFORMAT src, D3DFORMAT dst)
{
    if (adapter != 0) return D3DERR_INVALIDCALL;
    return GetFormat(src) && GetFormat(dst) ? D3D_OK : D3DERR_NOTAVAILABLE;
}

HRESULT Direct3D9::GetDeviceCaps(UINT adapter, D3DDEVTYPE, D3DCAPS9* caps)
{
    if (adapter != 0 || !caps) return D3DERR_INVALIDCALL;
    FillCaps(caps);
    return D3D_OK;
}

HMONITOR Direct3D9::GetAdapterMonitor(UINT adapter)
{
    // A stand-in handle: this DLL imports nothing from user32.
    return adapter == 0 ? reinterpret_cast<HMONITOR>(static_cast<uintptr_t>(0x10001)) : nullptr;
}

HRESULT Direct3D9::CreateDevice(UINT adapter, D3DDEVTYPE type, HWND focus, DWORD flags,
                                D3DPRESENT_PARAMETERS* params, IDirect3DDevice9** out)
{
    if (!out) return D3DERR_INVALIDCALL;
    *out = nullptr;
    if (adapter != 0 || !params) return D3DERR_INVALIDCALL;
    auto* device = new Device(this, adapter, type, focus, flags, ex);
    const HRESULT hr = device->Init(params);
    if (FAILED(hr)) {
        device->Release();
        return hr;
    }
    *out = device;
    return D3D_OK;
}

UINT Direct3D9::GetAdapterModeCountEx(UINT adapter, const D3DDISPLAYMODEFILTER*)
{
    return GetAdapterModeCount(adapter, D3DFMT_X8R8G8B8);
}

HRESULT Direct3D9::EnumAdapterModesEx(UINT adapter, const D3DDISPLAYMODEFILTER*, UINT mode, D3DDISPLAYMODEEX* out)
{
    if (!out) return D3DERR_INVALIDCALL;
    D3DDISPLAYMODE m;
    const HRESULT hr = EnumAdapterModes(adapter, D3DFMT_X8R8G8B8, mode, &m);
    if (FAILED(hr)) return hr;
    out->Size = sizeof *out;
    out->Width = m.Width;
    out->Height = m.Height;
    out->RefreshRate = m.RefreshRate;
    out->Format = m.Format;
    out->ScanLineOrdering = D3DSCANLINEORDERING_PROGRESSIVE;
    return D3D_OK;
}

HRESULT Direct3D9::GetAdapterDisplayModeEx(UINT adapter, D3DDISPLAYMODEEX* mode, D3DDISPLAYROTATION* rotation)
{
    if (mode) {
        D3DDISPLAYMODE m;
        const HRESULT hr = GetAdapterDisplayMode(adapter, &m);
        if (FAILED(hr)) return hr;
        mode->Size = sizeof *mode;
        mode->Width = m.Width;
        mode->Height = m.Height;
        mode->RefreshRate = m.RefreshRate;
        mode->Format = m.Format;
        mode->ScanLineOrdering = D3DSCANLINEORDERING_PROGRESSIVE;
    }
    if (rotation) *rotation = D3DDISPLAYROTATION_IDENTITY;
    return D3D_OK;
}

HRESULT Direct3D9::CreateDeviceEx(UINT adapter, D3DDEVTYPE type, HWND focus, DWORD flags, D3DPRESENT_PARAMETERS* params,
                                  D3DDISPLAYMODEEX*, IDirect3DDevice9Ex** out)
{
    IDirect3DDevice9* device = nullptr;
    const HRESULT hr = CreateDevice(adapter, type, focus, flags, params, &device);
    *out = static_cast<IDirect3DDevice9Ex*>(static_cast<Device*>(device));
    return hr;
}

HRESULT Direct3D9::GetAdapterLUID(UINT adapter, LUID* luid)
{
    if (adapter != 0 || !luid) return D3DERR_INVALIDCALL;
    luid->LowPart = 0x4E415456;   // "NATV"
    luid->HighPart = 0;
    return D3D_OK;
}

void Direct3D9::FillCaps(D3DCAPS9* c)
{
    std::memset(c, 0, sizeof *c);
    c->DeviceType = D3DDEVTYPE_HAL;
    c->Caps2 = D3DCAPS2_CANAUTOGENMIPMAP | D3DCAPS2_DYNAMICTEXTURES | D3DCAPS2_FULLSCREENGAMMA | D3DCAPS2_CANMANAGERESOURCE;
    c->Caps3 = D3DCAPS3_ALPHA_FULLSCREEN_FLIP_OR_DISCARD | D3DCAPS3_COPY_TO_VIDMEM | D3DCAPS3_COPY_TO_SYSTEMMEM |
               D3DCAPS3_LINEAR_TO_SRGB_PRESENTATION;
    c->PresentationIntervals = D3DPRESENT_INTERVAL_IMMEDIATE | D3DPRESENT_INTERVAL_ONE | D3DPRESENT_INTERVAL_TWO |
                               D3DPRESENT_INTERVAL_THREE | D3DPRESENT_INTERVAL_FOUR;
    c->CursorCaps = D3DCURSORCAPS_COLOR | D3DCURSORCAPS_LOWRES;
    c->DevCaps = D3DDEVCAPS_EXECUTESYSTEMMEMORY | D3DDEVCAPS_EXECUTEVIDEOMEMORY | D3DDEVCAPS_TLVERTEXSYSTEMMEMORY |
                 D3DDEVCAPS_TLVERTEXVIDEOMEMORY | D3DDEVCAPS_TEXTURESYSTEMMEMORY | D3DDEVCAPS_TEXTUREVIDEOMEMORY |
                 D3DDEVCAPS_DRAWPRIMTLVERTEX | D3DDEVCAPS_CANRENDERAFTERFLIP | D3DDEVCAPS_TEXTURENONLOCALVIDMEM |
                 D3DDEVCAPS_DRAWPRIMITIVES2 | D3DDEVCAPS_DRAWPRIMITIVES2EX | D3DDEVCAPS_HWTRANSFORMANDLIGHT |
                 D3DDEVCAPS_CANBLTSYSTONONLOCAL | D3DDEVCAPS_HWRASTERIZATION | D3DDEVCAPS_PUREDEVICE;
    c->PrimitiveMiscCaps = D3DPMISCCAPS_MASKZ | D3DPMISCCAPS_CULLNONE | D3DPMISCCAPS_CULLCW | D3DPMISCCAPS_CULLCCW |
                           D3DPMISCCAPS_COLORWRITEENABLE | D3DPMISCCAPS_CLIPPLANESCALEDPOINTS | D3DPMISCCAPS_TSSARGTEMP |
                           D3DPMISCCAPS_BLENDOP | D3DPMISCCAPS_INDEPENDENTWRITEMASKS | D3DPMISCCAPS_PERSTAGECONSTANT |
                           D3DPMISCCAPS_FOGANDSPECULARALPHA | D3DPMISCCAPS_SEPARATEALPHABLEND |
                           D3DPMISCCAPS_MRTINDEPENDENTBITDEPTHS | D3DPMISCCAPS_MRTPOSTPIXELSHADERBLENDING |
                           D3DPMISCCAPS_FOGVERTEXCLAMPED | D3DPMISCCAPS_POSTBLENDSRGBCONVERT;
    c->RasterCaps = D3DPRASTERCAPS_DITHER | D3DPRASTERCAPS_ZTEST | D3DPRASTERCAPS_FOGVERTEX | D3DPRASTERCAPS_FOGTABLE |
                    D3DPRASTERCAPS_MIPMAPLODBIAS | D3DPRASTERCAPS_ZFOG | D3DPRASTERCAPS_COLORPERSPECTIVE |
                    D3DPRASTERCAPS_ANISOTROPY | D3DPRASTERCAPS_WFOG | D3DPRASTERCAPS_SCISSORTEST |
                    D3DPRASTERCAPS_SLOPESCALEDEPTHBIAS | D3DPRASTERCAPS_DEPTHBIAS | D3DPRASTERCAPS_MULTISAMPLE_TOGGLE;
    c->ZCmpCaps = c->AlphaCmpCaps = 0xFF;
    c->SrcBlendCaps = c->DestBlendCaps =
        D3DPBLENDCAPS_ZERO | D3DPBLENDCAPS_ONE | D3DPBLENDCAPS_SRCCOLOR | D3DPBLENDCAPS_INVSRCCOLOR |
        D3DPBLENDCAPS_SRCALPHA | D3DPBLENDCAPS_INVSRCALPHA | D3DPBLENDCAPS_DESTALPHA | D3DPBLENDCAPS_INVDESTALPHA |
        D3DPBLENDCAPS_DESTCOLOR | D3DPBLENDCAPS_INVDESTCOLOR | D3DPBLENDCAPS_SRCALPHASAT | D3DPBLENDCAPS_BOTHSRCALPHA |
        D3DPBLENDCAPS_BOTHINVSRCALPHA | D3DPBLENDCAPS_BLENDFACTOR | D3DPBLENDCAPS_SRCCOLOR2 | D3DPBLENDCAPS_INVSRCCOLOR2;
    c->ShadeCaps = D3DPSHADECAPS_COLORGOURAUDRGB | D3DPSHADECAPS_SPECULARGOURAUDRGB | D3DPSHADECAPS_ALPHAGOURAUDBLEND |
                   D3DPSHADECAPS_FOGGOURAUD;
    c->TextureCaps = D3DPTEXTURECAPS_PERSPECTIVE | D3DPTEXTURECAPS_ALPHA | D3DPTEXTURECAPS_PROJECTED |
                     D3DPTEXTURECAPS_CUBEMAP | D3DPTEXTURECAPS_MIPMAP | D3DPTEXTURECAPS_MIPCUBEMAP |
                     D3DPTEXTURECAPS_TEXREPEATNOTSCALEDBYSIZE;
    c->TextureFilterCaps = c->CubeTextureFilterCaps =
        D3DPTFILTERCAPS_MINFPOINT | D3DPTFILTERCAPS_MINFLINEAR | D3DPTFILTERCAPS_MINFANISOTROPIC |
        D3DPTFILTERCAPS_MIPFPOINT | D3DPTFILTERCAPS_MIPFLINEAR | D3DPTFILTERCAPS_MAGFPOINT |
        D3DPTFILTERCAPS_MAGFLINEAR | D3DPTFILTERCAPS_MAGFANISOTROPIC;
    c->TextureAddressCaps = D3DPTADDRESSCAPS_WRAP | D3DPTADDRESSCAPS_MIRROR | D3DPTADDRESSCAPS_CLAMP |
                            D3DPTADDRESSCAPS_BORDER | D3DPTADDRESSCAPS_INDEPENDENTUV | D3DPTADDRESSCAPS_MIRRORONCE;
    c->LineCaps = D3DLINECAPS_TEXTURE | D3DLINECAPS_ZTEST | D3DLINECAPS_BLEND | D3DLINECAPS_ALPHACMP | D3DLINECAPS_FOG;
    c->MaxTextureWidth = c->MaxTextureHeight = 8192;
    c->MaxTextureRepeat = 8192;
    c->MaxTextureAspectRatio = 8192;
    c->MaxAnisotropy = 16;
    c->MaxVertexW = 1e10f;
    c->GuardBandLeft = c->GuardBandTop = -32768.0f;
    c->GuardBandRight = c->GuardBandBottom = 32768.0f;
    c->StencilCaps = D3DSTENCILCAPS_KEEP | D3DSTENCILCAPS_ZERO | D3DSTENCILCAPS_REPLACE | D3DSTENCILCAPS_INCRSAT |
                     D3DSTENCILCAPS_DECRSAT | D3DSTENCILCAPS_INVERT | D3DSTENCILCAPS_INCR | D3DSTENCILCAPS_DECR |
                     D3DSTENCILCAPS_TWOSIDED;
    c->FVFCaps = D3DFVFCAPS_PSIZE | 8;
    c->TextureOpCaps = 0x03FFFFFF;
    c->MaxTextureBlendStages = 8;
    c->MaxSimultaneousTextures = 8;
    c->VertexProcessingCaps = D3DVTXPCAPS_TEXGEN | D3DVTXPCAPS_MATERIALSOURCE7 | D3DVTXPCAPS_DIRECTIONALLIGHTS |
                              D3DVTXPCAPS_POSITIONALLIGHTS | D3DVTXPCAPS_LOCALVIEWER | D3DVTXPCAPS_TWEENING |
                              D3DVTXPCAPS_TEXGEN_SPHEREMAP;
    c->MaxActiveLights = 8;
    c->MaxUserClipPlanes = 6;
    c->MaxVertexBlendMatrices = 4;
    c->MaxPointSize = 256.0f;
    c->MaxPrimitiveCount = 0x555555;
    c->MaxVertexIndex = 0xFFFFFF;
    c->MaxStreams = 16;
    c->MaxStreamStride = 508;
    c->VertexShaderVersion = D3DVS_VERSION(3, 0);
    c->MaxVertexShaderConst = 256;
    c->PixelShaderVersion = D3DPS_VERSION(3, 0);
    c->PixelShader1xMaxValue = 65504.0f;
    c->DevCaps2 = D3DDEVCAPS2_STREAMOFFSET | D3DDEVCAPS2_VERTEXELEMENTSCANSHARESTREAMOFFSET |
                  D3DDEVCAPS2_CAN_STRETCHRECT_FROM_TEXTURES;
    c->NumberOfAdaptersInGroup = 1;
    c->DeclTypes = D3DDTCAPS_UBYTE4 | D3DDTCAPS_UBYTE4N | D3DDTCAPS_SHORT2N | D3DDTCAPS_SHORT4N |
                   D3DDTCAPS_USHORT2N | D3DDTCAPS_USHORT4N | D3DDTCAPS_UDEC3 | D3DDTCAPS_DEC3N |
                   D3DDTCAPS_FLOAT16_2 | D3DDTCAPS_FLOAT16_4;
    c->NumSimultaneousRTs = 4;
    c->StretchRectFilterCaps = D3DPTFILTERCAPS_MINFPOINT | D3DPTFILTERCAPS_MAGFPOINT |
                               D3DPTFILTERCAPS_MINFLINEAR | D3DPTFILTERCAPS_MAGFLINEAR;
    c->VS20Caps.Caps = D3DVS20CAPS_PREDICATION;
    c->VS20Caps.DynamicFlowControlDepth = 24;
    c->VS20Caps.NumTemps = 32;
    c->VS20Caps.StaticFlowControlDepth = 4;
    c->PS20Caps.Caps = D3DPS20CAPS_ARBITRARYSWIZZLE | D3DPS20CAPS_GRADIENTINSTRUCTIONS | D3DPS20CAPS_PREDICATION |
                       D3DPS20CAPS_NODEPENDENTREADLIMIT | D3DPS20CAPS_NOTEXINSTRUCTIONLIMIT;
    c->PS20Caps.DynamicFlowControlDepth = 24;
    c->PS20Caps.NumTemps = 32;
    c->PS20Caps.StaticFlowControlDepth = 4;
    c->PS20Caps.NumInstructionSlots = 512;
    c->VertexTextureFilterCaps = D3DPTFILTERCAPS_MINFPOINT | D3DPTFILTERCAPS_MINFLINEAR |
                                 D3DPTFILTERCAPS_MAGFPOINT | D3DPTFILTERCAPS_MAGFLINEAR;
    c->MaxVShaderInstructionsExecuted = 65535;
    c->MaxPShaderInstructionsExecuted = 65535;
    c->MaxVertexShader30InstructionSlots = 32768;
    c->MaxPixelShader30InstructionSlots = 32768;
}

} // namespace d3d9

// --- exports ------------------------------------------------------------------------

using d3d9::GetHost;

extern "C" {

IDirect3D9* WINAPI Direct3DCreate9(UINT sdkVersion)
{
    (void)sdkVersion;
    return new d3d9::Direct3D9(false);
}

HRESULT WINAPI Direct3DCreate9Ex(UINT sdkVersion, IDirect3D9Ex** out)
{
    (void)sdkVersion;
    if (!out) return D3DERR_INVALIDCALL;
    *out = new d3d9::Direct3D9(true);
    return D3D_OK;
}

// PIX markers: accepted and ignored.
int WINAPI D3DPERF_BeginEvent(D3DCOLOR, LPCWSTR) { return 0; }
int WINAPI D3DPERF_EndEvent() { return 0; }
void WINAPI D3DPERF_SetMarker(D3DCOLOR, LPCWSTR) {}
void WINAPI D3DPERF_SetRegion(D3DCOLOR, LPCWSTR) {}
BOOL WINAPI D3DPERF_QueryRepeatFrame() { return FALSE; }
void WINAPI D3DPERF_SetOptions(DWORD) {}
DWORD WINAPI D3DPERF_GetStatus() { return 0; }
void WINAPI DebugSetMute() {}
int WINAPI DebugSetLevel() { return 0; }
void* WINAPI Direct3DShaderValidatorCreate9() { return nullptr; }

// --- host hooks: how the Nativra loader plugs d3d9 into its own graphics ---

// Render on the app's D3D11 device (the one FrameMirror copies from).
void WINAPI NativraD3D9SetDevice(ID3D11Device* device)
{
    GetHost().device = device;
}

// Receive each finished back buffer on Present.
void WINAPI NativraD3D9SetPresent(d3d9::PresentFn present, void* context)
{
    GetHost().present = present;
    GetHost().presentContext = context;
}

// Hand out lock memory from the caller's allocator (a 32-bit guest's space).
void WINAPI NativraD3D9SetAllocator(d3d9::AllocFn alloc, d3d9::FreeFn free)
{
    GetHost().alloc = alloc;
    GetHost().free = free;
}

void WINAPI NativraD3D9SetLog(d3d9::LogFn log)
{
    GetHost().log = log;
}

void WINAPI NativraD3D9SetDefaultSize(UINT width, UINT height)
{
    if (width && height) {
        GetHost().width = width;
        GetHost().height = height;
    }
}

// Tests: draw with WARP instead of the hardware.
void WINAPI NativraD3D9UseWarp(BOOL warp)
{
    GetHost().warp = warp != FALSE;
}

} // extern "C"
