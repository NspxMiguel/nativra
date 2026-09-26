// Format tables, row conversion, vertex element types and host hooks.

#include "d3d9_common.h"

#include <cstdarg>
#include <cstdio>
#include <malloc.h>

namespace d3d9 {

Host& GetHost()
{
    static Host host;
    return host;
}

void* LockMemoryAlloc(size_t size)
{
    Host& h = GetHost();
    if (h.alloc) return h.alloc(size);
    return _aligned_malloc(size ? size : 1, 16);
}

void LockMemoryFree(void* p)
{
    if (!p) return;
    Host& h = GetHost();
    if (h.free) h.free(p);
    else _aligned_free(p);
}

void Log(const char* format, ...)
{
    char line[512];
    va_list args;
    va_start(args, format);
    std::vsnprintf(line, sizeof line, format, args);
    va_end(args);
    Host& h = GetHost();
    if (h.log) {
        h.log(line);
    } else {
        OutputDebugStringA("d3d9: ");
        OutputDebugStringA(line);
        OutputDebugStringA("\n");
    }
}

// --- formats ------------------------------------------------------------------

namespace {

constexpr D3DFORMAT FourCC(char a, char b, char c, char d)
{
    return static_cast<D3DFORMAT>(static_cast<uint32_t>(static_cast<uint8_t>(a)) |
                                  (static_cast<uint32_t>(static_cast<uint8_t>(b)) << 8) |
                                  (static_cast<uint32_t>(static_cast<uint8_t>(c)) << 16) |
                                  (static_cast<uint32_t>(static_cast<uint8_t>(d)) << 24));
}

#define U DXGI_FORMAT_UNKNOWN
const FormatInfo kFormats[] = {
    // d3d                    typeless                          view                              srgb                                depth                        d3d dxgi block convert
    { D3DFMT_A8R8G8B8,        DXGI_FORMAT_B8G8R8A8_TYPELESS,    DXGI_FORMAT_B8G8R8A8_UNORM,       DXGI_FORMAT_B8G8R8A8_UNORM_SRGB,    U, 4, 4, false, Convert::None },
    { D3DFMT_X8R8G8B8,        DXGI_FORMAT_B8G8R8A8_TYPELESS,    DXGI_FORMAT_B8G8R8A8_UNORM,       DXGI_FORMAT_B8G8R8A8_UNORM_SRGB,    U, 4, 4, false, Convert::X8ToA8 },
    { D3DFMT_R8G8B8,          DXGI_FORMAT_B8G8R8A8_TYPELESS,    DXGI_FORMAT_B8G8R8A8_UNORM,       DXGI_FORMAT_B8G8R8A8_UNORM_SRGB,    U, 3, 4, false, Convert::R8G8B8 },
    { D3DFMT_A8B8G8R8,        DXGI_FORMAT_R8G8B8A8_TYPELESS,    DXGI_FORMAT_R8G8B8A8_UNORM,       DXGI_FORMAT_R8G8B8A8_UNORM_SRGB,    U, 4, 4, false, Convert::None },
    { D3DFMT_X8B8G8R8,        DXGI_FORMAT_R8G8B8A8_TYPELESS,    DXGI_FORMAT_R8G8B8A8_UNORM,       DXGI_FORMAT_R8G8B8A8_UNORM_SRGB,    U, 4, 4, false, Convert::X8ToA8 },
    { D3DFMT_R5G6B5,          DXGI_FORMAT_B5G6R5_UNORM,         DXGI_FORMAT_B5G6R5_UNORM,         U,                                  U, 2, 2, false, Convert::None },
    { D3DFMT_X1R5G5B5,        DXGI_FORMAT_B5G5R5A1_UNORM,       DXGI_FORMAT_B5G5R5A1_UNORM,       U,                                  U, 2, 2, false, Convert::X1R5G5B5 },
    { D3DFMT_A1R5G5B5,        DXGI_FORMAT_B5G5R5A1_UNORM,       DXGI_FORMAT_B5G5R5A1_UNORM,       U,                                  U, 2, 2, false, Convert::None },
    { D3DFMT_A4R4G4B4,        DXGI_FORMAT_B4G4R4A4_UNORM,       DXGI_FORMAT_B4G4R4A4_UNORM,       U,                                  U, 2, 2, false, Convert::None },
    { D3DFMT_A8,              DXGI_FORMAT_A8_UNORM,             DXGI_FORMAT_A8_UNORM,             U,                                  U, 1, 1, false, Convert::None },
    { D3DFMT_A2B10G10R10,     DXGI_FORMAT_R10G10B10A2_UNORM,    DXGI_FORMAT_R10G10B10A2_UNORM,    U,                                  U, 4, 4, false, Convert::None },
    { D3DFMT_G16R16,          DXGI_FORMAT_R16G16_UNORM,         DXGI_FORMAT_R16G16_UNORM,         U,                                  U, 4, 4, false, Convert::None },
    { D3DFMT_A16B16G16R16,    DXGI_FORMAT_R16G16B16A16_UNORM,   DXGI_FORMAT_R16G16B16A16_UNORM,   U,                                  U, 8, 8, false, Convert::None },
    { D3DFMT_L8,              DXGI_FORMAT_R8G8B8A8_UNORM,       DXGI_FORMAT_R8G8B8A8_UNORM,       U,                                  U, 1, 4, false, Convert::L8 },
    { D3DFMT_A8L8,            DXGI_FORMAT_R8G8B8A8_UNORM,       DXGI_FORMAT_R8G8B8A8_UNORM,       U,                                  U, 2, 4, false, Convert::A8L8 },
    { D3DFMT_A4L4,            DXGI_FORMAT_R8G8B8A8_UNORM,       DXGI_FORMAT_R8G8B8A8_UNORM,       U,                                  U, 1, 4, false, Convert::A4L4 },
    { D3DFMT_L16,             DXGI_FORMAT_R16G16B16A16_UNORM,   DXGI_FORMAT_R16G16B16A16_UNORM,   U,                                  U, 2, 8, false, Convert::L16 },
    { D3DFMT_V8U8,            DXGI_FORMAT_R8G8_SNORM,           DXGI_FORMAT_R8G8_SNORM,           U,                                  U, 2, 2, false, Convert::None },
    { D3DFMT_Q8W8V8U8,        DXGI_FORMAT_R8G8B8A8_SNORM,       DXGI_FORMAT_R8G8B8A8_SNORM,       U,                                  U, 4, 4, false, Convert::None },
    { D3DFMT_V16U16,          DXGI_FORMAT_R16G16_SNORM,         DXGI_FORMAT_R16G16_SNORM,         U,                                  U, 4, 4, false, Convert::None },
    { D3DFMT_DXT1,            DXGI_FORMAT_BC1_TYPELESS,         DXGI_FORMAT_BC1_UNORM,            DXGI_FORMAT_BC1_UNORM_SRGB,         U, 8, 8, true,  Convert::None },
    { D3DFMT_DXT2,            DXGI_FORMAT_BC2_TYPELESS,         DXGI_FORMAT_BC2_UNORM,            DXGI_FORMAT_BC2_UNORM_SRGB,         U, 16, 16, true, Convert::None },
    { D3DFMT_DXT3,            DXGI_FORMAT_BC2_TYPELESS,         DXGI_FORMAT_BC2_UNORM,            DXGI_FORMAT_BC2_UNORM_SRGB,         U, 16, 16, true, Convert::None },
    { D3DFMT_DXT4,            DXGI_FORMAT_BC3_TYPELESS,         DXGI_FORMAT_BC3_UNORM,            DXGI_FORMAT_BC3_UNORM_SRGB,         U, 16, 16, true, Convert::None },
    { D3DFMT_DXT5,            DXGI_FORMAT_BC3_TYPELESS,         DXGI_FORMAT_BC3_UNORM,            DXGI_FORMAT_BC3_UNORM_SRGB,         U, 16, 16, true, Convert::None },
    { D3DFMT_R16F,            DXGI_FORMAT_R16_FLOAT,            DXGI_FORMAT_R16_FLOAT,            U,                                  U, 2, 2, false, Convert::None },
    { D3DFMT_G16R16F,         DXGI_FORMAT_R16G16_FLOAT,         DXGI_FORMAT_R16G16_FLOAT,         U,                                  U, 4, 4, false, Convert::None },
    { D3DFMT_A16B16G16R16F,   DXGI_FORMAT_R16G16B16A16_FLOAT,   DXGI_FORMAT_R16G16B16A16_FLOAT,   U,                                  U, 8, 8, false, Convert::None },
    { D3DFMT_R32F,            DXGI_FORMAT_R32_FLOAT,            DXGI_FORMAT_R32_FLOAT,            U,                                  U, 4, 4, false, Convert::None },
    { D3DFMT_G32R32F,         DXGI_FORMAT_R32G32_FLOAT,         DXGI_FORMAT_R32G32_FLOAT,         U,                                  U, 8, 8, false, Convert::None },
    { D3DFMT_A32B32G32R32F,   DXGI_FORMAT_R32G32B32A32_FLOAT,   DXGI_FORMAT_R32G32B32A32_FLOAT,   U,                                  U, 16, 16, false, Convert::None },
    // Depth formats: typeless resource, a sampleable view, and the DSV format.
    { D3DFMT_D16,             DXGI_FORMAT_R16_TYPELESS,         DXGI_FORMAT_R16_UNORM,            U, DXGI_FORMAT_D16_UNORM,         2, 2, false, Convert::None },
    { D3DFMT_D16_LOCKABLE,    DXGI_FORMAT_R16_TYPELESS,         DXGI_FORMAT_R16_UNORM,            U, DXGI_FORMAT_D16_UNORM,         2, 2, false, Convert::None },
    { D3DFMT_D24S8,           DXGI_FORMAT_R24G8_TYPELESS,       DXGI_FORMAT_R24_UNORM_X8_TYPELESS, U, DXGI_FORMAT_D24_UNORM_S8_UINT, 4, 4, false, Convert::None },
    { D3DFMT_D24X8,           DXGI_FORMAT_R24G8_TYPELESS,       DXGI_FORMAT_R24_UNORM_X8_TYPELESS, U, DXGI_FORMAT_D24_UNORM_S8_UINT, 4, 4, false, Convert::None },
    { D3DFMT_D24X4S4,         DXGI_FORMAT_R24G8_TYPELESS,       DXGI_FORMAT_R24_UNORM_X8_TYPELESS, U, DXGI_FORMAT_D24_UNORM_S8_UINT, 4, 4, false, Convert::None },
    { D3DFMT_D24FS8,          DXGI_FORMAT_R24G8_TYPELESS,       DXGI_FORMAT_R24_UNORM_X8_TYPELESS, U, DXGI_FORMAT_D24_UNORM_S8_UINT, 4, 4, false, Convert::None },
    { D3DFMT_D32,             DXGI_FORMAT_R32_TYPELESS,         DXGI_FORMAT_R32_FLOAT,            U, DXGI_FORMAT_D32_FLOAT,         4, 4, false, Convert::None },
    { D3DFMT_D32F_LOCKABLE,   DXGI_FORMAT_R32_TYPELESS,         DXGI_FORMAT_R32_FLOAT,            U, DXGI_FORMAT_D32_FLOAT,         4, 4, false, Convert::None },
    { FourCC('I','N','T','Z'), DXGI_FORMAT_R24G8_TYPELESS,      DXGI_FORMAT_R24_UNORM_X8_TYPELESS, U, DXGI_FORMAT_D24_UNORM_S8_UINT, 4, 4, false, Convert::None },
    { FourCC('D','F','2','4'), DXGI_FORMAT_R24G8_TYPELESS,      DXGI_FORMAT_R24_UNORM_X8_TYPELESS, U, DXGI_FORMAT_D24_UNORM_S8_UINT, 4, 4, false, Convert::None },
    { FourCC('D','F','1','6'), DXGI_FORMAT_R16_TYPELESS,        DXGI_FORMAT_R16_UNORM,            U, DXGI_FORMAT_D16_UNORM,         2, 2, false, Convert::None },
};
#undef U

} // namespace

const FormatInfo* GetFormat(D3DFORMAT format)
{
    for (const auto& f : kFormats)
        if (f.d3d == format) return &f;
    return nullptr;
}

void ConvertRows(const FormatInfo& f, const uint8_t* src, UINT srcPitch, uint8_t* dst, UINT dstPitch,
                 UINT width, UINT rows)
{
    const UINT srcRow = RowPitch(f, width, true);
    for (UINT y = 0; y < rows; y++) {
        const uint8_t* s = src + static_cast<size_t>(y) * srcPitch;
        uint8_t* d = dst + static_cast<size_t>(y) * dstPitch;
        switch (f.convert) {
        case Convert::None:
            std::memcpy(d, s, srcRow);
            break;
        case Convert::X8ToA8:
            for (UINT x = 0; x < width; x++) {
                std::memcpy(d + x * 4, s + x * 4, 3);
                d[x * 4 + 3] = 0xFF;
            }
            break;
        case Convert::R8G8B8:
            for (UINT x = 0; x < width; x++) {
                std::memcpy(d + x * 4, s + x * 3, 3);
                d[x * 4 + 3] = 0xFF;
            }
            break;
        case Convert::L8:
            for (UINT x = 0; x < width; x++) {
                const uint8_t l = s[x];
                d[x * 4 + 0] = l; d[x * 4 + 1] = l; d[x * 4 + 2] = l; d[x * 4 + 3] = 0xFF;
            }
            break;
        case Convert::A8L8:
            for (UINT x = 0; x < width; x++) {
                const uint8_t l = s[x * 2], a = s[x * 2 + 1];
                d[x * 4 + 0] = l; d[x * 4 + 1] = l; d[x * 4 + 2] = l; d[x * 4 + 3] = a;
            }
            break;
        case Convert::A4L4:
            for (UINT x = 0; x < width; x++) {
                const uint8_t l = static_cast<uint8_t>((s[x] & 0xF) * 17), a = static_cast<uint8_t>((s[x] >> 4) * 17);
                d[x * 4 + 0] = l; d[x * 4 + 1] = l; d[x * 4 + 2] = l; d[x * 4 + 3] = a;
            }
            break;
        case Convert::L16:
            for (UINT x = 0; x < width; x++) {
                uint16_t l;
                std::memcpy(&l, s + x * 2, 2);
                const uint16_t px[4] = { l, l, l, 0xFFFF };
                std::memcpy(d + x * 8, px, 8);
            }
            break;
        case Convert::X1R5G5B5:
            for (UINT x = 0; x < width; x++) {
                uint16_t v;
                std::memcpy(&v, s + x * 2, 2);
                v |= 0x8000;
                std::memcpy(d + x * 2, &v, 2);
            }
            break;
        }
    }
}

DXGI_FORMAT DeclTypeFormat(BYTE type, int* integer)
{
    *integer = 0;
    switch (type) {
    case D3DDECLTYPE_FLOAT1: return DXGI_FORMAT_R32_FLOAT;
    case D3DDECLTYPE_FLOAT2: return DXGI_FORMAT_R32G32_FLOAT;
    case D3DDECLTYPE_FLOAT3: return DXGI_FORMAT_R32G32B32_FLOAT;
    case D3DDECLTYPE_FLOAT4: return DXGI_FORMAT_R32G32B32A32_FLOAT;
    case D3DDECLTYPE_D3DCOLOR: return DXGI_FORMAT_B8G8R8A8_UNORM;
    case D3DDECLTYPE_UBYTE4: *integer = 2; return DXGI_FORMAT_R8G8B8A8_UINT;
    case D3DDECLTYPE_SHORT2: *integer = 1; return DXGI_FORMAT_R16G16_SINT;
    case D3DDECLTYPE_SHORT4: *integer = 1; return DXGI_FORMAT_R16G16B16A16_SINT;
    case D3DDECLTYPE_UBYTE4N: return DXGI_FORMAT_R8G8B8A8_UNORM;
    case D3DDECLTYPE_SHORT2N: return DXGI_FORMAT_R16G16_SNORM;
    case D3DDECLTYPE_SHORT4N: return DXGI_FORMAT_R16G16B16A16_SNORM;
    case D3DDECLTYPE_USHORT2N: return DXGI_FORMAT_R16G16_UNORM;
    case D3DDECLTYPE_USHORT4N: return DXGI_FORMAT_R16G16B16A16_UNORM;
    case D3DDECLTYPE_UDEC3: *integer = 2; return DXGI_FORMAT_R10G10B10A2_UINT;
    case D3DDECLTYPE_DEC3N: return DXGI_FORMAT_R10G10B10A2_UNORM;
    case D3DDECLTYPE_FLOAT16_2: return DXGI_FORMAT_R16G16_FLOAT;
    case D3DDECLTYPE_FLOAT16_4: return DXGI_FORMAT_R16G16B16A16_FLOAT;
    default: return DXGI_FORMAT_UNKNOWN;
    }
}

UINT DeclTypeSize(BYTE type)
{
    switch (type) {
    case D3DDECLTYPE_FLOAT1: return 4;
    case D3DDECLTYPE_FLOAT2: return 8;
    case D3DDECLTYPE_FLOAT3: return 12;
    case D3DDECLTYPE_FLOAT4: return 16;
    case D3DDECLTYPE_SHORT4: case D3DDECLTYPE_SHORT4N: case D3DDECLTYPE_USHORT4N: case D3DDECLTYPE_FLOAT16_4:
        return 8;
    case D3DDECLTYPE_UNUSED: return 0;
    default: return 4;
    }
}

} // namespace d3d9
