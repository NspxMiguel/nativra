// dxso differential test: D3D9 shader bytecode translated to SM5 must render
// the same image as the same HLSL compiled straight to SM5.
//
// For each case one HLSL source is compiled three ways:
//   * to vs_3_0/ps_3_0 (or 2_0) by Microsoft's compiler — real D3D9 bytecode;
//   * that bytecode through dxso to SM5 HLSL, then compiled — the path under test;
//   * straight to vs_5_0/ps_5_0 — the reference.
// Macros in the shared prelude make both compiles read the same constants
// from the same buffer offsets (register(cN) vs packoffset(cN)) and the same
// texture. With --render the two pipelines draw a full-screen quad under WARP
// into float render targets, and every pixel is compared. Without it (a host
// without WARP) only the translation and the SM5 compile are checked.

#include <windows.h>
#include <d3d11.h>
#include <d3dcompiler.h>

#include <cmath>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

#include "../d3d9/dxso.h"

namespace {

const char* const kPrelude = R"(
#ifdef NATIVRA_SM5
#define PSIN_POS float4 svpos : SV_Position;
#define PSIN_VPOS
#define PSIN_FACE bool isfront : SV_IsFrontFace;
#define POSOUT SV_Position
#define COLOROUT SV_Target
#define VPOS(i) ((i).svpos.xy - 0.5)
#define FACE(i) ((i).isfront ? 1.0 : -1.0)
cbuffer Consts : register(b0)
{
    float4 k0 : packoffset(c0);
    float4 k1 : packoffset(c1);
    float4 k2 : packoffset(c2);
    float4 k3 : packoffset(c3);
    float4x4 m0 : packoffset(c4);
    float4 arr[4] : packoffset(c8);
};
Texture2D tex0 : register(t0);
SamplerState smp0 : register(s0);
#define TEX2D(uv) tex0.Sample(smp0, (uv))
#define TEX2DPROJ(uv4) tex0.Sample(smp0, (uv4).xy / (uv4).w)
#define TEX2DBIAS(uv4) tex0.SampleBias(smp0, (uv4).xy, (uv4).w)
#define TEX2DLOD(uv4) tex0.SampleLevel(smp0, (uv4).xy, (uv4).w)
#else
#define PSIN_POS
#define PSIN_VPOS float2 vpos : VPOS;
#define PSIN_FACE float vface : VFACE;
#define POSOUT POSITION
#define COLOROUT COLOR
#define VPOS(i) ((i).vpos)
#define FACE(i) ((i).vface)
float4 k0 : register(c0);
float4 k1 : register(c1);
float4 k2 : register(c2);
float4 k3 : register(c3);
float4x4 m0 : register(c4);
float4 arr[4] : register(c8);
sampler2D smp0 : register(s0);
#define TEX2D(uv) tex2D(smp0, (uv))
#define TEX2DPROJ(uv4) tex2Dproj(smp0, (uv4))
#define TEX2DBIAS(uv4) tex2Dbias(smp0, (uv4))
#define TEX2DLOD(uv4) tex2Dlod(smp0, (uv4))
#endif
struct VS_IN { float4 pos : POSITION; float4 uv : TEXCOORD0; };
struct VS_OUT { float4 pos : POSOUT; float4 uv : TEXCOORD0; float4 col : COLOR0; };
)";

// The pass-through vertex shader most pixel cases share.
const char* const kPlainVs = R"(
VS_OUT VSMain(VS_IN i)
{
    VS_OUT o;
    o.pos = i.pos;
    o.uv = i.uv;
    o.col = float4(i.uv.xy, 1.0 - i.uv.x, 1.0);
    return o;
}
)";

struct Case {
    const char* name;
    const char* vsProfile;
    const char* psProfile;
    const char* source;     // defines PS_IN and PSMain (and VSMain unless plainVs)
    bool plainVs;
};

const Case kCases[] = {
    { "alu_sm3", "vs_3_0", "ps_3_0", R"(
struct PS_IN { PSIN_POS float4 uv : TEXCOORD0; float4 col : COLOR0; };
float4 PSMain(PS_IN i) : COLOROUT
{
    float2 uv = i.uv.xy;
    float4 a = k0 * uv.xyxy + k1;
    float4 b = frac(uv.xyyx * 3.7);
    float4 c = lerp(a, b, saturate(uv.x));
    float d = dot(normalize(float3(uv, 0.5)), float3(0.3, 0.6, 0.1));
    float e = pow(saturate(uv.y) + 0.1, 2.2);
    float f = exp2(uv.x) - log2(uv.y + 1.0);
    float g = rsqrt(uv.x + 0.5) + 1.0 / (uv.y + 0.5);
    float h = (uv.x > 0.5) ? 0.25 : 0.75;
    float4 s = float4(sin(uv.x * 6.0), cos(uv.y * 6.0), step(0.3, uv.x), abs(uv.x - uv.y));
    return c * 0.25 + float4(d, e, f, g) * 0.1 + h * 0.1 + s * 0.1
         + min(a, b) * 0.05 + max(a, b) * 0.05 + i.col * 0.1;
}
)", true },

    { "alu_sm2", "vs_2_0", "ps_2_0", R"(
struct PS_IN { PSIN_POS float4 uv : TEXCOORD0; float4 col : COLOR0; };
float4 PSMain(PS_IN i) : COLOROUT
{
    float2 uv = i.uv.xy;
    float4 a = k0 * uv.xyxy + k1;
    float4 b = frac(uv.xyyx * 3.7);
    float h = (uv.x > 0.5) ? 0.25 : 0.75;
    return lerp(a, b, uv.x) * 0.5 + h * 0.2 + i.col * 0.3;
}
)", true },

    { "texture_sm3", "vs_3_0", "ps_3_0", R"(
struct PS_IN { PSIN_POS float4 uv : TEXCOORD0; float4 col : COLOR0; };
float4 PSMain(PS_IN i) : COLOROUT
{
    float4 t = TEX2D(i.uv.xy);
    float4 p = TEX2DPROJ(float4(i.uv.xy * 2.0, 0.0, 2.0));
    float4 b = TEX2DBIAS(float4(i.uv.xy, 0.0, 0.5));
    return t * 0.5 + p * 0.25 + b * 0.25 * k2;
}
)", true },

    { "texture_sm2", "vs_2_0", "ps_2_0", R"(
struct PS_IN { PSIN_POS float4 uv : TEXCOORD0; float4 col : COLOR0; };
float4 PSMain(PS_IN i) : COLOROUT
{
    return TEX2D(i.uv.xy) * i.col + k2 * 0.1;
}
)", true },

    { "flow_sm3", "vs_3_0", "ps_3_0", R"(
struct PS_IN { PSIN_POS float4 uv : TEXCOORD0; float4 col : COLOR0; };
float4 PSMain(PS_IN i) : COLOROUT
{
    float4 acc = 0;
    [loop] for (int n = 0; n < 4; n++)
        acc += arr[n] * (i.uv.x + n * 0.1);
    if (i.uv.y > 0.5) acc *= 0.5; else acc += 0.25;
    if (k3.x > 0.0) acc.x = 1.0 - acc.x;
    return acc;
}
)", true },

    { "texkill_sm3", "vs_3_0", "ps_3_0", R"(
struct PS_IN { PSIN_POS float4 uv : TEXCOORD0; float4 col : COLOR0; };
float4 PSMain(PS_IN i) : COLOROUT
{
    clip(i.uv.x - 0.3);
    clip(0.8 - i.uv.y);
    return float4(i.uv.xy, 0.5, 1.0);
}
)", true },

    { "vpos_vface_sm3", "vs_3_0", "ps_3_0", R"(
struct PS_IN { PSIN_POS PSIN_VPOS float4 uv : TEXCOORD0; float4 col : COLOR0; PSIN_FACE };
float4 PSMain(PS_IN i) : COLOROUT
{
    return float4(frac(VPOS(i) / 8.0), FACE(i) * 0.25 + 0.5, 1.0);
}
)", true },

    { "vs_relative_sm3", "vs_3_0", "ps_3_0", R"(
VS_OUT VSMain(VS_IN i)
{
    VS_OUT o;
    o.pos = mul(i.pos, m0);
    o.uv = i.uv;
    int idx = (int)i.uv.z;
    o.col = arr[idx] + lit(dot(i.uv.xy, float2(0.5, 0.5)), 0.3, 8.0) * 0.1;
    return o;
}
struct PS_IN { PSIN_POS float4 uv : TEXCOORD0; float4 col : COLOR0; };
float4 PSMain(PS_IN i) : COLOROUT
{
    return i.col;
}
)", false },

    { "vs_relative_sm2", "vs_2_0", "ps_2_0", R"(
VS_OUT VSMain(VS_IN i)
{
    VS_OUT o;
    o.pos = mul(i.pos, m0);
    o.uv = i.uv;
    int idx = (int)i.uv.z;
    o.col = saturate(arr[idx] * 0.5 + i.uv.xyxy * 0.25);
    return o;
}
struct PS_IN { PSIN_POS float4 uv : TEXCOORD0; float4 col : COLOR0; };
float4 PSMain(PS_IN i) : COLOROUT
{
    return i.col;
}
)", false },
};

int failures = 0;

void Report(const char* name, bool ok, const std::string& detail = "")
{
    std::printf("%s %s%s%s\n", ok ? "PASS" : "FAIL", name, detail.empty() ? "" : ": ", detail.c_str());
    if (!ok) failures++;
}

ID3DBlob* Compile(const std::string& source, const char* entry, const char* profile, bool sm5, std::string& error)
{
    const D3D_SHADER_MACRO sm5Macros[] = { { "NATIVRA_SM5", "1" }, { nullptr, nullptr } };
    ID3DBlob* code = nullptr;
    ID3DBlob* errors = nullptr;
    const HRESULT hr = D3DCompile(source.data(), source.size(), "case", sm5 ? sm5Macros : nullptr, nullptr,
                                  entry, profile, D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &code, &errors);
    if (FAILED(hr)) {
        char buf[32];
        std::snprintf(buf, sizeof buf, "hr=0x%08lX ", static_cast<unsigned long>(hr));
        error = buf + std::string(errors ? static_cast<const char*>(errors->GetBufferPointer()) : "");
        if (code) code->Release();
        code = nullptr;
    }
    if (errors) errors->Release();
    return code;
}

// Translates D3D9 bytecode and compiles the result; fills `hlsl` either way.
ID3DBlob* Translate(ID3DBlob* sm3, std::string& hlsl, std::string& error)
{
    const auto* tokens = static_cast<const uint32_t*>(sm3->GetBufferPointer());
    const dxso::Result r = dxso::Translate(tokens, sm3->GetBufferSize() / 4);
    hlsl = r.hlsl;
    if (!r.ok) {
        error = "dxso: " + r.error;
        return nullptr;
    }
    ID3DBlob* code = nullptr;
    ID3DBlob* errors = nullptr;
    const HRESULT hr = D3DCompile(r.hlsl.data(), r.hlsl.size(), "dxso", nullptr, nullptr, "main", r.profile,
                                  D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &code, &errors);
    if (FAILED(hr)) {
        error = "SM5 compile of the translation failed: " +
                std::string(errors ? static_cast<const char*>(errors->GetBufferPointer()) : "");
        if (code) code->Release();
        code = nullptr;
    }
    if (errors) errors->Release();
    return code;
}

// --- rendering ---------------------------------------------------------------

constexpr int kSize = 64;

struct Renderer {
    ID3D11Device* device = nullptr;
    ID3D11DeviceContext* context = nullptr;
    ID3D11Texture2D* target = nullptr;
    ID3D11RenderTargetView* rtv = nullptr;
    ID3D11Texture2D* staging = nullptr;
    ID3D11Buffer* vertices = nullptr;
    ID3D11Buffer* cbuffers[4] = {};
    ID3D11ShaderResourceView* texture = nullptr;
    ID3D11SamplerState* sampler = nullptr;
    ID3D11RasterizerState* raster = nullptr;

    bool Init()
    {
        const D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_0 };
        if (FAILED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, 0, levels, 1, D3D11_SDK_VERSION,
                                     &device, nullptr, &context)))
            return false;

        D3D11_TEXTURE2D_DESC td = {};
        td.Width = td.Height = kSize;
        td.MipLevels = td.ArraySize = 1;
        td.Format = DXGI_FORMAT_R32G32B32A32_FLOAT;
        td.SampleDesc.Count = 1;
        td.Usage = D3D11_USAGE_DEFAULT;
        td.BindFlags = D3D11_BIND_RENDER_TARGET;
        if (FAILED(device->CreateTexture2D(&td, nullptr, &target))) return false;
        if (FAILED(device->CreateRenderTargetView(target, nullptr, &rtv))) return false;
        td.Usage = D3D11_USAGE_STAGING;
        td.BindFlags = 0;
        td.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        if (FAILED(device->CreateTexture2D(&td, nullptr, &staging))) return false;

        // Full-screen quad as a clockwise strip; uv.z is the vertex index.
        const float quad[] = {
            -1, -1, 0, 1,   0, 1, 0, 1,
            -1,  1, 0, 1,   0, 0, 1, 1,
             1, -1, 0, 1,   1, 1, 2, 1,
             1,  1, 0, 1,   1, 0, 3, 1,
        };
        D3D11_BUFFER_DESC bd = {};
        bd.ByteWidth = sizeof quad;
        bd.Usage = D3D11_USAGE_DEFAULT;
        bd.BindFlags = D3D11_BIND_VERTEX_BUFFER;
        D3D11_SUBRESOURCE_DATA init = { quad, 0, 0 };
        if (FAILED(device->CreateBuffer(&bd, &init, &vertices))) return false;

        // b0: the float constants both pipelines read (c0..c11 used).
        static float consts[256 * 4] = {};
        const float k[] = {
            0.8f, 0.3f, 0.5f, 1.0f,     // k0
            0.1f, 0.2f, 0.05f, 0.0f,    // k1
            0.9f, 0.7f, 0.4f, 1.0f,     // k2
            1.0f, 0.0f, 0.0f, 0.0f,     // k3
            1, 0, 0, 0,  0, 1, 0, 0,  0, 0, 1, 0,  0, 0, 0, 1,   // m0 (c4..c7): identity
            1.0f, 0.2f, 0.1f, 1.0f,     // arr[0] (c8)
            0.2f, 0.9f, 0.3f, 1.0f,     // arr[1]
            0.1f, 0.3f, 0.8f, 1.0f,     // arr[2]
            0.6f, 0.6f, 0.2f, 1.0f,     // arr[3]
        };
        std::memcpy(consts, k, sizeof k);
        static float zeros[256 * 4] = {};
        bd.ByteWidth = sizeof consts;
        bd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        for (int b = 0; b < 4; b++) {
            D3D11_SUBRESOURCE_DATA data = { b == 0 ? consts : zeros, 0, 0 };
            if (FAILED(device->CreateBuffer(&bd, &data, &cbuffers[b]))) return false;
        }

        // A 16x16 texture with a distinct pattern.
        uint32_t texels[16 * 16];
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
                texels[y * 16 + x] = 0xFF000000u | ((x * 16) << 16) | ((y * 16) << 8) | (((x ^ y) & 1) * 0xFF);
        D3D11_TEXTURE2D_DESC tex = {};
        tex.Width = tex.Height = 16;
        tex.MipLevels = tex.ArraySize = 1;
        tex.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        tex.SampleDesc.Count = 1;
        tex.Usage = D3D11_USAGE_DEFAULT;
        tex.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        D3D11_SUBRESOURCE_DATA texData = { texels, 16 * 4, 0 };
        ID3D11Texture2D* t = nullptr;
        if (FAILED(device->CreateTexture2D(&tex, &texData, &t))) return false;
        const HRESULT hr = device->CreateShaderResourceView(t, nullptr, &texture);
        t->Release();
        if (FAILED(hr)) return false;

        D3D11_SAMPLER_DESC sd = {};
        sd.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
        sd.AddressU = sd.AddressV = sd.AddressW = D3D11_TEXTURE_ADDRESS_WRAP;
        sd.MaxLOD = D3D11_FLOAT32_MAX;
        if (FAILED(device->CreateSamplerState(&sd, &sampler))) return false;

        D3D11_RASTERIZER_DESC rd = {};
        rd.FillMode = D3D11_FILL_SOLID;
        rd.CullMode = D3D11_CULL_NONE;
        rd.DepthClipEnable = TRUE;
        return SUCCEEDED(device->CreateRasterizerState(&rd, &raster));
    }

    // Draws the quad with the given shader pair; returns kSize*kSize*4 floats.
    bool Draw(ID3DBlob* vsCode, ID3DBlob* psCode, std::vector<float>& pixels, std::string& error)
    {
        ID3D11VertexShader* vs = nullptr;
        ID3D11PixelShader* ps = nullptr;
        ID3D11InputLayout* layout = nullptr;
        bool ok = false;
        do {
            if (FAILED(device->CreateVertexShader(vsCode->GetBufferPointer(), vsCode->GetBufferSize(), nullptr, &vs))) {
                error = "CreateVertexShader failed"; break;
            }
            if (FAILED(device->CreatePixelShader(psCode->GetBufferPointer(), psCode->GetBufferSize(), nullptr, &ps))) {
                error = "CreatePixelShader failed"; break;
            }
            const D3D11_INPUT_ELEMENT_DESC elements[] = {
                { "POSITION", 0, DXGI_FORMAT_R32G32B32A32_FLOAT, 0, 0, D3D11_INPUT_PER_VERTEX_DATA, 0 },
                { "TEXCOORD", 0, DXGI_FORMAT_R32G32B32A32_FLOAT, 0, 16, D3D11_INPUT_PER_VERTEX_DATA, 0 },
            };
            if (FAILED(device->CreateInputLayout(elements, 2, vsCode->GetBufferPointer(), vsCode->GetBufferSize(), &layout))) {
                error = "CreateInputLayout failed"; break;
            }

            const float clear[] = { 0.1f, 0.2f, 0.3f, 0.4f };
            context->ClearRenderTargetView(rtv, clear);
            context->OMSetRenderTargets(1, &rtv, nullptr);
            D3D11_VIEWPORT vp = { 0, 0, kSize, kSize, 0, 1 };
            context->RSSetViewports(1, &vp);
            context->RSSetState(raster);
            const UINT stride = 32, offset = 0;
            context->IASetVertexBuffers(0, 1, &vertices, &stride, &offset);
            context->IASetInputLayout(layout);
            context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
            context->VSSetShader(vs, nullptr, 0);
            context->PSSetShader(ps, nullptr, 0);
            context->VSSetConstantBuffers(0, 4, cbuffers);
            context->PSSetConstantBuffers(0, 4, cbuffers);
            context->PSSetShaderResources(0, 1, &texture);
            context->PSSetSamplers(0, 1, &sampler);
            context->VSSetShaderResources(0, 1, &texture);
            context->VSSetSamplers(0, 1, &sampler);
            context->Draw(4, 0);
            context->CopyResource(staging, target);

            D3D11_MAPPED_SUBRESOURCE mapped;
            if (FAILED(context->Map(staging, 0, D3D11_MAP_READ, 0, &mapped))) { error = "Map failed"; break; }
            pixels.resize(kSize * kSize * 4);
            for (int y = 0; y < kSize; y++)
                std::memcpy(&pixels[y * kSize * 4], static_cast<const char*>(mapped.pData) + y * mapped.RowPitch, kSize * 16);
            context->Unmap(staging, 0);
            ok = true;
        } while (false);
        if (layout) layout->Release();
        if (ps) ps->Release();
        if (vs) vs->Release();
        return ok;
    }
};

} // namespace

int main(int argc, char** argv)
{
    bool render = false, dump = false;
    for (int a = 1; a < argc; a++) {
        if (std::strcmp(argv[a], "--render") == 0) render = true;
        if (std::strcmp(argv[a], "--dump") == 0) dump = true;
    }
    Renderer renderer;
    if (render && !renderer.Init()) {
        std::printf("FAIL could not create a WARP device\n");
        return 1;
    }

    for (const Case& c : kCases) {
        const std::string source = std::string(kPrelude) + (c.plainVs ? kPlainVs : "") + c.source;
        std::string error, vsHlsl, psHlsl;

        ID3DBlob* vs3 = Compile(source, "VSMain", c.vsProfile, false, error);
        ID3DBlob* ps3 = vs3 ? Compile(source, "PSMain", c.psProfile, false, error) : nullptr;
        if (!vs3 || !ps3) { Report(c.name, false, "D3D9 compile failed: " + error); continue; }

        ID3DBlob* vsT = Translate(vs3, vsHlsl, error);
        ID3DBlob* psT = vsT ? Translate(ps3, psHlsl, error) : nullptr;
        if (!vsT || !psT) {
            Report(c.name, false, error);
            std::printf("----- vertex translation -----\n%s\n----- pixel translation -----\n%s\n",
                        vsHlsl.c_str(), psHlsl.c_str());
            continue;
        }
        if (dump) {
            std::printf("----- %s: vertex -----\n%s\n----- %s: pixel -----\n%s\n",
                        c.name, vsHlsl.c_str(), c.name, psHlsl.c_str());
        }
        if (!render) { Report(c.name, true, "translated and compiled"); continue; }

        ID3DBlob* vs5 = Compile(source, "VSMain", "vs_5_0", true, error);
        ID3DBlob* ps5 = vs5 ? Compile(source, "PSMain", "ps_5_0", true, error) : nullptr;
        if (!vs5 || !ps5) { Report(c.name, false, "reference SM5 compile failed: " + error); continue; }

        std::vector<float> expected, actual;
        if (!renderer.Draw(vs5, ps5, expected, error) || !renderer.Draw(vsT, psT, actual, error)) {
            Report(c.name, false, "draw failed: " + error);
            std::printf("----- vertex translation -----\n%s\n----- pixel translation -----\n%s\n",
                        vsHlsl.c_str(), psHlsl.c_str());
            continue;
        }

        double worst = 0;
        int worstIndex = 0;
        for (size_t k = 0; k < expected.size(); k++) {
            const double d = std::fabs(static_cast<double>(expected[k]) - actual[k]);
            if (d > worst || std::isnan(d)) { worst = std::isnan(d) ? 1e9 : d; worstIndex = static_cast<int>(k); }
        }
        char detail[160];
        const int px = worstIndex / 4;
        std::snprintf(detail, sizeof detail, "max |diff| %.6f at (%d,%d).%c: reference %.6f, translated %.6f",
                      worst, px % kSize, px / kSize, "rgba"[worstIndex % 4], expected[worstIndex], actual[worstIndex]);
        const bool ok = worst < 2e-3;
        Report(c.name, ok, detail);
        if (!ok) {
            std::printf("----- vertex translation -----\n%s\n----- pixel translation -----\n%s\n",
                        vsHlsl.c_str(), psHlsl.c_str());
        }
    }

    std::printf("%s: %d failure(s)\n", failures ? "FAILED" : "OK", failures);
    return failures ? 1 : 0;
}
