// dxso: Direct3D 9 shader bytecode (vs_1_1 .. vs_3_0, ps_2_0 .. ps_3_0) to
// Shader Model 5 HLSL.
//
// D3D11 cannot run D3D9 shader bytecode, so every shader a D3D9 game creates
// goes through here and is then compiled by d3dcompiler_47 (which the package
// already carries). The output is plain HLSL rather than DXBC so the one
// compiler that has to be right about DXBC is Microsoft's own.
//
// Everything is portable C++ with no Windows dependency: the translation is
// unit-tested on any host, and the rendering comparison against a direct SM5
// compile runs on the Windows runner under WARP.
//
// Interfaces the device relies on:
//   * constants: cbuffer b0 = float4 c[256], b1 = int4 ci[16], b2 = int4 cb[16]
//     (bool in .x), b3 = float4 nativra_fix[4] (see FixupSlot);
//   * samplers: D3D9 sampler sN -> Texture tN + SamplerState sN (vertex
//     texture samplers keep their 0..3 index in the vertex stage);
//   * vertex inputs keep the D3D9 semantic (POSITION0, TEXCOORD3, ...) so the
//     input layout is built straight from the vertex declaration;
//   * varyings travel in fixed slots (VaryingSlot), declared in full and in the
//     same order in both stages, so any vertex shader links with any pixel
//     shader under D3D11's register-matching rules.

#pragma once

#include <cstdint>
#include <cstddef>
#include <string>
#include <vector>

namespace dxso {

enum class Stage { Vertex, Pixel };

// D3DDECLUSAGE values.
enum Usage : uint8_t {
    UsagePosition = 0, UsageBlendWeight = 1, UsageBlendIndices = 2, UsageNormal = 3,
    UsagePSize = 4, UsageTexCoord = 5, UsageTangent = 6, UsageBinormal = 7,
    UsageTessFactor = 8, UsagePositionT = 9, UsageColor = 10, UsageFog = 11,
    UsageDepth = 12, UsageSample = 13,
};

enum class SamplerType : uint8_t { None = 0, Tex2D = 2, Cube = 3, Volume = 4 };

// Number of varying slots between the stages. With SV_Position (and, in the
// pixel stage, SV_IsFrontFace) this fills the 32 registers SM5 allows.
constexpr int VaryingSlots = 30;

// Layout of the fixup cbuffer (b3), which carries the D3D9 behaviours D3D11
// does not have.
//   vertex: nativra_fix[0].xy = position adjust per unit w (the D3D9 half-pixel
//           offset: (-1/width, +1/height)), 0 to disable.
//   pixel:  nativra_fix[0].x  = alpha test D3DCMPFUNC (0 or 8 = off),
//           nativra_fix[0].y  = alpha reference in [0,1].
enum FixupSlot { FixupPositionAdjust = 0, FixupAlphaTest = 0 };

// The fixed slot a varying semantic travels in, or -1 if it has none.
int VaryingSlot(uint8_t usage, uint8_t index);

// The HLSL semantic name for a D3D9 usage ("TEXCOORD", "COLOR", ...).
const char* UsageName(uint8_t usage);

struct InputDecl {
    uint32_t reg;     // v#
    uint8_t usage;
    uint8_t index;
};

struct Result {
    bool ok = false;
    std::string error;          // why translation failed, when !ok

    Stage stage = Stage::Vertex;
    uint32_t major = 0, minor = 0;
    std::string hlsl;           // SM5 source; entry point "main"
    const char* profile = "";   // "vs_5_0" or "ps_5_0"

    std::vector<InputDecl> inputs;              // vertex: declared inputs
    SamplerType samplers[16] = {};              // by D3D9 sampler index
    uint32_t floatConstantsUsed = 0;            // highest c# + 1 (256 when indexed)
    bool writesDepth = false;
    uint32_t renderTargets = 0;                 // pixel: bit n = writes oCn
};

// Translates a D3D9 shader token stream (starting at the version token).
// `count` is in DWORDs; the stream may be longer than the shader (the END
// token stops it).
Result Translate(const uint32_t* tokens, size_t count);

} // namespace dxso
