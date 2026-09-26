// IDirect3DDevice9Ex on D3D11: object creation, state, and the parts of the
// API that do not draw. Drawing, clears and copies live in d3d9_draw.cpp.

#include "d3d9_objects.h"

#include <d3dcompiler.h>

#include <algorithm>

namespace d3d9 {

#define LOCK_DEVICE std::lock_guard<std::recursive_mutex> guard(mutex)

namespace {

DWORD AsDword(float f) { DWORD v; std::memcpy(&v, &f, sizeof v); return v; }

D3DMATRIX Identity()
{
    D3DMATRIX m = {};
    m._11 = m._22 = m._33 = m._44 = 1.0f;
    return m;
}

} // namespace

// --- lifetime -----------------------------------------------------------------

Device::Device(Direct3D9* parent, UINT adapter, D3DDEVTYPE type, HWND focus, DWORD flags, bool ex)
    : RefCounted(true), parent(parent), adapter(adapter), deviceType(type), focusWindow(focus),
      behaviorFlags(flags), ex(ex)
{
    parent->AddRef();
    for (auto& m : state.transforms) m = Identity();
}

Device::~Device()
{
    // Unbind first: the bindings hold private references into objects that
    // may point back here.
    for (auto& r : renderTargets) r.Set(nullptr);
    depthStencil.Set(nullptr);
    for (auto& t : texRefs) t.Set(nullptr);
    for (auto& s : streamRefs) s.Set(nullptr);
    indexRef.Set(nullptr);
    declRef.Set(nullptr);
    vsRef.Set(nullptr);
    psRef.Set(nullptr);
    for (auto& d : fvfDecls) d.second->PrivateRelease();
    if (autoDepth) autoDepth->PrivateRelease();
    if (swapChain) swapChain->PrivateRelease();

    for (auto* b : cbVs) SafeRelease(b);
    for (auto* b : cbPs) SafeRelease(b);
    SafeRelease(upVertices);
    SafeRelease(upIndices);
    SafeRelease(zeroBuffer);
    for (auto& s : blendStates) s.second->Release();
    for (auto& s : depthStates) s.second->Release();
    for (auto& s : rasterStates) s.second->Release();
    for (auto& s : samplerStates) s.second->Release();
    for (auto& s : inputLayouts) s.second->Release();
    for (auto& c : compiled) if (c.second) c.second->Release();
    for (auto& v : fixedVs) { SafeRelease(v.second.variant.shader); SafeRelease(v.second.variant.bytecode); }
    for (auto& v : fixedPs) { SafeRelease(v.second.shader); SafeRelease(v.second.bytecode); }
    SafeRelease(cbFixed);
    SafeRelease(blitVs);
    SafeRelease(blitPs);
    SafeRelease(blitSampler[0]);
    SafeRelease(blitSampler[1]);
    SafeRelease(blitConstants);
    SafeRelease(blitRaster);
    SafeRelease(blitBlend);
    SafeRelease(blitDepth);
    SafeRelease(ctx1);
    if (ctx) ctx->ClearState();
    SafeRelease(ctx);
    SafeRelease(dev);
    parent->Release();
}

HRESULT Device::Init(D3DPRESENT_PARAMETERS* params)
{
    if (!params) return D3DERR_INVALIDCALL;
    Host& host = GetHost();
    if (host.device) {
        dev = host.device;
        dev->AddRef();
    } else {
        const D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
        HRESULT hr = E_FAIL;
        if (!host.warp) {
            hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                                   levels, 2, D3D11_SDK_VERSION, &dev, nullptr, nullptr);
        }
        if (FAILED(hr)) {
            hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                                   levels, 2, D3D11_SDK_VERSION, &dev, nullptr, nullptr);
        }
        if (FAILED(hr)) {
            Log("no D3D11 device: 0x%08lX", static_cast<unsigned long>(hr));
            return D3DERR_NOTAVAILABLE;
        }
    }
    dev->GetImmediateContext(&ctx);
    ctx->QueryInterface(__uuidof(ID3D11DeviceContext1), reinterpret_cast<void**>(&ctx1));

    // Constant buffers: float (256 x float4), int (16 x int4), bool (16 x int4), fixup.
    const UINT sizes[4] = { 4096, 256, 256, 64 };
    for (int k = 0; k < 4; k++) {
        D3D11_BUFFER_DESC desc = {};
        desc.ByteWidth = sizes[k];
        desc.Usage = D3D11_USAGE_DYNAMIC;
        desc.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        desc.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
        if (FAILED(dev->CreateBuffer(&desc, nullptr, &cbVs[k])) || FAILED(dev->CreateBuffer(&desc, nullptr, &cbPs[k])))
            return D3DERR_OUTOFVIDEOMEMORY;
    }
    {
        const float zeros[4] = {};
        D3D11_BUFFER_DESC desc = {};
        desc.ByteWidth = sizeof zeros;
        desc.Usage = D3D11_USAGE_IMMUTABLE;
        desc.BindFlags = D3D11_BIND_VERTEX_BUFFER;
        D3D11_SUBRESOURCE_DATA data = { zeros, 0, 0 };
        if (FAILED(dev->CreateBuffer(&desc, &data, &zeroBuffer))) return D3DERR_OUTOFVIDEOMEMORY;
    }
    if (!InitBlitter()) return D3DERR_NOTAVAILABLE;

    const HRESULT hr = ResetState(params);
    if (FAILED(hr)) return hr;
    return D3D_OK;
}

HRESULT Device::ResetState(D3DPRESENT_PARAMETERS* params)
{
    // Drop everything bound, then rebuild the implicit swap chain and depth buffer.
    for (auto& r : renderTargets) r.Set(nullptr);
    depthStencil.Set(nullptr);
    for (UINT s = 0; s < kSamplers; s++) { texRefs[s].Set(nullptr); state.textures[s] = nullptr; }
    for (UINT s = 0; s < 16; s++) { streamRefs[s].Set(nullptr); state.streams[s] = Stream(); }
    indexRef.Set(nullptr); state.indices = nullptr;
    declRef.Set(nullptr); state.decl = nullptr; state.fvf = 0;
    vsRef.Set(nullptr); state.vs = nullptr;
    psRef.Set(nullptr); state.ps = nullptr;
    if (autoDepth) { autoDepth->PrivateRelease(); autoDepth = nullptr; }
    if (swapChain) { swapChain->PrivateRelease(); swapChain = nullptr; }
    if (ctx) ctx->ClearState();

    swapChain = new SwapChain(this, *params);
    swapChain->PrivateAddRef();
    HRESULT hr = swapChain->Init();
    if (FAILED(hr)) return hr;
    *params = swapChain->params;   // D3D9 reports the defaults it filled in

    if (params->EnableAutoDepthStencil) {
        Image* image = new Image(this, params->BackBufferWidth, params->BackBufferHeight, 1, 1,
                                 params->AutoDepthStencilFormat, D3DUSAGE_DEPTHSTENCIL, D3DPOOL_DEFAULT, false);
        hr = image->Init();
        if (FAILED(hr)) { delete image; return hr; }
        autoDepth = new Surface(this, image, 0, nullptr, nullptr, true, false);
        autoDepth->PrivateAddRef();
    }

    renderTargets[0].Set(swapChain->backBuffer);
    depthStencil.Set(autoDepth);
    SetDefaultStates();
    ResetViewport();
    vsConstDirty = psConstDirty = true;
    return D3D_OK;
}

void Device::ResetViewport()
{
    Surface* rt = renderTargets[0];
    if (!rt) return;
    const Subresource& s = rt->image->subs[rt->sub];
    state.viewport = { 0, 0, s.width, s.height, 0.0f, 1.0f };
    state.scissor = { 0, 0, static_cast<LONG>(s.width), static_cast<LONG>(s.height) };
}

void Device::SetDefaultStates()
{
    DWORD* rs = state.rs;
    std::memset(state.rs, 0, sizeof state.rs);
    rs[D3DRS_ZENABLE] = autoDepth ? D3DZB_TRUE : D3DZB_FALSE;
    rs[D3DRS_FILLMODE] = D3DFILL_SOLID;
    rs[D3DRS_SHADEMODE] = D3DSHADE_GOURAUD;
    rs[D3DRS_ZWRITEENABLE] = TRUE;
    rs[D3DRS_ALPHATESTENABLE] = FALSE;
    rs[D3DRS_LASTPIXEL] = TRUE;
    rs[D3DRS_SRCBLEND] = D3DBLEND_ONE;
    rs[D3DRS_DESTBLEND] = D3DBLEND_ZERO;
    rs[D3DRS_CULLMODE] = D3DCULL_CCW;
    rs[D3DRS_ZFUNC] = D3DCMP_LESSEQUAL;
    rs[D3DRS_ALPHAREF] = 0;
    rs[D3DRS_ALPHAFUNC] = D3DCMP_ALWAYS;
    rs[D3DRS_FOGEND] = AsDword(1.0f);
    rs[D3DRS_FOGDENSITY] = AsDword(1.0f);
    rs[D3DRS_STENCILFAIL] = D3DSTENCILOP_KEEP;
    rs[D3DRS_STENCILZFAIL] = D3DSTENCILOP_KEEP;
    rs[D3DRS_STENCILPASS] = D3DSTENCILOP_KEEP;
    rs[D3DRS_STENCILFUNC] = D3DCMP_ALWAYS;
    rs[D3DRS_STENCILMASK] = 0xFFFFFFFF;
    rs[D3DRS_STENCILWRITEMASK] = 0xFFFFFFFF;
    rs[D3DRS_TEXTUREFACTOR] = 0xFFFFFFFF;
    rs[D3DRS_CLIPPING] = TRUE;
    rs[D3DRS_LIGHTING] = TRUE;
    rs[D3DRS_COLORVERTEX] = TRUE;
    rs[D3DRS_LOCALVIEWER] = TRUE;
    rs[D3DRS_DIFFUSEMATERIALSOURCE] = D3DMCS_COLOR1;
    rs[D3DRS_SPECULARMATERIALSOURCE] = D3DMCS_COLOR2;
    rs[D3DRS_AMBIENTMATERIALSOURCE] = D3DMCS_MATERIAL;
    rs[D3DRS_EMISSIVEMATERIALSOURCE] = D3DMCS_MATERIAL;
    rs[D3DRS_VERTEXBLEND] = D3DVBF_DISABLE;
    rs[D3DRS_POINTSIZE] = AsDword(1.0f);
    rs[D3DRS_POINTSIZE_MIN] = AsDword(1.0f);
    rs[D3DRS_POINTSCALE_A] = AsDword(1.0f);
    rs[D3DRS_MULTISAMPLEANTIALIAS] = TRUE;
    rs[D3DRS_MULTISAMPLEMASK] = 0xFFFFFFFF;
    rs[D3DRS_PATCHEDGESTYLE] = D3DPATCHEDGE_DISCRETE;
    rs[D3DRS_DEBUGMONITORTOKEN] = D3DDMT_ENABLE;
    rs[D3DRS_POINTSIZE_MAX] = AsDword(64.0f);
    rs[D3DRS_COLORWRITEENABLE] = 0xF;
    rs[D3DRS_BLENDOP] = D3DBLENDOP_ADD;
    rs[D3DRS_POSITIONDEGREE] = D3DDEGREE_CUBIC;
    rs[D3DRS_NORMALDEGREE] = D3DDEGREE_LINEAR;
    rs[D3DRS_MINTESSELLATIONLEVEL] = AsDword(1.0f);
    rs[D3DRS_MAXTESSELLATIONLEVEL] = AsDword(1.0f);
    rs[D3DRS_ADAPTIVETESS_W] = AsDword(1.0f);
    rs[D3DRS_CCW_STENCILFAIL] = D3DSTENCILOP_KEEP;
    rs[D3DRS_CCW_STENCILZFAIL] = D3DSTENCILOP_KEEP;
    rs[D3DRS_CCW_STENCILPASS] = D3DSTENCILOP_KEEP;
    rs[D3DRS_CCW_STENCILFUNC] = D3DCMP_ALWAYS;
    rs[D3DRS_COLORWRITEENABLE1] = 0xF;
    rs[D3DRS_COLORWRITEENABLE2] = 0xF;
    rs[D3DRS_COLORWRITEENABLE3] = 0xF;
    rs[D3DRS_BLENDFACTOR] = 0xFFFFFFFF;
    rs[D3DRS_SRCBLENDALPHA] = D3DBLEND_ONE;
    rs[D3DRS_DESTBLENDALPHA] = D3DBLEND_ZERO;
    rs[D3DRS_BLENDOPALPHA] = D3DBLENDOP_ADD;

    std::memset(state.tss, 0, sizeof state.tss);
    for (DWORD s = 0; s < 8; s++) {
        DWORD* t = state.tss[s];
        t[D3DTSS_COLOROP] = s == 0 ? D3DTOP_MODULATE : D3DTOP_DISABLE;
        t[D3DTSS_COLORARG1] = D3DTA_TEXTURE;
        t[D3DTSS_COLORARG2] = D3DTA_CURRENT;
        t[D3DTSS_ALPHAOP] = s == 0 ? D3DTOP_SELECTARG1 : D3DTOP_DISABLE;
        t[D3DTSS_ALPHAARG1] = D3DTA_TEXTURE;
        t[D3DTSS_ALPHAARG2] = D3DTA_CURRENT;
        t[D3DTSS_TEXCOORDINDEX] = s;
        t[D3DTSS_TEXTURETRANSFORMFLAGS] = D3DTTFF_DISABLE;
        t[D3DTSS_COLORARG0] = D3DTA_CURRENT;
        t[D3DTSS_ALPHAARG0] = D3DTA_CURRENT;
        t[D3DTSS_RESULTARG] = D3DTA_CURRENT;
    }

    std::memset(state.ss, 0, sizeof state.ss);
    for (UINT s = 0; s < kSamplers; s++) {
        DWORD* v = state.ss[s];
        v[D3DSAMP_ADDRESSU] = v[D3DSAMP_ADDRESSV] = v[D3DSAMP_ADDRESSW] = D3DTADDRESS_WRAP;
        v[D3DSAMP_MAGFILTER] = D3DTEXF_POINT;
        v[D3DSAMP_MINFILTER] = D3DTEXF_POINT;
        v[D3DSAMP_MIPFILTER] = D3DTEXF_NONE;
        v[D3DSAMP_MAXANISOTROPY] = 1;
    }

    for (auto& m : state.transforms) m = Identity();
    std::memset(&state.material, 0, sizeof state.material);
    state.lights.clear();
    std::memset(state.clipPlanes, 0, sizeof state.clipPlanes);
    std::memset(state.vsF, 0, sizeof state.vsF);
    std::memset(state.vsI, 0, sizeof state.vsI);
    std::memset(state.vsB, 0, sizeof state.vsB);
    std::memset(state.psF, 0, sizeof state.psF);
    std::memset(state.psI, 0, sizeof state.psI);
    std::memset(state.psB, 0, sizeof state.psB);
    stencilRef = 0;
}

HRESULT Device::QueryInterface(REFIID riid, void** out)
{
    if (!out) return E_POINTER;
    *out = nullptr;
    if (riid == __uuidof(IUnknown) || riid == __uuidof(IDirect3DDevice9) ||
        (ex && riid == __uuidof(IDirect3DDevice9Ex))) {
        AddRef();
        *out = static_cast<IDirect3DDevice9Ex*>(this);
        return S_OK;
    }
    return E_NOINTERFACE;
}

ID3DBlob* Device::CompileHlsl(const std::string& hlsl, const char* profile)
{
    auto it = compiled.find(hlsl);
    if (it != compiled.end()) {
        if (it->second) it->second->AddRef();
        return it->second;
    }
    ID3DBlob* code = nullptr;
    ID3DBlob* errors = nullptr;
    const HRESULT hr = D3DCompile(hlsl.data(), hlsl.size(), "d3d9", nullptr, nullptr, "main", profile,
                                  D3DCOMPILE_OPTIMIZATION_LEVEL1, 0, &code, &errors);
    if (FAILED(hr)) {
        Log("shader compile failed: %.300s", errors ? static_cast<const char*>(errors->GetBufferPointer()) : "?");
        SafeRelease(code);
    }
    SafeRelease(errors);
    compiled[hlsl] = code;
    if (code) code->AddRef();
    return code;
}

// --- simple queries -------------------------------------------------------------

HRESULT Device::TestCooperativeLevel() { return D3D_OK; }
UINT Device::GetAvailableTextureMem() { return 0x7FF00000u; }
HRESULT Device::EvictManagedResources() { return D3D_OK; }

HRESULT Device::GetDirect3D(IDirect3D9** out)
{
    if (!out) return D3DERR_INVALIDCALL;
    parent->AddRef();
    *out = parent;
    return D3D_OK;
}

HRESULT Device::GetDeviceCaps(D3DCAPS9* caps)
{
    if (!caps) return D3DERR_INVALIDCALL;
    Direct3D9::FillCaps(caps);
    return D3D_OK;
}

HRESULT Device::GetDisplayMode(UINT swapchain, D3DDISPLAYMODE* mode)
{
    if (swapchain != 0) return D3DERR_INVALIDCALL;
    return swapChain->GetDisplayMode(mode);
}

HRESULT Device::GetCreationParameters(D3DDEVICE_CREATION_PARAMETERS* params)
{
    if (!params) return D3DERR_INVALIDCALL;
    params->AdapterOrdinal = adapter;
    params->DeviceType = deviceType;
    params->hFocusWindow = focusWindow;
    params->BehaviorFlags = behaviorFlags;
    return D3D_OK;
}

HRESULT Device::SetCursorProperties(UINT, UINT, IDirect3DSurface9*) { return D3D_OK; }
void Device::SetCursorPosition(int, int, DWORD) {}
BOOL Device::ShowCursor(BOOL) { return FALSE; }

HRESULT Device::CreateAdditionalSwapChain(D3DPRESENT_PARAMETERS* params, IDirect3DSwapChain9** out)
{
    LOCK_DEVICE;
    if (!params || !out) return D3DERR_INVALIDCALL;
    auto* chain = new SwapChain(this, *params);
    const HRESULT hr = chain->Init();
    if (FAILED(hr)) { delete chain; return hr; }
    chain->AddRef();   // the game's reference (0 -> 1 also holds the device)
    *out = chain;
    return D3D_OK;
}

HRESULT Device::GetSwapChain(UINT index, IDirect3DSwapChain9** out)
{
    if (!out || index != 0) return D3DERR_INVALIDCALL;
    swapChain->AddRef();
    *out = swapChain;
    return D3D_OK;
}

UINT Device::GetNumberOfSwapChains() { return 1; }

HRESULT Device::Reset(D3DPRESENT_PARAMETERS* params)
{
    LOCK_DEVICE;
    if (!params) return D3DERR_INVALIDCALL;
    if (recording) { recording->PublicRelease(); recording = nullptr; }
    return ResetState(params);
}

HRESULT Device::ResetEx(D3DPRESENT_PARAMETERS* params, D3DDISPLAYMODEEX*) { return Reset(params); }

HRESULT Device::Present(const RECT*, const RECT*, HWND, const RGNDATA*)
{
    LOCK_DEVICE;
    PresentBackBuffer(swapChain);
    return D3D_OK;
}

HRESULT Device::PresentEx(const RECT*, const RECT*, HWND, const RGNDATA*, DWORD)
{
    LOCK_DEVICE;
    PresentBackBuffer(swapChain);
    return D3D_OK;
}

void Device::PresentBackBuffer(SwapChain* chain)
{
    Host& host = GetHost();
    chain->presents++;
    Image* image = chain->backBuffer->image;
    if (host.present && image->texture)
        host.present(host.presentContext, image->texture, image->width, image->height);
    ctx->Flush();
}

HRESULT Device::GetBackBuffer(UINT swapchain, UINT index, D3DBACKBUFFER_TYPE type, IDirect3DSurface9** out)
{
    if (swapchain != 0) return D3DERR_INVALIDCALL;
    return swapChain->GetBackBuffer(index, type, out);
}

HRESULT Device::GetRasterStatus(UINT, D3DRASTER_STATUS* status) { return swapChain->GetRasterStatus(status); }
HRESULT Device::SetDialogBoxMode(BOOL) { return D3D_OK; }
void Device::SetGammaRamp(UINT, DWORD, const D3DGAMMARAMP* ramp) { if (ramp) gamma = *ramp; }
void Device::GetGammaRamp(UINT, D3DGAMMARAMP* ramp) { if (ramp) *ramp = gamma; }

// --- resource creation ------------------------------------------------------------

HRESULT Device::CreateTexture(UINT width, UINT height, UINT levels, DWORD usage, D3DFORMAT format, D3DPOOL pool,
                              IDirect3DTexture9** out, HANDLE*)
{
    LOCK_DEVICE;
    if (!out) return D3DERR_INVALIDCALL;
    *out = nullptr;
    const bool lockable = !(usage & (D3DUSAGE_RENDERTARGET | D3DUSAGE_DEPTHSTENCIL)) || pool != D3DPOOL_DEFAULT;
    auto* texture = new Texture(this, std::unique_ptr<Image>(new Image(this, width, height, levels, 1, format, usage, pool, lockable)));
    const HRESULT hr = texture->Init();
    if (FAILED(hr)) { texture->Release(); return hr; }
    *out = texture;
    return D3D_OK;
}

HRESULT Device::CreateVolumeTexture(UINT, UINT, UINT, UINT, DWORD, D3DFORMAT, D3DPOOL, IDirect3DVolumeTexture9** out, HANDLE*)
{
    if (out) *out = nullptr;
    Log("CreateVolumeTexture is not implemented");
    return D3DERR_NOTAVAILABLE;
}

HRESULT Device::CreateCubeTexture(UINT edge, UINT levels, DWORD usage, D3DFORMAT format, D3DPOOL pool,
                                  IDirect3DCubeTexture9** out, HANDLE*)
{
    LOCK_DEVICE;
    if (!out) return D3DERR_INVALIDCALL;
    *out = nullptr;
    auto* texture = new CubeTexture(this, std::unique_ptr<Image>(new Image(this, edge, edge, levels, 6, format, usage, pool, true)));
    const HRESULT hr = texture->Init();
    if (FAILED(hr)) { texture->Release(); return hr; }
    *out = texture;
    return D3D_OK;
}

HRESULT Device::CreateVertexBuffer(UINT size, DWORD usage, DWORD fvf, D3DPOOL pool, IDirect3DVertexBuffer9** out, HANDLE*)
{
    LOCK_DEVICE;
    if (!out) return D3DERR_INVALIDCALL;
    *out = nullptr;
    auto* buffer = new VertexBuffer(this, size, usage, fvf, pool);
    const HRESULT hr = buffer->data_.Init();
    if (FAILED(hr)) { buffer->Release(); return hr; }
    *out = buffer;
    return D3D_OK;
}

HRESULT Device::CreateIndexBuffer(UINT size, DWORD usage, D3DFORMAT format, D3DPOOL pool, IDirect3DIndexBuffer9** out, HANDLE*)
{
    LOCK_DEVICE;
    if (!out || (format != D3DFMT_INDEX16 && format != D3DFMT_INDEX32)) return D3DERR_INVALIDCALL;
    *out = nullptr;
    auto* buffer = new IndexBuffer(this, size, usage, format, pool);
    const HRESULT hr = buffer->data_.Init();
    if (FAILED(hr)) { buffer->Release(); return hr; }
    *out = buffer;
    return D3D_OK;
}

HRESULT Device::CreateSurface(UINT width, UINT height, D3DFORMAT format, DWORD usage, D3DPOOL pool, bool lockable,
                              D3DMULTISAMPLE_TYPE ms, IDirect3DSurface9** out)
{
    if (!out) return D3DERR_INVALIDCALL;
    *out = nullptr;
    Image* image = new Image(this, width, height, 1, 1, format, usage, pool, lockable);
    const HRESULT hr = image->Init();
    if (FAILED(hr)) { delete image; return hr; }
    auto* surface = new Surface(this, image, 0, nullptr, nullptr, true, true);
    surface->multisample = ms;
    *out = surface;
    return D3D_OK;
}

HRESULT Device::CreateRenderTarget(UINT width, UINT height, D3DFORMAT format, D3DMULTISAMPLE_TYPE ms, DWORD,
                                   BOOL lockable, IDirect3DSurface9** out, HANDLE*)
{
    LOCK_DEVICE;
    return CreateSurface(width, height, format, D3DUSAGE_RENDERTARGET, D3DPOOL_DEFAULT, lockable != FALSE, ms, out);
}

HRESULT Device::CreateRenderTargetEx(UINT width, UINT height, D3DFORMAT format, D3DMULTISAMPLE_TYPE ms, DWORD quality,
                                     BOOL lockable, IDirect3DSurface9** out, HANDLE* shared, DWORD)
{
    return CreateRenderTarget(width, height, format, ms, quality, lockable, out, shared);
}

HRESULT Device::CreateDepthStencilSurface(UINT width, UINT height, D3DFORMAT format, D3DMULTISAMPLE_TYPE ms, DWORD,
                                          BOOL, IDirect3DSurface9** out, HANDLE*)
{
    LOCK_DEVICE;
    return CreateSurface(width, height, format, D3DUSAGE_DEPTHSTENCIL, D3DPOOL_DEFAULT, false, ms, out);
}

HRESULT Device::CreateDepthStencilSurfaceEx(UINT width, UINT height, D3DFORMAT format, D3DMULTISAMPLE_TYPE ms,
                                            DWORD quality, BOOL discard, IDirect3DSurface9** out, HANDLE* shared, DWORD)
{
    return CreateDepthStencilSurface(width, height, format, ms, quality, discard, out, shared);
}

HRESULT Device::CreateOffscreenPlainSurface(UINT width, UINT height, D3DFORMAT format, D3DPOOL pool,
                                            IDirect3DSurface9** out, HANDLE*)
{
    LOCK_DEVICE;
    return CreateSurface(width, height, format, 0, pool, true, D3DMULTISAMPLE_NONE, out);
}

HRESULT Device::CreateOffscreenPlainSurfaceEx(UINT width, UINT height, D3DFORMAT format, D3DPOOL pool,
                                              IDirect3DSurface9** out, HANDLE* shared, DWORD)
{
    return CreateOffscreenPlainSurface(width, height, format, pool, out, shared);
}

HRESULT Device::CreateVertexDeclaration(const D3DVERTEXELEMENT9* elements, IDirect3DVertexDeclaration9** out)
{
    LOCK_DEVICE;
    if (!elements || !out) return D3DERR_INVALIDCALL;
    *out = new VertexDeclaration(this, elements, 0);
    return D3D_OK;
}

HRESULT Device::CreateVertexShader(const DWORD* tokens, IDirect3DVertexShader9** out)
{
    LOCK_DEVICE;
    if (!tokens || !out) return D3DERR_INVALIDCALL;
    if ((tokens[0] >> 16) != 0xFFFE) return D3DERR_INVALIDCALL;
    *out = new VertexShader(this, tokens);
    return D3D_OK;
}

HRESULT Device::CreatePixelShader(const DWORD* tokens, IDirect3DPixelShader9** out)
{
    LOCK_DEVICE;
    if (!tokens || !out) return D3DERR_INVALIDCALL;
    if ((tokens[0] >> 16) != 0xFFFF) return D3DERR_INVALIDCALL;
    *out = new PixelShader(this, tokens);
    return D3D_OK;
}

HRESULT Device::CreateQuery(D3DQUERYTYPE type, IDirect3DQuery9** out)
{
    LOCK_DEVICE;
    if (!Query::Supported(type)) return D3DERR_NOTAVAILABLE;
    if (!out) return D3D_OK;
    auto* query = new Query(this, type);
    const HRESULT hr = query->Init();
    if (FAILED(hr)) { query->Release(); *out = nullptr; return hr; }
    *out = query;
    return D3D_OK;
}

// --- render targets -------------------------------------------------------------

HRESULT Device::SetRenderTarget(DWORD index, IDirect3DSurface9* surface)
{
    LOCK_DEVICE;
    if (index >= 4 || (index == 0 && !surface)) return D3DERR_INVALIDCALL;
    auto* s = static_cast<Surface*>(surface);
    if (s && !(s->image->usage & D3DUSAGE_RENDERTARGET)) return D3DERR_INVALIDCALL;
    renderTargets[index].Set(s);
    if (index == 0) ResetViewport();   // D3D9 resets viewport and scissor with RT0
    return D3D_OK;
}

HRESULT Device::GetRenderTarget(DWORD index, IDirect3DSurface9** out)
{
    LOCK_DEVICE;
    if (index >= 4 || !out) return D3DERR_INVALIDCALL;
    *out = renderTargets[index];
    if (!*out) return D3DERR_NOTFOUND;
    (*out)->AddRef();
    return D3D_OK;
}

HRESULT Device::SetDepthStencilSurface(IDirect3DSurface9* surface)
{
    LOCK_DEVICE;
    depthStencil.Set(static_cast<Surface*>(surface));
    return D3D_OK;
}

HRESULT Device::GetDepthStencilSurface(IDirect3DSurface9** out)
{
    LOCK_DEVICE;
    if (!out) return D3DERR_INVALIDCALL;
    *out = depthStencil;
    if (!*out) return D3DERR_NOTFOUND;
    (*out)->AddRef();
    return D3D_OK;
}

HRESULT Device::BeginScene()
{
    if (inScene) return D3DERR_INVALIDCALL;
    inScene = true;
    return D3D_OK;
}

HRESULT Device::EndScene()
{
    if (!inScene) return D3DERR_INVALIDCALL;
    inScene = false;
    return D3D_OK;
}

// --- state setters (recorded into a state block while one is open) ----------------

HRESULT Device::SetTransform(D3DTRANSFORMSTATETYPE type, const D3DMATRIX* matrix)
{
    LOCK_DEVICE;
    const UINT index = static_cast<UINT>(type);
    if (!matrix || index >= kTransforms) return D3DERR_INVALIDCALL;
    if (recording) {
        recording->state.transforms[index] = *matrix;
        recording->mask.transforms.set(index);
        return D3D_OK;
    }
    state.transforms[index] = *matrix;
    return D3D_OK;
}

HRESULT Device::GetTransform(D3DTRANSFORMSTATETYPE type, D3DMATRIX* matrix)
{
    LOCK_DEVICE;
    const UINT index = static_cast<UINT>(type);
    if (!matrix || index >= kTransforms) return D3DERR_INVALIDCALL;
    *matrix = state.transforms[index];
    return D3D_OK;
}

HRESULT Device::MultiplyTransform(D3DTRANSFORMSTATETYPE type, const D3DMATRIX* matrix)
{
    LOCK_DEVICE;
    const UINT index = static_cast<UINT>(type);
    if (!matrix || index >= kTransforms) return D3DERR_INVALIDCALL;
    const D3DMATRIX& a = state.transforms[index];
    D3DMATRIX r;
    for (int i = 0; i < 4; i++)
        for (int j = 0; j < 4; j++)
            r.m[i][j] = a.m[i][0] * matrix->m[0][j] + a.m[i][1] * matrix->m[1][j] +
                        a.m[i][2] * matrix->m[2][j] + a.m[i][3] * matrix->m[3][j];
    return SetTransform(type, &r);
}

HRESULT Device::SetViewport(const D3DVIEWPORT9* viewport)
{
    LOCK_DEVICE;
    if (!viewport) return D3DERR_INVALIDCALL;
    if (recording) { recording->state.viewport = *viewport; recording->mask.viewport = true; return D3D_OK; }
    state.viewport = *viewport;
    return D3D_OK;
}

HRESULT Device::GetViewport(D3DVIEWPORT9* viewport)
{
    LOCK_DEVICE;
    if (!viewport) return D3DERR_INVALIDCALL;
    *viewport = state.viewport;
    return D3D_OK;
}

HRESULT Device::SetMaterial(const D3DMATERIAL9* material)
{
    LOCK_DEVICE;
    if (!material) return D3DERR_INVALIDCALL;
    if (recording) { recording->state.material = *material; recording->mask.material = true; return D3D_OK; }
    state.material = *material;
    return D3D_OK;
}

HRESULT Device::GetMaterial(D3DMATERIAL9* material)
{
    LOCK_DEVICE;
    if (!material) return D3DERR_INVALIDCALL;
    *material = state.material;
    return D3D_OK;
}

static D3DLIGHT9 DefaultLight()
{
    D3DLIGHT9 l = {};
    l.Type = D3DLIGHT_DIRECTIONAL;
    l.Diffuse.r = l.Diffuse.g = l.Diffuse.b = 1.0f;
    l.Direction.z = 1.0f;
    return l;
}

HRESULT Device::SetLight(DWORD index, const D3DLIGHT9* light)
{
    LOCK_DEVICE;
    if (!light) return D3DERR_INVALIDCALL;
    DeviceState& target = recording ? recording->state : state;
    target.lights[index].light = *light;
    if (recording) recording->mask.lights[index] = true;
    return D3D_OK;
}

HRESULT Device::GetLight(DWORD index, D3DLIGHT9* light)
{
    LOCK_DEVICE;
    auto it = state.lights.find(index);
    if (!light || it == state.lights.end()) return D3DERR_INVALIDCALL;
    *light = it->second.light;
    return D3D_OK;
}

HRESULT Device::LightEnable(DWORD index, BOOL enable)
{
    LOCK_DEVICE;
    DeviceState& target = recording ? recording->state : state;
    auto it = target.lights.find(index);
    if (it == target.lights.end()) {
        // Enabling a light that was never set creates the default light.
        Light l;
        l.light = DefaultLight();
        it = target.lights.emplace(index, l).first;
    }
    it->second.enabled = enable != FALSE;
    if (recording) recording->mask.lights[index] = true;
    return D3D_OK;
}

HRESULT Device::GetLightEnable(DWORD index, BOOL* enable)
{
    LOCK_DEVICE;
    auto it = state.lights.find(index);
    if (!enable || it == state.lights.end()) return D3DERR_INVALIDCALL;
    *enable = it->second.enabled ? 128 : 0;   // D3D9 reports 128 for enabled
    return D3D_OK;
}

HRESULT Device::SetClipPlane(DWORD index, const float* plane)
{
    LOCK_DEVICE;
    if (index >= 6 || !plane) return D3DERR_INVALIDCALL;
    DeviceState& target = recording ? recording->state : state;
    std::memcpy(target.clipPlanes[index], plane, sizeof(float) * 4);
    if (recording) recording->mask.clipPlanes.set(index);
    return D3D_OK;
}

HRESULT Device::GetClipPlane(DWORD index, float* plane)
{
    LOCK_DEVICE;
    if (index >= 6 || !plane) return D3DERR_INVALIDCALL;
    std::memcpy(plane, state.clipPlanes[index], sizeof(float) * 4);
    return D3D_OK;
}

HRESULT Device::SetRenderState(D3DRENDERSTATETYPE type, DWORD value)
{
    LOCK_DEVICE;
    const UINT index = static_cast<UINT>(type);
    if (index >= kMaxRenderState) return D3DERR_INVALIDCALL;
    if (recording) {
        recording->state.rs[index] = value;
        recording->mask.rs.set(index);
        return D3D_OK;
    }
    state.rs[index] = value;
    return D3D_OK;
}

HRESULT Device::GetRenderState(D3DRENDERSTATETYPE type, DWORD* value)
{
    LOCK_DEVICE;
    const UINT index = static_cast<UINT>(type);
    if (!value || index >= kMaxRenderState) return D3DERR_INVALIDCALL;
    *value = state.rs[index];
    return D3D_OK;
}

HRESULT Device::SetClipStatus(const D3DCLIPSTATUS9*) { return D3D_OK; }

HRESULT Device::GetClipStatus(D3DCLIPSTATUS9* status)
{
    if (!status) return D3DERR_INVALIDCALL;
    std::memset(status, 0, sizeof *status);
    return D3D_OK;
}

Image* Device::ImageOf(IDirect3DBaseTexture9* texture)
{
    if (!texture) return nullptr;
    auto* binding = dynamic_cast<TextureBinding*>(texture);
    return binding ? binding->GetImage() : nullptr;
}

HRESULT Device::SetTexture(DWORD stage, IDirect3DBaseTexture9* texture)
{
    LOCK_DEVICE;
    const int slot = SamplerSlot(stage);
    if (slot < 0) return D3DERR_INVALIDCALL;
    auto* binding = texture ? dynamic_cast<TextureBinding*>(texture) : nullptr;
    if (texture && !binding) return D3DERR_INVALIDCALL;   // volume textures are not implemented
    if (recording) {
        recording->state.textures[slot] = texture;
        recording->mask.textures.set(slot);
        return D3D_OK;
    }
    texRefs[slot].Set(binding ? binding->Ref() : nullptr);
    state.textures[slot] = texture;
    return D3D_OK;
}

HRESULT Device::GetTexture(DWORD stage, IDirect3DBaseTexture9** texture)
{
    LOCK_DEVICE;
    const int slot = SamplerSlot(stage);
    if (slot < 0 || !texture) return D3DERR_INVALIDCALL;
    *texture = state.textures[slot];
    if (*texture) (*texture)->AddRef();
    return D3D_OK;
}

HRESULT Device::SetTextureStageState(DWORD stage, D3DTEXTURESTAGESTATETYPE type, DWORD value)
{
    LOCK_DEVICE;
    if (stage >= 8 || static_cast<UINT>(type) >= kMaxTss) return D3DERR_INVALIDCALL;
    if (recording) {
        recording->state.tss[stage][type] = value;
        recording->mask.tss.set(stage * kMaxTss + type);
        return D3D_OK;
    }
    state.tss[stage][type] = value;
    return D3D_OK;
}

HRESULT Device::GetTextureStageState(DWORD stage, D3DTEXTURESTAGESTATETYPE type, DWORD* value)
{
    LOCK_DEVICE;
    if (!value || stage >= 8 || static_cast<UINT>(type) >= kMaxTss) return D3DERR_INVALIDCALL;
    *value = state.tss[stage][type];
    return D3D_OK;
}

HRESULT Device::SetSamplerState(DWORD sampler, D3DSAMPLERSTATETYPE type, DWORD value)
{
    LOCK_DEVICE;
    const int slot = SamplerSlot(sampler);
    if (slot < 0 || static_cast<UINT>(type) >= kMaxSs) return D3DERR_INVALIDCALL;
    if (recording) {
        recording->state.ss[slot][type] = value;
        recording->mask.ss.set(slot * kMaxSs + type);
        return D3D_OK;
    }
    state.ss[slot][type] = value;
    return D3D_OK;
}

HRESULT Device::GetSamplerState(DWORD sampler, D3DSAMPLERSTATETYPE type, DWORD* value)
{
    LOCK_DEVICE;
    const int slot = SamplerSlot(sampler);
    if (!value || slot < 0 || static_cast<UINT>(type) >= kMaxSs) return D3DERR_INVALIDCALL;
    *value = state.ss[slot][type];
    return D3D_OK;
}

HRESULT Device::ValidateDevice(DWORD* passes)
{
    if (passes) *passes = 1;
    return D3D_OK;
}

HRESULT Device::SetPaletteEntries(UINT, const PALETTEENTRY*) { return D3D_OK; }
HRESULT Device::GetPaletteEntries(UINT, PALETTEENTRY* entries)
{
    if (entries) std::memset(entries, 0, sizeof(PALETTEENTRY) * 256);
    return D3D_OK;
}
HRESULT Device::SetCurrentTexturePalette(UINT) { return D3D_OK; }
HRESULT Device::GetCurrentTexturePalette(UINT* palette) { if (palette) *palette = 0; return D3D_OK; }

HRESULT Device::SetScissorRect(const RECT* rect)
{
    LOCK_DEVICE;
    if (!rect) return D3DERR_INVALIDCALL;
    if (recording) { recording->state.scissor = *rect; recording->mask.scissor = true; return D3D_OK; }
    state.scissor = *rect;
    return D3D_OK;
}

HRESULT Device::GetScissorRect(RECT* rect)
{
    LOCK_DEVICE;
    if (!rect) return D3DERR_INVALIDCALL;
    *rect = state.scissor;
    return D3D_OK;
}

HRESULT Device::SetSoftwareVertexProcessing(BOOL) { return D3D_OK; }
BOOL Device::GetSoftwareVertexProcessing() { return FALSE; }
HRESULT Device::SetNPatchMode(float) { return D3D_OK; }
float Device::GetNPatchMode() { return 0.0f; }

// --- declarations, shaders, streams, constants ----------------------------------------

HRESULT Device::SetVertexDeclaration(IDirect3DVertexDeclaration9* declaration)
{
    LOCK_DEVICE;
    auto* decl = static_cast<VertexDeclaration*>(declaration);
    if (recording) {
        recording->state.decl = decl;
        recording->state.fvf = decl ? decl->fvf : 0;
        recording->mask.decl = true;
        return D3D_OK;
    }
    declRef.Set(decl);
    state.decl = decl;
    state.fvf = decl ? decl->fvf : 0;
    return D3D_OK;
}

HRESULT Device::GetVertexDeclaration(IDirect3DVertexDeclaration9** out)
{
    LOCK_DEVICE;
    if (!out) return D3DERR_INVALIDCALL;
    *out = state.decl;
    if (*out) (*out)->AddRef();
    return D3D_OK;
}

HRESULT Device::SetFVF(DWORD fvf)
{
    LOCK_DEVICE;
    if (fvf == 0) return D3D_OK;
    return SetVertexDeclaration(FvfDeclaration(fvf));
}

HRESULT Device::GetFVF(DWORD* fvf)
{
    LOCK_DEVICE;
    if (!fvf) return D3DERR_INVALIDCALL;
    *fvf = state.fvf;
    return D3D_OK;
}

// The declaration an FVF code describes, made once per code.
VertexDeclaration* Device::FvfDeclaration(DWORD fvf)
{
    auto it = fvfDecls.find(fvf);
    if (it != fvfDecls.end()) return it->second;

    std::vector<D3DVERTEXELEMENT9> e;
    WORD offset = 0;
    auto add = [&](BYTE type, BYTE usage, BYTE index) {
        e.push_back({ 0, offset, type, D3DDECLMETHOD_DEFAULT, usage, index });
        offset = static_cast<WORD>(offset + DeclTypeSize(type));
    };
    const DWORD position = fvf & D3DFVF_POSITION_MASK;
    switch (position) {
    case D3DFVF_XYZ: add(D3DDECLTYPE_FLOAT3, D3DDECLUSAGE_POSITION, 0); break;
    case D3DFVF_XYZRHW: add(D3DDECLTYPE_FLOAT4, D3DDECLUSAGE_POSITIONT, 0); break;
    case D3DFVF_XYZW: add(D3DDECLTYPE_FLOAT4, D3DDECLUSAGE_POSITION, 0); break;
    case D3DFVF_XYZB1: case D3DFVF_XYZB2: case D3DFVF_XYZB3: case D3DFVF_XYZB4: case D3DFVF_XYZB5: {
        add(D3DDECLTYPE_FLOAT3, D3DDECLUSAGE_POSITION, 0);
        int betas = static_cast<int>((position - D3DFVF_XYZB1) / 2) + 1;
        const bool lastIsIndices = (fvf & (D3DFVF_LASTBETA_UBYTE4 | D3DFVF_LASTBETA_D3DCOLOR)) != 0;
        int weights = lastIsIndices ? betas - 1 : betas;
        if (weights > 0) add(static_cast<BYTE>(D3DDECLTYPE_FLOAT1 + weights - 1), D3DDECLUSAGE_BLENDWEIGHT, 0);
        if (lastIsIndices)
            add((fvf & D3DFVF_LASTBETA_UBYTE4) ? D3DDECLTYPE_UBYTE4 : D3DDECLTYPE_D3DCOLOR, D3DDECLUSAGE_BLENDINDICES, 0);
        break;
    }
    default: break;
    }
    if (fvf & D3DFVF_NORMAL) add(D3DDECLTYPE_FLOAT3, D3DDECLUSAGE_NORMAL, 0);
    if (fvf & D3DFVF_PSIZE) add(D3DDECLTYPE_FLOAT1, D3DDECLUSAGE_PSIZE, 0);
    if (fvf & D3DFVF_DIFFUSE) add(D3DDECLTYPE_D3DCOLOR, D3DDECLUSAGE_COLOR, 0);
    if (fvf & D3DFVF_SPECULAR) add(D3DDECLTYPE_D3DCOLOR, D3DDECLUSAGE_COLOR, 1);
    const DWORD texCount = (fvf & D3DFVF_TEXCOUNT_MASK) >> D3DFVF_TEXCOUNT_SHIFT;
    for (DWORD t = 0; t < texCount && t < 8; t++) {
        static const BYTE types[4] = { D3DDECLTYPE_FLOAT2, D3DDECLTYPE_FLOAT3, D3DDECLTYPE_FLOAT4, D3DDECLTYPE_FLOAT1 };
        add(types[(fvf >> (16 + t * 2)) & 3], D3DDECLUSAGE_TEXCOORD, static_cast<BYTE>(t));
    }
    const D3DVERTEXELEMENT9 end = D3DDECL_END();
    e.push_back(end);

    auto* decl = new VertexDeclaration(this, e.data(), fvf);
    // Owned by the device's cache, not by the game: trade the public
    // reference the constructor made for a private one.
    decl->PrivateAddRef();
    decl->Release();
    fvfDecls[fvf] = decl;
    return decl;
}

HRESULT Device::SetVertexShader(IDirect3DVertexShader9* shader)
{
    LOCK_DEVICE;
    auto* vs = static_cast<VertexShader*>(shader);
    if (recording) { recording->state.vs = vs; recording->mask.vs = true; return D3D_OK; }
    vsRef.Set(vs);
    state.vs = vs;
    return D3D_OK;
}

HRESULT Device::GetVertexShader(IDirect3DVertexShader9** out)
{
    LOCK_DEVICE;
    if (!out) return D3DERR_INVALIDCALL;
    *out = state.vs;
    if (*out) (*out)->AddRef();
    return D3D_OK;
}

HRESULT Device::SetPixelShader(IDirect3DPixelShader9* shader)
{
    LOCK_DEVICE;
    auto* ps = static_cast<PixelShader*>(shader);
    if (recording) { recording->state.ps = ps; recording->mask.ps = true; return D3D_OK; }
    psRef.Set(ps);
    state.ps = ps;
    return D3D_OK;
}

HRESULT Device::GetPixelShader(IDirect3DPixelShader9** out)
{
    LOCK_DEVICE;
    if (!out) return D3DERR_INVALIDCALL;
    *out = state.ps;
    if (*out) (*out)->AddRef();
    return D3D_OK;
}

template <size_t N, typename T, size_t Bits>
static HRESULT SetConstants(T (&dst)[N][4], std::bitset<Bits>* mask, UINT reg, const T* data, UINT count)
{
    if (!data || reg + count > N) return D3DERR_INVALIDCALL;
    std::memcpy(dst[reg], data, sizeof(T) * 4 * count);
    if (mask) for (UINT k = 0; k < count; k++) mask->set(reg + k);
    return D3D_OK;
}

template <size_t N, typename T>
static HRESULT GetConstants(const T (&src)[N][4], UINT reg, T* data, UINT count)
{
    if (!data || reg + count > N) return D3DERR_INVALIDCALL;
    std::memcpy(data, src[reg], sizeof(T) * 4 * count);
    return D3D_OK;
}

HRESULT Device::SetVertexShaderConstantF(UINT reg, const float* data, UINT count)
{
    LOCK_DEVICE;
    if (recording) return SetConstants(recording->state.vsF, &recording->mask.vsF, reg, data, count);
    vsConstDirty = true;
    return SetConstants<256, float, 256>(state.vsF, nullptr, reg, data, count);
}
HRESULT Device::GetVertexShaderConstantF(UINT reg, float* data, UINT count) { LOCK_DEVICE; return GetConstants(state.vsF, reg, data, count); }

HRESULT Device::SetVertexShaderConstantI(UINT reg, const int* data, UINT count)
{
    LOCK_DEVICE;
    if (recording) return SetConstants(recording->state.vsI, &recording->mask.vsI, reg, data, count);
    vsConstDirty = true;
    return SetConstants<16, int, 16>(state.vsI, nullptr, reg, data, count);
}
HRESULT Device::GetVertexShaderConstantI(UINT reg, int* data, UINT count) { LOCK_DEVICE; return GetConstants(state.vsI, reg, data, count); }

HRESULT Device::SetVertexShaderConstantB(UINT reg, const BOOL* data, UINT count)
{
    LOCK_DEVICE;
    if (!data || reg + count > 16) return D3DERR_INVALIDCALL;
    DeviceState& target = recording ? recording->state : state;
    std::memcpy(&target.vsB[reg], data, sizeof(BOOL) * count);
    if (recording) for (UINT k = 0; k < count; k++) recording->mask.vsB.set(reg + k);
    else vsConstDirty = true;
    return D3D_OK;
}
HRESULT Device::GetVertexShaderConstantB(UINT reg, BOOL* data, UINT count)
{
    LOCK_DEVICE;
    if (!data || reg + count > 16) return D3DERR_INVALIDCALL;
    std::memcpy(data, &state.vsB[reg], sizeof(BOOL) * count);
    return D3D_OK;
}

HRESULT Device::SetPixelShaderConstantF(UINT reg, const float* data, UINT count)
{
    LOCK_DEVICE;
    if (recording) return SetConstants(recording->state.psF, &recording->mask.psF, reg, data, count);
    psConstDirty = true;
    return SetConstants<224, float, 224>(state.psF, nullptr, reg, data, count);
}
HRESULT Device::GetPixelShaderConstantF(UINT reg, float* data, UINT count) { LOCK_DEVICE; return GetConstants(state.psF, reg, data, count); }

HRESULT Device::SetPixelShaderConstantI(UINT reg, const int* data, UINT count)
{
    LOCK_DEVICE;
    if (recording) return SetConstants(recording->state.psI, &recording->mask.psI, reg, data, count);
    psConstDirty = true;
    return SetConstants<16, int, 16>(state.psI, nullptr, reg, data, count);
}
HRESULT Device::GetPixelShaderConstantI(UINT reg, int* data, UINT count) { LOCK_DEVICE; return GetConstants(state.psI, reg, data, count); }

HRESULT Device::SetPixelShaderConstantB(UINT reg, const BOOL* data, UINT count)
{
    LOCK_DEVICE;
    if (!data || reg + count > 16) return D3DERR_INVALIDCALL;
    DeviceState& target = recording ? recording->state : state;
    std::memcpy(&target.psB[reg], data, sizeof(BOOL) * count);
    if (recording) for (UINT k = 0; k < count; k++) recording->mask.psB.set(reg + k);
    else psConstDirty = true;
    return D3D_OK;
}
HRESULT Device::GetPixelShaderConstantB(UINT reg, BOOL* data, UINT count)
{
    LOCK_DEVICE;
    if (!data || reg + count > 16) return D3DERR_INVALIDCALL;
    std::memcpy(data, &state.psB[reg], sizeof(BOOL) * count);
    return D3D_OK;
}

HRESULT Device::SetStreamSource(UINT stream, IDirect3DVertexBuffer9* buffer, UINT offset, UINT stride)
{
    LOCK_DEVICE;
    if (stream >= 16) return D3DERR_INVALIDCALL;
    auto* vb = static_cast<VertexBuffer*>(buffer);
    DeviceState& target = recording ? recording->state : state;
    target.streams[stream].buffer = vb;
    target.streams[stream].offset = offset;
    target.streams[stream].stride = stride;
    if (recording) recording->mask.streams.set(stream);
    else streamRefs[stream].Set(vb);
    return D3D_OK;
}

HRESULT Device::GetStreamSource(UINT stream, IDirect3DVertexBuffer9** buffer, UINT* offset, UINT* stride)
{
    LOCK_DEVICE;
    if (stream >= 16 || !buffer || !offset || !stride) return D3DERR_INVALIDCALL;
    *buffer = state.streams[stream].buffer;
    if (*buffer) (*buffer)->AddRef();
    *offset = state.streams[stream].offset;
    *stride = state.streams[stream].stride;
    return D3D_OK;
}

HRESULT Device::SetStreamSourceFreq(UINT stream, UINT frequency)
{
    LOCK_DEVICE;
    if (stream >= 16) return D3DERR_INVALIDCALL;
    DeviceState& target = recording ? recording->state : state;
    target.streams[stream].frequency = frequency;
    if (recording) recording->mask.streamFreq.set(stream);
    return D3D_OK;
}

HRESULT Device::GetStreamSourceFreq(UINT stream, UINT* frequency)
{
    LOCK_DEVICE;
    if (stream >= 16 || !frequency) return D3DERR_INVALIDCALL;
    *frequency = state.streams[stream].frequency;
    return D3D_OK;
}

HRESULT Device::SetIndices(IDirect3DIndexBuffer9* buffer)
{
    LOCK_DEVICE;
    auto* ib = static_cast<IndexBuffer*>(buffer);
    if (recording) { recording->state.indices = ib; recording->mask.indices = true; return D3D_OK; }
    indexRef.Set(ib);
    state.indices = ib;
    return D3D_OK;
}

HRESULT Device::GetIndices(IDirect3DIndexBuffer9** out)
{
    LOCK_DEVICE;
    if (!out) return D3DERR_INVALIDCALL;
    *out = state.indices;
    if (*out) (*out)->AddRef();
    return D3D_OK;
}

// --- state blocks -----------------------------------------------------------------

StateMask Device::MaskFor(D3DSTATEBLOCKTYPE type)
{
    static const D3DRENDERSTATETYPE pixelStates[] = {
        D3DRS_ZENABLE, D3DRS_FILLMODE, D3DRS_SHADEMODE, D3DRS_ZWRITEENABLE, D3DRS_ALPHATESTENABLE,
        D3DRS_LASTPIXEL, D3DRS_SRCBLEND, D3DRS_DESTBLEND, D3DRS_ZFUNC, D3DRS_ALPHAREF, D3DRS_ALPHAFUNC,
        D3DRS_DITHERENABLE, D3DRS_FOGSTART, D3DRS_FOGEND, D3DRS_FOGDENSITY, D3DRS_ALPHABLENDENABLE,
        D3DRS_DEPTHBIAS, D3DRS_STENCILENABLE, D3DRS_STENCILFAIL, D3DRS_STENCILZFAIL, D3DRS_STENCILPASS,
        D3DRS_STENCILFUNC, D3DRS_STENCILREF, D3DRS_STENCILMASK, D3DRS_STENCILWRITEMASK, D3DRS_TEXTUREFACTOR,
        D3DRS_WRAP0, D3DRS_WRAP1, D3DRS_WRAP2, D3DRS_WRAP3, D3DRS_WRAP4, D3DRS_WRAP5, D3DRS_WRAP6, D3DRS_WRAP7,
        D3DRS_WRAP8, D3DRS_WRAP9, D3DRS_WRAP10, D3DRS_WRAP11, D3DRS_WRAP12, D3DRS_WRAP13, D3DRS_WRAP14,
        D3DRS_WRAP15, D3DRS_COLORWRITEENABLE, D3DRS_BLENDOP, D3DRS_SCISSORTESTENABLE, D3DRS_SLOPESCALEDEPTHBIAS,
        D3DRS_ANTIALIASEDLINEENABLE, D3DRS_TWOSIDEDSTENCILMODE, D3DRS_CCW_STENCILFAIL, D3DRS_CCW_STENCILZFAIL,
        D3DRS_CCW_STENCILPASS, D3DRS_CCW_STENCILFUNC, D3DRS_COLORWRITEENABLE1, D3DRS_COLORWRITEENABLE2,
        D3DRS_COLORWRITEENABLE3, D3DRS_BLENDFACTOR, D3DRS_SRGBWRITEENABLE, D3DRS_SEPARATEALPHABLENDENABLE,
        D3DRS_SRCBLENDALPHA, D3DRS_DESTBLENDALPHA, D3DRS_BLENDOPALPHA,
    };
    static const D3DRENDERSTATETYPE vertexStates[] = {
        D3DRS_CULLMODE, D3DRS_FOGENABLE, D3DRS_FOGCOLOR, D3DRS_FOGTABLEMODE, D3DRS_FOGSTART, D3DRS_FOGEND,
        D3DRS_FOGDENSITY, D3DRS_RANGEFOGENABLE, D3DRS_AMBIENT, D3DRS_COLORVERTEX, D3DRS_FOGVERTEXMODE,
        D3DRS_CLIPPING, D3DRS_LIGHTING, D3DRS_LOCALVIEWER, D3DRS_EMISSIVEMATERIALSOURCE,
        D3DRS_AMBIENTMATERIALSOURCE, D3DRS_DIFFUSEMATERIALSOURCE, D3DRS_SPECULARMATERIALSOURCE,
        D3DRS_VERTEXBLEND, D3DRS_CLIPPLANEENABLE, D3DRS_POINTSIZE, D3DRS_POINTSIZE_MIN,
        D3DRS_POINTSPRITEENABLE, D3DRS_POINTSCALEENABLE, D3DRS_POINTSCALE_A, D3DRS_POINTSCALE_B,
        D3DRS_POINTSCALE_C, D3DRS_MULTISAMPLEANTIALIAS, D3DRS_MULTISAMPLEMASK, D3DRS_PATCHEDGESTYLE,
        D3DRS_POINTSIZE_MAX, D3DRS_INDEXEDVERTEXBLENDENABLE, D3DRS_TWEENFACTOR, D3DRS_POSITIONDEGREE,
        D3DRS_NORMALDEGREE, D3DRS_MINTESSELLATIONLEVEL, D3DRS_MAXTESSELLATIONLEVEL, D3DRS_ADAPTIVETESS_X,
        D3DRS_ADAPTIVETESS_Y, D3DRS_ADAPTIVETESS_Z, D3DRS_ADAPTIVETESS_W, D3DRS_ENABLEADAPTIVETESSELLATION,
        D3DRS_NORMALIZENORMALS, D3DRS_SPECULARENABLE, D3DRS_SHADEMODE,
    };

    StateMask m;
    const bool all = type == D3DSBT_ALL;
    const bool pixel = all || type == D3DSBT_PIXELSTATE;
    const bool vertex = all || type == D3DSBT_VERTEXSTATE;
    if (all) {
        m.rs.set();
        m.transforms.set();
        m.viewport = m.scissor = m.material = true;
        m.clipPlanes.set();
        m.streams.set();
        m.indices = true;
        m.textures.set();
    }
    if (pixel) {
        for (auto s : pixelStates) m.rs.set(s);
        m.tss.set();
        for (UINT s = 0; s < kSamplers; s++)
            for (UINT t = 0; t < kMaxSs; t++) m.ss.set(s * kMaxSs + t);
        m.psF.set(); m.psI.set(); m.psB.set();
        m.ps = true;
    }
    if (vertex) {
        for (auto s : vertexStates) m.rs.set(s);
        m.vsF.set(); m.vsI.set(); m.vsB.set();
        m.vs = true;
        m.decl = true;
        m.allLights = true;
        m.streamFreq.set();
    }
    return m;
}

HRESULT Device::CreateStateBlock(D3DSTATEBLOCKTYPE type, IDirect3DStateBlock9** out)
{
    LOCK_DEVICE;
    if (!out || (type != D3DSBT_ALL && type != D3DSBT_PIXELSTATE && type != D3DSBT_VERTEXSTATE))
        return D3DERR_INVALIDCALL;
    auto* block = new StateBlock(this, MaskFor(type));
    block->Capture();
    *out = block;
    return D3D_OK;
}

HRESULT Device::BeginStateBlock()
{
    LOCK_DEVICE;
    if (recording) return D3DERR_INVALIDCALL;
    recording = new StateBlock(this, StateMask());
    return D3D_OK;
}

HRESULT Device::EndStateBlock(IDirect3DStateBlock9** out)
{
    LOCK_DEVICE;
    if (!out || !recording) return D3DERR_INVALIDCALL;
    StateBlock* block = recording;
    recording = nullptr;
    // What was recorded is the block's state; now it holds references to it.
    for (UINT s = 0; s < kSamplers; s++) {
        if (!block->mask.textures[s] || !block->state.textures[s]) continue;
        auto* binding = dynamic_cast<TextureBinding*>(block->state.textures[s]);
        if (binding) binding->Ref()->PrivateAddRef();
    }
    for (UINT s = 0; s < 16; s++)
        if (block->mask.streams[s] && block->state.streams[s].buffer) block->state.streams[s].buffer->PrivateAddRef();
    if (block->mask.indices && block->state.indices) block->state.indices->PrivateAddRef();
    if (block->mask.decl && block->state.decl) block->state.decl->PrivateAddRef();
    if (block->mask.vs && block->state.vs) static_cast<ShaderBase*>(block->state.vs)->PrivateAddRef();
    if (block->mask.ps && block->state.ps) static_cast<ShaderBase*>(block->state.ps)->PrivateAddRef();
    *out = block;
    return D3D_OK;
}

void Device::CaptureInto(DeviceState& dst, const StateMask& m)
{
    for (UINT i = 0; i < kMaxRenderState; i++) if (m.rs[i]) dst.rs[i] = state.rs[i];
    for (UINT s = 0; s < 8; s++)
        for (UINT t = 0; t < kMaxTss; t++) if (m.tss[s * kMaxTss + t]) dst.tss[s][t] = state.tss[s][t];
    for (UINT s = 0; s < kSamplers; s++)
        for (UINT t = 0; t < kMaxSs; t++) if (m.ss[s * kMaxSs + t]) dst.ss[s][t] = state.ss[s][t];
    for (UINT i = 0; i < kTransforms; i++) if (m.transforms[i]) dst.transforms[i] = state.transforms[i];
    if (m.viewport) dst.viewport = state.viewport;
    if (m.scissor) dst.scissor = state.scissor;
    if (m.material) dst.material = state.material;
    if (m.allLights) dst.lights = state.lights;
    for (const auto& l : m.lights) {
        auto it = state.lights.find(l.first);
        if (it != state.lights.end()) dst.lights[l.first] = it->second;
    }
    for (UINT i = 0; i < 6; i++) if (m.clipPlanes[i]) std::memcpy(dst.clipPlanes[i], state.clipPlanes[i], 16);
    for (UINT i = 0; i < 256; i++) if (m.vsF[i]) std::memcpy(dst.vsF[i], state.vsF[i], 16);
    for (UINT i = 0; i < 16; i++) if (m.vsI[i]) std::memcpy(dst.vsI[i], state.vsI[i], 16);
    for (UINT i = 0; i < 16; i++) if (m.vsB[i]) dst.vsB[i] = state.vsB[i];
    for (UINT i = 0; i < 224; i++) if (m.psF[i]) std::memcpy(dst.psF[i], state.psF[i], 16);
    for (UINT i = 0; i < 16; i++) if (m.psI[i]) std::memcpy(dst.psI[i], state.psI[i], 16);
    for (UINT i = 0; i < 16; i++) if (m.psB[i]) dst.psB[i] = state.psB[i];
    for (UINT i = 0; i < 16; i++) {
        if (m.streams[i]) {
            dst.streams[i].buffer = state.streams[i].buffer;
            dst.streams[i].offset = state.streams[i].offset;
            dst.streams[i].stride = state.streams[i].stride;
        }
        if (m.streamFreq[i]) dst.streams[i].frequency = state.streams[i].frequency;
    }
    if (m.indices) dst.indices = state.indices;
    if (m.decl) { dst.decl = state.decl; dst.fvf = state.fvf; }
    if (m.vs) dst.vs = state.vs;
    if (m.ps) dst.ps = state.ps;
    for (UINT i = 0; i < kSamplers; i++) if (m.textures[i]) dst.textures[i] = state.textures[i];
}

void Device::ApplyFrom(const DeviceState& src, const StateMask& m)
{
    for (UINT i = 0; i < kMaxRenderState; i++) if (m.rs[i]) SetRenderState(static_cast<D3DRENDERSTATETYPE>(i), src.rs[i]);
    for (UINT s = 0; s < 8; s++)
        for (UINT t = 0; t < kMaxTss; t++)
            if (m.tss[s * kMaxTss + t]) SetTextureStageState(s, static_cast<D3DTEXTURESTAGESTATETYPE>(t), src.tss[s][t]);
    for (UINT s = 0; s < kSamplers; s++) {
        const DWORD sampler = s < 16 ? s : s == 16 ? D3DDMAPSAMPLER : D3DVERTEXTEXTURESAMPLER0 + (s - 17);
        for (UINT t = 0; t < kMaxSs; t++)
            if (m.ss[s * kMaxSs + t]) SetSamplerState(sampler, static_cast<D3DSAMPLERSTATETYPE>(t), src.ss[s][t]);
        if (m.textures[s]) SetTexture(sampler, src.textures[s]);
    }
    for (UINT i = 0; i < kTransforms; i++)
        if (m.transforms[i]) SetTransform(static_cast<D3DTRANSFORMSTATETYPE>(i), &src.transforms[i]);
    if (m.viewport) SetViewport(&src.viewport);
    if (m.scissor) SetScissorRect(&src.scissor);
    if (m.material) SetMaterial(&src.material);
    auto applyLight = [&](DWORD index) {
        auto it = src.lights.find(index);
        if (it == src.lights.end()) return;
        SetLight(index, &it->second.light);
        LightEnable(index, it->second.enabled);
    };
    if (m.allLights) for (const auto& l : src.lights) applyLight(l.first);
    for (const auto& l : m.lights) applyLight(l.first);
    for (UINT i = 0; i < 6; i++) if (m.clipPlanes[i]) SetClipPlane(i, src.clipPlanes[i]);
    for (UINT i = 0; i < 256; i++) if (m.vsF[i]) SetVertexShaderConstantF(i, src.vsF[i], 1);
    for (UINT i = 0; i < 16; i++) if (m.vsI[i]) SetVertexShaderConstantI(i, src.vsI[i], 1);
    for (UINT i = 0; i < 16; i++) if (m.vsB[i]) SetVertexShaderConstantB(i, &src.vsB[i], 1);
    for (UINT i = 0; i < 224; i++) if (m.psF[i]) SetPixelShaderConstantF(i, src.psF[i], 1);
    for (UINT i = 0; i < 16; i++) if (m.psI[i]) SetPixelShaderConstantI(i, src.psI[i], 1);
    for (UINT i = 0; i < 16; i++) if (m.psB[i]) SetPixelShaderConstantB(i, &src.psB[i], 1);
    for (UINT i = 0; i < 16; i++) {
        if (m.streams[i]) SetStreamSource(i, src.streams[i].buffer, src.streams[i].offset, src.streams[i].stride);
        if (m.streamFreq[i]) SetStreamSourceFreq(i, src.streams[i].frequency);
    }
    if (m.indices) SetIndices(src.indices);
    if (m.decl) SetVertexDeclaration(src.decl);
    if (m.vs) SetVertexShader(src.vs);
    if (m.ps) SetPixelShader(src.ps);
}

// --- IDirect3DDevice9Ex odds and ends ------------------------------------------------

HRESULT Device::SetConvolutionMonoKernel(UINT, UINT, float*, float*) { return D3D_OK; }
HRESULT Device::ComposeRects(IDirect3DSurface9*, IDirect3DSurface9*, IDirect3DVertexBuffer9*, UINT,
                             IDirect3DVertexBuffer9*, D3DCOMPOSERECTSOP, int, int) { return D3D_OK; }
HRESULT Device::GetGPUThreadPriority(INT* priority) { if (!priority) return D3DERR_INVALIDCALL; *priority = gpuPriority; return D3D_OK; }
HRESULT Device::SetGPUThreadPriority(INT priority) { gpuPriority = priority; return D3D_OK; }
HRESULT Device::WaitForVBlank(UINT) { return D3D_OK; }
HRESULT Device::CheckResourceResidency(IDirect3DResource9**, UINT32) { return D3D_OK; }
HRESULT Device::SetMaximumFrameLatency(UINT latency) { maxLatency = latency ? latency : 3; return D3D_OK; }
HRESULT Device::GetMaximumFrameLatency(UINT* latency) { if (!latency) return D3DERR_INVALIDCALL; *latency = maxLatency; return D3D_OK; }
HRESULT Device::CheckDeviceState(HWND) { return D3D_OK; }

HRESULT Device::GetDisplayModeEx(UINT swapchain, D3DDISPLAYMODEEX* mode, D3DDISPLAYROTATION* rotation)
{
    if (swapchain != 0) return D3DERR_INVALIDCALL;
    return swapChain->GetDisplayModeEx(mode, rotation);
}

HRESULT Device::ProcessVertices(UINT, UINT, UINT, IDirect3DVertexBuffer9*, IDirect3DVertexDeclaration9*, DWORD)
{
    Log("ProcessVertices is not implemented");
    return D3DERR_INVALIDCALL;
}

HRESULT Device::DrawRectPatch(UINT, const float*, const D3DRECTPATCH_INFO*) { return D3DERR_INVALIDCALL; }
HRESULT Device::DrawTriPatch(UINT, const float*, const D3DTRIPATCH_INFO*) { return D3DERR_INVALIDCALL; }
HRESULT Device::DeletePatch(UINT) { return D3DERR_INVALIDCALL; }

} // namespace d3d9
