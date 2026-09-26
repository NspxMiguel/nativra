// dxso: Direct3D 9 shader bytecode to Shader Model 5 HLSL. See dxso.h.
//
// Structure: a first pass decodes the token stream into instructions, a second
// collects declarations (dcl/def/defi/defb), and a third emits HLSL. Every
// D3D9 register becomes a `static` global, so subroutines (label/call/ret) are
// plain HLSL functions and `ret` is a plain `return`.

#include "dxso.h"

#include <cstdio>
#include <cstring>
#include <map>
#include <set>
#include <sstream>

namespace dxso {

namespace {

// --- token layout -----------------------------------------------------------

enum Opcode : uint32_t {
    OpNop = 0, OpMov = 1, OpAdd = 2, OpSub = 3, OpMad = 4, OpMul = 5, OpRcp = 6, OpRsq = 7,
    OpDp3 = 8, OpDp4 = 9, OpMin = 10, OpMax = 11, OpSlt = 12, OpSge = 13, OpExp = 14, OpLog = 15,
    OpLit = 16, OpDst = 17, OpLrp = 18, OpFrc = 19, OpM4x4 = 20, OpM4x3 = 21, OpM3x4 = 22,
    OpM3x3 = 23, OpM3x2 = 24, OpCall = 25, OpCallNz = 26, OpLoop = 27, OpRet = 28, OpEndLoop = 29,
    OpLabel = 30, OpDcl = 31, OpPow = 32, OpCrs = 33, OpSgn = 34, OpAbs = 35, OpNrm = 36,
    OpSinCos = 37, OpRep = 38, OpEndRep = 39, OpIf = 40, OpIfC = 41, OpElse = 42, OpEndIf = 43,
    OpBreak = 44, OpBreakC = 45, OpMova = 46, OpDefB = 47, OpDefI = 48,
    OpTexCoord = 64, OpTexKill = 65, OpTex = 66, OpExpP = 78, OpLogP = 79, OpCnd = 80, OpDef = 81,
    OpCmp = 88, OpDp2Add = 90, OpDsx = 91, OpDsy = 92, OpTexLdd = 93, OpSetP = 94, OpTexLdl = 95,
    OpBreakP = 96,
    OpPhase = 0xFFFD, OpComment = 0xFFFE, OpEnd = 0xFFFF,
};

enum RegType : uint32_t {
    RegTemp = 0, RegInput = 1, RegConst = 2, RegAddr = 3 /* vs a0, ps t# */, RegRastOut = 4,
    RegAttrOut = 5, RegTexCrdOut = 6 /* vs<3 oT#, vs3 o# */, RegConstInt = 7, RegColorOut = 8,
    RegDepthOut = 9, RegSampler = 10, RegConst2 = 11, RegConst3 = 12, RegConst4 = 13,
    RegConstBool = 14, RegLoop = 15, RegTempFloat16 = 16, RegMiscType = 17, RegLabel = 18,
    RegPredicate = 19,
};

uint32_t RegTypeOf(uint32_t token) { return ((token >> 28) & 0x7) | ((token >> 8) & 0x18); }
uint32_t RegNumOf(uint32_t token) { return token & 0x7FF; }
bool IsRelative(uint32_t token) { return (token & (1u << 13)) != 0; }

// Source modifiers (bits 24..27 of a source token).
enum SrcMod : uint32_t {
    ModNone = 0, ModNeg = 1, ModBias = 2, ModBiasNeg = 3, ModSign = 4, ModSignNeg = 5,
    ModComp = 6, ModX2 = 7, ModX2Neg = 8, ModDz = 9, ModDw = 10, ModAbs = 11, ModAbsNeg = 12,
    ModNot = 13,
};

// Comparison (instruction-token bits 16..18) for ifc/breakc/setp.
const char* CompareOp(uint32_t control)
{
    switch (control & 7) {
    case 1: return ">";
    case 2: return "==";
    case 3: return ">=";
    case 4: return "<";
    case 5: return "!=";
    case 6: return "<=";
    default: return nullptr;
    }
}

// Parameter-token count for vs_1_1 instructions, which carry no length field.
int Sm1ParamCount(uint32_t op)
{
    switch (op) {
    case OpNop: case OpRet: case OpEndLoop: case OpEndRep: case OpElse: case OpEndIf: case OpBreak:
        return 0;
    case OpLabel: case OpCall:
        return 1;
    case OpMov: case OpRcp: case OpRsq: case OpExp: case OpLog: case OpLit: case OpFrc:
    case OpExpP: case OpLogP: case OpAbs: case OpNrm: case OpMova: case OpDcl:
        return 2;
    case OpAdd: case OpSub: case OpMul: case OpDp3: case OpDp4: case OpMin: case OpMax:
    case OpSlt: case OpSge: case OpDst: case OpM4x4: case OpM4x3: case OpM3x4: case OpM3x3:
    case OpM3x2: case OpPow: case OpCrs:
        return 3;
    case OpMad: case OpLrp:
        return 4;
    case OpDef:
        return 5;
    default:
        return -1;
    }
}

std::string Swizzle(uint32_t token)
{
    const uint32_t swz = (token >> 16) & 0xFF;
    std::string s = ".";
    for (int c = 0; c < 4; c++) s += "xyzw"[(swz >> (c * 2)) & 3];
    return s;
}

std::string MaskSuffix(uint32_t mask)
{
    if (mask == 0xF || mask == 0) return "";
    std::string s = ".";
    if (mask & 1) s += 'x';
    if (mask & 2) s += 'y';
    if (mask & 4) s += 'z';
    if (mask & 8) s += 'w';
    return s;
}

std::string FloatLiteral(uint32_t bits)
{
    float f;
    std::memcpy(&f, &bits, sizeof f);
    const uint32_t exponent = (bits >> 23) & 0xFF;
    if (exponent == 0xFF) {
        // inf or NaN: spell the exact bit pattern.
        char buf[32];
        std::snprintf(buf, sizeof buf, "asfloat(0x%08Xu)", bits);
        return buf;
    }
    char buf[48];
    std::snprintf(buf, sizeof buf, "%.9g", f);
    std::string s = buf;
    if (s.find_first_of(".e") == std::string::npos) s += ".0";
    return s;
}

// --- decoded program ---------------------------------------------------------

struct Param {
    uint32_t token = 0;
    uint32_t type = 0;
    uint32_t num = 0;
    bool hasRel = false;
    uint32_t relToken = 0;     // SM2+: the relative-address token
};

struct Instruction {
    uint32_t op = 0;
    uint32_t control = 0;      // instruction-token bits 16..23
    bool predicated = false;
    std::vector<Param> params; // destination first (when present), then sources
    Param predicate;
    std::vector<uint32_t> literal;
    uint32_t dclToken = 0;
};

struct DclEntry {
    uint32_t type, num, mask;
    uint8_t usage, index;
    bool centroid;
};

class Translator {
public:
    explicit Translator(const Options& options) : options(options) {}
    Result Run(const uint32_t* tokens, size_t count);

    const Options options;

private:
    bool Decode(const uint32_t* tokens, size_t count);
    bool Collect();
    bool EmitFunction(size_t begin, size_t end, std::ostringstream& out);
    std::string Assemble(const std::map<uint32_t, std::string>& labelBodies, const std::string& mainBody);

    bool Fail(const std::string& why) { if (res.error.empty()) res.error = why; return false; }

    std::string RegName(const Param& p, int offset = 0);
    std::string Source(const Param& p, int offset = 0);
    std::string Scalar(const Param& p) { return "(" + Source(p) + ").x"; }
    std::string PredicateComponent(const Param& p);
    uint32_t DstMask(const Param& p) { return (p.token >> 16) & 0xF; }
    bool Saturate(const Param& p) { return ((p.token >> 20) & 1) != 0; }

    void Assign(const Instruction& ins, const std::string& value4);
    bool EmitInstruction(const Instruction& ins);
    void Line(const std::string& s) { (*out) << std::string(static_cast<size_t>(indent) * 4, ' ') << s << "\n"; }

    Result res;
    Stage stage = Stage::Vertex;
    uint32_t major = 0, minor = 0;
    std::vector<Instruction> program;

    std::vector<DclEntry> dcls;
    std::map<uint32_t, std::vector<uint32_t>> defF;   // bit patterns
    std::map<uint32_t, std::vector<int32_t>> defI;
    std::map<uint32_t, bool> defB;

    bool relativeConst = false;
    bool usesPos = false, usesFace = false;
    uint32_t maxTemp = 0;

    std::ostringstream* out = nullptr;
    int indent = 1;
    int loopDepth = 0;
    int uniqueId = 0;
    std::vector<std::string> loopRestore;
};

// --- decode ------------------------------------------------------------------

bool Translator::Decode(const uint32_t* tokens, size_t count)
{
    if (tokens == nullptr || count < 2) return Fail("shader shorter than a version token");
    const uint32_t version = tokens[0];
    const uint32_t kind = version >> 16;
    if (kind == 0xFFFE) stage = Stage::Vertex;
    else if (kind == 0xFFFF) stage = Stage::Pixel;
    else return Fail("not a D3D9 shader version token");
    major = (version >> 8) & 0xFF;
    minor = version & 0xFF;

    if (stage == Stage::Pixel && major < 2) return Fail("ps_1_x is not supported yet");
    if (major < 1 || major > 3) return Fail("unsupported shader model " + std::to_string(major));

    const bool sm1 = major < 2;
    size_t i = 1;
    while (i < count) {
        const uint32_t token = tokens[i++];
        const uint32_t op = token & 0xFFFF;
        if (op == OpEnd) return true;
        if (op == OpComment) { i += (token >> 16) & 0x7FFF; continue; }
        if (op == OpPhase) continue;

        Instruction ins;
        ins.op = op;
        ins.control = (token >> 16) & 0xFF;
        ins.predicated = (token & (1u << 28)) != 0;

        size_t length;
        if (!sm1) {
            length = (token >> 24) & 0xF;
        } else {
            const int n = Sm1ParamCount(op);
            if (n < 0) return Fail("vs_1_1: unknown opcode " + std::to_string(op));
            length = static_cast<size_t>(n);
        }
        if (i + length > count) return Fail("instruction runs past the end of the shader");
        const size_t end = i + length;

        if (op == OpDcl) ins.dclToken = tokens[i++];

        if (op == OpDef || op == OpDefI || op == OpDefB) {
            Param p;
            p.token = tokens[i++];
            p.type = RegTypeOf(p.token);
            p.num = RegNumOf(p.token);
            ins.params.push_back(p);
            while (i < end) ins.literal.push_back(tokens[i++]);
            program.push_back(ins);
            continue;
        }

        // Parameters, each optionally followed by a relative-address token
        // (SM2+); after a predicated instruction's destination comes the
        // predicate register.
        bool first = true;
        while (i < end) {
            Param p;
            p.token = tokens[i++];
            p.type = RegTypeOf(p.token);
            p.num = RegNumOf(p.token);
            if (IsRelative(p.token)) {
                p.hasRel = true;
                if (!sm1) {
                    if (i >= end) return Fail("relative-address token missing");
                    p.relToken = tokens[i++];
                }
            }
            ins.params.push_back(p);
            if (first && ins.predicated && i < end) {
                ins.predicate.token = tokens[i++];
                ins.predicate.type = RegTypeOf(ins.predicate.token);
                ins.predicate.num = RegNumOf(ins.predicate.token);
            }
            first = false;
        }
        i = end;
        program.push_back(ins);
    }
    return Fail("no END token");
}

// --- declarations -----------------------------------------------------------

bool Translator::Collect()
{
    for (const auto& ins : program) {
        if (ins.op == OpDcl) {
            if (ins.params.empty()) return Fail("dcl without a register");
            const Param& p = ins.params[0];
            if (p.type == RegSampler) {
                const uint32_t kind = (ins.dclToken >> 27) & 0xF;
                if (p.num >= 16) return Fail("sampler index out of range");
                if (kind != 2 && kind != 3 && kind != 4) return Fail("unsupported sampler type " + std::to_string(kind));
                res.samplers[p.num] = static_cast<SamplerType>(kind);
                continue;
            }
            DclEntry d;
            d.type = p.type;
            d.num = p.num;
            d.mask = DstMask(p);
            // A declaration that carries no components links nothing (some
            // compilers emit these for inputs the shader never reads).
            if (d.mask == 0 && p.type != RegMiscType) continue;
            d.usage = static_cast<uint8_t>(ins.dclToken & 0x1F);
            d.index = static_cast<uint8_t>((ins.dclToken >> 16) & 0xF);
            d.centroid = ((p.token >> 20) & 4) != 0;
            dcls.push_back(d);
            if (stage == Stage::Vertex && p.type == RegInput)
                res.inputs.push_back(InputDecl{ p.num, d.usage, d.index });
        } else if (ins.op == OpDef) {
            if (ins.literal.size() != 4) return Fail("def without four values");
            uint32_t base = ins.params[0].num;
            if (ins.params[0].type == RegConst2) base += 2048;
            else if (ins.params[0].type == RegConst3) base += 4096;
            else if (ins.params[0].type == RegConst4) base += 6144;
            defF[base] = ins.literal;
        } else if (ins.op == OpDefI) {
            if (ins.literal.size() != 4) return Fail("defi without four values");
            std::vector<int32_t> v;
            for (uint32_t x : ins.literal) v.push_back(static_cast<int32_t>(x));
            defI[ins.params[0].num] = v;
        } else if (ins.op == OpDefB) {
            if (ins.literal.empty()) return Fail("defb without a value");
            defB[ins.params[0].num] = ins.literal[0] != 0;
        }
    }
    return true;
}

// --- operands ----------------------------------------------------------------

// The register a parameter names. Float registers are float4; a0 is int4, aL
// int, p0 bool4, i# int4, b# bool, oFog/oPts/oDepth/vFace float.
std::string Translator::RegName(const Param& p, int offset)
{
    const uint32_t n = p.num + static_cast<uint32_t>(offset);
    std::string rel;
    if (p.hasRel) {
        if (major < 2) {
            rel = "a0.x + ";
        } else if (RegTypeOf(p.relToken) == RegLoop) {
            rel = "aL + ";
        } else {
            rel = std::string("a0.") + "xyzw"[(p.relToken >> 16) & 3] + " + ";
        }
    }

    switch (p.type) {
    case RegTemp:
        if (n + 1 > maxTemp) maxTemp = n + 1;
        return "r" + std::to_string(n);
    case RegInput:
        return "v[" + rel + std::to_string(n) + "]";
    case RegConst: case RegConst2: case RegConst3: case RegConst4: {
        uint32_t base = n;
        if (p.type == RegConst2) base += 2048;
        else if (p.type == RegConst3) base += 4096;
        else if (p.type == RegConst4) base += 6144;
        if (!p.hasRel) {
            auto it = defF.find(base);
            if (it != defF.end()) {
                return "float4(" + FloatLiteral(it->second[0]) + ", " + FloatLiteral(it->second[1]) + ", " +
                       FloatLiteral(it->second[2]) + ", " + FloatLiteral(it->second[3]) + ")";
            }
            if (base + 1 > res.floatConstantsUsed) res.floatConstantsUsed = base + 1;
            return "c[" + std::to_string(base) + "]";
        }
        relativeConst = true;
        res.floatConstantsUsed = 256;
        return "C(" + rel + std::to_string(base) + ")";
    }
    case RegAddr:
        if (stage == Stage::Vertex) return "a0";
        return "t[" + std::to_string(n) + "]";
    case RegRastOut:
        return n == 0 ? "oPos" : n == 1 ? "oFog" : "oPts";
    case RegAttrOut:
        return "oD[" + std::to_string(n) + "]";
    case RegTexCrdOut:
        if (stage == Stage::Vertex && major >= 3) return "o[" + rel + std::to_string(n) + "]";
        return "oT[" + std::to_string(n) + "]";
    case RegConstInt: {
        auto it = defI.find(n);
        if (it != defI.end()) {
            return "int4(" + std::to_string(it->second[0]) + ", " + std::to_string(it->second[1]) + ", " +
                   std::to_string(it->second[2]) + ", " + std::to_string(it->second[3]) + ")";
        }
        return "ci[" + std::to_string(n) + "]";
    }
    case RegColorOut:
        res.renderTargets |= 1u << n;
        return "oC[" + std::to_string(n) + "]";
    case RegDepthOut:
        res.writesDepth = true;
        return "oDepth";
    case RegSampler:
        return "s" + std::to_string(n);
    case RegConstBool: {
        auto it = defB.find(n);
        if (it != defB.end()) return it->second ? "true" : "false";
        return "(cb[" + std::to_string(n) + "].x != 0)";
    }
    case RegLoop:
        return "aL";
    case RegMiscType:
        if (n == 0) { usesPos = true; return "vPos"; }
        usesFace = true;
        return "vFace";
    case RegPredicate:
        return "p0";
    default:
        Fail("unsupported register type " + std::to_string(p.type));
        return "r0";
    }
}

// A source operand as a float4 expression, swizzle and modifier applied.
std::string Translator::Source(const Param& p, int offset)
{
    const std::string reg = RegName(p, offset);
    const std::string swizzle = Swizzle(p.token);

    std::string value;
    const bool vertexAddr = p.type == RegAddr && stage == Stage::Vertex;
    if (p.type == RegConstInt || vertexAddr) {
        value = "float4(" + reg + ")" + swizzle;
    } else if (p.type == RegLoop) {
        value = "float4(aL, aL, aL, aL)";
    } else if (p.type == RegConstBool) {
        value = "float4(" + reg + ", " + reg + ", " + reg + ", " + reg + ")";
    } else if (p.type == RegPredicate) {
        value = "float4(p0)" + swizzle;
    } else if ((p.type == RegRastOut && p.num != 0) || p.type == RegDepthOut ||
               (p.type == RegMiscType && p.num == 1)) {
        value = "(" + reg + ").xxxx";
    } else {
        value = swizzle == ".xyzw" ? reg : reg + swizzle;
    }

    switch ((p.token >> 24) & 0xF) {
    case ModNone: return value;
    case ModNeg: return "(-" + value + ")";
    case ModAbs: return "abs(" + value + ")";
    case ModAbsNeg: return "(-abs(" + value + "))";
    case ModBias: return "(" + value + " - 0.5)";
    case ModBiasNeg: return "(-(" + value + " - 0.5))";
    case ModSign: return "(2.0 * " + value + " - 1.0)";
    case ModSignNeg: return "(-(2.0 * " + value + " - 1.0))";
    case ModComp: return "(1.0 - " + value + ")";
    case ModX2: return "(2.0 * " + value + ")";
    case ModX2Neg: return "(-2.0 * " + value + ")";
    case ModNot: return "float4(!(" + value + " != 0.0))";
    default:
        Fail("unsupported source modifier " + std::to_string((p.token >> 24) & 0xF));
        return value;
    }
}

// "p0.x" or "!p0.x" for a predicate operand (if/breakp/callnz).
std::string Translator::PredicateComponent(const Param& p)
{
    const bool negate = ((p.token >> 24) & 0xF) == ModNot;
    return std::string(negate ? "!" : "") + "p0." + "xyzw"[(p.token >> 16) & 3];
}

// dst.mask = value (a float4 expression), with saturate and predication.
void Translator::Assign(const Instruction& ins, const std::string& value4)
{
    const Param& dst = ins.params[0];
    const std::string suffix = MaskSuffix(DstMask(dst));
    const std::string value = Saturate(dst) ? "saturate(" + value4 + ")" : "(" + value4 + ")";
    const std::string target = RegName(dst);

    // Scalar destinations.
    if ((dst.type == RegRastOut && dst.num != 0) || dst.type == RegDepthOut) {
        Line(target + " = " + value + ".x;");
        return;
    }

    std::string rhs;
    const char* type = "float4";
    if (dst.type == RegAddr && stage == Stage::Vertex) {
        rhs = "int4(floor(" + value + " + 0.5))" + suffix;   // mova rounds to nearest
        type = "int4";
    } else if (dst.type == RegPredicate) {
        rhs = "(" + value + " != 0.0)" + suffix;
        type = "bool4";
    } else {
        rhs = value + suffix;
    }

    if (!ins.predicated) {
        Line(target + suffix + " = " + rhs + ";");
        return;
    }
    // Per-component predication: keep the old value where the predicate is false.
    const bool negate = ((ins.predicate.token >> 24) & 0xF) == ModNot;
    const std::string pred = std::string(negate ? "!p0" : "p0") + Swizzle(ins.predicate.token);
    const std::string tmp = "nativra_t" + std::to_string(uniqueId++);
    Line(std::string(type) + " " + tmp + " = " + target + ";");
    Line(tmp + suffix + " = " + rhs + ";");
    Line(target + suffix + " = (" + pred + ")" + suffix + " ? " + tmp + suffix + " : " + target + suffix + ";");
}

// --- instructions ------------------------------------------------------------

bool Translator::EmitInstruction(const Instruction& ins)
{
    const auto& P = ins.params;
    auto need = [&](size_t n) {
        if (P.size() < n) return Fail("opcode " + std::to_string(ins.op) + " has too few operands");
        return true;
    };
    auto src = [&](size_t k) { return Source(P[k]); };

    switch (ins.op) {
    case OpNop: case OpDcl: case OpDef: case OpDefI: case OpDefB: case OpPhase: case OpLabel:
        return true;

    case OpMov:
        if (!need(2)) return false;
        // vs_1_1 loads a0 with mov, which truncates toward -inf.
        if (P[0].type == RegAddr && stage == Stage::Vertex && major < 2) Assign(ins, "floor(" + src(1) + ")");
        else Assign(ins, src(1));
        return true;
    case OpMova: if (!need(2)) return false; Assign(ins, src(1)); return true;
    case OpAdd: if (!need(3)) return false; Assign(ins, src(1) + " + " + src(2)); return true;
    case OpSub: if (!need(3)) return false; Assign(ins, src(1) + " - " + src(2)); return true;
    case OpMul: if (!need(3)) return false; Assign(ins, src(1) + " * " + src(2)); return true;
    case OpMad: if (!need(4)) return false; Assign(ins, src(1) + " * " + src(2) + " + " + src(3)); return true;
    case OpMin: if (!need(3)) return false; Assign(ins, "min(" + src(1) + ", " + src(2) + ")"); return true;
    case OpMax: if (!need(3)) return false; Assign(ins, "max(" + src(1) + ", " + src(2) + ")"); return true;
    case OpSlt: if (!need(3)) return false; Assign(ins, "float4(" + src(1) + " < " + src(2) + ")"); return true;
    case OpSge: if (!need(3)) return false; Assign(ins, "float4(" + src(1) + " >= " + src(2) + ")"); return true;
    case OpFrc: if (!need(2)) return false; Assign(ins, "frac(" + src(1) + ")"); return true;
    case OpAbs: if (!need(2)) return false; Assign(ins, "abs(" + src(1) + ")"); return true;
    case OpSgn: if (!need(2)) return false; Assign(ins, "sign(" + src(1) + ")"); return true;
    case OpDsx: if (!need(2)) return false; Assign(ins, "ddx(" + src(1) + ")"); return true;
    case OpDsy: if (!need(2)) return false; Assign(ins, "ddy(" + src(1) + ")"); return true;

    // Scalar ops read the first selected component and replicate the result.
    case OpRcp: if (!need(2)) return false; Assign(ins, "(1.0 / " + Scalar(P[1]) + ").xxxx"); return true;
    case OpRsq: if (!need(2)) return false; Assign(ins, "rsqrt(abs(" + Scalar(P[1]) + ")).xxxx"); return true;
    case OpExp: case OpExpP:
        if (!need(2)) return false;
        Assign(ins, "exp2(" + Scalar(P[1]) + ").xxxx");
        return true;
    case OpLog: case OpLogP:
        if (!need(2)) return false;
        Assign(ins, "log2(abs(" + Scalar(P[1]) + ")).xxxx");
        return true;
    case OpPow:
        if (!need(3)) return false;
        Assign(ins, "pow(abs(" + Scalar(P[1]) + "), " + Scalar(P[2]) + ").xxxx");
        return true;

    case OpDp3:
        if (!need(3)) return false;
        Assign(ins, "dot((" + src(1) + ").xyz, (" + src(2) + ").xyz).xxxx");
        return true;
    case OpDp4: if (!need(3)) return false; Assign(ins, "dot(" + src(1) + ", " + src(2) + ").xxxx"); return true;
    case OpDp2Add:
        if (!need(4)) return false;
        Assign(ins, "(dot((" + src(1) + ").xy, (" + src(2) + ").xy) + " + Scalar(P[3]) + ").xxxx");
        return true;
    case OpLrp: if (!need(4)) return false; Assign(ins, "lerp(" + src(3) + ", " + src(2) + ", " + src(1) + ")"); return true;
    case OpCmp:
        if (!need(4)) return false;
        Assign(ins, "(" + src(1) + " >= 0.0 ? " + src(2) + " : " + src(3) + ")");
        return true;
    case OpCnd:
        if (!need(4)) return false;
        Assign(ins, "(" + src(1) + " > 0.5 ? " + src(2) + " : " + src(3) + ")");
        return true;
    case OpCrs:
        if (!need(3)) return false;
        Assign(ins, "float4(cross((" + src(1) + ").xyz, (" + src(2) + ").xyz), 0.0)");
        return true;
    case OpNrm:
        if (!need(2)) return false;
        Assign(ins, src(1) + " * rsqrt(dot((" + src(1) + ").xyz, (" + src(1) + ").xyz))");
        return true;
    case OpLit: if (!need(2)) return false; Assign(ins, "nativra_lit(" + src(1) + ")"); return true;
    case OpDst:
        if (!need(3)) return false;
        Assign(ins, "float4(1.0, (" + src(1) + ").y * (" + src(2) + ").y, (" + src(1) + ").z, (" + src(2) + ").w)");
        return true;
    case OpSinCos:
        if (!need(2)) return false;
        Assign(ins, "float4(cos(" + Scalar(P[1]) + "), sin(" + Scalar(P[1]) + "), 0.0, 0.0)");
        return true;

    case OpM4x4: case OpM4x3: case OpM3x4: case OpM3x3: case OpM3x2: {
        if (!need(3)) return false;
        const int rows = ins.op == OpM4x4 || ins.op == OpM3x4 ? 4 : ins.op == OpM3x2 ? 2 : 3;
        const bool four = ins.op == OpM4x4 || ins.op == OpM4x3;
        std::string parts[4] = { "0.0", "0.0", "0.0", "0.0" };
        for (int r = 0; r < rows; r++) {
            parts[r] = four
                ? "dot(" + src(1) + ", " + Source(P[2], r) + ")"
                : "dot((" + src(1) + ").xyz, (" + Source(P[2], r) + ").xyz)";
        }
        Assign(ins, "float4(" + parts[0] + ", " + parts[1] + ", " + parts[2] + ", " + parts[3] + ")");
        return true;
    }

    case OpTexKill:
        if (!need(1)) return false;
        Line("if (any(" + RegName(P[0]) + " < 0.0)) discard;");
        return true;

    case OpTex: case OpTexLdl: case OpTexLdd: {
        if (!need(3)) return false;
        const uint32_t s = P[2].num;
        if (s >= 16) return Fail("sampler index out of range");
        const SamplerType type = res.samplers[s];
        const char* comps = type == SamplerType::Cube || type == SamplerType::Volume ? ".xyz" : ".xy";
        const std::string coord = "(" + src(1) + ")";
        const std::string texture = "t" + std::to_string(s);
        const std::string sampler = "s" + std::to_string(s);
        std::string value;
        if (ins.op == OpTexLdl || stage == Stage::Vertex) {
            value = texture + ".SampleLevel(" + sampler + ", " + coord + comps + ", " + coord + ".w)";
        } else if (ins.op == OpTexLdd) {
            if (!need(5)) return false;
            value = texture + ".SampleGrad(" + sampler + ", " + coord + comps + ", (" + src(3) + ")" + comps +
                    ", (" + src(4) + ")" + comps + ")";
        } else if (loopDepth > 0) {
            // Gradients are undefined inside a D3D11 loop; D3D9 let texld
            // through. Sample the top level rather than refuse the shader.
            value = texture + ".SampleLevel(" + sampler + ", " + coord + comps +
                    ((ins.control & 1) ? " / " + coord + ".w" : "") + ", 0.0)";
        } else if (ins.control & 1) {       // texldp
            value = texture + ".Sample(" + sampler + ", " + coord + comps + " / " + coord + ".w)";
        } else if (ins.control & 2) {       // texldb
            value = texture + ".SampleBias(" + sampler + ", " + coord + comps + ", " + coord + ".w)";
        } else {
            value = texture + ".Sample(" + sampler + ", " + coord + comps + ")";
        }
        // ps_2_x+ may swizzle the fetched value through the sampler operand.
        const std::string sw = Swizzle(P[2].token);
        if (sw != ".xyzw") value = "(" + value + ")" + sw;
        Assign(ins, value);
        return true;
    }

    case OpSetP: {
        if (!need(3)) return false;
        const char* cmp = CompareOp(ins.control);
        if (!cmp) return Fail("setp without a comparison");
        Assign(ins, "float4(" + src(1) + " " + cmp + " " + src(2) + ")");
        return true;
    }

    // --- flow control ----------------------------------------------------
    case OpIf:
        if (!need(1)) return false;
        Line("if (" + (P[0].type == RegPredicate ? PredicateComponent(P[0]) : RegName(P[0])) + ") {");
        indent++;
        return true;
    case OpIfC: {
        if (!need(2)) return false;
        const char* cmp = CompareOp(ins.control);
        if (!cmp) return Fail("ifc without a comparison");
        Line("if (" + Scalar(P[0]) + " " + cmp + " " + Scalar(P[1]) + ") {");
        indent++;
        return true;
    }
    case OpElse:
        indent--; Line("} else {"); indent++;
        return true;
    case OpEndIf:
        indent--; Line("}");
        return true;
    case OpRep: {
        if (!need(1)) return false;
        const std::string n = "nativra_rep" + std::to_string(uniqueId++);
        Line("[loop] for (int " + n + " = 0; " + n + " < min(" + RegName(P[0]) + ".x, 255); " + n + "++) {");
        indent++; loopDepth++;
        return true;
    }
    case OpEndRep:
        indent--; loopDepth--; Line("}");
        return true;
    case OpLoop: {
        if (!need(2)) return false;
        const std::string id = std::to_string(uniqueId++);
        const std::string ic = RegName(P[1]);
        Line("int nativra_savedAL" + id + " = aL;");
        Line("aL = " + ic + ".y;");
        Line("[loop] for (int nativra_loop" + id + " = 0; nativra_loop" + id + " < min(" + ic +
             ".x, 255); nativra_loop" + id + "++, aL += " + ic + ".z) {");
        indent++; loopDepth++;
        loopRestore.push_back("aL = nativra_savedAL" + id + ";");
        return true;
    }
    case OpEndLoop:
        indent--; loopDepth--; Line("}");
        if (!loopRestore.empty()) { Line(loopRestore.back()); loopRestore.pop_back(); }
        return true;
    case OpBreak:
        Line("break;");
        return true;
    case OpBreakC: {
        if (!need(2)) return false;
        const char* cmp = CompareOp(ins.control);
        if (!cmp) return Fail("breakc without a comparison");
        Line("if (" + Scalar(P[0]) + " " + cmp + " " + Scalar(P[1]) + ") break;");
        return true;
    }
    case OpBreakP:
        if (!need(1)) return false;
        Line("if (" + PredicateComponent(P[0]) + ") break;");
        return true;
    case OpCall:
        if (!need(1)) return false;
        Line("nativra_label" + std::to_string(P[0].num) + "();");
        return true;
    case OpCallNz:
        if (!need(2)) return false;
        Line("if (" + (P[1].type == RegPredicate ? PredicateComponent(P[1]) : RegName(P[1])) +
             ") nativra_label" + std::to_string(P[0].num) + "();");
        return true;
    case OpRet:
        Line("return;");
        return true;

    default:
        return Fail("unsupported opcode " + std::to_string(ins.op));
    }
}

bool Translator::EmitFunction(size_t begin, size_t end, std::ostringstream& body)
{
    out = &body;
    indent = 1;
    loopDepth = 0;
    for (size_t k = begin; k < end; k++)
        if (!EmitInstruction(program[k])) return false;
    return res.error.empty();
}

// --- assembly ----------------------------------------------------------------

const char* const kLit =
    "float4 nativra_lit(float4 s)\n"
    "{\n"
    "    float4 d = float4(1.0, 0.0, 0.0, 1.0);\n"
    "    float power = clamp(s.w, -127.9961, 127.9961);\n"
    "    if (s.x > 0.0) {\n"
    "        d.y = s.x;\n"
    "        if (s.y > 0.0) d.z = pow(s.y, power);\n"
    "    }\n"
    "    return d;\n"
    "}\n\n";

std::string Translator::Assemble(const std::map<uint32_t, std::string>& labelBodies, const std::string& mainBody)
{
    const bool vs = stage == Stage::Vertex;
    std::ostringstream s;
    s << "// Translated by nativra dxso from " << (vs ? "vs_" : "ps_") << major << "_" << minor << "\n\n";
    s << "cbuffer NativraFloat : register(b0) { float4 c[256]; };\n";
    s << "cbuffer NativraInt : register(b1) { int4 ci[16]; };\n";
    s << "cbuffer NativraBool : register(b2) { int4 cb[16]; };\n";
    s << "cbuffer NativraFixup : register(b3) { float4 nativra_fix[4]; };\n\n";

    for (int k = 0; k < 16; k++) {
        const SamplerType type = res.samplers[k];
        if (type == SamplerType::None) continue;
        const char* texType = type == SamplerType::Cube ? "TextureCube" : type == SamplerType::Volume ? "Texture3D" : "Texture2D";
        s << texType << " t" << k << " : register(t" << k << ");\n";
        s << "SamplerState s" << k << " : register(s" << k << ");\n";
    }
    s << "\n";

    for (uint32_t k = 0; k < maxTemp; k++) s << "static float4 r" << k << ";\n";
    s << "static float4 v[16];\n";
    s << "static int4 a0;\nstatic int aL;\nstatic bool4 p0;\n";
    if (vs) {
        if (major >= 3) s << "static float4 o[12];\n";
        else s << "static float4 oPos;\nstatic float oFog;\nstatic float oPts;\nstatic float4 oD[2];\nstatic float4 oT[8];\n";
    } else {
        s << "static float4 t[8];\nstatic float4 oC[4];\nstatic float oDepth;\nstatic float4 vPos;\nstatic float vFace;\n";
    }
    s << "\n";

    if (relativeConst) {
        s << "float4 C(int i)\n{\n";
        for (const auto& d : defF) {
            s << "    if (i == " << d.first << ") return float4(" << FloatLiteral(d.second[0]) << ", "
              << FloatLiteral(d.second[1]) << ", " << FloatLiteral(d.second[2]) << ", "
              << FloatLiteral(d.second[3]) << ");\n";
        }
        s << "    return c[i];\n}\n\n";
    }
    s << kLit;

    for (const auto& l : labelBodies) s << "void nativra_label" << l.first << "();\n";
    if (!labelBodies.empty()) s << "\n";
    for (const auto& l : labelBodies) s << "void nativra_label" << l.first << "()\n{\n" << l.second << "}\n\n";
    s << "void nativra_main()\n{\n" << mainBody << "}\n\n";

    // Varying struct: SV_Position then every slot, float4 each, fixed order.
    auto slots = [&](std::ostringstream& o, const std::set<int>& centroidSlots) {
        o << "    float4 pos : SV_Position;\n";
        for (int k = 0; k < VaryingSlots; k++) {
            o << "    " << (centroidSlots.count(k) ? "centroid " : "") << "float4 slot" << k << " : TEXCOORD" << k << ";\n";
        }
    };

    if (vs) {
        s << "struct NativraIn\n{\n";
        for (const auto& in : res.inputs) {
            const InputType type = in.reg < 16 ? options.inputTypes[in.reg] : InputType::Float;
            const char* hlslType = type == InputType::SInt ? "int4" : type == InputType::UInt ? "uint4" : "float4";
            s << "    " << hlslType << " i" << in.reg << " : " << UsageName(in.usage) << static_cast<int>(in.index) << ";\n";
        }
        if (res.inputs.empty()) s << "    uint nativra_vertex : SV_VertexID;\n";
        s << "};\n\nstruct NativraOut\n{\n";
        slots(s, {});
        s << "};\n\nNativraOut main(NativraIn input)\n{\n";
        for (const auto& in : res.inputs) s << "    v[" << in.reg << "] = float4(input.i" << in.reg << ");\n";
        s << "    nativra_main();\n";
        s << "    NativraOut output = (NativraOut)0;\n";
        if (major >= 3) {
            for (const auto& d : dcls) {
                if (d.type != RegTexCrdOut) continue;
                const std::string m = MaskSuffix(d.mask);
                if (d.usage == UsagePosition && d.index == 0) {
                    s << "    output.pos" << m << " = o[" << d.num << "]" << m << ";\n";
                } else {
                    const int slot = VaryingSlot(d.usage, d.index);
                    if (slot >= 0) s << "    output.slot" << slot << m << " = o[" << d.num << "]" << m << ";\n";
                }
            }
        } else {
            // vs_1_1/2_x: fixed outputs; colours are clamped as D3D9 does.
            s << "    output.pos = oPos;\n";
            s << "    output.slot" << VaryingSlot(UsageColor, 0) << " = saturate(oD[0]);\n";
            s << "    output.slot" << VaryingSlot(UsageColor, 1) << " = saturate(oD[1]);\n";
            for (int k = 0; k < 8; k++) s << "    output.slot" << VaryingSlot(UsageTexCoord, static_cast<uint8_t>(k)) << " = oT[" << k << "];\n";
            s << "    output.slot" << VaryingSlot(UsageFog, 0) << " = float4(oFog, 0.0, 0.0, 0.0);\n";
        }
        s << "    output.pos.xy += nativra_fix[0].xy * output.pos.w;\n";
        s << "    return output;\n}\n";
    } else {
        std::set<int> centroid;
        for (const auto& d : dcls) {
            if (!d.centroid) continue;
            int slot = -1;
            if (major >= 3 && d.type == RegInput) slot = VaryingSlot(d.usage, d.index);
            else if (d.type == RegAddr) slot = VaryingSlot(UsageTexCoord, static_cast<uint8_t>(d.num));
            else if (d.type == RegInput) slot = VaryingSlot(UsageColor, static_cast<uint8_t>(d.num));
            if (slot >= 0) centroid.insert(slot);
        }
        s << "struct NativraIn\n{\n";
        slots(s, centroid);
        if (usesFace) s << "    bool face : SV_IsFrontFace;\n";
        s << "};\n\nstruct NativraOut\n{\n";
        const uint32_t targets = res.renderTargets | 1u;
        for (int k = 0; k < 4; k++) if (targets & (1u << k)) s << "    float4 c" << k << " : SV_Target" << k << ";\n";
        if (res.writesDepth) s << "    float depth : SV_Depth;\n";
        s << "};\n\nNativraOut main(NativraIn input)\n{\n";
        for (const auto& d : dcls) {
            const std::string m = MaskSuffix(d.mask);
            int slot = -1;
            std::string reg;
            if (major >= 3 && d.type == RegInput) {
                slot = VaryingSlot(d.usage, d.index);
                reg = "v[" + std::to_string(d.num) + "]";
            } else if (d.type == RegAddr) {          // ps_2_x t#
                slot = VaryingSlot(UsageTexCoord, static_cast<uint8_t>(d.num));
                reg = "t[" + std::to_string(d.num) + "]";
            } else if (d.type == RegInput) {         // ps_2_x v# colours
                slot = VaryingSlot(UsageColor, static_cast<uint8_t>(d.num));
                reg = "v[" + std::to_string(d.num) + "]";
            }
            if (slot >= 0) s << "    " << reg << m << " = input.slot" << slot << m << ";\n";
        }
        if (usesPos) s << "    vPos = float4(input.pos.xy - 0.5, 0.0, 0.0);\n";
        if (usesFace) s << "    vFace = input.face ? 1.0 : -1.0;\n";
        s << "    nativra_main();\n";
        // D3D9 alpha test, which D3D11 dropped: func in .x (D3DCMPFUNC), ref in .y.
        s << "    int nativra_func = (int)nativra_fix[0].x;\n";
        s << "    if (nativra_func != 0 && nativra_func != 8) {\n";
        s << "        float nativra_a = oC[0].a, nativra_ref = nativra_fix[0].y;\n";
        s << "        bool nativra_pass =\n";
        s << "            nativra_func == 2 ? nativra_a < nativra_ref : nativra_func == 3 ? nativra_a == nativra_ref :\n";
        s << "            nativra_func == 4 ? nativra_a <= nativra_ref : nativra_func == 5 ? nativra_a > nativra_ref :\n";
        s << "            nativra_func == 6 ? nativra_a != nativra_ref : nativra_func == 7 ? nativra_a >= nativra_ref : false;\n";
        s << "        if (!nativra_pass) discard;\n";
        s << "    }\n";
        s << "    NativraOut output;\n";
        for (int k = 0; k < 4; k++) if (targets & (1u << k)) s << "    output.c" << k << " = oC[" << k << "];\n";
        if (res.writesDepth) s << "    output.depth = oDepth;\n";
        s << "    return output;\n}\n";
    }
    return s.str();
}

Result Translator::Run(const uint32_t* tokens, size_t count)
{
    if (!Decode(tokens, count) || !Collect()) return res;
    res.stage = stage;
    res.major = major;
    res.minor = minor;
    res.profile = stage == Stage::Vertex ? "vs_5_0" : "ps_5_0";

    // Main is everything before the first label; each label runs to the next.
    size_t firstLabel = program.size();
    for (size_t k = 0; k < program.size(); k++)
        if (program[k].op == OpLabel) { firstLabel = k; break; }

    std::ostringstream mainBody;
    if (!EmitFunction(0, firstLabel, mainBody)) return res;

    std::map<uint32_t, std::string> labelBodies;
    for (size_t k = firstLabel; k < program.size();) {
        const Instruction& label = program[k];
        if (label.params.empty()) { Fail("label without a number"); return res; }
        size_t end = k + 1;
        while (end < program.size() && program[end].op != OpLabel) end++;
        std::ostringstream body;
        if (!EmitFunction(k + 1, end, body)) return res;
        labelBodies[label.params[0].num] = body.str();
        k = end;
    }

    res.hlsl = Assemble(labelBodies, mainBody.str());
    res.ok = res.error.empty();
    return res;
}

} // namespace

int VaryingSlot(uint8_t usage, uint8_t index)
{
    switch (usage) {
    case UsageTexCoord: return index < 16 ? index : -1;
    case UsageColor: return index < 4 ? 16 + index : -1;
    case UsageFog: return index == 0 ? 20 : -1;
    case UsageNormal: return index < 2 ? 21 + index : -1;
    case UsageTangent: return index == 0 ? 23 : -1;
    case UsageBinormal: return index == 0 ? 24 : -1;
    case UsageBlendWeight: return index == 0 ? 25 : -1;
    case UsageBlendIndices: return index == 0 ? 26 : -1;
    case UsagePSize: return index == 0 ? 27 : -1;
    case UsagePosition: return index == 1 ? 28 : -1;   // POSITION0 is SV_Position
    case UsageDepth: return index == 0 ? 29 : -1;
    default: return -1;
    }
}

const char* UsageName(uint8_t usage)
{
    static const char* const names[] = {
        "POSITION", "BLENDWEIGHT", "BLENDINDICES", "NORMAL", "PSIZE", "TEXCOORD", "TANGENT",
        "BINORMAL", "TESSFACTOR", "POSITIONT", "COLOR", "FOG", "DEPTH", "SAMPLE",
    };
    return usage < sizeof(names) / sizeof(names[0]) ? names[usage] : "UNKNOWN";
}

Result Translate(const uint32_t* tokens, size_t count, const Options& options)
{
    Translator t(options);
    return t.Run(tokens, count);
}

} // namespace dxso
