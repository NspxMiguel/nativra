// A small text assembler for pixel shader 1.x, for the dxso tests: it turns
//
//     ps_1_1
//     tex t0
//     mul_x2_sat r0.rgb, t0_bx2, 1-v0
//     + mov r0.a, v0.a
//
// into the token stream a game hands to CreatePixelShader. The d3dcompiler that
// ships with Windows no longer targets ps_1_x and the D3DX assembler is gone, so
// the tests write the shaders as assembly and encode them here; that keeps them
// readable and independent of the translator under test.
//
// Syntax (one instruction per line, ';' starts a comment):
//   ps_M_N                         version (ps.M.N is accepted too)
//   def cN, a, b, c, d             constant
//   mnemonic[_x2|_x4|_x8|_d2|_d4|_d8][_sat][_pp] dst[.mask], src, ...
//   + mnemonic ...                 co-issued with the previous instruction
//   phase                          ps_1_4 phase marker
// dst: r#, t#, v#, c# with an optional .rgba mask. src: [-|1-]reg[_bias|_bx2|_x2|
// _dz|_dw][.swizzle]; a swizzle of fewer than four letters repeats its last one
// (.a is .aaaa, .rga is .rgaa), as in the assembler Microsoft shipped.

#pragma once

#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>

namespace ps1asm {

struct Assembled {
    std::vector<uint32_t> tokens;
    std::string error;      // empty when the text assembled
};

namespace detail {

struct Mnemonic {
    const char* name;
    uint32_t opcode;
};

// D3DSIO_* values; texcrd and texld are the ps_1_4 spellings of texcoord and tex.
const Mnemonic kMnemonics[] = {
    { "nop", 0 }, { "mov", 1 }, { "add", 2 }, { "sub", 3 }, { "mad", 4 }, { "mul", 5 }, { "dp3", 8 },
    { "dp4", 9 }, { "lrp", 18 }, { "texcoord", 64 }, { "texcrd", 64 }, { "texkill", 65 }, { "tex", 66 },
    { "texld", 66 }, { "texbem", 67 }, { "texbeml", 68 }, { "texreg2ar", 69 }, { "texreg2gb", 70 },
    { "texm3x2pad", 71 }, { "texm3x2tex", 72 }, { "texm3x3pad", 73 }, { "texm3x3tex", 74 },
    { "texm3x3spec", 76 }, { "texm3x3vspec", 77 }, { "cnd", 80 }, { "texreg2rgb", 82 },
    { "texdp3tex", 83 }, { "texm3x2depth", 84 }, { "texdp3", 85 }, { "texm3x3", 86 }, { "texdepth", 87 },
    { "cmp", 88 }, { "bem", 89 },
};

inline std::string Trim(const std::string& s)
{
    size_t b = 0, e = s.size();
    while (b < e && (s[b] == ' ' || s[b] == '\t' || s[b] == '\r')) b++;
    while (e > b && (s[e - 1] == ' ' || s[e - 1] == '\t' || s[e - 1] == '\r')) e--;
    return s.substr(b, e - b);
}

inline std::vector<std::string> Split(const std::string& s, char separator)
{
    std::vector<std::string> parts;
    size_t start = 0;
    for (;;) {
        const size_t at = s.find(separator, start);
        if (at == std::string::npos) {
            parts.push_back(s.substr(start));
            return parts;
        }
        parts.push_back(s.substr(start, at - start));
        start = at + 1;
    }
}

inline int Component(char c)
{
    switch (c) {
    case 'r': case 'x': return 0;
    case 'g': case 'y': return 1;
    case 'b': case 'z': return 2;
    case 'a': case 'w': return 3;
    default: return -1;
    }
}

// A register token: the type's low three bits go to 28..30, the rest to 11..12.
inline bool Register(const std::string& name, uint32_t* token, std::string* error)
{
    if (name.size() < 2) { *error = "bad register '" + name + "'"; return false; }
    uint32_t type;
    switch (name[0]) {
    case 'r': type = 0; break;
    case 'v': type = 1; break;
    case 'c': type = 2; break;
    case 't': type = 3; break;     // texture register
    default: *error = "bad register '" + name + "'"; return false;
    }
    char* end = nullptr;
    const unsigned long number = std::strtoul(name.c_str() + 1, &end, 10);
    if (*end != '\0') { *error = "bad register '" + name + "'"; return false; }
    *token = 0x80000000u | ((type & 7) << 28) | ((type & 0x18) << 8) | static_cast<uint32_t>(number);
    return true;
}

// dst[.mask] with the result modifiers of the mnemonic already folded in by the caller.
inline bool Destination(const std::string& text, uint32_t* token, std::string* error)
{
    std::string name = text, mask;
    const size_t dot = text.find('.');
    if (dot != std::string::npos) { name = text.substr(0, dot); mask = text.substr(dot + 1); }
    if (!Register(name, token, error)) return false;
    uint32_t bits = 0xF;
    if (!mask.empty()) {
        bits = 0;
        for (char c : mask) {
            const int k = Component(c);
            if (k < 0) { *error = "bad write mask '" + text + "'"; return false; }
            bits |= 1u << k;
        }
    }
    *token |= bits << 16;
    return true;
}

inline bool Source(const std::string& text, uint32_t* token, std::string* error)
{
    std::string rest = text;
    bool negate = false, complement = false;
    if (rest.compare(0, 2, "1-") == 0) { complement = true; rest = rest.substr(2); }
    else if (!rest.empty() && rest[0] == '-') { negate = true; rest = rest.substr(1); }

    std::string swizzle, modifier;
    size_t at = rest.find('.');
    if (at != std::string::npos) { swizzle = rest.substr(at + 1); rest = rest.substr(0, at); }
    at = rest.find('_');
    if (at != std::string::npos) { modifier = rest.substr(at + 1); rest = rest.substr(0, at); }
    if (!Register(rest, token, error)) return false;

    // D3DSPSM_*: none, neg, bias, biasneg, sign (bx2), signneg, comp (1-x), x2, x2neg, dz, dw.
    uint32_t mod;
    if (complement && modifier.empty()) mod = 6;
    else if (complement) { *error = "1- cannot be combined with _" + modifier; return false; }
    else if (modifier.empty()) mod = negate ? 1 : 0;
    else if (modifier == "bias") mod = negate ? 3 : 2;
    else if (modifier == "bx2") mod = negate ? 5 : 4;
    else if (modifier == "x2") mod = negate ? 8 : 7;
    else if ((modifier == "dz" || modifier == "dw") && !negate) mod = modifier == "dz" ? 9 : 10;
    else { *error = "bad source modifier in '" + text + "'"; return false; }

    uint32_t swz = 0xE4;     // .rgba
    if (!swizzle.empty()) {
        if (swizzle.size() > 4) { *error = "bad swizzle in '" + text + "'"; return false; }
        swz = 0;
        int last = 0;
        for (size_t k = 0; k < 4; k++) {
            if (k < swizzle.size()) {
                last = Component(swizzle[k]);
                if (last < 0) { *error = "bad swizzle in '" + text + "'"; return false; }
            }
            swz |= static_cast<uint32_t>(last) << (2 * k);
        }
    }
    *token |= (swz << 16) | (mod << 24);
    return true;
}

inline uint32_t FloatBits(float f)
{
    uint32_t bits;
    std::memcpy(&bits, &f, sizeof bits);
    return bits;
}

} // namespace detail

inline Assembled Assemble(const std::string& text)
{
    using namespace detail;
    Assembled out;
    auto fail = [&](size_t lineNumber, const std::string& why) {
        out.tokens.clear();
        out.error = "line " + std::to_string(lineNumber) + ": " + why;
        return out;
    };

    const std::vector<std::string> lines = Split(text, '\n');
    bool haveVersion = false;
    for (size_t n = 0; n < lines.size(); n++) {
        std::string line = lines[n];
        const size_t comment = line.find(';');
        if (comment != std::string::npos) line = line.substr(0, comment);
        line = Trim(line);
        if (line.empty()) continue;

        if (!haveVersion) {
            // ps_M_N or ps.M.N
            if (line.size() != 6 || line.compare(0, 2, "ps") != 0 || line[3] < '0' || line[3] > '9' || line[5] < '0' || line[5] > '9')
                return fail(n + 1, "expected a version such as ps_1_1");
            out.tokens.push_back(0xFFFF0000u | (static_cast<uint32_t>(line[3] - '0') << 8) | static_cast<uint32_t>(line[5] - '0'));
            haveVersion = true;
            continue;
        }

        uint32_t coissue = 0;
        if (line[0] == '+') {
            coissue = 0x40000000u;
            line = Trim(line.substr(1));
        }
        if (line == "phase") {
            out.tokens.push_back(0xFFFD);
            continue;
        }

        const size_t space = line.find(' ');
        const std::string head = space == std::string::npos ? line : line.substr(0, space);
        const std::string rest = space == std::string::npos ? std::string() : Trim(line.substr(space + 1));
        std::vector<std::string> operands;
        if (!rest.empty()) {
            for (const std::string& piece : Split(rest, ',')) operands.push_back(Trim(piece));
        }

        const std::vector<std::string> words = Split(head, '_');
        if (words[0] == "def") {
            uint32_t reg;
            std::string error;
            if (operands.size() != 5 || !Register(operands[0], &reg, &error)) return fail(n + 1, "def needs a register and four values");
            out.tokens.push_back(0x51);
            out.tokens.push_back(reg);
            for (size_t k = 1; k < 5; k++) out.tokens.push_back(FloatBits(std::strtof(operands[k].c_str(), nullptr)));
            continue;
        }

        const Mnemonic* mnemonic = nullptr;
        for (const Mnemonic& m : kMnemonics)
            if (words[0] == m.name) mnemonic = &m;
        if (!mnemonic) return fail(n + 1, "unknown instruction '" + words[0] + "'");

        // Result modifiers, folded into the destination token: _sat is bit 20,
        // _pp bit 21 and the result scale a signed value in bits 24..27.
        uint32_t resultBits = 0;
        for (size_t k = 1; k < words.size(); k++) {
            const std::string& w = words[k];
            if (w == "sat") resultBits |= 1u << 20;
            else if (w == "pp") resultBits |= 1u << 21;
            else if (w == "x2") resultBits |= 1u << 24;
            else if (w == "x4") resultBits |= 2u << 24;
            else if (w == "x8") resultBits |= 3u << 24;
            else if (w == "d2") resultBits |= 15u << 24;
            else if (w == "d4") resultBits |= 14u << 24;
            else if (w == "d8") resultBits |= 13u << 24;
            else return fail(n + 1, "unknown modifier '_" + w + "'");
        }

        out.tokens.push_back(mnemonic->opcode | coissue);
        for (size_t k = 0; k < operands.size(); k++) {
            uint32_t token;
            std::string error;
            // texkill's only operand names a register, like every destination.
            const bool ok = k == 0 ? Destination(operands[k], &token, &error) : Source(operands[k], &token, &error);
            if (!ok) return fail(n + 1, error);
            if (k == 0) token |= resultBits;
            out.tokens.push_back(token);
        }
    }
    if (!haveVersion) return fail(1, "empty shader");
    out.tokens.push_back(0xFFFF);
    return out;
}

} // namespace ps1asm
