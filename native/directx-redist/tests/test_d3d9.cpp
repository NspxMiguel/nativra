// d3d9.dll end to end: drives the D3D9 API exactly as a game would and reads
// the results back through the API, under WARP. Covers device creation and
// caps, clears, programmable draws through the shader translator, textures and
// D3D9's half-pixel convention, alpha test, blending, depth, triangle fans and
// user-pointer draws, StretchRect, state blocks and reference counting.

#include <windows.h>
#include <d3d9.h>
#include <d3dcompiler.h>

#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

namespace {

int failures = 0;

void Check(bool ok, const char* what, const std::string& detail = "")
{
    std::printf("%s %s%s%s\n", ok ? "PASS" : "FAIL", what, detail.empty() ? "" : ": ", detail.c_str());
    if (!ok) failures++;
}

std::string Hex(DWORD v)
{
    char b[16];
    std::snprintf(b, sizeof b, "0x%08lX", static_cast<unsigned long>(v));
    return b;
}

constexpr UINT kSize = 64;

struct Vertex {
    float x, y, z, w;
    D3DCOLOR color;
    float u, v;
};

const D3DVERTEXELEMENT9 kElements[] = {
    { 0, 0, D3DDECLTYPE_FLOAT4, D3DDECLMETHOD_DEFAULT, D3DDECLUSAGE_POSITION, 0 },
    { 0, 16, D3DDECLTYPE_D3DCOLOR, D3DDECLMETHOD_DEFAULT, D3DDECLUSAGE_COLOR, 0 },
    { 0, 20, D3DDECLTYPE_FLOAT2, D3DDECLMETHOD_DEFAULT, D3DDECLUSAGE_TEXCOORD, 0 },
    D3DDECL_END(),
};

const char* const kVs = R"(
float4 offset : register(c0);   // a whole-quad shift in clip space, per unit w
struct VIn { float4 pos : POSITION; float4 color : COLOR0; float2 uv : TEXCOORD0; };
struct VOut { float4 pos : POSITION; float4 color : COLOR0; float2 uv : TEXCOORD0; };
VOut main(VIn i)
{
    VOut o;
    o.pos = i.pos + float4(offset.xy, 0.0, 0.0) * i.pos.w;
    o.color = i.color;
    o.uv = i.uv;
    return o;
}
)";

const char* const kPsColor = "float4 main(float4 c : COLOR0) : COLOR { return c; }";
const char* const kPsTexture =
    "sampler2D s0 : register(s0);\n"
    "float4 main(float2 uv : TEXCOORD0) : COLOR { return tex2D(s0, uv); }";
const char* const kPsAlpha = "float4 main(float2 uv : TEXCOORD0) : COLOR { return float4(0.0, 0.0, 1.0, uv.x); }";

ID3DBlob* CompileSm3(const char* source, const char* profile)
{
    ID3DBlob* code = nullptr;
    ID3DBlob* errors = nullptr;
    if (FAILED(D3DCompile(source, std::strlen(source), "t", nullptr, nullptr, "main", profile, 0, 0, &code, &errors))) {
        std::printf("compile %s failed: %s\n", profile, errors ? static_cast<const char*>(errors->GetBufferPointer()) : "?");
        if (errors) errors->Release();
        return nullptr;
    }
    if (errors) errors->Release();
    return code;
}

// A full-viewport quad as a clockwise strip, at depth z.
void Quad(Vertex out[4], float z, D3DCOLOR color)
{
    const Vertex q[4] = {
        { -1, -1, z, 1, color, 0, 1 },
        { -1,  1, z, 1, color, 0, 0 },
        {  1, -1, z, 1, color, 1, 1 },
        {  1,  1, z, 1, color, 1, 0 },
    };
    std::memcpy(out, q, sizeof q);
}

struct Readback {
    std::vector<DWORD> pixels;
    DWORD At(UINT x, UINT y) const { return pixels[y * kSize + x] & 0x00FFFFFF; }
};

bool Read(IDirect3DDevice9* dev, IDirect3DSurface9* source, Readback& out)
{
    IDirect3DSurface9* sys = nullptr;
    if (FAILED(dev->CreateOffscreenPlainSurface(kSize, kSize, D3DFMT_X8R8G8B8, D3DPOOL_SYSTEMMEM, &sys, nullptr))) return false;
    bool ok = SUCCEEDED(dev->GetRenderTargetData(source, sys));
    D3DLOCKED_RECT locked;
    if (ok && SUCCEEDED(sys->LockRect(&locked, nullptr, D3DLOCK_READONLY))) {
        out.pixels.resize(kSize * kSize);
        for (UINT y = 0; y < kSize; y++)
            std::memcpy(&out.pixels[y * kSize], static_cast<const uint8_t*>(locked.pBits) + y * locked.Pitch, kSize * 4);
        sys->UnlockRect();
    } else {
        ok = false;
    }
    sys->Release();
    return ok;
}

bool ReadBackBuffer(IDirect3DDevice9* dev, Readback& out)
{
    IDirect3DSurface9* bb = nullptr;
    if (FAILED(dev->GetBackBuffer(0, 0, D3DBACKBUFFER_TYPE_MONO, &bb))) return false;
    const bool ok = Read(dev, bb, out);
    bb->Release();
    return ok;
}

bool Near(DWORD a, DWORD b, int tolerance = 2)
{
    for (int s = 0; s < 24; s += 8) {
        const int ca = (a >> s) & 0xFF, cb = (b >> s) & 0xFF;
        if (ca - cb > tolerance || cb - ca > tolerance) return false;
    }
    return true;
}

} // namespace

int main()
{
    // Load the d3d9.dll under test from beside this program, by path.
    wchar_t path[MAX_PATH];
    GetModuleFileNameW(nullptr, path, MAX_PATH);
    wchar_t* slash = wcsrchr(path, L'\\');
    if (slash) wcscpy(slash + 1, L"d3d9.dll");
    HMODULE dll = LoadLibraryW(path);
    if (!dll) { std::printf("FAIL cannot load d3d9.dll beside the test\n"); return 1; }
    using CreateFn = IDirect3D9* (WINAPI*)(UINT);
    using WarpFn = void (WINAPI*)(BOOL);
    auto create = reinterpret_cast<CreateFn>(GetProcAddress(dll, "Direct3DCreate9"));
    auto useWarp = reinterpret_cast<WarpFn>(GetProcAddress(dll, "NativraD3D9UseWarp"));
    if (!create || !useWarp) { std::printf("FAIL missing exports\n"); return 1; }
    useWarp(TRUE);
    // The DLL's own diagnostics (shader compile errors, unsupported paths)
    // belong in the test log, not in OutputDebugString.
    using LogFn = void (WINAPI*)(void (*)(const char*));
    if (auto setLog = reinterpret_cast<LogFn>(GetProcAddress(dll, "NativraD3D9SetLog")))
        setLog([](const char* line) { std::printf("  d3d9: %s\n", line); });

    IDirect3D9* d3d = create(D3D_SDK_VERSION);
    Check(d3d != nullptr, "Direct3DCreate9");
    if (!d3d) return 1;

    D3DCAPS9 caps;
    Check(SUCCEEDED(d3d->GetDeviceCaps(0, D3DDEVTYPE_HAL, &caps)) && caps.PixelShaderVersion >= D3DPS_VERSION(3, 0),
          "caps report shader model 3");
    Check(d3d->CheckDeviceFormat(0, D3DDEVTYPE_HAL, D3DFMT_X8R8G8B8, 0, D3DRTYPE_TEXTURE, D3DFMT_DXT5) == D3D_OK,
          "DXT5 textures available");
    Check(d3d->CheckDeviceFormat(0, D3DDEVTYPE_HAL, D3DFMT_X8R8G8B8, D3DUSAGE_DEPTHSTENCIL, D3DRTYPE_SURFACE, D3DFMT_D24S8) == D3D_OK,
          "D24S8 depth available");

    D3DPRESENT_PARAMETERS pp = {};
    pp.BackBufferWidth = kSize;
    pp.BackBufferHeight = kSize;
    pp.BackBufferFormat = D3DFMT_X8R8G8B8;
    pp.BackBufferCount = 1;
    pp.SwapEffect = D3DSWAPEFFECT_DISCARD;
    pp.Windowed = TRUE;
    pp.EnableAutoDepthStencil = TRUE;
    pp.AutoDepthStencilFormat = D3DFMT_D24S8;
    IDirect3DDevice9* dev = nullptr;
    const HRESULT created = d3d->CreateDevice(0, D3DDEVTYPE_HAL, nullptr, D3DCREATE_HARDWARE_VERTEXPROCESSING, &pp, &dev);
    Check(SUCCEEDED(created) && dev, "CreateDevice", Hex(created));
    if (!dev) return 1;

    Readback rb;

    // --- clear -------------------------------------------------------------
    dev->Clear(0, nullptr, D3DCLEAR_TARGET | D3DCLEAR_ZBUFFER, D3DCOLOR_XRGB(255, 0, 0), 1.0f, 0);
    Check(ReadBackBuffer(dev, rb) && rb.At(0, 0) == 0xFF0000 && rb.At(63, 63) == 0xFF0000, "Clear fills the target",
          rb.pixels.empty() ? "no readback" : Hex(rb.At(0, 0)));

    D3DRECT part = { 0, 0, 32, 64 };
    dev->Clear(1, &part, D3DCLEAR_TARGET, D3DCOLOR_XRGB(0, 0, 255), 1.0f, 0);
    Check(ReadBackBuffer(dev, rb) && rb.At(10, 10) == 0x0000FF && rb.At(50, 10) == 0xFF0000, "Clear honours rectangles",
          Hex(rb.At(10, 10)) + " / " + Hex(rb.At(50, 10)));

    // --- shaders and a coloured quad -----------------------------------------
    ID3DBlob* vsCode = CompileSm3(kVs, "vs_3_0");
    ID3DBlob* psColorCode = CompileSm3(kPsColor, "ps_3_0");
    ID3DBlob* psTextureCode = CompileSm3(kPsTexture, "ps_3_0");
    ID3DBlob* psAlphaCode = CompileSm3(kPsAlpha, "ps_3_0");
    if (!vsCode || !psColorCode || !psTextureCode || !psAlphaCode) { std::printf("FAIL shader compile\n"); return 1; }

    IDirect3DVertexShader9* vs = nullptr;
    IDirect3DPixelShader9 *psColor = nullptr, *psTexture = nullptr, *psAlpha = nullptr;
    dev->CreateVertexShader(static_cast<const DWORD*>(vsCode->GetBufferPointer()), &vs);
    dev->CreatePixelShader(static_cast<const DWORD*>(psColorCode->GetBufferPointer()), &psColor);
    dev->CreatePixelShader(static_cast<const DWORD*>(psTextureCode->GetBufferPointer()), &psTexture);
    dev->CreatePixelShader(static_cast<const DWORD*>(psAlphaCode->GetBufferPointer()), &psAlpha);
    Check(vs && psColor && psTexture && psAlpha, "shader objects created");

    IDirect3DVertexDeclaration9* decl = nullptr;
    dev->CreateVertexDeclaration(kElements, &decl);
    IDirect3DVertexBuffer9* vb = nullptr;
    dev->CreateVertexBuffer(sizeof(Vertex) * 4, D3DUSAGE_WRITEONLY, 0, D3DPOOL_MANAGED, &vb, nullptr);
    Vertex* vertices = nullptr;
    vb->Lock(0, 0, reinterpret_cast<void**>(&vertices), 0);
    Quad(vertices, 0.5f, D3DCOLOR_XRGB(0, 255, 0));
    vb->Unlock();

    const float zero[4] = {};
    dev->SetVertexShaderConstantF(0, zero, 1);
    dev->SetVertexDeclaration(decl);
    dev->SetStreamSource(0, vb, 0, sizeof(Vertex));
    dev->SetVertexShader(vs);
    dev->SetPixelShader(psColor);
    dev->SetRenderState(D3DRS_CULLMODE, D3DCULL_NONE);
    dev->SetRenderState(D3DRS_ZENABLE, D3DZB_FALSE);
    dev->BeginScene();
    dev->DrawPrimitive(D3DPT_TRIANGLESTRIP, 0, 2);
    dev->EndScene();
    Check(ReadBackBuffer(dev, rb) && rb.At(32, 32) == 0x00FF00 && rb.At(0, 0) == 0x00FF00 && rb.At(63, 63) == 0x00FF00,
          "programmable quad covers the target", Hex(rb.At(32, 32)));

    // --- textures and the half-pixel convention --------------------------------
    // Each texel is unique; D3D9 games shift a full-screen quad by -0.5 pixel
    // so point sampling hits texel (x, y) at pixel (x, y). The device must
    // reproduce D3D9's pixel centres for that to land exactly.
    IDirect3DTexture9* tex = nullptr;
    dev->CreateTexture(kSize, kSize, 1, 0, D3DFMT_A8R8G8B8, D3DPOOL_MANAGED, &tex, nullptr);
    D3DLOCKED_RECT lr;
    tex->LockRect(0, &lr, nullptr, 0);
    for (UINT y = 0; y < kSize; y++)
        for (UINT x = 0; x < kSize; x++)
            reinterpret_cast<DWORD*>(static_cast<uint8_t*>(lr.pBits) + y * lr.Pitch)[x] =
                D3DCOLOR_ARGB(255, x * 4, y * 4, (x ^ y) & 0xFF);
    tex->UnlockRect(0);

    const float halfPixel[4] = { -1.0f / kSize, 1.0f / kSize, 0, 0 };
    dev->SetVertexShaderConstantF(0, halfPixel, 1);
    dev->SetTexture(0, tex);
    dev->SetSamplerState(0, D3DSAMP_MINFILTER, D3DTEXF_POINT);
    dev->SetSamplerState(0, D3DSAMP_MAGFILTER, D3DTEXF_POINT);
    dev->SetPixelShader(psTexture);
    dev->DrawPrimitive(D3DPT_TRIANGLESTRIP, 0, 2);
    int mismatches = 0;
    std::string first;
    if (ReadBackBuffer(dev, rb)) {
        for (UINT y = 0; y < kSize; y++)
            for (UINT x = 0; x < kSize; x++) {
                const DWORD want = D3DCOLOR_XRGB(x * 4, y * 4, (x ^ y) & 0xFF) & 0x00FFFFFF;
                if (rb.At(x, y) != want) {
                    if (mismatches++ == 0)
                        first = "(" + std::to_string(x) + "," + std::to_string(y) + ") " + Hex(rb.At(x, y)) + " want " + Hex(want);
                }
            }
    } else {
        mismatches = -1;
    }
    Check(mismatches == 0, "texel-exact sampling with D3D9 pixel centres",
          mismatches ? std::to_string(mismatches) + " pixels differ, first " + first : "");
    dev->SetVertexShaderConstantF(0, zero, 1);

    // --- alpha test -------------------------------------------------------------
    dev->Clear(0, nullptr, D3DCLEAR_TARGET, D3DCOLOR_XRGB(255, 0, 0), 1.0f, 0);
    dev->SetPixelShader(psAlpha);
    dev->SetRenderState(D3DRS_ALPHATESTENABLE, TRUE);
    dev->SetRenderState(D3DRS_ALPHAFUNC, D3DCMP_GREATER);
    dev->SetRenderState(D3DRS_ALPHAREF, 128);
    dev->DrawPrimitive(D3DPT_TRIANGLESTRIP, 0, 2);
    Check(ReadBackBuffer(dev, rb) && rb.At(8, 32) == 0xFF0000 && rb.At(56, 32) == 0x0000FF,
          "alpha test discards below the reference", Hex(rb.At(8, 32)) + " / " + Hex(rb.At(56, 32)));
    dev->SetRenderState(D3DRS_ALPHATESTENABLE, FALSE);

    // --- blending -----------------------------------------------------------------
    dev->Clear(0, nullptr, D3DCLEAR_TARGET, D3DCOLOR_XRGB(255, 0, 0), 1.0f, 0);
    dev->SetRenderState(D3DRS_ALPHABLENDENABLE, TRUE);
    dev->SetRenderState(D3DRS_SRCBLEND, D3DBLEND_SRCALPHA);
    dev->SetRenderState(D3DRS_DESTBLEND, D3DBLEND_INVSRCALPHA);
    dev->DrawPrimitive(D3DPT_TRIANGLESTRIP, 0, 2);
    // At x = 32 alpha is ~0.5: half red, half blue.
    Check(ReadBackBuffer(dev, rb) && Near(rb.At(32, 32), 0x7F0080, 6), "alpha blending", Hex(rb.At(32, 32)));
    dev->SetRenderState(D3DRS_ALPHABLENDENABLE, FALSE);

    // --- depth ---------------------------------------------------------------------
    dev->Clear(0, nullptr, D3DCLEAR_TARGET | D3DCLEAR_ZBUFFER, 0, 1.0f, 0);
    dev->SetRenderState(D3DRS_ZENABLE, D3DZB_TRUE);
    dev->SetRenderState(D3DRS_ZFUNC, D3DCMP_LESSEQUAL);
    dev->SetPixelShader(psColor);
    Vertex near_[4], far_[4];
    Quad(near_, 0.2f, D3DCOLOR_XRGB(255, 0, 0));
    Quad(far_, 0.8f, D3DCOLOR_XRGB(0, 255, 0));
    dev->DrawPrimitiveUP(D3DPT_TRIANGLESTRIP, 2, near_, sizeof(Vertex));
    dev->DrawPrimitiveUP(D3DPT_TRIANGLESTRIP, 2, far_, sizeof(Vertex));
    Check(ReadBackBuffer(dev, rb) && rb.At(32, 32) == 0xFF0000, "depth test keeps the nearer quad", Hex(rb.At(32, 32)));
    dev->SetRenderState(D3DRS_ZENABLE, D3DZB_FALSE);

    // --- triangle fan through a user pointer ----------------------------------------
    dev->Clear(0, nullptr, D3DCLEAR_TARGET, 0, 1.0f, 0);
    const Vertex fan[4] = {
        { -1, -1, 0.5f, 1, D3DCOLOR_XRGB(255, 255, 0), 0, 1 },
        { -1,  1, 0.5f, 1, D3DCOLOR_XRGB(255, 255, 0), 0, 0 },
        {  1,  1, 0.5f, 1, D3DCOLOR_XRGB(255, 255, 0), 1, 0 },
        {  1, -1, 0.5f, 1, D3DCOLOR_XRGB(255, 255, 0), 1, 1 },
    };
    dev->DrawPrimitiveUP(D3DPT_TRIANGLEFAN, 2, fan, sizeof(Vertex));
    Check(ReadBackBuffer(dev, rb) && rb.At(5, 5) == 0xFFFF00 && rb.At(58, 58) == 0xFFFF00 && rb.At(58, 5) == 0xFFFF00,
          "triangle fan (DrawPrimitiveUP)", Hex(rb.At(58, 58)));
    IDirect3DVertexBuffer9* after = reinterpret_cast<IDirect3DVertexBuffer9*>(1);
    UINT offset = 1, stride = 1;
    dev->GetStreamSource(0, &after, &offset, &stride);
    Check(after == nullptr, "stream 0 unbound after a user-pointer draw");

    // --- indexed draw -----------------------------------------------------------------
    IDirect3DIndexBuffer9* ib = nullptr;
    dev->CreateIndexBuffer(6 * 2, 0, D3DFMT_INDEX16, D3DPOOL_MANAGED, &ib, nullptr);
    WORD* idx = nullptr;
    ib->Lock(0, 0, reinterpret_cast<void**>(&idx), 0);
    const WORD indices[6] = { 0, 1, 2, 2, 1, 3 };
    std::memcpy(idx, indices, sizeof indices);
    ib->Unlock();
    vb->Lock(0, 0, reinterpret_cast<void**>(&vertices), 0);
    Quad(vertices, 0.5f, D3DCOLOR_XRGB(0, 255, 255));
    vb->Unlock();
    dev->Clear(0, nullptr, D3DCLEAR_TARGET, 0, 1.0f, 0);
    dev->SetStreamSource(0, vb, 0, sizeof(Vertex));
    dev->SetIndices(ib);
    dev->DrawIndexedPrimitive(D3DPT_TRIANGLELIST, 0, 0, 4, 0, 2);
    Check(ReadBackBuffer(dev, rb) && rb.At(32, 32) == 0x00FFFF && rb.At(2, 61) == 0x00FFFF,
          "indexed triangle list", Hex(rb.At(32, 32)));

    // --- render to texture and StretchRect ---------------------------------------------
    IDirect3DTexture9* rtTex = nullptr;
    dev->CreateTexture(kSize, kSize, 1, D3DUSAGE_RENDERTARGET, D3DFMT_A8R8G8B8, D3DPOOL_DEFAULT, &rtTex, nullptr);
    IDirect3DSurface9 *rtSurface = nullptr, *backBuffer = nullptr;
    rtTex->GetSurfaceLevel(0, &rtSurface);
    dev->GetRenderTarget(0, &backBuffer);
    dev->SetRenderTarget(0, rtSurface);
    dev->Clear(0, nullptr, D3DCLEAR_TARGET, D3DCOLOR_XRGB(10, 200, 30), 1.0f, 0);
    dev->SetRenderTarget(0, backBuffer);
    dev->Clear(0, nullptr, D3DCLEAR_TARGET, 0, 1.0f, 0);
    RECT dst = { 0, 0, 32, 32 };
    const HRESULT stretched = dev->StretchRect(rtSurface, nullptr, backBuffer, &dst, D3DTEXF_LINEAR);
    Check(SUCCEEDED(stretched) && ReadBackBuffer(dev, rb) && Near(rb.At(10, 10), 0x0AC81E) && rb.At(50, 50) == 0,
          "StretchRect scales into a sub-rectangle", Hex(rb.At(10, 10)));

    // --- state blocks ---------------------------------------------------------------------
    IDirect3DStateBlock9* block = nullptr;
    dev->BeginStateBlock();
    dev->SetRenderState(D3DRS_CULLMODE, D3DCULL_CW);
    dev->SetRenderState(D3DRS_FOGENABLE, TRUE);
    dev->EndStateBlock(&block);
    DWORD cull = 0;
    dev->GetRenderState(D3DRS_CULLMODE, &cull);
    Check(cull == D3DCULL_NONE, "recording does not change the device");
    block->Apply();
    dev->GetRenderState(D3DRS_CULLMODE, &cull);
    Check(cull == D3DCULL_CW, "a recorded block applies its states");
    block->Release();

    // --- fixed function ---------------------------------------------------------------------
    dev->SetVertexShader(nullptr);
    dev->SetPixelShader(nullptr);
    dev->SetRenderState(D3DRS_ZENABLE, D3DZB_FALSE);
    dev->SetRenderState(D3DRS_CULLMODE, D3DCULL_NONE);

    // A 2D sprite the D3D9 way: pre-transformed vertices shifted by -0.5 so
    // each pixel samples its own texel, under the default stage (texture x
    // diffuse). Most 2D games and every UI draw like this.
    struct TlVertex { float x, y, z, rhw; D3DCOLOR color; float u, v; };
    const float lo = -0.5f, hi = kSize - 0.5f;
    const TlVertex sprite[4] = {
        { lo, lo, 0.5f, 1.0f, 0xFFFFFFFF, 0, 0 },
        { hi, lo, 0.5f, 1.0f, 0xFFFFFFFF, 1, 0 },
        { lo, hi, 0.5f, 1.0f, 0xFFFFFFFF, 0, 1 },
        { hi, hi, 0.5f, 1.0f, 0xFFFFFFFF, 1, 1 },
    };
    dev->SetFVF(D3DFVF_XYZRHW | D3DFVF_DIFFUSE | D3DFVF_TEX1);
    dev->SetTexture(0, tex);
    dev->Clear(0, nullptr, D3DCLEAR_TARGET, 0, 1.0f, 0);
    dev->DrawPrimitiveUP(D3DPT_TRIANGLESTRIP, 2, sprite, sizeof(TlVertex));
    mismatches = 0;
    first.clear();
    if (ReadBackBuffer(dev, rb)) {
        for (UINT y = 0; y < kSize; y++)
            for (UINT x = 0; x < kSize; x++) {
                const DWORD want = D3DCOLOR_XRGB(x * 4, y * 4, (x ^ y) & 0xFF) & 0x00FFFFFF;
                if (rb.At(x, y) != want && mismatches++ == 0)
                    first = "(" + std::to_string(x) + "," + std::to_string(y) + ") " + Hex(rb.At(x, y)) + " want " + Hex(want);
            }
    } else {
        mismatches = -1;
    }
    Check(mismatches == 0, "fixed function: pre-transformed sprite is texel exact",
          mismatches ? std::to_string(mismatches) + " pixels differ, first " + first : "");

    // Stage 0 selecting the texture factor.
    dev->SetTextureStageState(0, D3DTSS_COLOROP, D3DTOP_SELECTARG1);
    dev->SetTextureStageState(0, D3DTSS_COLORARG1, D3DTA_TFACTOR);
    dev->SetRenderState(D3DRS_TEXTUREFACTOR, D3DCOLOR_XRGB(12, 34, 56));
    dev->DrawPrimitiveUP(D3DPT_TRIANGLESTRIP, 2, sprite, sizeof(TlVertex));
    Check(ReadBackBuffer(dev, rb) && rb.At(20, 20) == 0x0C2238, "fixed function: texture stage selects TFACTOR",
          Hex(rb.At(20, 20)));
    dev->SetTextureStageState(0, D3DTSS_COLOROP, D3DTOP_MODULATE);
    dev->SetTextureStageState(0, D3DTSS_COLORARG1, D3DTA_TEXTURE);
    dev->SetTexture(0, nullptr);

    // Untransformed geometry, lit by one directional light head-on.
    struct LitVertex { float x, y, z, nx, ny, nz; };
    const LitVertex lit[4] = {
        { -1, -1, 0.5f, 0, 0, -1 }, { -1, 1, 0.5f, 0, 0, -1 }, { 1, -1, 0.5f, 0, 0, -1 }, { 1, 1, 0.5f, 0, 0, -1 },
    };
    D3DMATRIX identity = {};
    identity._11 = identity._22 = identity._33 = identity._44 = 1.0f;
    dev->SetTransform(D3DTS_WORLD, &identity);
    dev->SetTransform(D3DTS_VIEW, &identity);
    dev->SetTransform(D3DTS_PROJECTION, &identity);
    D3DMATERIAL9 material = {};
    material.Diffuse = { 0.5f, 1.0f, 0.25f, 1.0f };
    dev->SetMaterial(&material);
    D3DLIGHT9 light = {};
    light.Type = D3DLIGHT_DIRECTIONAL;
    light.Diffuse = { 1.0f, 1.0f, 1.0f, 1.0f };
    light.Direction = { 0.0f, 0.0f, 1.0f };   // shining along +z, onto normals facing -z
    dev->SetLight(0, &light);
    dev->LightEnable(0, TRUE);
    dev->SetRenderState(D3DRS_LIGHTING, TRUE);
    dev->SetRenderState(D3DRS_AMBIENT, 0);
    dev->SetFVF(D3DFVF_XYZ | D3DFVF_NORMAL);
    dev->Clear(0, nullptr, D3DCLEAR_TARGET, 0, 1.0f, 0);
    dev->DrawPrimitiveUP(D3DPT_TRIANGLESTRIP, 2, lit, sizeof(LitVertex));
    Check(ReadBackBuffer(dev, rb) && Near(rb.At(32, 32), 0x80FF40), "fixed function: directional light x material",
          Hex(rb.At(32, 32)));

    // Linear vertex fog: depth 0.5 between start 0 and end 1 is half fogged.
    dev->SetRenderState(D3DRS_FOGENABLE, TRUE);
    dev->SetRenderState(D3DRS_FOGVERTEXMODE, D3DFOG_LINEAR);
    dev->SetRenderState(D3DRS_FOGCOLOR, D3DCOLOR_XRGB(0, 0, 255));
    const float fogStart = 0.0f, fogEnd = 1.0f;
    DWORD bits;
    std::memcpy(&bits, &fogStart, 4); dev->SetRenderState(D3DRS_FOGSTART, bits);
    std::memcpy(&bits, &fogEnd, 4); dev->SetRenderState(D3DRS_FOGEND, bits);
    dev->Clear(0, nullptr, D3DCLEAR_TARGET, 0, 1.0f, 0);
    dev->DrawPrimitiveUP(D3DPT_TRIANGLESTRIP, 2, lit, sizeof(LitVertex));
    Check(ReadBackBuffer(dev, rb) && Near(rb.At(32, 32), 0x4080A0, 3), "fixed function: linear vertex fog",
          Hex(rb.At(32, 32)));
    dev->SetRenderState(D3DRS_FOGENABLE, FALSE);
    dev->SetRenderState(D3DRS_LIGHTING, FALSE);

    // --- reference counting ----------------------------------------------------------------
    const ULONG countBefore = rtTex->AddRef() - 1;
    rtTex->Release();
    rtSurface->AddRef();
    const ULONG countAfter = rtTex->AddRef() - 1;
    rtTex->Release();
    rtSurface->Release();
    Check(countAfter == countBefore + 1, "a texture's surface shares the texture's count",
          std::to_string(countBefore) + " -> " + std::to_string(countAfter));

    rtSurface->Release();
    backBuffer->Release();
    rtTex->Release();
    ib->Release();
    tex->Release();
    vb->Release();
    decl->Release();
    dev->SetVertexShader(nullptr);
    dev->SetPixelShader(nullptr);
    vs->Release();
    psColor->Release();
    psTexture->Release();
    psAlpha->Release();
    dev->SetStreamSource(0, nullptr, 0, 0);
    dev->SetIndices(nullptr);
    dev->SetTexture(0, nullptr);
    dev->SetVertexDeclaration(nullptr);
    const ULONG left = dev->Release();
    Check(left == 0, "the device is released once the game lets go", std::to_string(left));
    d3d->Release();

    std::printf("%s: %d failure(s)\n", failures ? "FAILED" : "OK", failures);
    return failures ? 1 : 0;
}
