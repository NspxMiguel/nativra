// d3d9.dll end to end: drives the D3D9 API exactly as a game would and reads
// the results back through the API, under WARP. Covers device creation and
// caps, clears, programmable draws through the shader translator, textures and
// D3D9's half-pixel convention, alpha test, blending, depth, triangle fans and
// user-pointer draws, StretchRect, state blocks, volume textures and reference
// counting.

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

// --- volume texture helpers ----------------------------------------------------
// The volumes under test code each texel with its own position, and a draw
// shows one slice of one across the target, split into a grid of cells: the
// centre of cell (x, y) shows texel (x, y) of the slice that was sampled.

using Coding = DWORD (*)(UINT x, UINT y, UINT z);

// x, y and z become red, green and blue.
DWORD Coded(UINT x, UINT y, UINT z) { return D3DCOLOR_XRGB(x * 85, y * 85, z * 85) & 0x00FFFFFF; }
// The same with the axes rotated, to tell one volume from another.
DWORD Permuted(UINT x, UINT y, UINT z) { return Coded(z, x, y); }
// A 2x2x2 level: each axis is 0 or full.
DWORD Corners(UINT x, UINT y, UINT z) { return D3DCOLOR_XRGB(x * 255, y * 255, z * 255) & 0x00FFFFFF; }
DWORD White(UINT, UINT, UINT) { return 0x00FFFFFF; }
// Coded, with the box of texels 1..2 on every axis overwritten by white.
DWORD Whitened(UINT x, UINT y, UINT z)
{
    const bool inside = x >= 1 && x <= 2 && y >= 1 && y <= 2 && z >= 1 && z <= 2;
    return inside ? 0x00FFFFFF : Coded(x, y, z);
}
// The 8x8x2 DXT1 volume: a red slice, then a blue one, and one green block in the first.
DWORD DxtBlocks(UINT x, UINT y, UINT z)
{
    return z == 1 ? 0x000000FF : (x == 1 && y == 1) ? 0x0000FF00 : 0x00FF0000;
}

// Fills the first `size` texels of every axis of a locked A8R8G8B8 volume level.
void FillVolume(const D3DLOCKED_BOX& box, UINT size, Coding code)
{
    for (UINT z = 0; z < size; z++)
        for (UINT y = 0; y < size; y++) {
            auto* row = reinterpret_cast<DWORD*>(static_cast<uint8_t*>(box.pBits) +
                                                 static_cast<size_t>(z) * box.SlicePitch +
                                                 static_cast<size_t>(y) * box.RowPitch);
            for (UINT x = 0; x < size; x++) row[x] = 0xFF000000u | code(x, y, z);
        }
}

using DrawSlice = bool (*)(IDirect3DDevice9* dev, float slice, Readback& out);

// A full-target quad through a pixel shader that samples the volume on
// sampler 1 at depth `slice`.
bool DrawSliceShader(IDirect3DDevice9* dev, float slice, Readback& out)
{
    const float coordinate[4] = { slice, 0, 0, 0 };
    dev->SetPixelShaderConstantF(0, coordinate, 1);
    dev->Clear(0, nullptr, D3DCLEAR_TARGET, 0, 1.0f, 0);
    Vertex quad[4];
    Quad(quad, 0.5f, 0);
    dev->DrawPrimitiveUP(D3DPT_TRIANGLESTRIP, 2, quad, sizeof(Vertex));
    return ReadBackBuffer(dev, out);
}

// The same through fixed-function stage 0, with the depth as the third
// texture coordinate.
bool DrawSliceFixed(IDirect3DDevice9* dev, float slice, Readback& out)
{
    struct V { float x, y, z, rhw; D3DCOLOR color; float u, v, w; };
    const float lo = -0.5f, hi = kSize - 0.5f;
    const V quad[4] = {
        { lo, lo, 0.5f, 1.0f, 0xFFFFFFFF, 0, 0, slice },
        { hi, lo, 0.5f, 1.0f, 0xFFFFFFFF, 1, 0, slice },
        { lo, hi, 0.5f, 1.0f, 0xFFFFFFFF, 0, 1, slice },
        { hi, hi, 0.5f, 1.0f, 0xFFFFFFFF, 1, 1, slice },
    };
    dev->Clear(0, nullptr, D3DCLEAR_TARGET, 0, 1.0f, 0);
    dev->DrawPrimitiveUP(D3DPT_TRIANGLESTRIP, 2, quad, sizeof(V));
    return ReadBackBuffer(dev, out);
}

// Samples every slice with `draw` and counts the cells of a cells x cells grid
// whose centre differs from code(x, y, z): -1 when a draw cannot be read back.
// `first` describes the first miss.
int WrongCells(IDirect3DDevice9* dev, DrawSlice draw, UINT cells, UINT depth, Coding code, std::string& first)
{
    int wrong = 0;
    const UINT cell = kSize / cells;
    for (UINT z = 0; z < depth; z++) {
        Readback shot;
        if (!draw(dev, (z + 0.5f) / depth, shot)) return -1;
        for (UINT y = 0; y < cells; y++)
            for (UINT x = 0; x < cells; x++) {
                const DWORD got = shot.At(x * cell + cell / 2, y * cell + cell / 2);
                const DWORD want = code(x, y, z);
                if (!Near(got, want) && wrong++ == 0)
                    first = "(" + std::to_string(x) + "," + std::to_string(y) + "," + std::to_string(z) + ") " +
                            Hex(got) + " want " + Hex(want);
            }
    }
    return wrong;
}

// A texel as the game reads it back through a locked level.
DWORD TexelAt(const D3DLOCKED_BOX& box, UINT x, UINT y, UINT z)
{
    return reinterpret_cast<const DWORD*>(static_cast<const uint8_t*>(box.pBits) +
                                          static_cast<size_t>(z) * box.SlicePitch +
                                          static_cast<size_t>(y) * box.RowPitch)[x];
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

    Check(d3d->CheckDeviceFormat(0, D3DDEVTYPE_HAL, D3DFMT_UNKNOWN, 0, D3DRTYPE_TEXTURE, D3DFMT_A8R8G8B8) ==
              D3DERR_INVALIDCALL,
          "format query rejects unknown adapter format");
    Check(d3d->CheckDeviceFormat(0, D3DDEVTYPE_HAL, D3DFMT_X8R8G8B8, 0, D3DRTYPE_VERTEXBUFFER, D3DFMT_A8R8G8B8) ==
              D3DERR_INVALIDCALL,
          "format query rejects buffer resource types");
    Check(d3d->CheckDeviceFormat(0, D3DDEVTYPE_HAL, D3DFMT_X8R8G8B8, D3DUSAGE_DMAP, D3DRTYPE_TEXTURE,
                                 D3DFMT_A8R8G8B8) == D3DERR_NOTAVAILABLE,
          "unimplemented displacement maps are not advertised");
    Check(d3d->CheckDeviceFormat(0, D3DDEVTYPE_HAL, D3DFMT_X8R8G8B8, 0, D3DRTYPE_SURFACE, D3DFMT_D24S8) ==
              D3DERR_NOTAVAILABLE,
          "plain non-lockable depth surfaces are not advertised");
    Check(d3d->CheckDeviceFormat(0, D3DDEVTYPE_HAL, D3DFMT_X8R8G8B8, 0, D3DRTYPE_SURFACE, D3DFMT_D16_LOCKABLE) ==
              D3D_OK,
          "plain lockable depth surfaces remain available");
    DWORD quality = 99;
    Check(d3d->CheckDeviceMultiSampleType(0, D3DDEVTYPE_HAL, D3DFMT_X8R8G8B8, TRUE, D3DMULTISAMPLE_NONE, &quality) ==
                  D3D_OK &&
              quality == 1,
          "single sample query has one quality level");
    Check(d3d->CheckDeviceMultiSampleType(0, D3DDEVTYPE_HAL, D3DFMT_X8R8G8B8, TRUE, D3DMULTISAMPLE_NONMASKABLE,
                                          &quality) == D3DERR_NOTAVAILABLE &&
              quality == 0,
          "nonmaskable multisampling is not advertised as single sample");
    Check(d3d->CheckDeviceMultiSampleType(0, D3DDEVTYPE_HAL, D3DFMT_UNKNOWN, TRUE, D3DMULTISAMPLE_NONE, &quality) ==
              D3DERR_INVALIDCALL,
          "multisample query validates the surface format");
    Check(d3d->CheckDepthStencilMatch(0, D3DDEVTYPE_HAL, D3DFMT_X8R8G8B8, D3DFMT_DXT1, D3DFMT_D24S8) ==
              D3DERR_NOTAVAILABLE,
          "depth match rejects a compressed render target");
    Check(d3d->CheckDepthStencilMatch(0, D3DDEVTYPE_HAL, D3DFMT_X8R8G8B8, D3DFMT_A8R8G8B8, D3DFMT_D24S8) == D3D_OK,
          "depth match accepts a supported color and depth pair");

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
    bool readOk = false;

    // --- clear -------------------------------------------------------------
    dev->Clear(0, nullptr, D3DCLEAR_TARGET | D3DCLEAR_ZBUFFER, D3DCOLOR_XRGB(255, 0, 0), 1.0f, 0);
    readOk = ReadBackBuffer(dev, rb);
    Check(readOk && rb.At(0, 0) == 0xFF0000 && rb.At(63, 63) == 0xFF0000, "Clear fills the target",
          rb.pixels.empty() ? "no readback" : Hex(rb.At(0, 0)));

    D3DRECT part = { 0, 0, 32, 64 };
    dev->Clear(1, &part, D3DCLEAR_TARGET, D3DCOLOR_XRGB(0, 0, 255), 1.0f, 0);
    readOk = ReadBackBuffer(dev, rb);
    Check(readOk && rb.At(10, 10) == 0x0000FF && rb.At(50, 10) == 0xFF0000, "Clear honours rectangles",
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
    readOk = ReadBackBuffer(dev, rb);
    Check(readOk && rb.At(32, 32) == 0x00FF00 && rb.At(0, 0) == 0x00FF00 && rb.At(63, 63) == 0x00FF00,
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
    readOk = ReadBackBuffer(dev, rb);
    Check(readOk && rb.At(8, 32) == 0xFF0000 && rb.At(56, 32) == 0x0000FF,
          "alpha test discards below the reference", Hex(rb.At(8, 32)) + " / " + Hex(rb.At(56, 32)));
    dev->SetRenderState(D3DRS_ALPHATESTENABLE, FALSE);

    // --- blending -----------------------------------------------------------------
    dev->Clear(0, nullptr, D3DCLEAR_TARGET, D3DCOLOR_XRGB(255, 0, 0), 1.0f, 0);
    dev->SetRenderState(D3DRS_ALPHABLENDENABLE, TRUE);
    dev->SetRenderState(D3DRS_SRCBLEND, D3DBLEND_SRCALPHA);
    dev->SetRenderState(D3DRS_DESTBLEND, D3DBLEND_INVSRCALPHA);
    dev->DrawPrimitive(D3DPT_TRIANGLESTRIP, 0, 2);
    // At x = 32 alpha is ~0.5: half red, half blue.
    readOk = ReadBackBuffer(dev, rb);
    Check(readOk && Near(rb.At(32, 32), 0x7F0080, 6), "alpha blending", Hex(rb.At(32, 32)));
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
    readOk = ReadBackBuffer(dev, rb);
    Check(readOk && rb.At(32, 32) == 0xFF0000, "depth test keeps the nearer quad", Hex(rb.At(32, 32)));
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
    readOk = ReadBackBuffer(dev, rb);
    Check(readOk && rb.At(5, 5) == 0xFFFF00 && rb.At(58, 58) == 0xFFFF00 && rb.At(58, 5) == 0xFFFF00,
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
    readOk = ReadBackBuffer(dev, rb);
    Check(readOk && rb.At(32, 32) == 0x00FFFF && rb.At(2, 61) == 0x00FFFF,
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
    readOk = ReadBackBuffer(dev, rb);
    Check(SUCCEEDED(stretched) && readOk && Near(rb.At(10, 10), 0x0AC81E) && rb.At(50, 50) == 0,
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
    dev->SetRenderState(D3DRS_FOGENABLE, FALSE);
    dev->SetRenderState(D3DRS_CULLMODE, D3DCULL_NONE);

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
    readOk = ReadBackBuffer(dev, rb);
    Check(readOk && rb.At(20, 20) == 0x0C2238, "fixed function: texture stage selects TFACTOR",
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
    readOk = ReadBackBuffer(dev, rb);
    Check(readOk && Near(rb.At(32, 32), 0x80FF40), "fixed function: directional light x material",
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
    readOk = ReadBackBuffer(dev, rb);
    Check(readOk && Near(rb.At(32, 32), 0x4080A0, 3), "fixed function: linear vertex fog",
          Hex(rb.At(32, 32)));
    dev->SetRenderState(D3DRS_FOGENABLE, FALSE);
    dev->SetRenderState(D3DRS_LIGHTING, FALSE);

    // --- volume textures ---------------------------------------------------------------------
    // Volumes sampled through a ps_3_0 tex3D on sampler 1 (not 0: the view must
    // land in the slot the shader declares), and through fixed-function stage 0.
    {
        Check(d3d->CheckDeviceFormat(0, D3DDEVTYPE_HAL, D3DFMT_X8R8G8B8, 0, D3DRTYPE_VOLUMETEXTURE, D3DFMT_A8R8G8B8) == D3D_OK,
              "A8R8G8B8 volume textures available");
        Check(d3d->CheckDeviceFormat(0, D3DDEVTYPE_HAL, D3DFMT_X8R8G8B8, 0, D3DRTYPE_VOLUMETEXTURE, D3DFMT_DXT1) == D3D_OK,
              "DXT1 volume textures available");
        Check(d3d->CheckDeviceFormat(0, D3DDEVTYPE_HAL, D3DFMT_X8R8G8B8, D3DUSAGE_RENDERTARGET, D3DRTYPE_VOLUMETEXTURE,
                                     D3DFMT_A8R8G8B8) != D3D_OK &&
              d3d->CheckDeviceFormat(0, D3DDEVTYPE_HAL, D3DFMT_X8R8G8B8, D3DUSAGE_DEPTHSTENCIL, D3DRTYPE_VOLUMETEXTURE,
                                     D3DFMT_D24S8) != D3D_OK,
              "volume textures refuse render target and depth-stencil use");
        Check((caps.TextureCaps & D3DPTEXTURECAPS_VOLUMEMAP) && (caps.TextureCaps & D3DPTEXTURECAPS_MIPVOLUMEMAP) &&
                  caps.MaxVolumeExtent >= 256 && (caps.VolumeTextureFilterCaps & D3DPTFILTERCAPS_MAGFLINEAR),
              "caps advertise volume textures");

        IDirect3DVolumeTexture9* rejected = nullptr;
        Check(dev->CreateVolumeTexture(4, 4, 0, 1, 0, D3DFMT_A8R8G8B8, D3DPOOL_MANAGED, &rejected, nullptr) == D3DERR_INVALIDCALL &&
                  !rejected,
              "a volume with no depth is refused");
        Check(dev->CreateVolumeTexture(4, 4, 4, 1, D3DUSAGE_RENDERTARGET, D3DFMT_A8R8G8B8, D3DPOOL_DEFAULT, &rejected, nullptr) ==
                      D3DERR_INVALIDCALL && !rejected,
              "a volume cannot be a render target");
        Check(dev->CreateVolumeTexture(4, 4, 4, 1, 0, D3DFMT_D24S8, D3DPOOL_DEFAULT, &rejected, nullptr) == D3DERR_INVALIDCALL &&
                  !rejected,
              "a volume cannot hold depth");

        const char* const kPsVolume =
            "sampler3D s1 : register(s1);\n"
            "float4 slice : register(c0);   // where to sample along the third axis\n"
            "float4 main(float2 uv : TEXCOORD0) : COLOR { return tex3D(s1, float3(uv, slice.x)); }";
        ID3DBlob* psVolumeCode = CompileSm3(kPsVolume, "ps_3_0");
        IDirect3DPixelShader9* psVolume = nullptr;
        if (psVolumeCode) dev->CreatePixelShader(static_cast<const DWORD*>(psVolumeCode->GetBufferPointer()), &psVolume);
        Check(psVolume != nullptr, "tex3D pixel shader created");

        auto report = [&](const char* what, int wrong, const std::string& where) {
            std::string detail;
            if (wrong < 0) detail = "no readback";
            else if (wrong > 0) detail = std::to_string(wrong) + " cells differ, first " + where;
            Check(wrong == 0, what, detail);
        };
        std::string where;

        dev->SetVertexShader(vs);
        dev->SetVertexDeclaration(decl);
        dev->SetPixelShader(psVolume);
        dev->SetVertexShaderConstantF(0, zero, 1);
        dev->SetRenderState(D3DRS_ZENABLE, D3DZB_FALSE);
        dev->SetRenderState(D3DRS_CULLMODE, D3DCULL_NONE);

        // --- a managed 4x4x4 volume, filled through LockBox ---
        IDirect3DVolumeTexture9* volume = nullptr;
        const HRESULT madeVolume = dev->CreateVolumeTexture(4, 4, 4, 1, 0, D3DFMT_A8R8G8B8, D3DPOOL_MANAGED, &volume, nullptr);
        Check(SUCCEEDED(madeVolume) && volume, "CreateVolumeTexture (managed)", Hex(madeVolume));
        if (volume && psVolume) {
            D3DLOCKED_BOX box = {};
            const HRESULT lockedAll = volume->LockBox(0, &box, nullptr, 0);
            Check(SUCCEEDED(lockedAll) && box.RowPitch >= 16 && box.SlicePitch >= box.RowPitch * 4,
                  "LockBox reports a row and a slice pitch", std::to_string(box.RowPitch) + " / " + std::to_string(box.SlicePitch));
            D3DLOCKED_BOX second = {};
            Check(volume->LockBox(0, &second, nullptr, 0) == D3DERR_INVALIDCALL, "a locked level cannot be locked again");
            if (SUCCEEDED(lockedAll)) {
                FillVolume(box, 4, Coded);
                volume->UnlockBox(0);
            }
            Check(volume->UnlockBox(0) == D3DERR_INVALIDCALL, "UnlockBox without a lock fails");

            dev->SetTexture(1, volume);
            int wrong = WrongCells(dev, DrawSliceShader, 4, 4, Coded, where);
            report("volume: every texel is sampled at its own coordinates", wrong, where);

            // Halfway between slices 1 and 2 a linear filter blends them: blue 85 and 170 meet at 128.
            dev->SetSamplerState(1, D3DSAMP_MINFILTER, D3DTEXF_LINEAR);
            dev->SetSamplerState(1, D3DSAMP_MAGFILTER, D3DTEXF_LINEAR);
            const bool blended = DrawSliceShader(dev, 0.5f, rb);
            Check(blended && Near(rb.At(8, 8), 0x000080, 6), "volume: linear filtering blends neighbouring slices",
                  blended ? Hex(rb.At(8, 8)) : "no readback");
            dev->SetSamplerState(1, D3DSAMP_MINFILTER, D3DTEXF_POINT);
            dev->SetSamplerState(1, D3DSAMP_MAGFILTER, D3DTEXF_POINT);

            // A box lock edits part of a level: texels 1..2 on every axis go white.
            const D3DBOX inner = { 1, 1, 3, 3, 1, 3 };   // left, top, right, bottom, front, back
            D3DLOCKED_BOX part = {};
            const HRESULT lockedPart = volume->LockBox(0, &part, &inner, 0);
            Check(SUCCEEDED(lockedPart), "LockBox with a box", Hex(lockedPart));
            if (SUCCEEDED(lockedPart)) {
                for (UINT z = 0; z < 2; z++)
                    for (UINT y = 0; y < 2; y++) {
                        auto* row = reinterpret_cast<DWORD*>(static_cast<uint8_t*>(part.pBits) +
                                                             static_cast<size_t>(z) * part.SlicePitch +
                                                             static_cast<size_t>(y) * part.RowPitch);
                        row[0] = row[1] = 0xFFFFFFFF;
                    }
                volume->UnlockBox(0);
            }
            wrong = WrongCells(dev, DrawSliceShader, 4, 4, Whitened, where);
            report("volume: a box lock changes only its own texels", wrong, where);

            // A static level's CPU copy is gone after the upload; locking it again reads the GPU's back.
            D3DLOCKED_BOX again = {};
            DWORD kept = 0, edited = 0;
            if (SUCCEEDED(volume->LockBox(0, &again, nullptr, D3DLOCK_READONLY))) {
                kept = TexelAt(again, 1, 2, 3);
                edited = TexelAt(again, 2, 2, 1);
                volume->UnlockBox(0);
            }
            Check(kept == 0xFF55AAFFu && edited == 0xFFFFFFFFu, "a static volume relocked after upload keeps its texels",
                  Hex(kept) + " / " + Hex(edited));

            // Its CPU copy dropped again, UpdateTexture copies the volume on the GPU.
            IDirect3DVolumeTexture9* duplicate = nullptr;
            dev->CreateVolumeTexture(4, 4, 4, 1, 0, D3DFMT_A8R8G8B8, D3DPOOL_DEFAULT, &duplicate, nullptr);
            if (duplicate) {
                const HRESULT copied = dev->UpdateTexture(volume, duplicate);
                dev->SetTexture(1, duplicate);
                wrong = WrongCells(dev, DrawSliceShader, 4, 4, Whitened, where);
                report("volume: UpdateTexture copies a static volume on the GPU", wrong, where);
                Check(copied == D3D_OK, "UpdateTexture from a static volume", Hex(copied));
                dev->SetTexture(1, volume);
                duplicate->Release();
            }

            // sRGB sampling decodes before the shader sees the texel: 85, 170 and 255 become about 23, 102 and 255.
            dev->SetSamplerState(1, D3DSAMP_SRGBTEXTURE, TRUE);
            const bool decoded = DrawSliceShader(dev, 3.5f / 4, rb);
            Check(decoded && Near(rb.At(24, 40), 0x1767FF, 4), "volume: sRGB sampling decodes the texels",
                  decoded ? Hex(rb.At(24, 40)) : "no readback");
            dev->SetSamplerState(1, D3DSAMP_SRGBTEXTURE, FALSE);

            const D3DBOX outside = { 0, 0, 5, 4, 0, 4 };
            const D3DBOX empty = { 2, 0, 2, 4, 0, 4 };
            Check(volume->LockBox(0, &second, &outside, 0) == D3DERR_INVALIDCALL &&
                      volume->LockBox(0, &second, &empty, 0) == D3DERR_INVALIDCALL,
                  "LockBox refuses a box outside the level or without volume");

            // The resource methods.
            D3DVOLUME_DESC desc = {};
            const HRESULT described = volume->GetLevelDesc(0, &desc);
            Check(SUCCEEDED(described) && desc.Width == 4 && desc.Height == 4 && desc.Depth == 4 &&
                      desc.Format == D3DFMT_A8R8G8B8 && desc.Type == D3DRTYPE_VOLUME && desc.Pool == D3DPOOL_MANAGED,
                  "GetLevelDesc describes the level", Hex(described));
            Check(volume->GetType() == D3DRTYPE_VOLUMETEXTURE && volume->GetLevelCount() == 1 &&
                      volume->GetLevelDesc(1, &desc) == D3DERR_INVALIDCALL,
                  "a volume texture reports its type and level count");
            IDirect3DDevice9* owner = nullptr;
            volume->GetDevice(&owner);
            Check(owner == dev, "GetDevice returns the creating device");
            if (owner) owner->Release();
            Check(volume->SetPriority(7) == 0 && volume->GetPriority() == 7 && volume->SetLOD(1) == 0 && volume->GetLOD() == 1 &&
                      volume->SetAutoGenFilterType(D3DTEXF_POINT) == D3D_OK && volume->GetAutoGenFilterType() == D3DTEXF_POINT &&
                      volume->AddDirtyBox(nullptr) == D3D_OK,
                  "priority, LOD, auto-generation filter and dirty box");
            volume->PreLoad();
            volume->GenerateMipSubLevels();
            volume->SetLOD(0);

            // The level object shares the texture's counts and knows its container.
            IDirect3DVolume9 *level0 = nullptr, *missing = nullptr;
            const HRESULT gotLevel = volume->GetVolumeLevel(0, &level0);
            Check(SUCCEEDED(gotLevel) && level0 && volume->GetVolumeLevel(1, &missing) == D3DERR_INVALIDCALL && !missing,
                  "GetVolumeLevel", Hex(gotLevel));
            if (level0) {
                const ULONG volumeBefore = volume->AddRef() - 1;
                volume->Release();
                level0->AddRef();
                const ULONG volumeAfter = volume->AddRef() - 1;
                volume->Release();
                level0->Release();
                Check(volumeAfter == volumeBefore + 1, "a volume level shares the texture's count",
                      std::to_string(volumeBefore) + " -> " + std::to_string(volumeAfter));

                void* container = nullptr;
                Check(SUCCEEDED(level0->GetContainer(__uuidof(IDirect3DVolumeTexture9), &container)) && container == volume,
                      "a volume level finds its container");
                if (container) static_cast<IUnknown*>(container)->Release();

                D3DVOLUME_DESC levelDesc = {};
                level0->GetDesc(&levelDesc);
                Check(levelDesc.Width == 4 && levelDesc.Depth == 4 && levelDesc.Type == D3DRTYPE_VOLUME,
                      "IDirect3DVolume9::GetDesc");

                // Locking through the level is the same as through the texture.
                D3DLOCKED_BOX viaLevel = {};
                DWORD viaLevelTexel = 0;
                if (SUCCEEDED(level0->LockBox(&viaLevel, nullptr, D3DLOCK_READONLY))) {
                    viaLevelTexel = TexelAt(viaLevel, 1, 2, 3);
                    level0->UnlockBox();
                }
                Check(viaLevelTexel == 0xFF55AAFFu, "IDirect3DVolume9::LockBox", Hex(viaLevelTexel));

                const GUID tag = { 0x6e617469, 0x7672, 0x6139, { 'v', 'o', 'l', 'u', 'm', 'e', '0', '1' } };
                const DWORD stored = 0x1234;
                DWORD loaded = 0, loadedSize = sizeof loaded;
                level0->SetPrivateData(tag, &stored, sizeof stored, 0);
                Check(SUCCEEDED(level0->GetPrivateData(tag, &loaded, &loadedSize)) && loaded == stored,
                      "a volume level keeps private data");
                level0->Release();
            }

            // State blocks and GetTexture hold the volume like any texture.
            IDirect3DStateBlock9* recorded = nullptr;
            dev->BeginStateBlock();
            dev->SetTexture(1, volume);
            dev->EndStateBlock(&recorded);
            dev->SetTexture(1, nullptr);
            recorded->Apply();
            IDirect3DBaseTexture9* bound = nullptr;
            dev->GetTexture(1, &bound);
            Check(bound == volume, "a state block binds a volume texture");
            if (bound) bound->Release();
            recorded->Release();

            // Fixed function samples a volume through a stage, with three texture coordinates.
            dev->SetTexture(1, nullptr);
            dev->SetTexture(0, volume);
            dev->SetVertexShader(nullptr);
            dev->SetPixelShader(nullptr);
            dev->SetFVF(D3DFVF_XYZRHW | D3DFVF_DIFFUSE | D3DFVF_TEX1 | D3DFVF_TEXCOORDSIZE3(0));
            wrong = WrongCells(dev, DrawSliceFixed, 4, 4, Whitened, where);
            report("volume: fixed function samples a volume with (u, v, w)", wrong, where);
            dev->SetTexture(0, nullptr);
            dev->SetVertexShader(vs);
            dev->SetVertexDeclaration(decl);
            dev->SetPixelShader(psVolume);
        }

        // --- a chain of levels: each level is its own volume ---
        IDirect3DVolumeTexture9* chain = nullptr;
        Check(SUCCEEDED(dev->CreateVolumeTexture(4, 4, 4, 0, 0, D3DFMT_A8R8G8B8, D3DPOOL_MANAGED, &chain, nullptr)) && chain &&
                  chain->GetLevelCount() == 3,
              "levels 0 asks for the whole volume chain");
        if (chain && psVolume) {
            D3DVOLUME_DESC last = {};
            chain->GetLevelDesc(2, &last);
            Check(last.Width == 1 && last.Height == 1 && last.Depth == 1, "the last level of a volume chain is 1x1x1");
            D3DLOCKED_BOX top = {}, mid = {};
            if (SUCCEEDED(chain->LockBox(0, &top, nullptr, 0))) { FillVolume(top, 4, Coded); chain->UnlockBox(0); }
            if (SUCCEEDED(chain->LockBox(1, &mid, nullptr, 0))) {
                Check(mid.RowPitch >= 8 && mid.SlicePitch >= 16, "a level's pitches follow its own size",
                      std::to_string(mid.RowPitch) + " / " + std::to_string(mid.SlicePitch));
                FillVolume(mid, 2, Corners);
                chain->UnlockBox(1);
            }
            // Forcing the sampler down to level 1 shows the 2x2x2 level the game filled separately.
            dev->SetTexture(1, chain);
            dev->SetSamplerState(1, D3DSAMP_MIPFILTER, D3DTEXF_POINT);
            dev->SetSamplerState(1, D3DSAMP_MAXMIPLEVEL, 1);
            const int wrong = WrongCells(dev, DrawSliceShader, 2, 2, Corners, where);
            report("volume: each mip level is uploaded to its own level", wrong, where);
            dev->SetSamplerState(1, D3DSAMP_MAXMIPLEVEL, 0);
            dev->SetSamplerState(1, D3DSAMP_MIPFILTER, D3DTEXF_NONE);
            dev->SetTexture(1, nullptr);
        }

        // --- a volume that generates its own mip levels from level 0 ---
        IDirect3DVolumeTexture9* generated = nullptr;
        const HRESULT madeGenerated =
            dev->CreateVolumeTexture(4, 4, 4, 0, D3DUSAGE_AUTOGENMIPMAP, D3DFMT_A8R8G8B8, D3DPOOL_MANAGED, &generated, nullptr);
        Check(SUCCEEDED(madeGenerated) && generated, "CreateVolumeTexture (auto-generated mips)", Hex(madeGenerated));
        if (generated && psVolume) {
            D3DLOCKED_BOX base = {};
            if (SUCCEEDED(generated->LockBox(0, &base, nullptr, 0))) { FillVolume(base, 4, White); generated->UnlockBox(0); }
            // Only level 0 was written; level 1 is white if it was generated from it.
            dev->SetTexture(1, generated);
            dev->SetSamplerState(1, D3DSAMP_MIPFILTER, D3DTEXF_POINT);
            dev->SetSamplerState(1, D3DSAMP_MAXMIPLEVEL, 1);
            const int wrong = WrongCells(dev, DrawSliceShader, 2, 2, White, where);
            report("volume: mip levels are generated from level 0", wrong, where);
            dev->SetSamplerState(1, D3DSAMP_MAXMIPLEVEL, 0);
            dev->SetSamplerState(1, D3DSAMP_MIPFILTER, D3DTEXF_NONE);
            dev->SetTexture(1, nullptr);
        }

        // --- a DEFAULT-pool volume filled by UpdateTexture from system memory ---
        IDirect3DVolumeTexture9 *staged = nullptr, *resident = nullptr;
        dev->CreateVolumeTexture(4, 4, 4, 1, 0, D3DFMT_A8R8G8B8, D3DPOOL_SYSTEMMEM, &staged, nullptr);
        dev->CreateVolumeTexture(4, 4, 4, 1, 0, D3DFMT_A8R8G8B8, D3DPOOL_DEFAULT, &resident, nullptr);
        Check(staged && resident, "system-memory and DEFAULT volumes created");
        if (staged && resident && psVolume) {
            D3DLOCKED_BOX staging = {};
            if (SUCCEEDED(staged->LockBox(0, &staging, nullptr, 0))) { FillVolume(staging, 4, Permuted); staged->UnlockBox(0); }
            const HRESULT updated = dev->UpdateTexture(staged, resident);
            Check(updated == D3D_OK, "UpdateTexture copies a system-memory volume to the GPU", Hex(updated));
            dev->SetTexture(1, resident);
            const int wrong = WrongCells(dev, DrawSliceShader, 4, 4, Permuted, where);
            report("volume: a DEFAULT volume shows what UpdateTexture copied", wrong, where);
            dev->SetTexture(1, nullptr);
            Check(dev->UpdateTexture(staged, tex) == D3DERR_INVALIDCALL && dev->UpdateTexture(tex, resident) == D3DERR_INVALIDCALL,
                  "UpdateTexture does not mix volumes and 2D textures");
        }

        // --- a dynamic volume refilled with D3DLOCK_DISCARD ---
        IDirect3DVolumeTexture9* dynamic = nullptr;
        dev->CreateVolumeTexture(4, 4, 4, 1, D3DUSAGE_DYNAMIC, D3DFMT_A8R8G8B8, D3DPOOL_DEFAULT, &dynamic, nullptr);
        Check(dynamic != nullptr, "dynamic volume created");
        if (dynamic && psVolume) {
            dev->SetTexture(1, dynamic);
            const Coding fills[2] = { Coded, Permuted };
            for (int pass = 0; pass < 2; pass++) {
                D3DLOCKED_BOX frame = {};
                if (SUCCEEDED(dynamic->LockBox(0, &frame, nullptr, D3DLOCK_DISCARD))) {
                    FillVolume(frame, 4, fills[pass]);
                    dynamic->UnlockBox(0);
                }
                const int wrong = WrongCells(dev, DrawSliceShader, 4, 4, fills[pass], where);
                report(pass == 0 ? "volume: a dynamic volume after its first fill" : "volume: a dynamic volume after a DISCARD refill",
                       wrong, where);
            }
            dev->SetTexture(1, nullptr);
        }

        // --- a block-compressed volume: pitches count 4x4 blocks, a slice holds whole block rows ---
        IDirect3DVolumeTexture9* dxt = nullptr;
        const HRESULT madeDxt = dev->CreateVolumeTexture(8, 8, 2, 1, 0, D3DFMT_DXT1, D3DPOOL_MANAGED, &dxt, nullptr);
        Check(SUCCEEDED(madeDxt) && dxt, "CreateVolumeTexture (DXT1)", Hex(madeDxt));
        if (dxt && psVolume) {
            // A BC1 block is two RGB565 endpoints and 2-bit indices; index 0 everywhere shows the first endpoint.
            auto put = [](void* at, uint16_t color) {
                const uint16_t endpoints[2] = { color, 0 };
                std::memcpy(at, endpoints, sizeof endpoints);
                std::memset(static_cast<uint8_t*>(at) + 4, 0, 4);
            };
            D3DLOCKED_BOX whole = {};
            if (SUCCEEDED(dxt->LockBox(0, &whole, nullptr, 0))) {
                Check(whole.RowPitch == 16 && whole.SlicePitch == 32, "a compressed volume's pitches count blocks",
                      std::to_string(whole.RowPitch) + " / " + std::to_string(whole.SlicePitch));
                for (UINT z = 0; z < 2; z++)
                    for (UINT by = 0; by < 2; by++)
                        for (UINT bx = 0; bx < 2; bx++)
                            put(static_cast<uint8_t*>(whole.pBits) + z * whole.SlicePitch + by * whole.RowPitch + bx * 8,
                                z == 0 ? 0xF800 : 0x001F);   // red slice, then blue slice
                dxt->UnlockBox(0);
            }
            // A box on block boundaries edits one block: the lower right of slice 0 turns green.
            const D3DBOX corner = { 4, 4, 8, 8, 0, 1 };
            D3DLOCKED_BOX one = {};
            if (SUCCEEDED(dxt->LockBox(0, &one, &corner, 0))) {
                put(one.pBits, 0x07E0);
                dxt->UnlockBox(0);
            }
            const D3DBOX straddling = { 2, 0, 8, 8, 0, 1 };
            Check(dxt->LockBox(0, &one, &straddling, 0) == D3DERR_INVALIDCALL, "a compressed volume locks whole blocks only");

            dev->SetTexture(1, dxt);
            const int wrong = WrongCells(dev, DrawSliceShader, 2, 2, DxtBlocks, where);
            report("volume: block-compressed slices sample as the blocks say", wrong, where);
            dev->SetTexture(1, nullptr);
        }

        dev->SetPixelShader(nullptr);
        dev->SetVertexShader(nullptr);
        dev->SetVertexDeclaration(nullptr);
        if (dxt) dxt->Release();
        if (dynamic) dynamic->Release();
        if (resident) resident->Release();
        if (staged) staged->Release();
        if (generated) generated->Release();
        if (chain) chain->Release();
        if (volume) volume->Release();
        if (psVolume) psVolume->Release();
        if (psVolumeCode) psVolumeCode->Release();
    }

    // --- reference counting ----------------------------------------------------------------
    const ULONG countBefore = rtTex->AddRef() - 1;
    rtTex->Release();
    rtSurface->AddRef();
    const ULONG countAfter = rtTex->AddRef() - 1;
    rtTex->Release();
    rtSurface->Release();
    Check(countAfter == countBefore + 1, "a texture's surface shares the texture's count",
          std::to_string(countBefore) + " -> " + std::to_string(countAfter));

    // --- CPU copies dropped after upload come back from the GPU ------------------------
    {
        IDirect3DTexture9* still = nullptr;
        Check(SUCCEEDED(dev->CreateTexture(16, 16, 1, 0, D3DFMT_A8R8G8B8, D3DPOOL_MANAGED, &still, nullptr)),
              "static texture created");
        D3DLOCKED_RECT lr = {};
        still->LockRect(0, &lr, nullptr, 0);
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
                reinterpret_cast<uint32_t*>(static_cast<uint8_t*>(lr.pBits) + y * lr.Pitch)[x] = 0xFF000000u | (y << 8) | x;
        still->UnlockRect(0);
        std::memset(&lr, 0, sizeof lr);
        still->LockRect(0, &lr, nullptr, D3DLOCK_READONLY);
        const uint32_t texel = reinterpret_cast<uint32_t*>(static_cast<uint8_t*>(lr.pBits) + 9 * lr.Pitch)[5];
        still->UnlockRect(0);
        Check(texel == 0xFF000905u, "a static texture relocked after upload keeps its texels", Hex(texel));
        still->Release();

        IDirect3DVertexBuffer9* stillVb = nullptr;
        Check(SUCCEEDED(dev->CreateVertexBuffer(256, 0, 0, D3DPOOL_MANAGED, &stillVb, nullptr)), "static vertex buffer created");
        void* data = nullptr;
        stillVb->Lock(0, 0, &data, 0);
        for (int i = 0; i < 64; i++) static_cast<uint32_t*>(data)[i] = 0xC0DE0000u + i;
        stillVb->Unlock();
        data = nullptr;
        stillVb->Lock(64, 16, &data, D3DLOCK_READONLY);
        const uint32_t word = data ? static_cast<uint32_t*>(data)[1] : 0;
        stillVb->Unlock();
        Check(word == 0xC0DE0011u, "a static vertex buffer relocked after upload keeps its data", Hex(word));
        stillVb->Release();
    }

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
