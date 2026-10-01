// dxso, pixel shader 1.1 .. 1.4: translation tests that need no Windows and no
// GPU. The shaders are hand-written assembly (ps1_asm.h encodes them into the
// token stream a game passes to CreatePixelShader); each is translated and the
// HLSL is checked for the exact computation the instruction calls for, for the
// state the shader hands the device (Result), and for the precise errors on
// shaders it cannot translate. test_dxso.cpp renders the same kind of shader
// under WARP and compares it with equivalent HLSL; this one runs anywhere:
//
//   c++ -std=c++17 -I../d3d9 test_dxso_ps1.cpp ../d3d9/dxso.cpp -o test_dxso_ps1

#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

#include "../d3d9/dxso.h"
#include "ps1_asm.h"

namespace {

int failures = 0;

// Details are for failures: what the translator produced instead.
void Report(const std::string& name, bool ok, const std::string& detail = "")
{
    const bool showDetail = !ok && !detail.empty();
    std::printf("%s %s%s%s\n", ok ? "PASS" : "FAIL", name.c_str(), showDetail ? ": " : "", showDetail ? detail.c_str() : "");
    if (!ok) failures++;
}

bool Has(const std::string& text, const std::string& fragment)
{
    return text.find(fragment) != std::string::npos;
}

// The body of nativra_main(), the part a failing test needs to see.
std::string MainBody(const std::string& hlsl)
{
    const size_t begin = hlsl.find("void nativra_main()");
    const size_t end = hlsl.find("\n}\n", begin);
    return begin == std::string::npos || end == std::string::npos ? hlsl : hlsl.substr(begin, end - begin);
}

dxso::Result Translate(const char* assembly, const dxso::Options& options = dxso::Options())
{
    const ps1asm::Assembled a = ps1asm::Assemble(assembly);
    if (!a.error.empty()) {
        dxso::Result r;
        r.error = "test assembly: " + a.error;
        return r;
    }
    return dxso::Translate(a.tokens.data(), a.tokens.size(), options);
}

// Translation must succeed and contain every fragment (and none of `forbidden`).
void Expect(const char* name, const char* assembly, const std::vector<std::string>& fragments,
            const std::vector<std::string>& forbidden = {}, const dxso::Options& options = dxso::Options())
{
    const dxso::Result r = Translate(assembly, options);
    if (!r.ok) {
        Report(name, false, "translation failed: " + r.error);
        return;
    }
    for (const std::string& f : fragments) {
        if (!Has(r.hlsl, f)) {
            Report(name, false, "missing `" + f + "` in\n" + MainBody(r.hlsl));
            return;
        }
    }
    for (const std::string& f : forbidden) {
        if (Has(r.hlsl, f)) {
            Report(name, false, "unexpected `" + f + "` in\n" + MainBody(r.hlsl));
            return;
        }
    }
    Report(name, true);
}

// Translation must fail with exactly this error.
void ExpectError(const char* name, const std::vector<uint32_t>& tokens, const std::string& error)
{
    const dxso::Result r = dxso::Translate(tokens.data(), tokens.size());
    Report(name, !r.ok && r.error == error, r.ok ? "translated" : "error was `" + r.error + "`");
}

void ExpectAssemblyError(const char* name, const char* assembly, const std::string& error)
{
    const dxso::Result r = Translate(assembly);
    Report(name, !r.ok && r.error == error, r.ok ? "translated" : "error was `" + r.error + "`");
}

std::string Slot(uint8_t usage, uint8_t index)
{
    return "input.slot" + std::to_string(dxso::VaryingSlot(usage, index));
}

} // namespace

int main()
{
    // --- the shader as a whole ---------------------------------------------------
    {
        const dxso::Result r = Translate("ps_1_1\ntex t0\nmul r0, t0, v0");
        Report("ps_1_1 translates to a ps_5_0 pixel shader",
               r.ok && r.stage == dxso::Stage::Pixel && r.major == 1 && r.minor == 1 && std::string(r.profile) == "ps_5_0",
               r.error);
        Report("tex t0 samples stage 0 at the coordinates of t0",
               Has(r.hlsl, "t[0] = (t0.Sample(s0, (t[0]).xy));"), MainBody(r.hlsl));
        Report("arithmetic reads the sampled colour from t0", Has(r.hlsl, "r0 = (t[0] * v[0]);"), MainBody(r.hlsl));
        Report("the colour result is r0", Has(r.hlsl, "oC[0] = r0;"), MainBody(r.hlsl));
        Report("v0/v1 are COLOR0/COLOR1 and t0..t3 are TEXCOORD0..3",
               Has(r.hlsl, "v[0] = " + Slot(dxso::UsageColor, 0) + ";") && Has(r.hlsl, "v[1] = " + Slot(dxso::UsageColor, 1) + ";") &&
               Has(r.hlsl, "t[0] = " + Slot(dxso::UsageTexCoord, 0) + ";") && Has(r.hlsl, "t[3] = " + Slot(dxso::UsageTexCoord, 3) + ";") &&
               !Has(r.hlsl, "t[4] = " + Slot(dxso::UsageTexCoord, 4) + ";"));
        Report("the sampler is declared 2D by default and left to the device",
               r.samplers[0] == dxso::SamplerType::Tex2D && r.guessedSamplers == 1 && r.projectableSamplers == 1 &&
               !r.usesBumpEnv && Has(r.hlsl, "Texture2D t0 : register(t0);"));
        Report("a shader that reads no bump state declares the 4-slot fixup buffer",
               Has(r.hlsl, "nativra_fix[4]") && !Has(r.hlsl, "nativra_fix[13]"));
    }
    {
        const dxso::Result r = Translate("ps_1_4\ntexld r0, t5\nmov r1, c0");
        Report("ps_1_4 has six texture registers", r.ok && Has(r.hlsl, "t[5] = " + Slot(dxso::UsageTexCoord, 5) + ";"), r.error);
        Report("ps_1_4 texld has no projection to ask the device about", r.ok && r.projectableSamplers == 0);
    }

    // --- what the device decides -------------------------------------------------
    {
        dxso::Options cube;
        cube.samplerTypes[0] = dxso::SamplerType::Cube;
        Expect("a cube map bound to a tex stage is sampled with three coordinates", "ps_1_1\ntex t0\nmov r0, t0",
               { "TextureCube t0 : register(t0);", "t[0] = (t0.Sample(s0, (t[0]).xyz));" }, {}, cube);

        dxso::Options projected;
        projected.projectDivisor[0] = 4;
        projected.projectDivisor[1] = 3;
        Expect("D3DTTFF_PROJECTED divides by the last counted component", "ps_1_1\ntex t0\ntex t1\nadd r0, t0, t1",
               { "t[0] = (t0.Sample(s0, ((t[0]).xy / (t[0]).w)));", "t[1] = (t1.Sample(s1, ((t[1]).xy / (t[1]).z)));" }, {}, projected);

        Expect("projection does not touch ps_1_4, which projects with _dw", "ps_1_4\ntexld r0, t0\nmov r0, r0",
               { "r0 = (t0.Sample(s0, (t[0]).xy));" }, { " / " }, projected);

        dxso::Options volume;
        volume.samplerTypes[1] = dxso::SamplerType::Volume;
        Expect("a volume texture is sampled with three coordinates", "ps_1_1\ntex t1\nmov r0, t1",
               { "Texture3D t1 : register(t1);", "t1.Sample(s1, (t[1]).xyz)" }, {}, volume);

        // Instructions that only exist for one dimension ignore what the device guessed.
        dxso::Options wrong;
        for (int s = 0; s < 4; s++) wrong.samplerTypes[s] = dxso::SamplerType::Volume;
        const dxso::Result r = Translate("ps_1_1\ntex t0\ntexm3x3pad t1, t0_bx2\ntexm3x3pad t2, t0_bx2\ntexm3x3tex t3, t0_bx2\nmov r0, t3", wrong);
        Report("texm3x3tex always reads a cube map, texbem a 2D one",
               r.ok && r.samplers[3] == dxso::SamplerType::Cube && Has(r.hlsl, "TextureCube t3 : register(t3);") &&
               Translate("ps_1_1\ntex t0\ntexbem t1, t0\nmov r0, t1", wrong).samplers[1] == dxso::SamplerType::Tex2D,
               r.error);
    }

    // --- fixed-point range: constants ---------------------------------------------
    Expect("ps_1_1 constants are clamped to [-1,1]", "ps_1_1\nmov r0, c0\nmul r0, r0, c7.a",
           { "r0 = (clamp(c[0], -1.0, 1.0));", "(r0 * clamp(c[7], -1.0, 1.0).wwww)" });
    Expect("ps_1_4 constants are clamped to [-8,8]", "ps_1_4\nmov r0, c0", { "r0 = (clamp(c[0], -8.0, 8.0));" });
    Expect("def constants are clamped when translated (ps_1_1)", "ps_1_1\ndef c0, 2.0, -3.0, 0.25, 0.75\nmov r0, c0",
           { "r0 = (float4(1.0, -1.0, 0.25, 0.75));" });
    Expect("def constants are clamped when translated (ps_1_4)", "ps_1_4\ndef c0, 4.0, -12.0, 0.25, 9.5\nmov r0, c0",
           { "r0 = (float4(4.0, -8.0, 0.25, 8.0));" });
    {
        const dxso::Result r = Translate("ps_1_1\nmov r0, c3\nadd r0, r0, c1");
        Report("floatConstantsUsed counts the c# registers read", r.ok && r.floatConstantsUsed == 4, r.error);
    }

    // --- result modifiers, source modifiers, masks, swizzles ----------------------
    Expect("result scale applies before saturate", "ps_1_1\nmul_x2_sat r0, v0, v1\nmad_d2 r1, v0, v1, v0\nadd_x4 r0.rgb, r0, r1\nsub_d8 r0.a, r0, r1",
           { "r0 = saturate((v[0] * v[1]) * 2.0);", "r1 = ((v[0] * v[1] + v[0]) * 0.5);", "r0.xyz = ((r0 + r1) * 4.0).xyz;",
             "r0.w = ((r0 - r1) * 0.125).w;" });
    Expect("source modifiers", "ps_1_1\ntex t0\nmad r0, -t0_bx2, 1-v0, v1_bias\nadd r1, -v0_bias, t0_x2\nmul r1, -v1_x2.b, t0",
           { "r0 = ((-(2.0 * t[0] - 1.0)) * (1.0 - v[0]) + (v[1] - 0.5));", "r1 = ((-(v[0] - 0.5)) + (2.0 * t[0]));",
             "r1 = ((-2.0 * v[1].zzzz) * t[0]);" });
    Expect("write masks and replicated swizzles", "ps_1_1\nmov r0.rgb, v0\nmov r0.a, v1.b\nmov r1.a, v0.a\nmul r1.rgb, r0, v1.g",
           { "r0.xyz = (v[0]).xyz;", "r0.w = (v[1].zzzz).w;", "r1.w = (v[0].wwww).w;", "r1.xyz = (r0 * v[1].yyyy).xyz;" });
    Expect("sub, mad, lrp and dp3/dp4 (dp3 ignores w, results replicate)", "ps_1_2\nsub r0, v0, v1\nmad r0, r0, v0, v1\nlrp r1, v0.a, r0, v1\ndp3 r0.rgb, r0, r1\ndp4 r1, r0, r1",
           { "r0 = (v[0] - v[1]);", "r0 = (r0 * v[0] + v[1]);", "r1 = (lerp(v[1], r0, v[0].wwww));",
             "r0.xyz = (dot((r0).xyz, (r1).xyz).xxxx).xyz;", "r1 = (dot(r0, r1).xxxx);" });
    Expect("cnd compares against 0.5, cmp against 0", "ps_1_2\nsub r0.a, v0.a, v1.a\ncnd r1.rgb, r0.a, v0, v1\ncmp r0, v0_bias, v0, v1",
           { "r1.xyz = ((r0.wwww > 0.5 ? v[0] : v[1])).xyz;", "r0 = (((v[0] - 0.5) >= 0.0 ? v[0] : v[1]));" });
    Expect("co-issued pairs are emitted in program order", "ps_1_1\nmul r0.rgb, v0, v1\n+ mul r0.a, v0.a, v1.a",
           { "r0.xyz = (v[0] * v[1]).xyz;\n    r0.w = (v[0].wwww * v[1].wwww).w;" });
    Expect("arithmetic may write a texture register (ps_1_1)", "ps_1_1\ntex t0\ntex t1\ndp3_sat t1, t0_bx2, v0_bx2\nmul r0, t1, v1",
           { "t[1] = saturate(dot(((2.0 * t[0] - 1.0)).xyz, ((2.0 * v[0] - 1.0)).xyz).xxxx);", "r0 = (t[1] * v[1]);" });
    Expect("a shader that never writes r0 still declares it", "ps_1_1\nnop", { "static float4 r0;", "oC[0] = r0;" });

    // --- texture addressing, ps_1_1 .. ps_1_3 --------------------------------------
    Expect("texcoord copies the coordinates, clamped to [0,1], with alpha 1", "ps_1_1\ntexcoord t1\nmov r0, t1",
           { "t[1] = (float4(saturate((t[1]).xyz), 1.0));", "r0 = (t[1]);" });
    Expect("texkill kills on a negative u, v or w and ignores q", "ps_1_1\ntexkill t1\ntexkill t2\nmov r0, v0",
           { "if (any((t[1]).xyz < 0.0)) discard;", "if (any((t[2]).xyz < 0.0)) discard;" });
    {
        const dxso::Result r = Translate("ps_1_1\ntex t0\ntexbem t1, t0\nmul r0, t1, v0");
        Report("texbem offsets the coordinates by the stage's bump matrix",
               r.ok && r.usesBumpEnv &&
               Has(r.hlsl, "float2 nativra_uv1 = (t[1]).xy + nativra_bump1.x * nativra_fix[2].xy + nativra_bump1.y * nativra_fix[2].zw;") &&
               Has(r.hlsl, "t[1] = (nativra_texel1);") && Has(r.hlsl, "nativra_fix[13]") &&
               r.samplers[1] == dxso::SamplerType::Tex2D && (r.guessedSamplers & 2) == 0,
               r.ok ? MainBody(r.hlsl) : r.error);
        Report("the bump slots are the ones dxso.h documents",
               dxso::FixupBumpMatrix + 1 == 2 && dxso::FixupBumpLuminance + 1 == 8 && dxso::FixupSlots >= dxso::FixupBumpLuminance + dxso::BumpStages);
    }
    Expect("texbeml scales the colour by the clamped luminance", "ps_1_1\ntex t0\ntexbeml t1, t0\nmov r0, t1",
           { "nativra_texel1.rgb *= saturate(nativra_bump1.z * nativra_fix[8].x + nativra_fix[8].y);" });
    Expect("texreg2ar and texreg2gb read a register's colour as coordinates", "ps_1_1\ntex t0\ntexreg2ar t1, t0\ntexreg2gb t2, t0_bx2\nadd r0, t1, t2",
           { "t[1] = (t1.Sample(s1, (float4((t[0]).wx, 0.0, 0.0)).xy));",
             "t[2] = (t2.Sample(s2, (float4(((2.0 * t[0] - 1.0)).yz, 0.0, 0.0)).xy));" });
    Expect("texreg2rgb reads 3D coordinates", "ps_1_2\ntex t0\ntexreg2rgb t1, t0\nmov r0, t1",
           { "TextureCube t1 : register(t1);", "t[1] = (t1.Sample(s1, (float4((t[0]).xyz, 0.0)).xyz));" });
    Expect("texm3x2pad/texm3x2tex multiply a 3x2 matrix and sample 2D",
           "ps_1_1\ntex t0\ntexm3x2pad t1, t0_bx2\ntexm3x2tex t2, t0_bx2\nmov r0, t2",
           { "float nativra_row1 = dot((t[1]).xyz, ((2.0 * t[0] - 1.0)).xyz);", "float nativra_row2 = dot((t[2]).xyz, ((2.0 * t[0] - 1.0)).xyz);",
             "t[2] = (t2.Sample(s2, (float4(nativra_row1, nativra_row2, 0.0, 0.0)).xy));" });
    Expect("texm3x3pad x2 + texm3x3tex sample a cube map",
           "ps_1_1\ntex t0\ntexm3x3pad t1, t0_bx2\ntexm3x3pad t2, t0_bx2\ntexm3x3tex t3, t0_bx2\nmov r0, t3",
           { "TextureCube t3 : register(t3);", "float nativra_row3 = dot((t[3]).xyz, ((2.0 * t[0] - 1.0)).xyz);",
             "t[3] = (t3.Sample(s3, (float4(float3(nativra_row1, nativra_row2, nativra_row3), 0.0)).xyz));" });
    Expect("texm3x3 (ps_1_2) stores the product without a lookup", "ps_1_2\ntex t0\ntexm3x3pad t1, t0_bx2\ntexm3x3pad t2, t0_bx2\ntexm3x3 t3, t0_bx2\nmov r0, t3",
           { "t[3] = (float4(float3(nativra_row1, nativra_row2, nativra_row3), 1.0));" }, { "t3.Sample" });
    Expect("texm3x3spec reflects the constant eye vector about the normal",
           "ps_1_1\ntex t0\ntexm3x3pad t1, t0_bx2\ntexm3x3pad t2, t0_bx2\ntexm3x3spec t3, t0_bx2, c0\nmov r0, t3",
           { "float3 nativra_e3 = (clamp(c[0], -1.0, 1.0)).xyz;",
             "float3 nativra_r3 = 2.0 * dot(nativra_n3, nativra_e3) / dot(nativra_n3, nativra_n3) * nativra_n3 - nativra_e3;",
             "t[3] = (t3.Sample(s3, (float4(nativra_r3, 0.0)).xyz));" });
    Expect("texm3x3vspec takes the eye vector from the q of the three coordinate sets",
           "ps_1_1\ntex t0\ntexm3x3pad t1, t0_bx2\ntexm3x3pad t2, t0_bx2\ntexm3x3vspec t3, t0_bx2\nmov r0, t3",
           { "float3 nativra_e3 = float3((t[1]).w, (t[2]).w, (t[3]).w);" });
    Expect("texdp3 and texdp3tex", "ps_1_2\ntex t0\ntexdp3 t1, t0_bx2\ntexdp3tex t2, t0_bx2\nadd r0, t1, t2",
           { "t[1] = (float4(nativra_dp1, nativra_dp1, nativra_dp1, nativra_dp1));",
             "t[2] = (t2.Sample(s2, (float4(nativra_dp2, 0.0, 0.0, 0.0)).xy));" });
    {
        const dxso::Result r = Translate("ps_1_3\ntex t0\ntexm3x2pad t1, t0_bx2\ntexm3x2depth t2, t0_bx2\nmov r0, t0");
        Report("texm3x2depth writes depth as the first row over the second",
               r.ok && r.writesDepth && Has(r.hlsl, "oDepth = nativra_row2 == 0.0 ? 1.0 : nativra_row1 / nativra_row2;") &&
               Has(r.hlsl, "float depth : SV_Depth;"),
               r.ok ? MainBody(r.hlsl) : r.error);
    }
    {
        // Two matrices in one shader: each pad sequence is consumed by its own last instruction.
        Expect("two independent texm3x2 matrices",
               "ps_1_1\ntex t0\ntexm3x2pad t1, t0_bx2\ntexm3x2tex t2, t0_bx2\ntexm3x2pad t3, t0_bx2\ntexm3x2tex t4, t0_bx2\nmov r0, t2",
               { "float nativra_row1 = ", "float nativra_row2 = ", "float nativra_row3 = ", "float nativra_row4 = " });
    }

    // --- ps_1_4 --------------------------------------------------------------------
    {
        const dxso::Result r = Translate(
            "ps_1_4\ntexld r0, t0\ntexld r3, t5\ntexcrd r1.rgb, t1\ntexcrd r2.rg, t2_dw.rga\nmul r1.rg, r1, r0.a\nphase\n"
            "texld r1, r1\ntexld r2, r2\nmov r0, r1");
        Report("ps_1_4 texld samples the stage numbered like its destination register",
               r.ok && Has(r.hlsl, "r0 = (t0.Sample(s0, (t[0]).xy));") && Has(r.hlsl, "r3 = (t3.Sample(s3, (t[5]).xy));") &&
               r.samplers[3] == dxso::SamplerType::Tex2D && r.samplers[5] == dxso::SamplerType::None,
               r.ok ? MainBody(r.hlsl) : r.error);
        Report("texcrd copies unclamped; _dw divides x and y by the swizzled w",
               r.ok && Has(r.hlsl, "r1.xyz = (t[1]).xyz;") && Has(r.hlsl, "r2.xy = (nativra_dw(t[2].xyww)).xy;") &&
               Has(r.hlsl, "return float4(v.xy / v.w, v.zw);"),
               r.ok ? MainBody(r.hlsl) : r.error);
        Report("phase 2 dependent reads use the registers phase 1 left behind",
               r.ok && Has(r.hlsl, "r1 = (t1.Sample(s1, (r1).xy));") && Has(r.hlsl, "r2 = (t2.Sample(s2, (r2).xy));") &&
               Has(r.hlsl, "r1.xy = (r1 * r0.wwww).xy;"),
               r.ok ? MainBody(r.hlsl) : r.error);
    }
    Expect("_dz divides by the swizzled z", "ps_1_4\ntexcrd r1.rg, t1_dz.xyz\ntexld r0, r1",
           { "r1.xy = (nativra_dz(t[1].xyzz)).xy;", "return float4(v.xy / v.z, v.zw);" });
    Expect("bem adds the stage's bump matrix times the bump map (stage = destination register)",
           "ps_1_4\ntexld r0, t0\ntexcrd r1.rg, t1\nbem r1.rg, r1, r0\nphase\ntexld r1, r1\nmov r0, r1",
           { "float2 nativra_uv2 = (r1).xy + nativra_bump2.x * nativra_fix[2].xy + nativra_bump2.y * nativra_fix[2].zw;",
             "r1.xy = (float4(nativra_uv2, 0.0, 0.0)).xy;", "nativra_fix[13]" });
    Expect("ps_1_4 cnd is per component", "ps_1_4\ntexld r0, t0\ncnd r1, r0, r0, c0\nmov r0, r1",
           { "r1 = ((r0 > 0.5 ? r0 : clamp(c[0], -8.0, 8.0)));" });
    {
        const dxso::Result r = Translate("ps_1_4\ntexld r0, t0\ntexcrd r5.rgb, t1\nmov r5.g, r0.a\nphase\ntexdepth r5\nmov r0, v0");
        Report("texdepth writes depth as r5.r over r5.g",
               r.ok && r.writesDepth && Has(r.hlsl, "oDepth = (r5).y == 0.0 ? 1.0 : (r5).x / (r5).y;"),
               r.ok ? MainBody(r.hlsl) : r.error);
    }
    Expect("ps_1_4 results keep float range (no implicit clamp)", "ps_1_4\nmul r0, v0, c0\nmov r0, r0", { "r0 = (v[0] * clamp(c[0], -8.0, 8.0));" },
           { "saturate" });

    // --- errors ---------------------------------------------------------------------
    ExpectError("an unknown pixel shader version", { 0xFFFF0105, 0xFFFF }, "unsupported pixel shader version ps_1_5");
    ExpectError("an opcode ps_1_x does not have (rcp)", { 0xFFFF0101, 0x6, 0x800F0000, 0x80E40000, 0xFFFF }, "ps_1_x: unknown opcode 6");
    ExpectError("an operand count the opcode disagrees with (mul with two operands)",
                { 0xFFFF0101, 0x5, 0x800F0000, 0x80E40000, 0xFFFF }, "ps_1_x: operand count does not match opcode 5");
    ExpectError("a shader without an END token", { 0xFFFF0101, 0x1, 0x800F0000, 0x80E40000 }, "no END token");
    ExpectError("an instruction that runs past the end", { 0xFFFF0101, 0x4, 0x800F0000 }, "instruction runs past the end of the shader");
    ExpectAssemblyError("texm3x2tex without its pad", "ps_1_1\ntex t0\ntexm3x2tex t2, t0_bx2", "texm3x2tex needs one texm3x2pad before it");
    ExpectAssemblyError("texm3x3tex with one pad", "ps_1_1\ntex t0\ntexm3x3pad t1, t0_bx2\ntexm3x3tex t3, t0_bx2",
                        "texm3x3tex needs two texm3x3pad instructions before it");
    ExpectAssemblyError("a 3x2 pad does not complete a 3x3 matrix",
                        "ps_1_1\ntex t0\ntexm3x2pad t1, t0_bx2\ntexm3x2pad t2, t0_bx2\ntexm3x3vspec t3, t0_bx2",
                        "texm3x3vspec needs two texm3x3pad instructions before it");
    ExpectAssemblyError("texbem on a stage the bump state does not cover", "ps_1_1\ntex t0\ntexbem t7, t0", "texbem: texture stage out of range");

    std::printf("%s: %d failure(s)\n", failures ? "FAILED" : "OK", failures);
    return failures ? 1 : 0;
}
