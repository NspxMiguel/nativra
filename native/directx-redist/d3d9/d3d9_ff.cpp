// The D3D9 fixed-function pipeline, as generated SM5 shaders.
//
// Every draw without a vertex or pixel shader builds a small key from the
// state that shapes the pipeline (vertex layout, lighting, fog, texture
// stages), generates HLSL for it once, and caches the compiled result. The
// fixed-function vertex shader writes the same varying slots the shader
// translator uses (texture coordinate set s in slot s, colours in 16/17, fog
// in 20), so it pairs with translated pixel shaders and the reverse, as D3D9
// allows mixing.
//
// Values that change without changing the shape (matrices, material, light
// colours and positions, texture factor, fog colour) live in cbuffer b4.

#include "d3d9_objects.h"

#include <cmath>
#include <sstream>

namespace d3d9 {

namespace {

float AsFloat(DWORD v) { float f; std::memcpy(&f, &v, sizeof f); return f; }

// Mirrors cbuffer NativraFixed below; every member is float4-aligned.
struct FixedConstants {
    float worldView[16];
    float proj[16];
    float normal[16];
    float tex[8][16];
    float matDiffuse[4], matAmbient[4], matSpecular[4], matEmissive[4];
    float matPowerFog[4];          // power, fog start, fog end, fog density
    float globalAmbient[4];
    float lightDiffuse[8][4], lightSpecular[8][4], lightAmbient[8][4];
    float lightPosition[8][4];     // view space, w = range
    float lightDirection[8][4];    // view space, w = falloff
    float lightAtten[8][4];        // att0, att1, att2
    float lightSpot[8][4];         // cos(theta/2), cos(phi/2)
    float viewport[4];             // x, y, width, height
    float depthRange[4];           // minZ, maxZ
    float tfactor[4];
    float stageConstant[8][4];
    float fogColor[4];
};

const char* const kFixedCbuffer = R"(
cbuffer NativraFixed : register(b4)
{
    row_major float4x4 ffWorldView;
    row_major float4x4 ffProj;
    row_major float4x4 ffNormal;
    row_major float4x4 ffTex[8];
    float4 ffMatDiffuse, ffMatAmbient, ffMatSpecular, ffMatEmissive;
    float4 ffMatPowerFog;
    float4 ffGlobalAmbient;
    float4 ffLightDiffuse[8];
    float4 ffLightSpecular[8];
    float4 ffLightAmbient[8];
    float4 ffLightPosition[8];
    float4 ffLightDirection[8];
    float4 ffLightAtten[8];
    float4 ffLightSpot[8];
    float4 ffViewport;
    float4 ffDepthRange;
    float4 ffTFactor;
    float4 ffStageConstant[8];
    float4 ffFogColor;
};
cbuffer NativraFixup : register(b3) { float4 nativra_fix[4]; };
)";

void Multiply(const D3DMATRIX& a, const D3DMATRIX& b, float out[16])
{
    for (int i = 0; i < 4; i++)
        for (int j = 0; j < 4; j++)
            out[i * 4 + j] = a.m[i][0] * b.m[0][j] + a.m[i][1] * b.m[1][j] + a.m[i][2] * b.m[2][j] + a.m[i][3] * b.m[3][j];
}

// Inverse-transpose of a 4x4 (for normals); identity if singular.
void InverseTranspose(const float m[16], float out[16])
{
    float inv[16];
    inv[0] = m[5] * m[10] * m[15] - m[5] * m[11] * m[14] - m[9] * m[6] * m[15] + m[9] * m[7] * m[14] + m[13] * m[6] * m[11] - m[13] * m[7] * m[10];
    inv[4] = -m[4] * m[10] * m[15] + m[4] * m[11] * m[14] + m[8] * m[6] * m[15] - m[8] * m[7] * m[14] - m[12] * m[6] * m[11] + m[12] * m[7] * m[10];
    inv[8] = m[4] * m[9] * m[15] - m[4] * m[11] * m[13] - m[8] * m[5] * m[15] + m[8] * m[7] * m[13] + m[12] * m[5] * m[11] - m[12] * m[7] * m[9];
    inv[12] = -m[4] * m[9] * m[14] + m[4] * m[10] * m[13] + m[8] * m[5] * m[14] - m[8] * m[6] * m[13] - m[12] * m[5] * m[10] + m[12] * m[6] * m[9];
    inv[1] = -m[1] * m[10] * m[15] + m[1] * m[11] * m[14] + m[9] * m[2] * m[15] - m[9] * m[3] * m[14] - m[13] * m[2] * m[11] + m[13] * m[3] * m[10];
    inv[5] = m[0] * m[10] * m[15] - m[0] * m[11] * m[14] - m[8] * m[2] * m[15] + m[8] * m[3] * m[14] + m[12] * m[2] * m[11] - m[12] * m[3] * m[10];
    inv[9] = -m[0] * m[9] * m[15] + m[0] * m[11] * m[13] + m[8] * m[1] * m[15] - m[8] * m[3] * m[13] - m[12] * m[1] * m[11] + m[12] * m[3] * m[9];
    inv[13] = m[0] * m[9] * m[14] - m[0] * m[10] * m[13] - m[8] * m[1] * m[14] + m[8] * m[2] * m[13] + m[12] * m[1] * m[10] - m[12] * m[2] * m[9];
    inv[2] = m[1] * m[6] * m[15] - m[1] * m[7] * m[14] - m[5] * m[2] * m[15] + m[5] * m[3] * m[14] + m[13] * m[2] * m[7] - m[13] * m[3] * m[6];
    inv[6] = -m[0] * m[6] * m[15] + m[0] * m[7] * m[14] + m[4] * m[2] * m[15] - m[4] * m[3] * m[14] - m[12] * m[2] * m[7] + m[12] * m[3] * m[6];
    inv[10] = m[0] * m[5] * m[15] - m[0] * m[7] * m[13] - m[4] * m[1] * m[15] + m[4] * m[3] * m[13] + m[12] * m[1] * m[7] - m[12] * m[3] * m[5];
    inv[14] = -m[0] * m[5] * m[14] + m[0] * m[6] * m[13] + m[4] * m[1] * m[14] - m[4] * m[2] * m[13] - m[12] * m[1] * m[6] + m[12] * m[2] * m[5];
    inv[3] = -m[1] * m[6] * m[11] + m[1] * m[7] * m[10] + m[5] * m[2] * m[11] - m[5] * m[3] * m[10] - m[9] * m[2] * m[7] + m[9] * m[3] * m[6];
    inv[7] = m[0] * m[6] * m[11] - m[0] * m[7] * m[10] - m[4] * m[2] * m[11] + m[4] * m[3] * m[10] + m[8] * m[2] * m[7] - m[8] * m[3] * m[6];
    inv[11] = -m[0] * m[5] * m[11] + m[0] * m[7] * m[9] + m[4] * m[1] * m[11] - m[4] * m[3] * m[9] - m[8] * m[1] * m[7] + m[8] * m[3] * m[5];
    inv[15] = m[0] * m[5] * m[10] - m[0] * m[6] * m[9] - m[4] * m[1] * m[10] + m[4] * m[2] * m[9] + m[8] * m[1] * m[6] - m[8] * m[2] * m[5];
    const float det = m[0] * inv[0] + m[1] * inv[4] + m[2] * inv[8] + m[3] * inv[12];
    if (std::fabs(det) < 1e-20f) {
        for (int k = 0; k < 16; k++) out[k] = (k % 5 == 0) ? 1.0f : 0.0f;
        return;
    }
    for (int i = 0; i < 4; i++)
        for (int j = 0; j < 4; j++) out[i * 4 + j] = inv[j * 4 + i] / det;
}

void Color(const D3DCOLORVALUE& c, float out[4]) { out[0] = c.r; out[1] = c.g; out[2] = c.b; out[3] = c.a; }

void ArgbToFloat(DWORD c, float out[4])
{
    out[0] = ((c >> 16) & 0xFF) / 255.0f;
    out[1] = ((c >> 8) & 0xFF) / 255.0f;
    out[2] = (c & 0xFF) / 255.0f;
    out[3] = ((c >> 24) & 0xFF) / 255.0f;
}

std::string N(int v) { return std::to_string(v); }

// --- vertex stage ------------------------------------------------------------------

struct VsKey {
    uint8_t positionT = 0, positionDims = 3, hasNormal = 0, hasColor0 = 0, hasColor1 = 0;
    uint8_t lighting = 0, localViewer = 0, normalize = 0, colorVertex = 0, specular = 0;
    uint8_t diffuseSrc = 0, ambientSrc = 0, specularSrc = 0, emissiveSrc = 0;
    uint8_t fogEnable = 0, fogVertexMode = 0, fogTableMode = 0, rangeFog = 0;
    uint8_t lightCount = 0;
    uint8_t lightTypes[8] = {};
    uint8_t inputDims[8] = {};           // per texcoord input set, 0 = absent
    uint8_t texSource[8] = {};           // TEXCOORDINDEX low bits per stage
    uint8_t texGen[8] = {};              // TCI mode >> 16 per stage
    uint8_t texTransform[8] = {};        // TTFF count per stage (0 = none)
};

const char* MaterialSource(uint8_t src, const VsKey& k, const char* material)
{
    if (!k.colorVertex) return material;
    if (src == D3DMCS_COLOR1 && k.hasColor0) return "color0";
    if (src == D3DMCS_COLOR2 && k.hasColor1) return "color1";
    return material;
}

std::string VertexHlsl(const VsKey& k)
{
    std::ostringstream s;
    s << "// nativra fixed-function vertex shader\n" << kFixedCbuffer;
    s << "struct NativraIn\n{\n";
    s << "    float4 pos : " << (k.positionT ? "POSITIONT0" : "POSITION0") << ";\n";
    if (k.hasNormal) s << "    float3 normal : NORMAL0;\n";
    if (k.hasColor0) s << "    float4 color0 : COLOR0;\n";
    if (k.hasColor1) s << "    float4 color1 : COLOR1;\n";
    for (int t = 0; t < 8; t++) if (k.inputDims[t]) s << "    float4 tc" << t << " : TEXCOORD" << t << ";\n";
    s << "};\n\nstruct NativraOut\n{\n    float4 pos : SV_Position;\n";
    for (int slot = 0; slot < dxso::VaryingSlots; slot++) s << "    float4 slot" << slot << " : TEXCOORD" << slot << ";\n";
    s << "};\n\n";

    // D3D9 pads a texture coordinate with 1 after its last component before
    // a texture matrix sees it: (u, v) becomes (u, v, 1, 0).
    s << "float4 Pad(float4 tc, int dims)\n{\n"
         "    if (dims == 1) return float4(tc.x, 1.0, 0.0, 0.0);\n"
         "    if (dims == 2) return float4(tc.xy, 1.0, 0.0);\n"
         "    if (dims == 3) return float4(tc.xyz, 1.0);\n"
         "    return tc;\n}\n\n";

    s << "NativraOut main(NativraIn i)\n{\n";
    s << "    NativraOut o = (NativraOut)0;\n";
    s << "    float4 color0 = " << (k.hasColor0 ? "i.color0" : "float4(1.0, 1.0, 1.0, 1.0)") << ";\n";
    s << "    float4 color1 = " << (k.hasColor1 ? "i.color1" : "float4(0.0, 0.0, 0.0, 0.0)") << ";\n";
    s << "    float3 P = float3(0.0, 0.0, 0.0);\n    float3 Nv = float3(0.0, 0.0, 0.0);\n    float depth = 0.0;\n";
    if (k.positionT) {
        // Screen-space vertices bypass transform and the viewport: undo the
        // viewport transform D3D11 will apply.
        s << "    float w = i.pos.w != 0.0 ? 1.0 / i.pos.w : 1.0;\n";
        s << "    float2 ndc = float2((i.pos.x - ffViewport.x) / ffViewport.z * 2.0 - 1.0,\n"
             "                        1.0 - (i.pos.y - ffViewport.y) / ffViewport.w * 2.0);\n";
        s << "    float z = (i.pos.z - ffDepthRange.x) / max(ffDepthRange.y - ffDepthRange.x, 1e-6);\n";
        s << "    o.pos = float4(ndc * w, z * w, w);\n";
        s << "    depth = w;\n";
    } else {
        s << "    float4 world = " << (k.positionDims == 4 ? "i.pos" : "float4(i.pos.xyz, 1.0)") << ";\n";
        s << "    float4 view = mul(world, ffWorldView);\n";
        s << "    P = view.xyz;\n";
        s << "    o.pos = mul(view, ffProj);\n";
        s << "    depth = " << (k.rangeFog ? "length(P)" : "abs(P.z)") << ";\n";
        if (k.hasNormal) {
            s << "    Nv = mul(float4(i.normal, 0.0), ffNormal).xyz;\n";
            if (k.normalize) s << "    Nv = normalize(Nv);\n";
        }
    }

    // Colours.
    s << "    float4 diffuse = color0;\n    float4 specular = color1;\n";
    if (k.lighting && !k.positionT) {
        const std::string md = MaterialSource(k.diffuseSrc, k, "ffMatDiffuse");
        const std::string ma = MaterialSource(k.ambientSrc, k, "ffMatAmbient");
        const std::string ms = MaterialSource(k.specularSrc, k, "ffMatSpecular");
        const std::string me = MaterialSource(k.emissiveSrc, k, "ffMatEmissive");
        s << "    float3 ambientSum = ffGlobalAmbient.rgb;\n";
        s << "    float3 diffuseSum = float3(0.0, 0.0, 0.0);\n";
        s << "    float3 specularSum = float3(0.0, 0.0, 0.0);\n";
        for (int l = 0; l < k.lightCount; l++) {
            const std::string L = N(l);
            s << "    {\n";
            s << "        float3 Ldir; float att = 1.0;\n";
            if (k.lightTypes[l] == D3DLIGHT_DIRECTIONAL) {
                s << "        Ldir = -ffLightDirection[" << L << "].xyz;\n";
            } else {
                s << "        float3 d = ffLightPosition[" << L << "].xyz - P;\n";
                s << "        float dist = length(d);\n";
                s << "        Ldir = d / max(dist, 1e-6);\n";
                s << "        att = dist > ffLightPosition[" << L << "].w ? 0.0 : 1.0 / max(ffLightAtten[" << L << "].x + ffLightAtten["
                  << L << "].y * dist + ffLightAtten[" << L << "].z * dist * dist, 1e-6);\n";
                if (k.lightTypes[l] == D3DLIGHT_SPOT) {
                    s << "        float rho = dot(-Ldir, ffLightDirection[" << L << "].xyz);\n";
                    s << "        float cosTheta = ffLightSpot[" << L << "].x, cosPhi = ffLightSpot[" << L << "].y;\n";
                    s << "        att *= rho > cosTheta ? 1.0 : rho <= cosPhi ? 0.0 : pow(saturate((rho - cosPhi) / max(cosTheta - cosPhi, 1e-6)), ffLightDirection["
                      << L << "].w);\n";
                }
            }
            s << "        ambientSum += att * ffLightAmbient[" << L << "].rgb;\n";
            s << "        float NdotL = dot(Nv, Ldir);\n";
            s << "        if (NdotL > 0.0) {\n";
            s << "            diffuseSum += att * NdotL * ffLightDiffuse[" << L << "].rgb;\n";
            if (k.specular) {
                s << "            float3 H = normalize(Ldir + " << (k.localViewer ? "normalize(-P)" : "float3(0.0, 0.0, -1.0)") << ");\n";
                s << "            float NdotH = dot(Nv, H);\n";
                s << "            if (NdotH > 0.0) specularSum += att * pow(NdotH, ffMatPowerFog.x) * ffLightSpecular[" << L << "].rgb;\n";
            }
            s << "        }\n    }\n";
        }
        s << "    diffuse = float4(" << me << ".rgb + " << ma << ".rgb * ambientSum + " << md << ".rgb * diffuseSum, " << md << ".a);\n";
        s << "    specular = float4(" << ms << ".rgb * specularSum, " << ms << ".a);\n";
    }
    s << "    o.slot" << dxso::VaryingSlot(dxso::UsageColor, 0) << " = saturate(diffuse);\n";
    s << "    o.slot" << dxso::VaryingSlot(dxso::UsageColor, 1) << " = saturate(specular);\n";

    // Texture coordinates, one set per texture stage.
    for (int st = 0; st < 8; st++) {
        const int src = k.texSource[st];
        std::string tc;
        switch (k.texGen[st]) {
        case 1: tc = "float4(Nv, 1.0)"; break;                                              // CAMERASPACENORMAL
        case 2: tc = "float4(P, 1.0)"; break;                                               // CAMERASPACEPOSITION
        case 3: tc = "float4(reflect(normalize(P), Nv), 1.0)"; break;                       // REFLECTIONVECTOR
        case 4: tc = "float4(reflect(normalize(P), Nv).xy * 0.5 + 0.5, 0.0, 1.0)"; break;  // SPHEREMAP (approximate)
        default:
            if (src < 8 && k.inputDims[src]) tc = "Pad(i.tc" + N(src) + ", " + N(k.inputDims[src]) + ")";
            break;
        }
        if (tc.empty()) continue;
        if (k.texTransform[st] && !k.positionT) tc = "mul(" + tc + ", ffTex[" + N(st) + "])";
        s << "    o.slot" << st << " = " << tc << ";\n";
    }

    // Fog: a per-vertex factor (vertex fog, or the specular alpha when no
    // fog formula is set), and the eye depth table fog uses per pixel.
    std::string factor = "1.0";
    if (k.fogEnable && !k.fogTableMode) {
        if (k.fogVertexMode && !k.positionT) {
            switch (k.fogVertexMode) {
            case D3DFOG_LINEAR: factor = "(ffMatPowerFog.z - depth) / max(ffMatPowerFog.z - ffMatPowerFog.y, 1e-6)"; break;
            case D3DFOG_EXP: factor = "exp(-depth * ffMatPowerFog.w)"; break;
            case D3DFOG_EXP2: factor = "exp(-(depth * ffMatPowerFog.w) * (depth * ffMatPowerFog.w))"; break;
            }
        } else {
            factor = "specular.a";
        }
    }
    s << "    o.slot" << dxso::VaryingSlot(dxso::UsageFog, 0) << " = float4(saturate(" << factor << "), depth, 0.0, 0.0);\n";
    s << "    o.pos.xy += nativra_fix[0].xy * o.pos.w;\n";
    s << "    return o;\n}\n";
    return s.str();
}

// --- pixel stage ---------------------------------------------------------------------

struct PsStage {
    uint8_t colorOp = 0, colorArg[3] = {}, alphaOp = 0, alphaArg[3] = {}, result = 0;
    uint8_t texType = 0;       // 0 none, 2 2D, 3 cube, 4 volume
    uint8_t projected = 0;     // component to divide by + 1 (0 = not projected)
};

struct PsKey {
    PsStage stage[8];
    uint8_t specular = 0, fogEnable = 0, fogTableMode = 0;
};

std::string Arg(uint8_t arg, int st)
{
    std::string v;
    switch (arg & D3DTA_SELECTMASK) {
    case D3DTA_DIFFUSE: v = "diffuse"; break;
    case D3DTA_CURRENT: v = "current"; break;
    case D3DTA_TEXTURE: v = "tex" + N(st); break;
    case D3DTA_TFACTOR: v = "ffTFactor"; break;
    case D3DTA_SPECULAR: v = "specular"; break;
    case D3DTA_TEMP: v = "temp"; break;
    case D3DTA_CONSTANT: v = "ffStageConstant[" + N(st) + "]"; break;
    default: v = "current"; break;
    }
    if (arg & D3DTA_ALPHAREPLICATE) v = "(" + v + ").aaaa";
    if (arg & D3DTA_COMPLEMENT) v = "(1.0 - " + v + ")";
    return v;
}

// One texture-stage operation as a float4 expression.
std::string Op(uint8_t op, const std::string& a0, const std::string& a1, const std::string& a2, int st)
{
    const std::string tex = "tex" + N(st);
    switch (op) {
    case D3DTOP_SELECTARG1: return a1;
    case D3DTOP_SELECTARG2: return a2;
    case D3DTOP_MODULATE: return a1 + " * " + a2;
    case D3DTOP_MODULATE2X: return a1 + " * " + a2 + " * 2.0";
    case D3DTOP_MODULATE4X: return a1 + " * " + a2 + " * 4.0";
    case D3DTOP_ADD: return a1 + " + " + a2;
    case D3DTOP_ADDSIGNED: return a1 + " + " + a2 + " - 0.5";
    case D3DTOP_ADDSIGNED2X: return "(" + a1 + " + " + a2 + " - 0.5) * 2.0";
    case D3DTOP_SUBTRACT: return a1 + " - " + a2;
    case D3DTOP_ADDSMOOTH: return a1 + " + " + a2 + " - " + a1 + " * " + a2;
    case D3DTOP_BLENDDIFFUSEALPHA: return "lerp(" + a2 + ", " + a1 + ", diffuse.a)";
    case D3DTOP_BLENDTEXTUREALPHA: return "lerp(" + a2 + ", " + a1 + ", " + tex + ".a)";
    case D3DTOP_BLENDFACTORALPHA: return "lerp(" + a2 + ", " + a1 + ", ffTFactor.a)";
    case D3DTOP_BLENDTEXTUREALPHAPM: return a1 + " + " + a2 + " * (1.0 - " + tex + ".a)";
    case D3DTOP_BLENDCURRENTALPHA: return "lerp(" + a2 + ", " + a1 + ", current.a)";
    case D3DTOP_PREMODULATE: return a1;
    case D3DTOP_MODULATEALPHA_ADDCOLOR: return a1 + " + (" + a1 + ").a * " + a2;
    case D3DTOP_MODULATECOLOR_ADDALPHA: return a1 + " * " + a2 + " + (" + a1 + ").a";
    case D3DTOP_MODULATEINVALPHA_ADDCOLOR: return "(1.0 - (" + a1 + ").a) * " + a2 + " + " + a1;
    case D3DTOP_MODULATEINVCOLOR_ADDALPHA: return "(1.0 - " + a1 + ") * " + a2 + " + (" + a1 + ").a";
    case D3DTOP_DOTPRODUCT3: return "saturate(dot(((" + a1 + ").rgb - 0.5) * 2.0, ((" + a2 + ").rgb - 0.5) * 2.0)).xxxx";
    case D3DTOP_MULTIPLYADD: return a0 + " + " + a1 + " * " + a2;
    case D3DTOP_LERP: return "lerp(" + a2 + ", " + a1 + ", " + a0 + ")";
    default: return a1;   // bump mapping ops: not implemented, keep the stage's first argument
    }
}

std::string PixelHlsl(const PsKey& k)
{
    std::ostringstream s;
    s << "// nativra fixed-function pixel shader\n" << kFixedCbuffer;
    for (int st = 0; st < 8; st++) {
        const uint8_t t = k.stage[st].texType;
        if (!t) continue;
        const char* type = t == 3 ? "TextureCube" : t == 4 ? "Texture3D" : "Texture2D";
        s << type << " t" << st << " : register(t" << st << ");\nSamplerState s" << st << " : register(s" << st << ");\n";
    }
    s << "struct NativraIn\n{\n    float4 pos : SV_Position;\n";
    for (int slot = 0; slot < dxso::VaryingSlots; slot++) s << "    float4 slot" << slot << " : TEXCOORD" << slot << ";\n";
    s << "};\n\n";
    s << "float4 main(NativraIn i) : SV_Target0\n{\n";
    s << "    float4 diffuse = i.slot" << dxso::VaryingSlot(dxso::UsageColor, 0) << ";\n";
    s << "    float4 specular = i.slot" << dxso::VaryingSlot(dxso::UsageColor, 1) << ";\n";
    s << "    float4 current = diffuse;\n    float4 temp = float4(0.0, 0.0, 0.0, 0.0);\n";

    for (int st = 0; st < 8; st++) {
        const PsStage& g = k.stage[st];
        if (g.colorOp == D3DTOP_DISABLE || g.colorOp == 0) break;
        const std::string S = N(st);
        if (g.texType) {
            std::string coord = "i.slot" + S;
            if (g.projected) coord = "(" + coord + " / i.slot" + S + "." + "xyzw"[g.projected - 1] + ")";
            const char* comps = g.texType == 2 ? ".xy" : ".xyz";
            s << "    float4 tex" << S << " = t" << S << ".Sample(s" << S << ", " << coord << comps << ");\n";
        } else {
            // No texture bound: D3D9 reads white, so MODULATE keeps the diffuse colour.
            s << "    float4 tex" << S << " = float4(1.0, 1.0, 1.0, 1.0);\n";
        }
        const std::string color = Op(g.colorOp, Arg(g.colorArg[0], st), Arg(g.colorArg[1], st), Arg(g.colorArg[2], st), st);
        const std::string alpha = (g.alphaOp == D3DTOP_DISABLE || g.alphaOp == 0)
            ? "current"
            : Op(g.alphaOp, Arg(g.alphaArg[0], st), Arg(g.alphaArg[1], st), Arg(g.alphaArg[2], st), st);
        const char* target = (g.result & D3DTA_SELECTMASK) == D3DTA_TEMP ? "temp" : "current";
        s << "    {\n";
        s << "        float4 c = saturate(" << color << ");\n";
        s << "        float4 a = saturate(" << alpha << ");\n";
        s << "        " << target << " = float4(c.rgb, a.a);\n";
        s << "    }\n";
    }
    if (k.specular) s << "    current.rgb = saturate(current.rgb + specular.rgb);\n";
    if (k.fogEnable) {
        const std::string fog = "i.slot" + N(dxso::VaryingSlot(dxso::UsageFog, 0));
        std::string factor = fog + ".x";
        switch (k.fogTableMode) {
        case D3DFOG_LINEAR:
            factor = "(ffMatPowerFog.z - " + fog + ".y) / max(ffMatPowerFog.z - ffMatPowerFog.y, 1e-6)";
            break;
        case D3DFOG_EXP: factor = "exp(-" + fog + ".y * ffMatPowerFog.w)"; break;
        case D3DFOG_EXP2: factor = "exp(-(" + fog + ".y * ffMatPowerFog.w) * (" + fog + ".y * ffMatPowerFog.w))"; break;
        default: break;
        }
        s << "    current.rgb = lerp(ffFogColor.rgb, current.rgb, saturate(" << factor << "));\n";
    }
    // Alpha test, as for translated shaders.
    s << "    int func = (int)nativra_fix[0].x;\n";
    s << "    if (func != 0 && func != 8) {\n";
    s << "        float alphaValue = current.a, alphaRef = nativra_fix[0].y;\n";
    s << "        bool keep = func == 2 ? alphaValue < alphaRef : func == 3 ? alphaValue == alphaRef :\n";
    s << "                    func == 4 ? alphaValue <= alphaRef : func == 5 ? alphaValue > alphaRef :\n";
    s << "                    func == 6 ? alphaValue != alphaRef : func == 7 ? alphaValue >= alphaRef : false;\n";
    s << "        if (!keep) discard;\n";
    s << "    }\n";
    s << "    return current;\n}\n";
    return s.str();
}

template <typename Key>
std::string KeyBytes(const Key& k) { return std::string(reinterpret_cast<const char*>(&k), sizeof k); }

} // namespace

// --- device side ---------------------------------------------------------------------

ShaderVariant* Device::FixedVertexShader(const VertexDeclaration* decl, std::vector<dxso::InputDecl>* inputs)
{
    VsKey k;
    for (const auto& e : decl->elements) {
        switch (e.Usage) {
        case D3DDECLUSAGE_POSITION:
            if (e.UsageIndex == 0) k.positionDims = e.Type == D3DDECLTYPE_FLOAT4 ? 4 : 3;
            break;
        case D3DDECLUSAGE_POSITIONT: k.positionT = 1; break;
        case D3DDECLUSAGE_NORMAL: if (e.UsageIndex == 0) k.hasNormal = 1; break;
        case D3DDECLUSAGE_COLOR:
            if (e.UsageIndex == 0) k.hasColor0 = 1;
            if (e.UsageIndex == 1) k.hasColor1 = 1;
            break;
        case D3DDECLUSAGE_TEXCOORD:
            if (e.UsageIndex < 8)
                k.inputDims[e.UsageIndex] = static_cast<uint8_t>(e.Type <= D3DDECLTYPE_FLOAT4 ? e.Type + 1 : 4);
            break;
        default: break;
        }
    }
    const DWORD* rs = state.rs;
    k.lighting = rs[D3DRS_LIGHTING] ? 1 : 0;
    k.localViewer = rs[D3DRS_LOCALVIEWER] ? 1 : 0;
    k.normalize = rs[D3DRS_NORMALIZENORMALS] ? 1 : 0;
    k.colorVertex = rs[D3DRS_COLORVERTEX] ? 1 : 0;
    k.specular = rs[D3DRS_SPECULARENABLE] ? 1 : 0;
    k.diffuseSrc = static_cast<uint8_t>(rs[D3DRS_DIFFUSEMATERIALSOURCE]);
    k.ambientSrc = static_cast<uint8_t>(rs[D3DRS_AMBIENTMATERIALSOURCE]);
    k.specularSrc = static_cast<uint8_t>(rs[D3DRS_SPECULARMATERIALSOURCE]);
    k.emissiveSrc = static_cast<uint8_t>(rs[D3DRS_EMISSIVEMATERIALSOURCE]);
    k.fogEnable = rs[D3DRS_FOGENABLE] ? 1 : 0;
    k.fogVertexMode = static_cast<uint8_t>(rs[D3DRS_FOGVERTEXMODE]);
    k.fogTableMode = static_cast<uint8_t>(rs[D3DRS_FOGTABLEMODE]);
    k.rangeFog = rs[D3DRS_RANGEFOGENABLE] ? 1 : 0;
    if (k.lighting) {
        for (const auto& l : state.lights) {
            if (!l.second.enabled || k.lightCount >= 8) continue;
            k.lightTypes[k.lightCount++] = static_cast<uint8_t>(l.second.light.Type);
        }
    }
    for (int st = 0; st < 8; st++) {
        const DWORD index = state.tss[st][D3DTSS_TEXCOORDINDEX];
        k.texSource[st] = static_cast<uint8_t>(index & 0xFFFF);
        k.texGen[st] = static_cast<uint8_t>(index >> 16);
        k.texTransform[st] = static_cast<uint8_t>(state.tss[st][D3DTSS_TEXTURETRANSFORMFLAGS] & 0xFF);
    }

    const std::string key = KeyBytes(k);
    auto it = fixedVs.find(key);
    if (it == fixedVs.end()) {
        FixedVertex& fv = fixedVs[key];
        // What the generated shader reads, for the input layout.
        fv.inputs.push_back({ 0, static_cast<uint8_t>(k.positionT ? dxso::UsagePositionT : dxso::UsagePosition), 0 });
        if (k.hasNormal) fv.inputs.push_back({ 0, dxso::UsageNormal, 0 });
        if (k.hasColor0) fv.inputs.push_back({ 0, dxso::UsageColor, 0 });
        if (k.hasColor1) fv.inputs.push_back({ 0, dxso::UsageColor, 1 });
        for (int t = 0; t < 8; t++) if (k.inputDims[t]) fv.inputs.push_back({ 0, dxso::UsageTexCoord, static_cast<uint8_t>(t) });
        fv.variant.bytecode = CompileHlsl(VertexHlsl(k), "vs_5_0");
        if (fv.variant.bytecode) {
            ID3D11VertexShader* vs = nullptr;
            if (SUCCEEDED(dev->CreateVertexShader(fv.variant.bytecode->GetBufferPointer(),
                                                  fv.variant.bytecode->GetBufferSize(), nullptr, &vs)))
                fv.variant.shader = vs;
        }
        it = fixedVs.find(key);
    }
    if (!it->second.variant.shader) return nullptr;
    *inputs = it->second.inputs;
    return &it->second.variant;
}

ShaderVariant* Device::FixedPixelShader()
{
    PsKey k;
    for (int st = 0; st < 8; st++) {
        const DWORD* t = state.tss[st];
        PsStage& g = k.stage[st];
        g.colorOp = static_cast<uint8_t>(t[D3DTSS_COLOROP]);
        if (g.colorOp == D3DTOP_DISABLE) break;
        g.colorArg[0] = static_cast<uint8_t>(t[D3DTSS_COLORARG0]);
        g.colorArg[1] = static_cast<uint8_t>(t[D3DTSS_COLORARG1]);
        g.colorArg[2] = static_cast<uint8_t>(t[D3DTSS_COLORARG2]);
        g.alphaOp = static_cast<uint8_t>(t[D3DTSS_ALPHAOP]);
        g.alphaArg[0] = static_cast<uint8_t>(t[D3DTSS_ALPHAARG0]);
        g.alphaArg[1] = static_cast<uint8_t>(t[D3DTSS_ALPHAARG1]);
        g.alphaArg[2] = static_cast<uint8_t>(t[D3DTSS_ALPHAARG2]);
        g.result = static_cast<uint8_t>(t[D3DTSS_RESULTARG]);
        if (Image* image = ImageOf(state.textures[st])) g.texType = image->faces == 6 ? 3 : 2;
        const DWORD flags = t[D3DTSS_TEXTURETRANSFORMFLAGS];
        if (flags & D3DTTFF_PROJECTED) {
            const DWORD count = flags & 0xFF;
            g.projected = static_cast<uint8_t>(count >= 2 && count <= 4 ? count : 4);
        }
    }
    const DWORD* rs = state.rs;
    k.specular = rs[D3DRS_SPECULARENABLE] ? 1 : 0;
    k.fogEnable = rs[D3DRS_FOGENABLE] ? 1 : 0;
    k.fogTableMode = static_cast<uint8_t>(rs[D3DRS_FOGTABLEMODE]);

    const std::string key = KeyBytes(k);
    auto it = fixedPs.find(key);
    if (it == fixedPs.end()) {
        ShaderVariant& v = fixedPs[key];
        v.bytecode = CompileHlsl(PixelHlsl(k), "ps_5_0");
        if (v.bytecode) {
            ID3D11PixelShader* ps = nullptr;
            if (SUCCEEDED(dev->CreatePixelShader(v.bytecode->GetBufferPointer(), v.bytecode->GetBufferSize(), nullptr, &ps)))
                v.shader = ps;
        }
        it = fixedPs.find(key);
    }
    return it->second.shader ? &it->second : nullptr;
}

void Device::UploadFixedConstants()
{
    if (!cbFixed) {
        D3D11_BUFFER_DESC d = {};
        d.ByteWidth = (sizeof(FixedConstants) + 15) & ~15u;
        d.Usage = D3D11_USAGE_DYNAMIC;
        d.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        d.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
        if (FAILED(dev->CreateBuffer(&d, nullptr, &cbFixed))) return;
    }
    FixedConstants c = {};
    const D3DMATRIX& world = state.transforms[D3DTS_WORLDMATRIX(0)];
    const D3DMATRIX& view = state.transforms[D3DTS_VIEW];
    Multiply(world, view, c.worldView);
    std::memcpy(c.proj, &state.transforms[D3DTS_PROJECTION], sizeof c.proj);
    InverseTranspose(c.worldView, c.normal);
    for (int t = 0; t < 8; t++) std::memcpy(c.tex[t], &state.transforms[D3DTS_TEXTURE0 + t], sizeof c.tex[t]);

    const D3DMATERIAL9& m = state.material;
    Color(m.Diffuse, c.matDiffuse);
    Color(m.Ambient, c.matAmbient);
    Color(m.Specular, c.matSpecular);
    Color(m.Emissive, c.matEmissive);
    c.matPowerFog[0] = m.Power;
    c.matPowerFog[1] = AsFloat(state.rs[D3DRS_FOGSTART]);
    c.matPowerFog[2] = AsFloat(state.rs[D3DRS_FOGEND]);
    c.matPowerFog[3] = AsFloat(state.rs[D3DRS_FOGDENSITY]);
    ArgbToFloat(state.rs[D3DRS_AMBIENT], c.globalAmbient);

    // Lights move into view space here, once, rather than per vertex.
    int n = 0;
    if (state.rs[D3DRS_LIGHTING]) {
        for (const auto& entry : state.lights) {
            if (!entry.second.enabled || n >= 8) continue;
            const D3DLIGHT9& l = entry.second.light;
            Color(l.Diffuse, c.lightDiffuse[n]);
            Color(l.Specular, c.lightSpecular[n]);
            Color(l.Ambient, c.lightAmbient[n]);
            const float px = l.Position.x, py = l.Position.y, pz = l.Position.z;
            c.lightPosition[n][0] = px * view._11 + py * view._21 + pz * view._31 + view._41;
            c.lightPosition[n][1] = px * view._12 + py * view._22 + pz * view._32 + view._42;
            c.lightPosition[n][2] = px * view._13 + py * view._23 + pz * view._33 + view._43;
            c.lightPosition[n][3] = l.Range;
            float dx = l.Direction.x * view._11 + l.Direction.y * view._21 + l.Direction.z * view._31;
            float dy = l.Direction.x * view._12 + l.Direction.y * view._22 + l.Direction.z * view._32;
            float dz = l.Direction.x * view._13 + l.Direction.y * view._23 + l.Direction.z * view._33;
            const float len = std::sqrt(dx * dx + dy * dy + dz * dz);
            if (len > 0) { dx /= len; dy /= len; dz /= len; }
            c.lightDirection[n][0] = dx;
            c.lightDirection[n][1] = dy;
            c.lightDirection[n][2] = dz;
            c.lightDirection[n][3] = l.Falloff;
            c.lightAtten[n][0] = l.Attenuation0;
            c.lightAtten[n][1] = l.Attenuation1;
            c.lightAtten[n][2] = l.Attenuation2;
            c.lightSpot[n][0] = std::cos(l.Theta * 0.5f);
            c.lightSpot[n][1] = std::cos(l.Phi * 0.5f);
            n++;
        }
    }
    c.viewport[0] = static_cast<float>(state.viewport.X);
    c.viewport[1] = static_cast<float>(state.viewport.Y);
    c.viewport[2] = static_cast<float>(state.viewport.Width ? state.viewport.Width : 1);
    c.viewport[3] = static_cast<float>(state.viewport.Height ? state.viewport.Height : 1);
    c.depthRange[0] = state.viewport.MinZ;
    c.depthRange[1] = state.viewport.MaxZ;
    ArgbToFloat(state.rs[D3DRS_TEXTUREFACTOR], c.tfactor);
    for (int st = 0; st < 8; st++) ArgbToFloat(state.tss[st][D3DTSS_CONSTANT], c.stageConstant[st]);
    ArgbToFloat(state.rs[D3DRS_FOGCOLOR], c.fogColor);

    D3D11_MAPPED_SUBRESOURCE mapped;
    if (SUCCEEDED(ctx->Map(cbFixed, 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped))) {
        std::memcpy(mapped.pData, &c, sizeof c);
        ctx->Unmap(cbFixed, 0);
    }
    ctx->VSSetConstantBuffers(4, 1, &cbFixed);
    ctx->PSSetConstantBuffers(4, 1, &cbFixed);
}

} // namespace d3d9
