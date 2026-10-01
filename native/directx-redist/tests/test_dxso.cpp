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
//
// Pixel shaders 1.x cannot be compiled by d3dcompiler_47, so those cases carry
// the shader as hand-written assembly instead (assembled by ps1_asm.h into the
// token stream a game would pass to CreatePixelShader). The source then holds
// only the equivalent HLSL, which is compiled straight to SM5 as the reference.

#include <windows.h>
#include <d3d11.h>
#include <d3dcompiler.h>

#include <cmath>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

#include "../d3d9/dxso.h"
#include "ps1_asm.h"

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
Texture2D tex1 : register(t1);
Texture2D tex2 : register(t2);
Texture2D tex3 : register(t3);
SamplerState smp1 : register(s1);
SamplerState smp2 : register(s2);
SamplerState smp3 : register(s3);
#define TEX2D1(uv) tex1.Sample(smp1, (uv))
#define TEX2D2(uv) tex2.Sample(smp2, (uv))
#define TEX2D3(uv) tex3.Sample(smp3, (uv))
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
sampler2D smp1 : register(s1);
sampler2D smp2 : register(s2);
sampler2D smp3 : register(s3);
#define TEX2D1(uv) tex2D(smp1, (uv))
#define TEX2D2(uv) tex2D(smp2, (uv))
#define TEX2D3(uv) tex2D(smp3, (uv))
#endif
struct VS_IN { float4 pos : POSITION; float4 uv : TEXCOORD0; };
struct VS_OUT { float4 pos : POSOUT; float4 uv : TEXCOORD0; float4 col : COLOR0; };

// For the ps_1_x cases: the bump-environment state of stage 1 as the renderer
// puts it in the pixel fixup buffer (D3DTSS_BUMPENVMAT00/01/10/11 and
// BUMPENVLSCALE/LOFFSET), and the component-wise selects of cnd and cmp.
static const float4 kBump1 = float4(0.12, 0.05, -0.08, 0.15);
static const float2 kLum1 = float2(0.6, 0.2);
float4 CMP4(float4 c, float4 a, float4 b)
{
    return float4(c.x >= 0.0 ? a.x : b.x, c.y >= 0.0 ? a.y : b.y, c.z >= 0.0 ? a.z : b.z, c.w >= 0.0 ? a.w : b.w);
}
float4 CND4(float4 c, float4 a, float4 b)
{
    return float4(c.x > 0.5 ? a.x : b.x, c.y > 0.5 ? a.y : b.y, c.z > 0.5 ? a.z : b.z, c.w > 0.5 ? a.w : b.w);
}
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

// The vertex shader of the ps_1_x cases: four texture coordinate sets (each with
// a region where some component is negative or q is not 1, so texcoord, texkill
// and projection have something to do) and both colours. PS1_IN is what the
// reference pixel shaders read.
const char* const kPs1Vs = R"(
struct VS1_OUT {
    float4 pos : POSOUT;
    float4 uv0 : TEXCOORD0; float4 uv1 : TEXCOORD1; float4 uv2 : TEXCOORD2; float4 uv3 : TEXCOORD3;
    float4 col : COLOR0; float4 spec : COLOR1;
};
struct PS1_IN {
    PSIN_POS
    float4 uv0 : TEXCOORD0; float4 uv1 : TEXCOORD1; float4 uv2 : TEXCOORD2; float4 uv3 : TEXCOORD3;
    float4 col : COLOR0; float4 spec : COLOR1;
};
VS1_OUT VSMain(VS_IN i)
{
    VS1_OUT o;
    o.pos = i.pos;
    o.uv0 = float4(i.uv.xy, 0.0, 1.0);
    o.uv1 = float4(i.uv.x * 0.8 + 0.1, i.uv.y * 0.9 + 0.05, i.uv.x - i.uv.y, 1.5 - i.uv.x);
    o.uv2 = float4(i.uv.y * 0.9 - 0.2, i.uv.x - 0.1, 0.4, 1.0 + i.uv.y);
    o.uv3 = float4(i.uv.y * 0.7 + 0.2, i.uv.x * 0.7 + 0.2, 0.3 + i.uv.x * 0.2, 0.9);
    o.col = float4(i.uv.xy, 1.0 - i.uv.x, 0.75 - i.uv.y * 0.5);
    o.spec = float4(0.2, i.uv.y, i.uv.x * 0.5, 0.3);
    return o;
}
)";

struct Case {
    const char* name;
    const char* vsProfile;
    const char* psProfile;  // null for a ps_1_x case, whose shader is psAsm
    const char* source;     // defines PS_IN and PSMain (and VSMain unless plainVs); for ps_1_x PSMain only
    bool plainVs;
    const char* psAsm = nullptr;   // ps_1_x assembly (kPs1Vs is the vertex shader then)
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

    // --- pixel shaders 1.1 .. 1.4 ---------------------------------------------------
    // Each is hand-written assembly with the HLSL that computes the same thing (the
    // instruction descriptions of the ps_1_x reference, written out). Stages 0 and 2
    // sample the pattern texture, stages 1 and 3 a second one; c0..c3 are k0..k3 and
    // the bump state of stage 1 is kBump1/kLum1 (see kPrelude).
    { "ps11_modulate", "vs_3_0", nullptr, R"(
float4 PSMain(PS1_IN i) : COLOROUT
{
    return TEX2D(i.uv0.xy) * i.col;
}
)", false, R"asm(
ps_1_1
tex t0
mul r0, t0, v0
)asm" },

    { "ps11_two_stage_arith", "vs_3_0", nullptr, R"(
float4 PSMain(PS1_IN i) : COLOROUT
{
    float4 t0 = TEX2D(i.uv0.xy);
    float4 t1 = TEX2D1(i.uv1.xy);
    float4 r0 = float4(0.0, 0.0, 0.0, 0.0);
    r0.rgb = (((2.0 * t0 - 1.0) * t1 + (i.col - 0.5)) * 2.0).rgb;                 // mad_x2 r0.rgb, t0_bx2, t1, v0_bias
    r0.a = i.spec.a;                                                              // + mov r0.a, v1.a
    float4 r1 = float4(0.0, 0.0, 0.0, 0.0);
    r1.rgb = saturate(i.col.aaaa * t0 + (1.0 - i.col.aaaa) * (1.0 - t1)).rgb;     // lrp_sat r1.rgb, v0.a, t0, 1-t1
    r0.rgb = (r0 * r1).rgb * 0.5;                                                 // mul_d2 r0.rgb, r0, r1
    return r0;
}
)", false, R"asm(
ps_1_1
tex t0
tex t1
mad_x2 r0.rgb, t0_bx2, t1, v0_bias
+ mov r0.a, v1.a
lrp_sat r1.rgb, v0.a, t0, 1-t1
mul_d2 r0.rgb, r0, r1
)asm" },

    // def constants are clamped to [-1,1]; the result is not clamped (the target is float).
    { "ps11_def_clamp", "vs_3_0", nullptr, R"(
float4 PSMain(PS1_IN i) : COLOROUT
{
    float4 c5 = float4(1.0, -1.0, 0.25, 0.75);
    float4 r0 = TEX2D(i.uv0.xy) * c5;
    r0.rgb = (r0 + (k0 - 0.5)).rgb;
    r0.a = r0.a * k2.a;
    return r0;
}
)", false, R"asm(
ps_1_1
def c5, 2.0, -3.0, 0.25, 0.75
tex t0
mul r0, t0, c5
add r0.rgb, r0, c0_bias
+ mul r0.a, r0.a, c2.a
)asm" },

    { "ps11_cnd", "vs_3_0", nullptr, R"(
float4 PSMain(PS1_IN i) : COLOROUT
{
    float4 t0 = TEX2D(i.uv0.xy);
    float4 t1 = TEX2D1(i.uv1.xy);
    float4 r0 = float4(0.0, 0.0, 0.0, 0.0);
    r0.a = t0.a - i.col.a;
    r0.rgb = (r0.a > 0.5 ? t0 : t1).rgb;
    return r0;
}
)", false, R"asm(
ps_1_1
tex t0
tex t1
sub r0.a, t0.a, v0.a
cnd r0.rgb, r0.a, t0, t1
)asm" },

    { "ps12_cmp_dp4", "vs_3_0", nullptr, R"(
float4 PSMain(PS1_IN i) : COLOROUT
{
    float4 t0 = TEX2D(i.uv0.xy);
    float4 t1 = TEX2D1(i.uv1.xy);
    float4 r1 = t0 - k0;
    float4 r0 = CMP4(r1, t0, t1);
    r1 = saturate(dot(r0, k1).xxxx);
    r0.rgb = (r0 * r1).rgb;
    return r0;
}
)", false, R"asm(
ps_1_2
tex t0
tex t1
sub r1, t0, c0
cmp r0, r1, t0, t1
dp4_sat r1, r0, c1
mul r0.rgb, r0, r1
)asm" },

    // texcoord copies the coordinates clamped to [0,1] with alpha 1; texkill kills where
    // any of u, v, w is negative (uv2 is, for part of the quad).
    { "ps11_texcoord_texkill", "vs_3_0", nullptr, R"(
float4 PSMain(PS1_IN i) : COLOROUT
{
    float4 t0 = TEX2D(i.uv0.xy);
    float4 t1 = float4(saturate(i.uv1.xyz), 1.0);
    clip(min(min(i.uv2.x, i.uv2.y), i.uv2.z));
    return t0 * t1;
}
)", false, R"asm(
ps_1_1
tex t0
texcoord t1
texkill t2
mul r0, t0, t1
)asm" },

    // Bump-environment mapping: stage 1's matrix offsets its coordinates by the bump map.
    { "ps11_texbem", "vs_3_0", nullptr, R"(
float4 PSMain(PS1_IN i) : COLOROUT
{
    float4 t0 = TEX2D(i.uv0.xy);
    float2 uv = i.uv1.xy + t0.x * kBump1.xy + t0.y * kBump1.zw;
    return TEX2D1(uv) * i.col;
}
)", false, R"asm(
ps_1_1
tex t0
texbem t1, t0
mul r0, t1, v0
)asm" },

    { "ps11_texbeml", "vs_3_0", nullptr, R"(
float4 PSMain(PS1_IN i) : COLOROUT
{
    float4 t0 = TEX2D(i.uv0.xy);
    float2 uv = i.uv1.xy + t0.x * kBump1.xy + t0.y * kBump1.zw;
    float4 t1 = TEX2D1(uv);
    t1.rgb *= saturate(t0.b * kLum1.x + kLum1.y);
    return t1 * i.col;
}
)", false, R"asm(
ps_1_1
tex t0
texbeml t1, t0
mul r0, t1, v0
)asm" },

    // Dependent reads: the colours of t0 are the coordinates of the next lookups.
    { "ps11_texreg2ar_gb", "vs_3_0", nullptr, R"(
float4 PSMain(PS1_IN i) : COLOROUT
{
    float4 t0 = TEX2D(i.uv0.xy);
    float4 t1 = TEX2D1(t0.ar);
    float4 t2 = TEX2D2(t0.gb);
    float4 r0 = t1 + t2;
    r0.rgb = (r0 * i.col).rgb;
    return r0;
}
)", false, R"asm(
ps_1_1
tex t0
texreg2ar t1, t0
texreg2gb t2, t0
add r0, t1, t2
mul r0.rgb, r0, v0
)asm" },

    { "ps11_texm3x2", "vs_3_0", nullptr, R"(
float4 PSMain(PS1_IN i) : COLOROUT
{
    float4 t0 = TEX2D(i.uv0.xy);
    float3 n = 2.0 * t0.xyz - 1.0;
    float2 uv = float2(dot(i.uv1.xyz, n), dot(i.uv2.xyz, n));
    return TEX2D2(uv) * i.col;
}
)", false, R"asm(
ps_1_1
tex t0
texm3x2pad t1, t0_bx2
texm3x2tex t2, t0_bx2
mul r0, t2, v0
)asm" },

    // ps_1_4: texld takes its stage from the destination register, phase 2 reads what
    // phase 1 computed, and registers keep their values across the phase marker.
    { "ps14_phase_dependent_read", "vs_3_0", nullptr, R"(
float4 PSMain(PS1_IN i) : COLOROUT
{
    float4 r0 = TEX2D(i.uv0.xy);
    float4 r1 = float4(i.uv1.xyz, 0.0);
    r1.rgb = ((2.0 * r0 - 1.0) * k0 + r1).rgb;
    r1 = TEX2D1(r1.xy);
    return r1 * i.col;
}
)", false, R"asm(
ps_1_4
texld r0, t0
texcrd r1.rgb, t1
mad r1.rgb, r0_bx2, c0, r1
phase
texld r1, r1
mul r0, r1, v0
)asm" },

    // _dw divides x and y by the swizzled w; bem adds the matrix times the bump map.
    { "ps14_projective_bem", "vs_3_0", nullptr, R"(
float4 PSMain(PS1_IN i) : COLOROUT
{
    float4 r0 = TEX2D(i.uv0.xy);
    float2 r1 = i.uv1.xy / i.uv1.w;
    r1 = r1 + r0.x * kBump1.xy + r0.y * kBump1.zw;
    return TEX2D1(r1) * i.col * 2.0;
}
)", false, R"asm(
ps_1_4
texld r0, t0
texcrd r1.rg, t1_dw.rga
bem r1.rg, r1, r0
phase
texld r1, r1
mul_x2 r0, r1, v0
)asm" },

    // ps_1_4 constants are clamped to [-8,8]; cnd selects per component.
    { "ps14_cnd_def", "vs_3_0", nullptr, R"(
float4 PSMain(PS1_IN i) : COLOROUT
{
    float4 c7 = float4(4.0, -8.0, 0.5, 0.25);
    float4 r0 = TEX2D(i.uv0.xy);
    float4 r1 = TEX2D1(i.uv1.xy);
    float4 r2 = CND4(r0, r1, c7);
    return r2 + r0;
}
)", false, R"asm(
ps_1_4
def c7, 4.0, -9.0, 0.5, 0.25
texld r0, t0
texld r1, t1
cnd r2, r0, r1, c7
add r0, r2, r0
)asm" },
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

// Translates a D3D9 token stream and compiles the result; fills `hlsl` either way.
ID3DBlob* TranslateTokens(const uint32_t* tokens, size_t count, std::string& hlsl, std::string& error)
{
    const dxso::Result r = dxso::Translate(tokens, count);
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

// The same for the bytecode of a D3DCompile blob.
ID3DBlob* Translate(ID3DBlob* sm3, std::string& hlsl, std::string& error)
{
    return TranslateTokens(static_cast<const uint32_t*>(sm3->GetBufferPointer()), sm3->GetBufferSize() / 4, hlsl, error);
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
    ID3D11ShaderResourceView* texture2 = nullptr;    // stages 1 and 3 of the ps_1_x cases
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

        // b3: the fixup buffer. Slot 0 stays zero (no half-pixel shift, no alpha test); the
        // ps_1_x bump-environment state of stage 1 is kBump1 and kLum1 of the prelude.
        static float fixup[256 * 4] = {};
        const float bumpMatrix[4] = { 0.12f, 0.05f, -0.08f, 0.15f };     // D3DTSS_BUMPENVMAT00, 01, 10, 11
        std::memcpy(&fixup[(dxso::FixupBumpMatrix + 1) * 4], bumpMatrix, sizeof bumpMatrix);
        fixup[(dxso::FixupBumpLuminance + 1) * 4 + 0] = 0.6f;            // D3DTSS_BUMPENVLSCALE
        fixup[(dxso::FixupBumpLuminance + 1) * 4 + 1] = 0.2f;            // D3DTSS_BUMPENVLOFFSET

        bd.ByteWidth = sizeof consts;
        bd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        for (int b = 0; b < 4; b++) {
            D3D11_SUBRESOURCE_DATA data = { b == 0 ? consts : b == 3 ? fixup : zeros, 0, 0 };
            if (FAILED(device->CreateBuffer(&bd, &data, &cbuffers[b]))) return false;
        }

        // Two 16x16 textures with distinct patterns.
        uint32_t texels[16 * 16], texels2[16 * 16];
        for (int y = 0; y < 16; y++) {
            for (int x = 0; x < 16; x++) {
                texels[y * 16 + x] = 0xFF000000u | ((x * 16) << 16) | ((y * 16) << 8) | (((x ^ y) & 1) * 0xFF);
                texels2[y * 16 + x] = ((0x80u + ((x ^ y) & 7) * 16) << 24) | ((y * 16) << 16) | ((((x + y) & 15) * 16) << 8) | ((15 - x) * 16);
            }
        }
        D3D11_TEXTURE2D_DESC tex = {};
        tex.Width = tex.Height = 16;
        tex.MipLevels = tex.ArraySize = 1;
        tex.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        tex.SampleDesc.Count = 1;
        tex.Usage = D3D11_USAGE_DEFAULT;
        tex.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        const uint32_t* const patterns[2] = { texels, texels2 };
        ID3D11ShaderResourceView** const views[2] = { &texture, &texture2 };
        for (int p = 0; p < 2; p++) {
            D3D11_SUBRESOURCE_DATA texData = { patterns[p], 16 * 4, 0 };
            ID3D11Texture2D* t = nullptr;
            if (FAILED(device->CreateTexture2D(&tex, &texData, &t))) return false;
            const HRESULT hr = device->CreateShaderResourceView(t, nullptr, views[p]);
            t->Release();
            if (FAILED(hr)) return false;
        }

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
            // Stages 0 and 2 read the first texture and 1 and 3 the second (ps_1_x cases use all four).
            ID3D11ShaderResourceView* const views[4] = { texture, texture2, texture, texture2 };
            ID3D11SamplerState* const samplers[4] = { sampler, sampler, sampler, sampler };
            context->PSSetShaderResources(0, 4, views);
            context->PSSetSamplers(0, 4, samplers);
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
        const bool ps1 = c.psAsm != nullptr;
        const std::string source = std::string(kPrelude) + (ps1 ? kPs1Vs : c.plainVs ? kPlainVs : "") + c.source;
        std::string error, vsHlsl, psHlsl;

        ID3DBlob* vs3 = Compile(source, "VSMain", c.vsProfile, false, error);
        // d3dcompiler_47 has no ps_1_x target, so a ps_1_x shader is assembled, not compiled.
        ps1asm::Assembled assembled;
        ID3DBlob* ps3 = nullptr;
        if (ps1) {
            assembled = ps1asm::Assemble(c.psAsm);
            if (!assembled.error.empty()) { Report(c.name, false, "assembly: " + assembled.error); continue; }
        } else if (vs3) {
            ps3 = Compile(source, "PSMain", c.psProfile, false, error);
        }
        if (!vs3 || (!ps1 && !ps3)) { Report(c.name, false, "D3D9 compile failed: " + error); continue; }

        ID3DBlob* vsT = Translate(vs3, vsHlsl, error);
        ID3DBlob* psT = nullptr;
        if (vsT) {
            psT = ps1 ? TranslateTokens(assembled.tokens.data(), assembled.tokens.size(), psHlsl, error)
                      : Translate(ps3, psHlsl, error);
        }
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
