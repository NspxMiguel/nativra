// dxso: Direct3D 9 shader bytecode (vs_1_1 .. vs_3_0, ps_1_1 .. ps_3_0) to
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
//     shader under D3D11's register-matching rules;
//   * ps_1_x (1.1 .. 1.4) texture registers t# read the TEXCOORD# varyings and
//     the colour registers v0/v1 read COLOR0/COLOR1. Those shaders declare no
//     samplers, so a sampler's dimension and the projective divide of `tex`
//     come from device state, which the device hands in through Options (see
//     there); the bump-environment matrices of texbem/texbeml/bem travel in
//     the pixel fixup buffer (see FixupSlot).

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
//   vertex: nativra_fix[0].xy = position adjust per unit w, the D3D9 half-pixel
//           offset. D3D9 puts pixel centres on integers, D3D10+ on halves, so
//           a D3D9 position lands half a pixel right and down in D3D11:
//           (+1/viewport width, -1/viewport height) in NDC. 0 disables it.
//   pixel:  nativra_fix[0].x  = alpha test D3DCMPFUNC (0 or 8 = off),
//           nativra_fix[0].y  = alpha reference in [0,1];
//           for a ps_1_x shader that uses texbem, texbeml or bem (see
//           Result::usesBumpEnv), the bump-environment state of texture stage
//           n = 0 .. BumpStages - 1, as the floats the application set:
//             nativra_fix[FixupBumpMatrix + n] = (D3DTSS_BUMPENVMAT00, 01, 10, 11),
//             nativra_fix[FixupBumpLuminance + n].xy = (D3DTSS_BUMPENVLSCALE,
//                                                       D3DTSS_BUMPENVLOFFSET).
//           A shader declares only the slots it reads (4, or FixupSlots with
//           bump mapping), so a pixel fixup buffer FixupSlots * 16 bytes wide
//           serves every shader.
enum FixupSlot {
    FixupPositionAdjust = 0,
    FixupAlphaTest = 0,
    FixupBumpMatrix = 1,
    FixupBumpLuminance = 7,
    FixupSlots = 13,
};
constexpr int BumpStages = 6;   // ps_1_x has up to 4 (1.1 .. 1.3) or 6 (1.4) texture stages

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

    // ps_1_x only. Those shaders declare no samplers and say nothing about
    // projective texturing; the device supplies both through Options:
    //   bit n of guessedSamplers: stage n is read by `tex`/`texld`/`texreg2rgb`,
    //       and its dimension is Options::samplerTypes[n] (a guess from the
    //       instruction when that is None, recorded in `samplers`);
    //   bit n of projectableSamplers: stage n is read by a ps_1_1 .. ps_1_3
    //       `tex`, which divides the coordinates by the last component when the
    //       stage's D3DTSS_TEXTURETRANSFORMFLAGS carry D3DTTFF_PROJECTED
    //       (Options::projectDivisor[n]);
    //   usesBumpEnv: texbem/texbeml/bem read the bump-environment state from the
    //       pixel fixup buffer (see FixupSlot).
    // A device that does not care gets the defaults (2D textures, no projection).
    uint32_t guessedSamplers = 0;
    uint32_t projectableSamplers = 0;
    bool usesBumpEnv = false;
};

// How the input assembler hands a vertex input to the shader. D3D9 delivers
// UBYTE4, SHORT2 and SHORT4 as integer values in float registers; D3D11 has no
// format that converts that way, so such inputs are fetched as integers and
// converted in the shader.
enum class InputType : uint8_t { Float = 0, SInt = 1, UInt = 2 };

struct Options {
    InputType inputTypes[16] = {};   // by vertex input register v#

    // ps_1_x state (see Result::guessedSamplers and projectableSamplers),
    // indexed by texture stage. A different value is a different translation,
    // so a device keys its compiled variants on all of it.
    SamplerType samplerTypes[8] = {};      // dimension of the bound texture; None = 2D
    uint8_t projectDivisor[8] = {};        // 2 .. 4 = divide by .y, .z or .w (D3DTTFF_COUNTn with
                                           // D3DTTFF_PROJECTED), 0 = no projection
};

// Translates a D3D9 shader token stream (starting at the version token).
// `count` is in DWORDs; the stream may be longer than the shader (the END
// token stops it).
Result Translate(const uint32_t* tokens, size_t count, const Options& options = Options());

} // namespace dxso
