// Drawing: D3D9 state to D3D11 state objects, input layouts, constants,
// primitive conversion (fans, user-pointer draws), clears and copies.

#include "d3d9_objects.h"

#include <d3dcompiler.h>

#include <algorithm>
#include <cfloat>
#include <cstdio>

namespace d3d9 {

#define LOCK_DEVICE std::lock_guard<std::recursive_mutex> guard(mutex)

namespace {

float AsFloat(DWORD v) { float f; std::memcpy(&f, &v, sizeof f); return f; }

void ColorToFloat(D3DCOLOR c, float out[4])
{
    out[0] = ((c >> 16) & 0xFF) / 255.0f;
    out[1] = ((c >> 8) & 0xFF) / 255.0f;
    out[2] = (c & 0xFF) / 255.0f;
    out[3] = ((c >> 24) & 0xFF) / 255.0f;
}

D3D11_BLEND BlendFactor(DWORD b, bool alpha)
{
    switch (b) {
    case D3DBLEND_ZERO: return D3D11_BLEND_ZERO;
    case D3DBLEND_ONE: return D3D11_BLEND_ONE;
    case D3DBLEND_SRCCOLOR: return alpha ? D3D11_BLEND_SRC_ALPHA : D3D11_BLEND_SRC_COLOR;
    case D3DBLEND_INVSRCCOLOR: return alpha ? D3D11_BLEND_INV_SRC_ALPHA : D3D11_BLEND_INV_SRC_COLOR;
    case D3DBLEND_SRCALPHA: return D3D11_BLEND_SRC_ALPHA;
    case D3DBLEND_INVSRCALPHA: return D3D11_BLEND_INV_SRC_ALPHA;
    case D3DBLEND_DESTALPHA: return D3D11_BLEND_DEST_ALPHA;
    case D3DBLEND_INVDESTALPHA: return D3D11_BLEND_INV_DEST_ALPHA;
    case D3DBLEND_DESTCOLOR: return alpha ? D3D11_BLEND_DEST_ALPHA : D3D11_BLEND_DEST_COLOR;
    case D3DBLEND_INVDESTCOLOR: return alpha ? D3D11_BLEND_INV_DEST_ALPHA : D3D11_BLEND_INV_DEST_COLOR;
    case D3DBLEND_SRCALPHASAT: return D3D11_BLEND_SRC_ALPHA_SAT;
    case D3DBLEND_BLENDFACTOR: return D3D11_BLEND_BLEND_FACTOR;
    case D3DBLEND_INVBLENDFACTOR: return D3D11_BLEND_INV_BLEND_FACTOR;
    case D3DBLEND_SRCCOLOR2: return alpha ? D3D11_BLEND_SRC1_ALPHA : D3D11_BLEND_SRC1_COLOR;
    case D3DBLEND_INVSRCCOLOR2: return alpha ? D3D11_BLEND_INV_SRC1_ALPHA : D3D11_BLEND_INV_SRC1_COLOR;
    default: return D3D11_BLEND_ONE;
    }
}

D3D11_BLEND_OP BlendOp(DWORD op)
{
    return op >= D3DBLENDOP_ADD && op <= D3DBLENDOP_MAX ? static_cast<D3D11_BLEND_OP>(op) : D3D11_BLEND_OP_ADD;
}

D3D11_COMPARISON_FUNC Compare(DWORD f)
{
    return f >= D3DCMP_NEVER && f <= D3DCMP_ALWAYS ? static_cast<D3D11_COMPARISON_FUNC>(f) : D3D11_COMPARISON_ALWAYS;
}

D3D11_STENCIL_OP StencilOp(DWORD op)
{
    return op >= D3DSTENCILOP_KEEP && op <= D3DSTENCILOP_DECR ? static_cast<D3D11_STENCIL_OP>(op) : D3D11_STENCIL_OP_KEEP;
}

D3D11_TEXTURE_ADDRESS_MODE Address(DWORD a)
{
    return a >= D3DTADDRESS_WRAP && a <= D3DTADDRESS_MIRRORONCE ? static_cast<D3D11_TEXTURE_ADDRESS_MODE>(a)
                                                                 : D3D11_TEXTURE_ADDRESS_WRAP;
}

D3D11_PRIMITIVE_TOPOLOGY Topology(D3DPRIMITIVETYPE type)
{
    switch (type) {
    case D3DPT_POINTLIST: return D3D11_PRIMITIVE_TOPOLOGY_POINTLIST;
    case D3DPT_LINELIST: return D3D11_PRIMITIVE_TOPOLOGY_LINELIST;
    case D3DPT_LINESTRIP: return D3D11_PRIMITIVE_TOPOLOGY_LINESTRIP;
    case D3DPT_TRIANGLESTRIP: return D3D11_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP;
    default: return D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST;
    }
}

UINT VertexCount(D3DPRIMITIVETYPE type, UINT primitives)
{
    switch (type) {
    case D3DPT_POINTLIST: return primitives;
    case D3DPT_LINELIST: return primitives * 2;
    case D3DPT_LINESTRIP: return primitives + 1;
    case D3DPT_TRIANGLELIST: return primitives * 3;
    case D3DPT_TRIANGLESTRIP: case D3DPT_TRIANGLEFAN: return primitives + 2;
    default: return 0;
    }
}

const char* const kBlitHlsl = R"(
cbuffer Blit : register(b0) { float4 srcRect; };
Texture2DArray source : register(t0);
SamplerState smp : register(s0);
struct V { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
V vsmain(uint id : SV_VertexID)
{
    V o;
    float2 p = float2((id << 1) & 2, id & 2);
    o.uv = srcRect.xy + p * srcRect.zw;
    o.pos = float4(p * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    return o;
}
float4 psmain(V i) : SV_Target { return source.Sample(smp, float3(i.uv, 0.0)); }
)";

} // namespace

// --- blitter --------------------------------------------------------------------

bool Device::InitBlitter()
{
    ID3DBlob* code = nullptr;
    ID3DBlob* errors = nullptr;
    if (FAILED(D3DCompile(kBlitHlsl, std::strlen(kBlitHlsl), "blit", nullptr, nullptr, "vsmain", "vs_5_0", 0, 0, &code, &errors))) {
        Log("blit vs: %s", errors ? static_cast<const char*>(errors->GetBufferPointer()) : "?");
        SafeRelease(errors);
        return false;
    }
    dev->CreateVertexShader(code->GetBufferPointer(), code->GetBufferSize(), nullptr, &blitVs);
    code->Release();
    if (FAILED(D3DCompile(kBlitHlsl, std::strlen(kBlitHlsl), "blit", nullptr, nullptr, "psmain", "ps_5_0", 0, 0, &code, &errors))) {
        SafeRelease(errors);
        return false;
    }
    dev->CreatePixelShader(code->GetBufferPointer(), code->GetBufferSize(), nullptr, &blitPs);
    code->Release();

    for (int k = 0; k < 2; k++) {
        D3D11_SAMPLER_DESC sd = {};
        sd.Filter = k ? D3D11_FILTER_MIN_MAG_MIP_LINEAR : D3D11_FILTER_MIN_MAG_MIP_POINT;
        sd.AddressU = sd.AddressV = sd.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
        sd.MaxLOD = D3D11_FLOAT32_MAX;
        dev->CreateSamplerState(&sd, &blitSampler[k]);
    }
    D3D11_BUFFER_DESC bd = {};
    bd.ByteWidth = 16;
    bd.Usage = D3D11_USAGE_DYNAMIC;
    bd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    bd.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
    dev->CreateBuffer(&bd, nullptr, &blitConstants);
    D3D11_RASTERIZER_DESC rd = {};
    rd.FillMode = D3D11_FILL_SOLID;
    rd.CullMode = D3D11_CULL_NONE;
    rd.DepthClipEnable = TRUE;
    dev->CreateRasterizerState(&rd, &blitRaster);
    D3D11_BLEND_DESC bld = {};
    bld.RenderTarget[0].RenderTargetWriteMask = D3D11_COLOR_WRITE_ENABLE_ALL;
    dev->CreateBlendState(&bld, &blitBlend);
    D3D11_DEPTH_STENCIL_DESC dsd = {};
    dev->CreateDepthStencilState(&dsd, &blitDepth);
    return blitVs && blitPs && blitSampler[0] && blitSampler[1] && blitConstants && blitRaster && blitBlend && blitDepth;
}

// Scaled or converting copy of one subresource into another, by drawing.
bool Device::Blit(Image* src, UINT srcSub, const RECT& srcRect, Image* dst, UINT dstSub, const RECT& dstRect, bool linear)
{
    if (!src->texture || !dst->texture) return false;
    ID3D11RenderTargetView* rtv = dst->Rtv(dstSub, false);
    if (!rtv) return false;

    D3D11_SHADER_RESOURCE_VIEW_DESC sd = {};
    sd.Format = src->fmt->view;
    sd.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2DARRAY;
    sd.Texture2DArray.MostDetailedMip = srcSub % src->levels;
    sd.Texture2DArray.MipLevels = 1;
    sd.Texture2DArray.FirstArraySlice = srcSub / src->levels;
    sd.Texture2DArray.ArraySize = 1;
    ID3D11ShaderResourceView* srv = nullptr;
    if (FAILED(dev->CreateShaderResourceView(src->texture, &sd, &srv))) return false;

    const Subresource& s = src->subs[srcSub];
    D3D11_MAPPED_SUBRESOURCE mapped;
    if (SUCCEEDED(ctx->Map(blitConstants, 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped))) {
        const float rect[4] = {
            srcRect.left / static_cast<float>(s.width), srcRect.top / static_cast<float>(s.height),
            (srcRect.right - srcRect.left) / static_cast<float>(s.width),
            (srcRect.bottom - srcRect.top) / static_cast<float>(s.height),
        };
        std::memcpy(mapped.pData, rect, sizeof rect);
        ctx->Unmap(blitConstants, 0);
    }

    ctx->OMSetRenderTargets(1, &rtv, nullptr);
    D3D11_VIEWPORT vp = { static_cast<float>(dstRect.left), static_cast<float>(dstRect.top),
                          static_cast<float>(dstRect.right - dstRect.left),
                          static_cast<float>(dstRect.bottom - dstRect.top), 0.0f, 1.0f };
    ctx->RSSetViewports(1, &vp);
    ctx->RSSetState(blitRaster);
    ctx->OMSetBlendState(blitBlend, nullptr, 0xFFFFFFFF);
    ctx->OMSetDepthStencilState(blitDepth, 0);
    ctx->IASetInputLayout(nullptr);
    ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    ctx->VSSetShader(blitVs, nullptr, 0);
    ctx->PSSetShader(blitPs, nullptr, 0);
    ctx->PSSetConstantBuffers(0, 1, &blitConstants);
    ctx->VSSetConstantBuffers(0, 1, &blitConstants);
    ctx->PSSetShaderResources(0, 1, &srv);
    ctx->PSSetSamplers(0, 1, &blitSampler[linear ? 1 : 0]);
    ctx->Draw(3, 0);
    ID3D11ShaderResourceView* none = nullptr;
    ctx->PSSetShaderResources(0, 1, &none);
    srv->Release();
    return true;
}

// --- pipeline setup -------------------------------------------------------------

bool Device::BindShaders(UINT instanceMask)
{
    VertexDeclaration* decl = state.decl;
    if (!decl) return false;

    std::vector<dxso::InputDecl> inputs;
    if (state.vs) {
        // Integer vertex types need a variant that fetches them as integers.
        dxso::Options options;
        for (const auto& in : state.vs->base.inputs) {
            for (const auto& e : decl->elements) {
                if (e.Usage != in.usage || e.UsageIndex != in.index || in.reg >= 16) continue;
                int integer = 0;
                DeclTypeFormat(e.Type, &integer);
                options.inputTypes[in.reg] = integer == 1 ? dxso::InputType::SInt
                                           : integer == 2 ? dxso::InputType::UInt : dxso::InputType::Float;
                break;
            }
        }
        currentVs = state.vs->Variant(options);
        inputs = state.vs->base.inputs;
    } else {
        currentVs = FixedVertexShader(decl, &inputs);
    }
    currentPs = state.ps ? state.ps->Variant(dxso::Options()) : FixedPixelShader();
    if (!currentVs || !currentPs) {
        if (!warnedFixedFunction) {
            Log("draw skipped: no usable shader (vs %s, ps %s)", state.vs ? "translated" : "fixed",
                state.ps ? "translated" : "fixed");
            warnedFixedFunction = true;
        }
        return false;
    }
    usingFixed = !state.vs || !state.ps;

    ID3D11InputLayout* layout = InputLayout(currentVs, decl, instanceMask, inputs);
    if (!layout) return false;
    ctx->IASetInputLayout(layout);
    ctx->VSSetShader(static_cast<ID3D11VertexShader*>(currentVs->shader), nullptr, 0);
    ctx->PSSetShader(static_cast<ID3D11PixelShader*>(currentPs->shader), nullptr, 0);
    return true;
}

ID3D11InputLayout* Device::InputLayout(ShaderVariant* vs, const VertexDeclaration* decl, UINT instanceMask,
                                       const std::vector<dxso::InputDecl>& inputs)
{
    const auto key = std::make_pair((decl->id << 16) | instanceMask, vs->shader);
    auto it = inputLayouts.find(key);
    if (it != inputLayouts.end()) return it->second;

    std::vector<D3D11_INPUT_ELEMENT_DESC> elements;
    std::vector<std::pair<BYTE, BYTE>> seen;
    for (const auto& e : decl->elements) {
        const auto sem = std::make_pair(e.Usage, e.UsageIndex);
        if (std::find(seen.begin(), seen.end(), sem) != seen.end()) continue;   // D3D11 rejects duplicates
        int integer = 0;
        const DXGI_FORMAT format = DeclTypeFormat(e.Type, &integer);
        if (format == DXGI_FORMAT_UNKNOWN) continue;
        seen.push_back(sem);
        D3D11_INPUT_ELEMENT_DESC d = {};
        d.SemanticName = dxso::UsageName(e.Usage);
        d.SemanticIndex = e.UsageIndex;
        d.Format = format;
        d.InputSlot = e.Stream;
        d.AlignedByteOffset = e.Offset;
        const bool perInstance = (instanceMask >> e.Stream) & 1;
        d.InputSlotClass = perInstance ? D3D11_INPUT_PER_INSTANCE_DATA : D3D11_INPUT_PER_VERTEX_DATA;
        d.InstanceDataStepRate = perInstance ? 1 : 0;
        elements.push_back(d);
    }
    // Inputs the shader reads but the declaration does not supply read zeros.
    for (const auto& in : inputs) {
        const auto sem = std::make_pair(in.usage, in.index);
        if (std::find(seen.begin(), seen.end(), sem) != seen.end()) continue;
        seen.push_back(sem);
        D3D11_INPUT_ELEMENT_DESC d = {};
        d.SemanticName = dxso::UsageName(in.usage);
        d.SemanticIndex = in.index;
        d.Format = DXGI_FORMAT_R32G32B32A32_FLOAT;
        d.InputSlot = 16;
        d.InputSlotClass = D3D11_INPUT_PER_VERTEX_DATA;
        elements.push_back(d);
    }

    ID3D11InputLayout* layout = nullptr;
    const HRESULT hr = dev->CreateInputLayout(elements.data(), static_cast<UINT>(elements.size()),
                                              vs->bytecode->GetBufferPointer(), vs->bytecode->GetBufferSize(), &layout);
    if (FAILED(hr)) {
        Log("CreateInputLayout failed: 0x%08lX", static_cast<unsigned long>(hr));
        layout = nullptr;
    }
    inputLayouts[key] = layout;
    return layout;
}

void Device::FlushConstants()
{
    auto upload = [this](ID3D11Buffer* buffer, const void* data, size_t bytes) {
        D3D11_MAPPED_SUBRESOURCE mapped;
        if (FAILED(ctx->Map(buffer, 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped))) return;
        std::memcpy(mapped.pData, data, bytes);
        ctx->Unmap(buffer, 0);
    };
    auto bools = [](const BOOL* in, int out[16][4]) {
        for (int k = 0; k < 16; k++) { out[k][0] = in[k] ? 1 : 0; out[k][1] = out[k][2] = out[k][3] = 0; }
    };

    if (vsConstDirty) {
        upload(cbVs[0], state.vsF, sizeof state.vsF);
        upload(cbVs[1], state.vsI, sizeof state.vsI);
        int b[16][4];
        bools(state.vsB, b);
        upload(cbVs[2], b, sizeof b);
        vsConstDirty = false;
    }
    if (psConstDirty) {
        float f[256][4] = {};
        std::memcpy(f, state.psF, sizeof state.psF);
        upload(cbPs[0], f, sizeof f);
        upload(cbPs[1], state.psI, sizeof state.psI);
        int b[16][4];
        bools(state.psB, b);
        upload(cbPs[2], b, sizeof b);
        psConstDirty = false;
    }

    // D3D9's half-pixel offset and alpha test, which D3D11 lacks.
    const float vw = state.viewport.Width ? static_cast<float>(state.viewport.Width) : 1.0f;
    const float vh = state.viewport.Height ? static_cast<float>(state.viewport.Height) : 1.0f;
    float vsFix[16] = { 1.0f / vw, -1.0f / vh };
    if (std::memcmp(vsFix, lastVsFix, sizeof vsFix) != 0) {
        upload(cbVs[3], vsFix, sizeof vsFix);
        std::memcpy(lastVsFix, vsFix, sizeof vsFix);
    }
    float psFix[16] = {};
    psFix[0] = state.rs[D3DRS_ALPHATESTENABLE] ? static_cast<float>(state.rs[D3DRS_ALPHAFUNC]) : 8.0f;
    psFix[1] = (state.rs[D3DRS_ALPHAREF] & 0xFF) / 255.0f;
    if (std::memcmp(psFix, lastPsFix, sizeof psFix) != 0) {
        upload(cbPs[3], psFix, sizeof psFix);
        std::memcpy(lastPsFix, psFix, sizeof psFix);
    }
    ctx->VSSetConstantBuffers(0, 4, cbVs);
    ctx->PSSetConstantBuffers(0, 4, cbPs);
}

void Device::BindOutputs()
{
    ID3D11RenderTargetView* rtvs[4] = {};
    const bool srgb = state.rs[D3DRS_SRGBWRITEENABLE] != 0;
    for (int k = 0; k < 4; k++) {
        Surface* s = renderTargets[k];
        if (s) rtvs[k] = s->image->Rtv(s->sub, srgb);
    }
    ID3D11DepthStencilView* dsv = nullptr;
    Surface* ds = depthStencil;
    Surface* rt0 = renderTargets[0];
    if (ds && rt0) {
        const Subresource& a = ds->image->subs[ds->sub];
        const Subresource& b = rt0->image->subs[rt0->sub];
        // D3D11 refuses a depth buffer whose size differs from the targets;
        // D3D9 allows a larger one. Draw without depth rather than not at all.
        if (a.width == b.width && a.height == b.height) {
            dsv = ds->image->Dsv(ds->sub);
        } else if (!warnedDepthSize) {
            Log("depth buffer %ux%u does not match render target %ux%u; drawing without it",
                a.width, a.height, b.width, b.height);
            warnedDepthSize = true;
        }
    }
    ctx->OMSetRenderTargets(4, rtvs, dsv);
    boundDepthFormat = dsv ? ds->image->format : D3DFMT_UNKNOWN;

    D3D11_VIEWPORT vp = { static_cast<float>(state.viewport.X), static_cast<float>(state.viewport.Y),
                          static_cast<float>(state.viewport.Width), static_cast<float>(state.viewport.Height),
                          state.viewport.MinZ, state.viewport.MaxZ };
    ctx->RSSetViewports(1, &vp);
    D3D11_RECT scissor = { state.scissor.left, state.scissor.top, state.scissor.right, state.scissor.bottom };
    ctx->RSSetScissorRects(1, &scissor);
}

void Device::BindStates()
{
    const DWORD* rs = state.rs;

    // Blend.
    const uint64_t blendKey =
        (uint64_t(rs[D3DRS_ALPHABLENDENABLE] ? 1 : 0)) | (uint64_t(rs[D3DRS_SRCBLEND] & 31) << 1) |
        (uint64_t(rs[D3DRS_DESTBLEND] & 31) << 6) | (uint64_t(rs[D3DRS_BLENDOP] & 7) << 11) |
        (uint64_t(rs[D3DRS_SEPARATEALPHABLENDENABLE] ? 1 : 0) << 14) | (uint64_t(rs[D3DRS_SRCBLENDALPHA] & 31) << 15) |
        (uint64_t(rs[D3DRS_DESTBLENDALPHA] & 31) << 20) | (uint64_t(rs[D3DRS_BLENDOPALPHA] & 7) << 25) |
        (uint64_t(rs[D3DRS_COLORWRITEENABLE] & 15) << 28) | (uint64_t(rs[D3DRS_COLORWRITEENABLE1] & 15) << 32) |
        (uint64_t(rs[D3DRS_COLORWRITEENABLE2] & 15) << 36) | (uint64_t(rs[D3DRS_COLORWRITEENABLE3] & 15) << 40);
    ID3D11BlendState*& blend = blendStates[blendKey];
    if (!blend) {
        D3D11_BLEND_DESC d = {};
        d.IndependentBlendEnable = TRUE;
        DWORD src = rs[D3DRS_SRCBLEND], dst = rs[D3DRS_DESTBLEND];
        if (src == D3DBLEND_BOTHSRCALPHA) { src = D3DBLEND_SRCALPHA; dst = D3DBLEND_INVSRCALPHA; }
        if (src == D3DBLEND_BOTHINVSRCALPHA) { src = D3DBLEND_INVSRCALPHA; dst = D3DBLEND_SRCALPHA; }
        const bool separate = rs[D3DRS_SEPARATEALPHABLENDENABLE] != 0;
        const DWORD writes[4] = { rs[D3DRS_COLORWRITEENABLE], rs[D3DRS_COLORWRITEENABLE1],
                                  rs[D3DRS_COLORWRITEENABLE2], rs[D3DRS_COLORWRITEENABLE3] };
        for (int k = 0; k < 4; k++) {
            auto& t = d.RenderTarget[k];
            t.BlendEnable = rs[D3DRS_ALPHABLENDENABLE] ? TRUE : FALSE;
            t.SrcBlend = BlendFactor(src, false);
            t.DestBlend = BlendFactor(dst, false);
            t.BlendOp = BlendOp(rs[D3DRS_BLENDOP]);
            t.SrcBlendAlpha = BlendFactor(separate ? rs[D3DRS_SRCBLENDALPHA] : src, true);
            t.DestBlendAlpha = BlendFactor(separate ? rs[D3DRS_DESTBLENDALPHA] : dst, true);
            t.BlendOpAlpha = BlendOp(separate ? rs[D3DRS_BLENDOPALPHA] : rs[D3DRS_BLENDOP]);
            t.RenderTargetWriteMask = static_cast<UINT8>(writes[k] & 15);
        }
        if (FAILED(dev->CreateBlendState(&d, &blend))) blend = nullptr;
    }
    float factor[4];
    ColorToFloat(rs[D3DRS_BLENDFACTOR], factor);
    ctx->OMSetBlendState(blend, factor, 0xFFFFFFFF);

    // Depth and stencil.
    const uint64_t depthKey =
        uint64_t(rs[D3DRS_ZENABLE] ? 1 : 0) | (uint64_t(rs[D3DRS_ZWRITEENABLE] ? 1 : 0) << 1) |
        (uint64_t(rs[D3DRS_ZFUNC] & 15) << 2) | (uint64_t(rs[D3DRS_STENCILENABLE] ? 1 : 0) << 6) |
        (uint64_t(rs[D3DRS_STENCILFAIL] & 15) << 7) | (uint64_t(rs[D3DRS_STENCILZFAIL] & 15) << 11) |
        (uint64_t(rs[D3DRS_STENCILPASS] & 15) << 15) | (uint64_t(rs[D3DRS_STENCILFUNC] & 15) << 19) |
        (uint64_t(rs[D3DRS_STENCILMASK] & 0xFF) << 23) | (uint64_t(rs[D3DRS_STENCILWRITEMASK] & 0xFF) << 31) |
        (uint64_t(rs[D3DRS_TWOSIDEDSTENCILMODE] ? 1 : 0) << 39) | (uint64_t(rs[D3DRS_CCW_STENCILFAIL] & 15) << 40) |
        (uint64_t(rs[D3DRS_CCW_STENCILZFAIL] & 15) << 44) | (uint64_t(rs[D3DRS_CCW_STENCILPASS] & 15) << 48) |
        (uint64_t(rs[D3DRS_CCW_STENCILFUNC] & 15) << 52);
    ID3D11DepthStencilState*& depth = depthStates[depthKey];
    if (!depth) {
        D3D11_DEPTH_STENCIL_DESC d = {};
        d.DepthEnable = rs[D3DRS_ZENABLE] ? TRUE : FALSE;
        d.DepthWriteMask = rs[D3DRS_ZWRITEENABLE] ? D3D11_DEPTH_WRITE_MASK_ALL : D3D11_DEPTH_WRITE_MASK_ZERO;
        d.DepthFunc = Compare(rs[D3DRS_ZFUNC]);
        d.StencilEnable = rs[D3DRS_STENCILENABLE] ? TRUE : FALSE;
        d.StencilReadMask = static_cast<UINT8>(rs[D3DRS_STENCILMASK]);
        d.StencilWriteMask = static_cast<UINT8>(rs[D3DRS_STENCILWRITEMASK]);
        // D3D9 front faces are clockwise, as D3D11's default.
        d.FrontFace = { StencilOp(rs[D3DRS_STENCILFAIL]), StencilOp(rs[D3DRS_STENCILZFAIL]),
                        StencilOp(rs[D3DRS_STENCILPASS]), Compare(rs[D3DRS_STENCILFUNC]) };
        d.BackFace = rs[D3DRS_TWOSIDEDSTENCILMODE]
            ? D3D11_DEPTH_STENCILOP_DESC{ StencilOp(rs[D3DRS_CCW_STENCILFAIL]), StencilOp(rs[D3DRS_CCW_STENCILZFAIL]),
                                          StencilOp(rs[D3DRS_CCW_STENCILPASS]), Compare(rs[D3DRS_CCW_STENCILFUNC]) }
            : d.FrontFace;
        if (FAILED(dev->CreateDepthStencilState(&d, &depth))) depth = nullptr;
    }
    ctx->OMSetDepthStencilState(depth, rs[D3DRS_STENCILREF] & 0xFF);

    // Rasterizer. D3D9's depth bias is in depth units; D3D11's in units of
    // the depth format's resolution.
    float biasScale = 16777215.0f;   // D24
    if (boundDepthFormat == D3DFMT_D16 || boundDepthFormat == D3DFMT_D16_LOCKABLE) biasScale = 65535.0f;
    else if (boundDepthFormat == D3DFMT_D32 || boundDepthFormat == D3DFMT_D32F_LOCKABLE) biasScale = 8388608.0f;
    const INT bias = static_cast<INT>(AsFloat(rs[D3DRS_DEPTHBIAS]) * biasScale);
    char rasterKey[32];
    std::snprintf(rasterKey, sizeof rasterKey, "%lu:%lu:%lu:%d:%lu", static_cast<unsigned long>(rs[D3DRS_FILLMODE]),
                  static_cast<unsigned long>(rs[D3DRS_CULLMODE]), static_cast<unsigned long>(rs[D3DRS_SCISSORTESTENABLE]),
                  bias, static_cast<unsigned long>(rs[D3DRS_SLOPESCALEDEPTHBIAS]));
    ID3D11RasterizerState*& raster = rasterStates[rasterKey];
    if (!raster) {
        D3D11_RASTERIZER_DESC d = {};
        d.FillMode = rs[D3DRS_FILLMODE] == D3DFILL_WIREFRAME ? D3D11_FILL_WIREFRAME : D3D11_FILL_SOLID;
        d.CullMode = rs[D3DRS_CULLMODE] == D3DCULL_CW ? D3D11_CULL_FRONT
                   : rs[D3DRS_CULLMODE] == D3DCULL_CCW ? D3D11_CULL_BACK : D3D11_CULL_NONE;
        d.FrontCounterClockwise = FALSE;
        d.DepthBias = bias;
        d.SlopeScaledDepthBias = AsFloat(rs[D3DRS_SLOPESCALEDEPTHBIAS]);
        d.DepthClipEnable = TRUE;
        d.ScissorEnable = rs[D3DRS_SCISSORTESTENABLE] ? TRUE : FALSE;
        if (FAILED(dev->CreateRasterizerState(&d, &raster))) raster = nullptr;
    }
    ctx->RSSetState(raster);
}

ID3D11SamplerState* Device::Sampler(UINT slot)
{
    const DWORD* ss = state.ss[slot];
    const std::string key(reinterpret_cast<const char*>(ss), sizeof(DWORD) * kMaxSs);
    ID3D11SamplerState*& sampler = samplerStates[key];
    if (sampler) return sampler;

    D3D11_SAMPLER_DESC d = {};
    const DWORD minF = ss[D3DSAMP_MINFILTER], magF = ss[D3DSAMP_MAGFILTER], mipF = ss[D3DSAMP_MIPFILTER];
    const bool aniso = (minF == D3DTEXF_ANISOTROPIC || magF == D3DTEXF_ANISOTROPIC) && ss[D3DSAMP_MAXANISOTROPY] > 1;
    if (aniso) {
        d.Filter = D3D11_FILTER_ANISOTROPIC;
        d.MaxAnisotropy = std::min<DWORD>(ss[D3DSAMP_MAXANISOTROPY], 16);
    } else {
        const bool minLinear = minF >= D3DTEXF_LINEAR, magLinear = magF >= D3DTEXF_LINEAR;
        const bool mipLinear = mipF >= D3DTEXF_LINEAR;
        d.Filter = static_cast<D3D11_FILTER>((minLinear ? 0x10 : 0) | (magLinear ? 0x4 : 0) | (mipLinear ? 0x1 : 0));
        d.MaxAnisotropy = 1;
    }
    d.AddressU = Address(ss[D3DSAMP_ADDRESSU]);
    d.AddressV = Address(ss[D3DSAMP_ADDRESSV]);
    d.AddressW = Address(ss[D3DSAMP_ADDRESSW]);
    d.MipLODBias = std::max(-16.0f, std::min(15.99f, AsFloat(ss[D3DSAMP_MIPMAPLODBIAS])));
    d.ComparisonFunc = D3D11_COMPARISON_NEVER;
    ColorToFloat(ss[D3DSAMP_BORDERCOLOR], d.BorderColor);
    d.MinLOD = static_cast<float>(ss[D3DSAMP_MAXMIPLEVEL]);
    d.MaxLOD = mipF == D3DTEXF_NONE ? d.MinLOD : D3D11_FLOAT32_MAX;
    if (FAILED(dev->CreateSamplerState(&d, &sampler))) sampler = nullptr;
    return sampler;
}

void Device::BindTextures()
{
    ID3D11ShaderResourceView* srvs[16] = {};
    ID3D11SamplerState* samplers[16] = {};
    for (UINT s = 0; s < 16; s++) {
        Image* image = ImageOf(state.textures[s]);
        if (image) srvs[s] = image->Srv(state.ss[s][D3DSAMP_SRGBTEXTURE] != 0);
        samplers[s] = Sampler(s);
    }
    ctx->PSSetShaderResources(0, 16, srvs);
    ctx->PSSetSamplers(0, 16, samplers);

    ID3D11ShaderResourceView* vsrvs[4] = {};
    ID3D11SamplerState* vsamplers[4] = {};
    for (UINT s = 0; s < 4; s++) {
        Image* image = ImageOf(state.textures[17 + s]);
        if (image) vsrvs[s] = image->Srv(state.ss[17 + s][D3DSAMP_SRGBTEXTURE] != 0);
        vsamplers[s] = Sampler(17 + s);
    }
    ctx->VSSetShaderResources(0, 4, vsrvs);
    ctx->VSSetSamplers(0, 4, vsamplers);
}

// Binds everything a draw needs. `instances` receives the instance count
// (1 unless D3D9 stream-frequency instancing is on).
bool Device::PrepareDraw(UINT* instances)
{
    *instances = 1;
    UINT instanceMask = 0;
    if (state.streams[0].frequency & D3DSTREAMSOURCE_INDEXEDDATA) {
        *instances = std::max(1u, state.streams[0].frequency & 0x3FFFFFFF);
        for (UINT s = 1; s < 16; s++)
            if (state.streams[s].frequency & D3DSTREAMSOURCE_INSTANCEDATA) instanceMask |= 1u << s;
    }
    if (!BindShaders(instanceMask)) return false;

    ID3D11Buffer* buffers[17] = {};
    UINT strides[17] = {}, offsets[17] = {};
    for (UINT s = 0; s < 16; s++) {
        const Stream& st = state.streams[s];
        if (s == 0 && upStream.buffer) {
            buffers[0] = upStream.buffer;
            strides[0] = upStream.stride;
            offsets[0] = upStream.offset;
            continue;
        }
        if (!st.buffer) continue;
        buffers[s] = st.buffer->data_.buffer;
        strides[s] = st.stride;
        offsets[s] = st.offset;
    }
    buffers[16] = zeroBuffer;
    ctx->IASetVertexBuffers(0, 17, buffers, strides, offsets);

    FlushConstants();
    if (usingFixed) UploadFixedConstants();
    BindOutputs();
    BindStates();
    BindTextures();
    return true;
}

// --- draws ------------------------------------------------------------------------

HRESULT Device::UploadUp(const void* data, UINT bytes, UINT* offset, ID3D11Buffer** buffer, bool index)
{
    ID3D11Buffer*& ring = index ? upIndices : upVertices;
    UINT& cursor = index ? upIndexCursor : upVertexCursor;
    UINT& capacity = index ? upIndexCapacity : upVertexCapacity;
    if (!ring || bytes > capacity) {
        SafeRelease(ring);
        capacity = std::max<UINT>(bytes, index ? (1u << 20) : (4u << 20));
        D3D11_BUFFER_DESC d = {};
        d.ByteWidth = capacity;
        d.Usage = D3D11_USAGE_DYNAMIC;
        d.BindFlags = index ? D3D11_BIND_INDEX_BUFFER : D3D11_BIND_VERTEX_BUFFER;
        d.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
        if (FAILED(dev->CreateBuffer(&d, nullptr, &ring))) return E_OUTOFMEMORY;
        cursor = capacity;   // forces a discard on first use
    }
    D3D11_MAP mode = D3D11_MAP_WRITE_NO_OVERWRITE;
    if (cursor + bytes > capacity) { cursor = 0; mode = D3D11_MAP_WRITE_DISCARD; }
    D3D11_MAPPED_SUBRESOURCE mapped;
    if (FAILED(ctx->Map(ring, 0, mode, 0, &mapped))) return E_FAIL;
    std::memcpy(static_cast<uint8_t*>(mapped.pData) + cursor, data, bytes);
    ctx->Unmap(ring, 0);
    *offset = cursor;
    *buffer = ring;
    cursor += (bytes + 15) & ~15u;
    return S_OK;
}

// D3D11 has no triangle fans: expand to a list through an index buffer.
HRESULT Device::DrawFan(bool indexed, INT baseVertex, UINT start, UINT count, const void* upIndices, D3DFORMAT upFormat)
{
    std::vector<uint32_t> list;
    list.reserve(static_cast<size_t>(count) * 3);
    auto source = [&](UINT i) -> uint32_t {
        if (upIndices) {
            return upFormat == D3DFMT_INDEX32 ? static_cast<const uint32_t*>(upIndices)[start + i]
                                              : static_cast<const uint16_t*>(upIndices)[start + i];
        }
        if (!indexed) return i;
        IndexBuffer* ib = state.indices;
        return ib->format == D3DFMT_INDEX32
            ? reinterpret_cast<const uint32_t*>(ib->data_.shadow)[start + i]
            : reinterpret_cast<const uint16_t*>(ib->data_.shadow)[start + i];
    };
    for (UINT t = 0; t < count; t++) {
        list.push_back(source(0));
        list.push_back(source(t + 1));
        list.push_back(source(t + 2));
    }
    UINT offset = 0;
    ID3D11Buffer* buffer = nullptr;
    if (FAILED(UploadUp(list.data(), static_cast<UINT>(list.size() * 4), &offset, &buffer, true))) return E_OUTOFMEMORY;
    ctx->IASetIndexBuffer(buffer, DXGI_FORMAT_R32_UINT, offset);
    ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    ctx->DrawIndexed(static_cast<UINT>(list.size()), 0, indexed || upIndices ? baseVertex : static_cast<INT>(start));
    return D3D_OK;
}

HRESULT Device::DrawPrimitive(D3DPRIMITIVETYPE type, UINT start, UINT count)
{
    LOCK_DEVICE;
    if (count == 0) return D3D_OK;
    UINT instances;
    if (!PrepareDraw(&instances)) return D3D_OK;
    if (type == D3DPT_TRIANGLEFAN) return DrawFan(false, 0, start, count, nullptr, D3DFMT_UNKNOWN);
    ctx->IASetPrimitiveTopology(Topology(type));
    const UINT vertices = VertexCount(type, count);
    if (instances > 1) ctx->DrawInstanced(vertices, instances, start, 0);
    else ctx->Draw(vertices, start);
    return D3D_OK;
}

HRESULT Device::DrawIndexedPrimitive(D3DPRIMITIVETYPE type, INT baseVertex, UINT, UINT, UINT startIndex, UINT count)
{
    LOCK_DEVICE;
    if (count == 0) return D3D_OK;
    if (!state.indices) return D3DERR_INVALIDCALL;
    UINT instances;
    if (!PrepareDraw(&instances)) return D3D_OK;
    if (type == D3DPT_TRIANGLEFAN) return DrawFan(true, baseVertex, startIndex, count, nullptr, D3DFMT_UNKNOWN);
    const DXGI_FORMAT format = state.indices->format == D3DFMT_INDEX32 ? DXGI_FORMAT_R32_UINT : DXGI_FORMAT_R16_UINT;
    ctx->IASetIndexBuffer(state.indices->data_.buffer, format, 0);
    ctx->IASetPrimitiveTopology(Topology(type));
    const UINT indices = VertexCount(type, count);
    if (instances > 1) ctx->DrawIndexedInstanced(indices, instances, startIndex, baseVertex, 0);
    else ctx->DrawIndexed(indices, startIndex, baseVertex);
    return D3D_OK;
}

HRESULT Device::DrawPrimitiveUP(D3DPRIMITIVETYPE type, UINT count, const void* data, UINT stride)
{
    LOCK_DEVICE;
    if (!data || stride == 0) return D3DERR_INVALIDCALL;
    if (count == 0) return D3D_OK;
    const UINT vertices = VertexCount(type, count);
    UINT offset = 0;
    ID3D11Buffer* buffer = nullptr;
    if (FAILED(UploadUp(data, vertices * stride, &offset, &buffer, false))) return E_OUTOFMEMORY;
    upStream = { buffer, offset, stride };

    UINT instances;
    HRESULT hr = D3D_OK;
    if (PrepareDraw(&instances)) {
        if (type == D3DPT_TRIANGLEFAN) {
            hr = DrawFan(false, 0, 0, count, nullptr, D3DFMT_UNKNOWN);
        } else {
            ctx->IASetPrimitiveTopology(Topology(type));
            ctx->Draw(vertices, 0);
        }
    }
    upStream = {};
    // D3D9 leaves stream 0 unbound after a user-pointer draw.
    SetStreamSource(0, nullptr, 0, 0);
    return hr;
}

HRESULT Device::DrawIndexedPrimitiveUP(D3DPRIMITIVETYPE type, UINT minIndex, UINT vertices, UINT count,
                                       const void* indexData, D3DFORMAT format, const void* data, UINT stride)
{
    LOCK_DEVICE;
    if (!data || !indexData || stride == 0) return D3DERR_INVALIDCALL;
    if (count == 0) return D3D_OK;
    UINT vbOffset = 0;
    ID3D11Buffer* vb = nullptr;
    if (FAILED(UploadUp(data, (minIndex + vertices) * stride, &vbOffset, &vb, false))) return E_OUTOFMEMORY;
    upStream = { vb, vbOffset, stride };

    UINT instances;
    HRESULT hr = D3D_OK;
    if (PrepareDraw(&instances)) {
        if (type == D3DPT_TRIANGLEFAN) {
            hr = DrawFan(true, 0, 0, count, indexData, format);
        } else {
            const UINT indices = VertexCount(type, count);
            const UINT indexSize = format == D3DFMT_INDEX32 ? 4 : 2;
            UINT ibOffset = 0;
            ID3D11Buffer* ib = nullptr;
            if (SUCCEEDED(UploadUp(indexData, indices * indexSize, &ibOffset, &ib, true))) {
                ctx->IASetIndexBuffer(ib, indexSize == 4 ? DXGI_FORMAT_R32_UINT : DXGI_FORMAT_R16_UINT, ibOffset);
                ctx->IASetPrimitiveTopology(Topology(type));
                ctx->DrawIndexed(indices, 0, 0);
            }
        }
    }
    upStream = {};
    SetStreamSource(0, nullptr, 0, 0);
    SetIndices(nullptr);
    return hr;
}

// --- clears and copies --------------------------------------------------------------

HRESULT Device::Clear(DWORD count, const D3DRECT* rects, DWORD flags, D3DCOLOR color, float z, DWORD stencil)
{
    LOCK_DEVICE;
    Surface* rt0 = renderTargets[0];
    if (!rt0) return D3DERR_INVALIDCALL;
    const Subresource& target = rt0->image->subs[rt0->sub];

    // D3D9 clears the viewport, further limited by any rectangles given.
    const RECT viewport = { static_cast<LONG>(state.viewport.X), static_cast<LONG>(state.viewport.Y),
                            static_cast<LONG>(state.viewport.X + state.viewport.Width),
                            static_cast<LONG>(state.viewport.Y + state.viewport.Height) };
    std::vector<D3D11_RECT> area;
    if (count && rects) {
        for (DWORD k = 0; k < count; k++) {
            D3D11_RECT r = { std::max<LONG>(rects[k].x1, viewport.left), std::max<LONG>(rects[k].y1, viewport.top),
                             std::min<LONG>(rects[k].x2, viewport.right), std::min<LONG>(rects[k].y2, viewport.bottom) };
            if (r.right > r.left && r.bottom > r.top) area.push_back(r);
        }
        if (area.empty()) return D3D_OK;
    }
    const bool whole = area.empty() && viewport.left == 0 && viewport.top == 0 &&
                       static_cast<UINT>(viewport.right) >= target.width && static_cast<UINT>(viewport.bottom) >= target.height;
    if (area.empty()) area.push_back({ viewport.left, viewport.top, viewport.right, viewport.bottom });

    if (flags & D3DCLEAR_TARGET) {
        float c[4];
        ColorToFloat(color, c);
        const bool srgb = state.rs[D3DRS_SRGBWRITEENABLE] != 0;
        for (int k = 0; k < 4; k++) {
            Surface* s = renderTargets[k];
            if (!s) continue;
            ID3D11RenderTargetView* rtv = s->image->Rtv(s->sub, srgb);
            if (!rtv) continue;
            if (whole || !ctx1) ctx->ClearRenderTargetView(rtv, c);
            else ctx1->ClearView(rtv, c, area.data(), static_cast<UINT>(area.size()));
        }
    }
    if ((flags & (D3DCLEAR_ZBUFFER | D3DCLEAR_STENCIL)) && depthStencil) {
        ID3D11DepthStencilView* dsv = depthStencil->image->Dsv(depthStencil->sub);
        if (dsv) {
            if (!whole && !warnedPartialDepthClear) {
                Log("partial depth clear done as a full clear");
                warnedPartialDepthClear = true;
            }
            UINT d3d11Flags = 0;
            if (flags & D3DCLEAR_ZBUFFER) d3d11Flags |= D3D11_CLEAR_DEPTH;
            if (flags & D3DCLEAR_STENCIL) d3d11Flags |= D3D11_CLEAR_STENCIL;
            ctx->ClearDepthStencilView(dsv, d3d11Flags, z, static_cast<UINT8>(stencil));
        }
    }
    return D3D_OK;
}

HRESULT Device::StretchRect(IDirect3DSurface9* srcSurface, const RECT* srcRect, IDirect3DSurface9* dstSurface,
                            const RECT* dstRect, D3DTEXTUREFILTERTYPE filter)
{
    LOCK_DEVICE;
    auto* src = static_cast<Surface*>(srcSurface);
    auto* dst = static_cast<Surface*>(dstSurface);
    if (!src || !dst) return D3DERR_INVALIDCALL;
    const Subresource& ss = src->image->subs[src->sub];
    const Subresource& ds = dst->image->subs[dst->sub];
    const RECT sr = srcRect ? *srcRect : RECT{ 0, 0, static_cast<LONG>(ss.width), static_cast<LONG>(ss.height) };
    const RECT dr = dstRect ? *dstRect : RECT{ 0, 0, static_cast<LONG>(ds.width), static_cast<LONG>(ds.height) };
    const bool sameSize = sr.right - sr.left == dr.right - dr.left && sr.bottom - sr.top == dr.bottom - dr.top;

    if (src->image->texture && dst->image->texture) {
        const bool sameFormat = src->image->fmt->typeless == dst->image->fmt->typeless;
        if (sameSize && sameFormat) {
            const bool depth = src->image->IsDepth();
            if (depth) {
                // Depth copies must cover the whole subresource.
                if (sr.left || sr.top || static_cast<UINT>(sr.right) != ss.width || static_cast<UINT>(sr.bottom) != ss.height)
                    return D3DERR_INVALIDCALL;
                ctx->CopySubresourceRegion(dst->image->texture, dst->sub, 0, 0, 0, src->image->texture, src->sub, nullptr);
            } else {
                D3D11_BOX box = { static_cast<UINT>(sr.left), static_cast<UINT>(sr.top), 0,
                                  static_cast<UINT>(sr.right), static_cast<UINT>(sr.bottom), 1 };
                ctx->CopySubresourceRegion(dst->image->texture, dst->sub, static_cast<UINT>(dr.left),
                                           static_cast<UINT>(dr.top), 0, src->image->texture, src->sub, &box);
            }
            return D3D_OK;
        }
        if (!src->image->IsDepth() &&
            Blit(src->image, src->sub, sr, dst->image, dst->sub, dr, filter != D3DTEXF_POINT && filter != D3DTEXF_NONE))
            return D3D_OK;
        Log("StretchRect between formats %d and %d not supported", static_cast<int>(src->image->format),
            static_cast<int>(dst->image->format));
        return D3DERR_INVALIDCALL;
    }
    return D3DERR_INVALIDCALL;
}

HRESULT Device::ColorFill(IDirect3DSurface9* surface, const RECT* rect, D3DCOLOR color)
{
    LOCK_DEVICE;
    auto* s = static_cast<Surface*>(surface);
    if (!s) return D3DERR_INVALIDCALL;
    float c[4];
    ColorToFloat(color, c);
    ID3D11RenderTargetView* rtv = s->image->Rtv(s->sub, false);
    if (rtv) {
        if (rect && ctx1) {
            const D3D11_RECT r = { rect->left, rect->top, rect->right, rect->bottom };
            ctx1->ClearView(rtv, c, &r, 1);
        } else {
            ctx->ClearRenderTargetView(rtv, c);
        }
        return D3D_OK;
    }
    // Not a render target: fill the CPU copy (32-bit colour formats only).
    if (s->image->fmt->d3dBytes != 4 || s->image->fmt->block) return D3DERR_INVALIDCALL;
    D3DLOCKED_RECT locked;
    if (FAILED(s->image->Lock(s->sub, &locked, rect, 0))) return D3DERR_INVALIDCALL;
    const Subresource& sub = s->image->subs[s->sub];
    const UINT w = rect ? static_cast<UINT>(rect->right - rect->left) : sub.width;
    const UINT h = rect ? static_cast<UINT>(rect->bottom - rect->top) : sub.height;
    const bool abgr = s->image->format == D3DFMT_A8B8G8R8 || s->image->format == D3DFMT_X8B8G8R8;
    const D3DCOLOR value = abgr ? ((color & 0xFF00FF00) | ((color >> 16) & 0xFF) | ((color & 0xFF) << 16)) : color;
    for (UINT y = 0; y < h; y++) {
        auto* row = reinterpret_cast<D3DCOLOR*>(static_cast<uint8_t*>(locked.pBits) + static_cast<size_t>(y) * locked.Pitch);
        for (UINT x = 0; x < w; x++) row[x] = value;
    }
    return s->image->Unlock(s->sub);
}

HRESULT Device::UpdateSurface(IDirect3DSurface9* srcSurface, const RECT* srcRect, IDirect3DSurface9* dstSurface,
                              const POINT* dstPoint)
{
    LOCK_DEVICE;
    auto* src = static_cast<Surface*>(srcSurface);
    auto* dst = static_cast<Surface*>(dstSurface);
    if (!src || !dst || src->image->format != dst->image->format || !dst->image->texture) return D3DERR_INVALIDCALL;
    const Subresource& ss = src->image->subs[src->sub];
    const RECT r = srcRect ? *srcRect : RECT{ 0, 0, static_cast<LONG>(ss.width), static_cast<LONG>(ss.height) };
    const POINT p = dstPoint ? *dstPoint : POINT{ 0, 0 };
    uint8_t* shadow = src->image->Shadow(src->sub);
    if (!shadow) return E_OUTOFMEMORY;

    const FormatInfo& f = *src->image->fmt;
    const UINT width = static_cast<UINT>(r.right - r.left), height = static_cast<UINT>(r.bottom - r.top);
    const UINT rows = RowCount(f, height);
    const uint8_t* start = shadow + (f.block ? (r.top / 4) * ss.pitch + (r.left / 4) * f.d3dBytes
                                             : r.top * ss.pitch + r.left * f.d3dBytes);
    std::vector<uint8_t> converted;
    const void* data = start;
    UINT pitch = ss.pitch;
    if (f.convert != Convert::None) {
        pitch = RowPitch(f, width, false);
        converted.resize(static_cast<size_t>(pitch) * rows);
        ConvertRows(f, start, ss.pitch, converted.data(), pitch, width, rows);
        data = converted.data();
    }
    const D3D11_BOX box = { static_cast<UINT>(p.x), static_cast<UINT>(p.y), 0,
                            static_cast<UINT>(p.x) + width, static_cast<UINT>(p.y) + height, 1 };
    ctx->UpdateSubresource(dst->image->texture, dst->sub, &box, data, pitch, 0);
    return D3D_OK;
}

HRESULT Device::UpdateTexture(IDirect3DBaseTexture9* srcTexture, IDirect3DBaseTexture9* dstTexture)
{
    LOCK_DEVICE;
    Image* src = ImageOf(srcTexture);
    Image* dst = ImageOf(dstTexture);
    if (!src || !dst || src->format != dst->format || src->faces != dst->faces || !dst->texture) return D3DERR_INVALIDCALL;
    // Match levels by size: the destination may carry fewer, smaller ones.
    UINT srcFirst = 0;
    while (srcFirst < src->levels && src->subs[srcFirst].width != dst->width) srcFirst++;
    if (srcFirst == src->levels) return D3DERR_INVALIDCALL;
    for (UINT face = 0; face < dst->faces; face++) {
        for (UINT level = 0; level < dst->levels && srcFirst + level < src->levels; level++) {
            const UINT s = src->Sub(face, srcFirst + level);
            if (!src->subs[s].shadow) continue;   // never written
            const Subresource& sub = src->subs[s];
            const FormatInfo& f = *src->fmt;
            const UINT rows = RowCount(f, sub.height);
            if (f.convert == Convert::None) {
                ctx->UpdateSubresource(dst->texture, dst->Sub(face, level), nullptr, sub.shadow, sub.pitch, 0);
            } else {
                const UINT pitch = RowPitch(f, sub.width, false);
                std::vector<uint8_t> converted(static_cast<size_t>(pitch) * rows);
                ConvertRows(f, sub.shadow, sub.pitch, converted.data(), pitch, sub.width, rows);
                ctx->UpdateSubresource(dst->texture, dst->Sub(face, level), nullptr, converted.data(), pitch, 0);
            }
        }
    }
    if ((dst->usage & D3DUSAGE_AUTOGENMIPMAP) && dst->Srv(false)) ctx->GenerateMips(dst->Srv(false));
    return D3D_OK;
}

HRESULT Device::GetRenderTargetData(IDirect3DSurface9* rtSurface, IDirect3DSurface9* dstSurface)
{
    LOCK_DEVICE;
    auto* rt = static_cast<Surface*>(rtSurface);
    auto* dst = static_cast<Surface*>(dstSurface);
    if (!rt || !dst || rt->image->format != dst->image->format) return D3DERR_INVALIDCALL;
    const Subresource& a = rt->image->subs[rt->sub];
    const Subresource& b = dst->image->subs[dst->sub];
    if (a.width != b.width || a.height != b.height) return D3DERR_INVALIDCALL;
    if (!rt->image->ReadBack(rt->sub)) return D3DERR_DRIVERINTERNALERROR;
    uint8_t* to = dst->image->Shadow(dst->sub);
    if (!to) return E_OUTOFMEMORY;
    const UINT rows = RowCount(*rt->image->fmt, a.height);
    const UINT bytes = std::min(a.pitch, b.pitch);
    for (UINT y = 0; y < rows; y++)
        std::memcpy(to + static_cast<size_t>(y) * b.pitch, a.shadow + static_cast<size_t>(y) * a.pitch, bytes);
    dst->image->subs[dst->sub].valid = true;
    if (dst->image->texture) dst->image->Upload(dst->sub);
    return D3D_OK;
}

HRESULT Device::GetFrontBufferData(UINT swapchain, IDirect3DSurface9* dst)
{
    if (swapchain != 0) return D3DERR_INVALIDCALL;
    return GetRenderTargetData(swapChain->backBuffer, dst);
}

} // namespace d3d9
