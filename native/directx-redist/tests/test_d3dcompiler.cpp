// Tests for the d3dcompiler_43 fallback.
//
// d3dcompiler_43.dll forwards the D3DCompile family to the platform's
// d3dcompiler_47.dll. These checks confirm the forwarder loads and resolves,
// that a representative set of HLSL shaders compile through it to valid DXBC,
// and that the bytecode is identical to what the real compiler produces
// directly (byte for byte, since the forwarder reaches the same function) —
// plus that the container helpers a game uses (D3DGetBlobPart, D3DReflect)
// work on the result.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3dcompiler.h>
#include <d3d11shader.h>
#include <cstdio>
#include <cstring>

static int g_failures = 0;
#define CHECK(cond, msg) do { if (!(cond)) { printf("FAIL: %s\n", msg); ++g_failures; } } while (0)

typedef HRESULT (WINAPI *PFN_D3DCompile)(LPCVOID, SIZE_T, LPCSTR, const D3D_SHADER_MACRO*,
    ID3DInclude*, LPCSTR, LPCSTR, UINT, UINT, ID3DBlob**, ID3DBlob**);
typedef HRESULT (WINAPI *PFN_D3DGetBlobPart)(LPCVOID, SIZE_T, D3D_BLOB_PART, UINT, ID3DBlob**);
typedef HRESULT (WINAPI *PFN_D3DReflect)(LPCVOID, SIZE_T, REFIID, void**);

struct Shader { const char* name; const char* src; const char* entry; const char* target; };

static const Shader kShaders[] =
{
    { "vs4", "float4 main(float3 p:POSITION):SV_Position { return float4(p,1); }", "main", "vs_4_0" },
    { "ps4", "cbuffer C:register(b0){float4 tint;} float4 main():SV_Target { return tint; }", "main", "ps_4_0" },
    { "vs5", "float4 main(uint id:SV_VertexID):SV_Position { return float4(id,0,0,1); }", "main", "vs_5_0" },
    { "ps5", "Texture2D t:register(t0); SamplerState s:register(s0);"
             "float4 main(float2 uv:TEXCOORD):SV_Target { return t.Sample(s,uv); }", "main", "ps_5_0" },
    { "cs5", "RWBuffer<float> o:register(u0);"
             "[numthreads(64,1,1)] void main(uint i:SV_DispatchThreadID){ o[i]=i; }", "main", "cs_5_0" },
};

static bool CompileWith(PFN_D3DCompile fn, const Shader& s, ID3DBlob** out)
{
    ID3DBlob* errors = nullptr;
    HRESULT hr = fn(s.src, strlen(s.src), s.name, nullptr, nullptr, s.entry, s.target,
        D3DCOMPILE_OPTIMIZATION_LEVEL0, 0, out, &errors);
    if (FAILED(hr))
    {
        if (errors) printf("  %s: %.*s\n", s.name, (int)errors->GetBufferSize(), (char*)errors->GetBufferPointer());
    }
    if (errors) errors->Release();
    return SUCCEEDED(hr);
}

int main()
{
    HMODULE lib = LoadLibraryA("d3dcompiler_43.dll");
    CHECK(lib != nullptr, "load d3dcompiler_43.dll forwarder");
    if (!lib) { printf("d3dcompiler_43: cannot load forwarder\n"); return 1; }

    auto compileFwd = (PFN_D3DCompile)GetProcAddress(lib, "D3DCompile");
    auto getPartFwd = (PFN_D3DGetBlobPart)GetProcAddress(lib, "D3DGetBlobPart");
    auto reflectFwd = (PFN_D3DReflect)GetProcAddress(lib, "D3DReflect");
    CHECK(compileFwd && getPartFwd && reflectFwd, "resolve forwarded exports");

    for (const Shader& s : kShaders)
    {
        ID3DBlob* viaForward = nullptr;
        ID3DBlob* viaReal = nullptr;
        bool okF = compileFwd && CompileWith(compileFwd, s, &viaForward);
        bool okR = CompileWith(&D3DCompile, s, &viaReal);   // linked real compiler (47)
        CHECK(okF, s.name);
        CHECK(okR, s.name);
        if (okF && okR)
        {
            // Valid DXBC container.
            CHECK(viaForward->GetBufferSize() >= 4 &&
                  memcmp(viaForward->GetBufferPointer(), "DXBC", 4) == 0, "DXBC fourcc");
            // Identical to the real compiler's output.
            CHECK(viaForward->GetBufferSize() == viaReal->GetBufferSize() &&
                  memcmp(viaForward->GetBufferPointer(), viaReal->GetBufferPointer(),
                         viaForward->GetBufferSize()) == 0, "forwarder matches real compiler");

            // A container helper games use: pull the input signature part out.
            ID3DBlob* part = nullptr;
            HRESULT hr = getPartFwd(viaForward->GetBufferPointer(), viaForward->GetBufferSize(),
                D3D_BLOB_INPUT_SIGNATURE_BLOB, 0, &part);
            if (s.target[0] == 'v')   // vertex shaders carry an input signature
                CHECK(SUCCEEDED(hr) && part && part->GetBufferSize() > 0, "input signature part");
            if (part) part->Release();

            // Reflection resolves and returns a shader description.
            ID3D11ShaderReflection* refl = nullptr;
            hr = reflectFwd(viaForward->GetBufferPointer(), viaForward->GetBufferSize(),
                IID_ID3D11ShaderReflection, (void**)&refl);
            CHECK(SUCCEEDED(hr) && refl, "reflect");
            if (refl)
            {
                D3D11_SHADER_DESC desc = {};
                refl->GetDesc(&desc);
                CHECK(desc.Version != 0, "reflection version");
                refl->Release();
            }
        }
        if (viaForward) viaForward->Release();
        if (viaReal) viaReal->Release();
    }

    FreeLibrary(lib);
    if (g_failures == 0) printf("d3dcompiler_43: all checks passed\n");
    else printf("d3dcompiler_43: %d check(s) failed\n", g_failures);
    return g_failures == 0 ? 0 : 1;
}
