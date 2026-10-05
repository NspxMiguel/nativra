// Additional D3DX9 entry points. Included by d3dx9_43.cpp for the x86 build.
#include <cstdlib>
#include <cstring>
#include <d3d9.h>
#include <new>
#include <stdint.h>
#define STBI_NO_STDIO
#define STB_IMAGE_IMPLEMENTATION
#include "third_party/stb_image.h"
#define STB_IMAGE_WRITE_IMPLEMENTATION
#include "third_party/stb_image_write.h"

#define D3DX_DEFAULT 0xffffffffu
#define D3DX_DEFAULT_NONPOW2 0xfffffffeu
#define D3DXERR_INVALIDDATA ((HRESULT)0x88760b59)

struct D3DXIMAGE_INFO
{
    UINT Width, Height, Depth, MipLevels;
    D3DFORMAT Format;
    D3DRESOURCETYPE ResourceType;
    int ImageFileFormat;
};
struct D3DXRTS_DESC
{
    UINT Width, Height;
    D3DFORMAT Format;
    BOOL DepthStencil;
    D3DFORMAT DepthStencilFormat;
};
struct D3DXMACRO
{
    LPCSTR Name, Definition;
};
struct ID3DXInclude
{
    virtual HRESULT __stdcall Open(int, LPCSTR, LPCVOID, LPCVOID *, UINT *) = 0;
    virtual HRESULT __stdcall Close(LPCVOID) = 0;
};
struct ID3DXBuffer : IUnknown
{
    virtual LPVOID __stdcall GetBufferPointer() = 0;
    virtual DWORD __stdcall GetBufferSize() = 0;
};
struct ID3DXRenderToSurface : IUnknown
{
    virtual HRESULT __stdcall GetDevice(IDirect3DDevice9 **) = 0;
    virtual HRESULT __stdcall GetDesc(D3DXRTS_DESC *) = 0;
    virtual HRESULT __stdcall BeginScene(IDirect3DSurface9 *, const D3DVIEWPORT9 *) = 0;
    virtual HRESULT __stdcall EndScene(DWORD) = 0;
    virtual HRESULT __stdcall OnLostDevice() = 0;
    virtual HRESULT __stdcall OnResetDevice() = 0;
};

namespace
{
const GUID iidUnknown = {0, 0, 0, {0xc0, 0, 0, 0, 0, 0, 0, 0x46}};
const GUID iidBuffer = {0x8ba5fb08, 0x5195, 0x40e2, {0xac, 0x58, 0x0d, 0x98, 0x9c, 0x3a, 0x01, 0x02}};
const GUID iidRenderToSurface = {0x6985f346, 0x2c3d, 0x43b3, {0xbe, 0x8b, 0xda, 0xae, 0x8a, 0x03, 0xd8, 0x94}};
class Buffer final : public ID3DXBuffer
{
    volatile LONG refs_;
    void *bytes_;
    DWORD size_;

  public:
    Buffer(const void *bytes, DWORD size) : refs_(1), bytes_(malloc(size ? size : 1)), size_(size)
    {
        if (bytes_ && bytes && size)
            memcpy(bytes_, bytes, size);
    }
    bool valid() const
    {
        return bytes_ != nullptr;
    }
    HRESULT __stdcall QueryInterface(REFIID iid, void **out) override
    {
        if (!out)
            return E_POINTER;
        *out = nullptr;
        if (IsEqualGUID(iid, iidUnknown) || IsEqualGUID(iid, iidBuffer))
        {
            *out = static_cast<ID3DXBuffer *>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }
    ULONG __stdcall AddRef() override
    {
        return InterlockedIncrement(&refs_);
    }
    ULONG __stdcall Release() override
    {
        ULONG n = InterlockedDecrement(&refs_);
        if (!n)
        {
            free(bytes_);
            delete this;
        }
        return n;
    }
    void *__stdcall GetBufferPointer() override
    {
        return bytes_;
    }
    DWORD __stdcall GetBufferSize() override
    {
        return size_;
    }
};
struct Blob : IUnknown
{
    virtual void *__stdcall GetBufferPointer() = 0;
    virtual SIZE_T __stdcall GetBufferSize() = 0;
};
HRESULT WrapBlob(Blob *src, ID3DXBuffer **dst)
{
    if (!dst)
    {
        if (src)
            src->Release();
        return S_OK;
    }
    *dst = nullptr;
    if (!src)
        return S_OK;
    SIZE_T size = src->GetBufferSize();
    if (size > 0xffffffffu)
    {
        src->Release();
        return E_OUTOFMEMORY;
    }
    Buffer *b = new (std::nothrow) Buffer(src->GetBufferPointer(), static_cast<DWORD>(size));
    src->Release();
    if (!b)
        return E_OUTOFMEMORY;
    if (!b->valid())
    {
        b->Release();
        return E_OUTOFMEMORY;
    }
    *dst = b;
    return S_OK;
}

struct Image
{
    UINT w, h, mips;
    D3DFORMAT format;
    int fileFormat;
    unsigned char *rgba;
};
uint32_t U32(const unsigned char *p)
{
    return uint32_t(p[0]) | (uint32_t(p[1]) << 8) | (uint32_t(p[2]) << 16) | (uint32_t(p[3]) << 24);
}
void PutPixel(unsigned char *p, uint32_t c)
{
    p[0] = (c >> 16) & 255;
    p[1] = (c >> 8) & 255;
    p[2] = c & 255;
    p[3] = (c >> 24) & 255;
}
uint32_t Color565(uint16_t c)
{
    return 0xff000000u | ((c >> 11) * 255 / 31 << 16) | (((c >> 5) & 63) * 255 / 63 << 8) | ((c & 31) * 255 / 31);
}
void DecodeColorBlock(const unsigned char *block, unsigned char *out, UINT pitch, UINT width, UINT height, UINT x0,
                      UINT y0, bool transparent)
{
    uint16_t c0 = uint16_t(block[0] | block[1] << 8), c1 = uint16_t(block[2] | block[3] << 8);
    uint32_t colors[4] = {Color565(c0), Color565(c1), 0, 0};
    if (c0 > c1 || !transparent)
    {
        unsigned char r0 = (colors[0] >> 16) & 255, g0 = (colors[0] >> 8) & 255, b0 = colors[0] & 255;
        unsigned char r1 = (colors[1] >> 16) & 255, g1 = (colors[1] >> 8) & 255, b1 = colors[1] & 255;
        colors[2] = 0xff000000u | ((2 * r0 + r1) / 3 << 16) | ((2 * g0 + g1) / 3 << 8) | (2 * b0 + b1) / 3;
        colors[3] = 0xff000000u | ((r0 + 2 * r1) / 3 << 16) | ((g0 + 2 * g1) / 3 << 8) | (b0 + 2 * b1) / 3;
    }
    else
    {
        colors[2] = 0xff000000u | ((((colors[0] >> 16) & 255) + ((colors[1] >> 16) & 255)) / 2 << 16) |
                    ((((colors[0] >> 8) & 255) + ((colors[1] >> 8) & 255)) / 2 << 8) |
                    ((colors[0] & 255) + (colors[1] & 255)) / 2;
    }
    uint32_t bits = U32(block + 4);
    for (UINT y = 0; y < 4; y++)
        for (UINT x = 0; x < 4; x++)
            if (x0 + x < width && y0 + y < height)
                PutPixel(out + (y0 + y) * pitch + (x0 + x) * 4, colors[(bits >> (2 * (y * 4 + x))) & 3]);
}
// DDS header is 128 bytes. Only conventional 2D DXT1/3/5 and BGRA surfaces are accepted.
HRESULT DecodeDDS(const unsigned char *data, size_t size, Image *image)
{
    if (size < 128 || U32(data) != 0x20534444 || U32(data + 4) != 124 || U32(data + 76) != 32)
        return D3DXERR_INVALIDDATA;
    UINT h = U32(data + 12), w = U32(data + 16), mips = U32(data + 28);
    if (!w || !h || w > 16384 || h > 16384 || size_t(w) * h > SIZE_MAX / 4)
        return D3DXERR_INVALIDDATA;
    uint32_t flags = U32(data + 80), fourcc = U32(data + 84);
    int type = 0;
    if (flags & 4)
    {
        if (fourcc == 0x31545844)
            type = 1;
        else if (fourcc == 0x33545844)
            type = 3;
        else if (fourcc == 0x35545844)
            type = 5;
        else
            return D3DXERR_INVALIDDATA;
    }
    else if (U32(data + 88) == 32 && U32(data + 92) == 0x00ff0000 && U32(data + 96) == 0x0000ff00 &&
             U32(data + 100) == 0x000000ff && (U32(data + 104) == 0xff000000 || U32(data + 104) == 0))
        type = U32(data + 104) ? 8 : 9;
    else
        return D3DXERR_INVALIDDATA;
    size_t need = type == 1 ? size_t((w + 3) / 4) * ((h + 3) / 4) * 8
                            : (type == 3 || type == 5 ? size_t((w + 3) / 4) * ((h + 3) / 4) * 16 : size_t(w) * h * 4);
    if (need > size - 128)
        return D3DXERR_INVALIDDATA;
    unsigned char *rgba = (unsigned char *)malloc(size_t(w) * h * 4);
    if (!rgba)
        return E_OUTOFMEMORY;
    if (type == 8 || type == 9)
    {
        for (size_t i = 0; i < size_t(w) * h; i++)
        {
            rgba[i * 4] = data[128 + i * 4 + 2];
            rgba[i * 4 + 1] = data[128 + i * 4 + 1];
            rgba[i * 4 + 2] = data[128 + i * 4];
            rgba[i * 4 + 3] = type == 8 ? data[128 + i * 4 + 3] : 255;
        }
    }
    else
    {
        size_t off = 128;
        for (UINT by = 0; by < (h + 3) / 4; by++)
            for (UINT bx = 0; bx < (w + 3) / 4; bx++)
            {
                const unsigned char *block = data + off;
                DecodeColorBlock(block + (type == 1 ? 0 : 8), rgba, w * 4, w, h, bx * 4, by * 4, type == 1);
                if (type == 3)
                    for (UINT y = 0; y < 4; y++)
                        for (UINT x = 0; x < 4; x++)
                            if (bx * 4 + x < w && by * 4 + y < h)
                                rgba[((by * 4 + y) * w + bx * 4 + x) * 4 + 3] =
                                    ((block[(y * 4 + x) / 2] >> (((y * 4 + x) & 1) * 4)) & 15) * 17;
                if (type == 5)
                {
                    unsigned char a[8] = {block[0], block[1], 0, 0, 0, 0, 0, 0};
                    for (int i = 2; i < 8; i++)
                        a[i] = a[0] > a[1] ? (unsigned char)(((8 - i) * a[0] + (i - 1) * a[1]) / 7)
                                           : (i < 6 ? (unsigned char)(((6 - i) * a[0] + (i - 1) * a[1]) / 5)
                                                    : (unsigned char)(i == 6 ? 0 : 255));
                    uint64_t bits = 0;
                    for (int i = 0; i < 6; i++)
                        bits |= uint64_t(block[2 + i]) << (8 * i);
                    for (UINT y = 0; y < 4; y++)
                        for (UINT x = 0; x < 4; x++)
                            if (bx * 4 + x < w && by * 4 + y < h)
                                rgba[((by * 4 + y) * w + bx * 4 + x) * 4 + 3] = a[(bits >> (3 * (y * 4 + x))) & 7];
                }
                off += type == 1 ? 8 : 16;
            }
    }
    image->w = w;
    image->h = h;
    image->mips = mips ? mips : 1;
    image->rgba = rgba;
    image->fileFormat = 4;
    image->format = type == 1   ? D3DFMT_DXT1
                    : type == 3 ? D3DFMT_DXT3
                    : type == 5 ? D3DFMT_DXT5
                    : type == 8 ? D3DFMT_A8R8G8B8
                                : D3DFMT_X8R8G8B8;
    return S_OK;
}
// What a failed texture call saw, in the debugger channel the x86 host mirrors into its diagnostic log.
void TraceFailure(const char *what, HRESULT hr, const void *bytes, UINT size, UINT a, UINT b)
{
    static int reported = 0;
    if (reported++ >= 12)
        return;
    // No CRT formatting here (the shim links nothing beyond kernel32): append by hand.
    char line[200];
    int n = 0;
    auto text = [&](const char *t) { while (*t && n < 190) line[n++] = *t++; };
    auto number = [&](unsigned v, int base) {
        char digits[16];
        int k = 0;
        do { digits[k++] = "0123456789ABCDEF"[v % base]; v /= base; } while (v && k < 15);
        while (k > 0 && n < 190) line[n++] = digits[--k];
    };
    const unsigned char *d = (const unsigned char *)bytes;
    text("D3DX "); text(what); text(" failed hr=0x"); number((unsigned)hr, 16);
    text(" size="); number(size, 10); text(" head=");
    for (UINT i = 0; i < 4; i++) { number(d && size > i ? d[i] : 0, 16); text(" "); }
    text("args="); number(a, 10); text(","); number(b, 10);
    line[n] = 0;
    OutputDebugStringA(line);
}
HRESULT DecodeImage(const void *bytes, UINT size, Image *image)
{
    if (!bytes || !size || size > 0x7fffffffu)
        return D3DERR_INVALIDCALL;
    const unsigned char *data = (const unsigned char *)bytes;
    if (size >= 4 && U32(data) == 0x20534444)
        return DecodeDDS(data, size, image);
    int w = 0, h = 0, channels = 0;
    unsigned char *rgba = stbi_load_from_memory(data, size, &w, &h, &channels, 4);
    if (!rgba)
        return D3DXERR_INVALIDDATA;
    if (w <= 0 || h <= 0)
    {
        stbi_image_free(rgba);
        return D3DXERR_INVALIDDATA;
    }
    image->w = w;
    image->h = h;
    image->mips = 1;
    image->rgba = rgba;
    image->format = D3DFMT_A8R8G8B8;
    image->fileFormat = size >= 8 && !memcmp(data, "\x89PNG\r\n\x1a\n", 8)                        ? 3
                        : size >= 3 && data[0] == 0xff && data[1] == 0xd8                         ? 1
                        : size >= 2 && data[0] == 'B' && data[1] == 'M'                           ? 0
                        : size >= 6 && (!memcmp(data, "GIF87a", 6) || !memcmp(data, "GIF89a", 6)) ? 3
                                                                                                  : 2;
    return S_OK;
}
void FillInfo(const Image &image, D3DXIMAGE_INFO *info)
{
    if (!info)
        return;
    info->Width = image.w;
    info->Height = image.h;
    info->Depth = 1;
    info->MipLevels = image.mips;
    info->Format = image.format;
    info->ResourceType = D3DRTYPE_TEXTURE;
    info->ImageFileFormat = image.fileFormat;
}
HRESULT ReadFileW(LPCWSTR path, unsigned char **data, DWORD *size)
{
    *data = nullptr;
    *size = 0;
    if (!path)
        return D3DERR_INVALIDCALL;
    HANDLE f = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (f == INVALID_HANDLE_VALUE)
        return D3DXERR_INVALIDDATA;
    LARGE_INTEGER length;
    if (!GetFileSizeEx(f, &length) || length.QuadPart <= 0 || length.QuadPart > 0x7fffffff)
    {
        CloseHandle(f);
        return D3DXERR_INVALIDDATA;
    }
    *size = (DWORD)length.QuadPart;
    *data = (unsigned char *)malloc(*size);
    DWORD got = 0;
    BOOL ok = *data && ReadFile(f, *data, *size, &got, nullptr) && got == *size;
    CloseHandle(f);
    if (!ok)
    {
        free(*data);
        *data = nullptr;
        return D3DXERR_INVALIDDATA;
    }
    return S_OK;
}
HRESULT ReadFileA(LPCSTR path, unsigned char **data, DWORD *size)
{
    if (!path)
        return D3DERR_INVALIDCALL;
    int n = MultiByteToWideChar(CP_ACP, 0, path, -1, nullptr, 0);
    if (!n)
        return D3DXERR_INVALIDDATA;
    wchar_t *wide = (wchar_t *)malloc(n * sizeof(wchar_t));
    if (!wide)
        return E_OUTOFMEMORY;
    MultiByteToWideChar(CP_ACP, 0, path, -1, wide, n);
    HRESULT hr = ReadFileW(wide, data, size);
    free(wide);
    return hr;
}
UINT FullMips(UINT w, UINT h)
{
    UINT n = 1;
    while (w > 1 || h > 1)
    {
        w = w > 1 ? w / 2 : 1;
        h = h > 1 ? h / 2 : 1;
        n++;
    }
    return n;
}
UINT DefaultDimension(UINT v, UINT source)
{
    if (v == D3DX_DEFAULT_NONPOW2)
        return source;
    if (v && v != D3DX_DEFAULT)
        return v;
    UINT p = 1;
    while (p < source && p < 0x80000000u)
        p <<= 1;
    return p;
}
HRESULT CreateFromImage(IDirect3DDevice9 *device, const Image &image, UINT width, UINT height, UINT levels, DWORD usage,
                        D3DFORMAT format, D3DPOOL pool, DWORD colorKey, D3DXIMAGE_INFO *info,
                        IDirect3DTexture9 **result)
{
    if (!device || !result)
        return D3DERR_INVALIDCALL;
    *result = nullptr;
    FillInfo(image, info);
    width = DefaultDimension(width, image.w);
    height = DefaultDimension(height, image.h);
    if (!width || !height || width > 16384 || height > 16384)
        return D3DERR_INVALIDCALL;
    if (format == D3DFMT_UNKNOWN || (DWORD)format == D3DX_DEFAULT)
        format = D3DFMT_A8R8G8B8;
    if (format != D3DFMT_A8R8G8B8 && format != D3DFMT_X8R8G8B8)
        return D3DERR_NOTAVAILABLE;
    if (!levels || levels == D3DX_DEFAULT)
        levels = FullMips(width, height);
    if (levels > FullMips(width, height))
        return D3DERR_INVALIDCALL;
    // Default-pool textures are filled through a system-memory staging texture.
    IDirect3DTexture9 *texture = nullptr;
    HRESULT hr = device->CreateTexture(width, height, levels, usage, format, pool, &texture, nullptr);
    if (FAILED(hr))
        return hr;
    IDirect3DTexture9 *staging = texture;
    if (pool == D3DPOOL_DEFAULT)
    {
        hr = device->CreateTexture(width, height, levels, 0, format, D3DPOOL_SYSTEMMEM, &staging, nullptr);
        if (FAILED(hr))
        {
            texture->Release();
            return hr;
        }
    }
    size_t pixels = size_t(width) * height;
    if (pixels > SIZE_MAX / 4)
    {
        if (staging != texture)
            staging->Release();
        texture->Release();
        return E_OUTOFMEMORY;
    }
    unsigned char *level = (unsigned char *)malloc(pixels * 4);
    if (!level)
    {
        if (staging != texture)
            staging->Release();
        texture->Release();
        return E_OUTOFMEMORY;
    }
    for (UINT y = 0; y < height; y++)
        for (UINT x = 0; x < width; x++)
        {
            UINT sx = (uint64_t(x) * image.w) / width, sy = (uint64_t(y) * image.h) / height;
            const unsigned char *src = image.rgba + (size_t(sy) * image.w + sx) * 4;
            unsigned char *dst = level + (size_t(y) * width + x) * 4;
            dst[0] = src[2];
            dst[1] = src[1];
            dst[2] = src[0];
            dst[3] = format == D3DFMT_X8R8G8B8 ? 255 : src[3];
            if (colorKey && ((uint32_t(dst[2]) << 16) | (uint32_t(dst[1]) << 8) | dst[0]) == (colorKey & 0xffffff))
                dst[3] = 0;
        }
    UINT w = width, h = height;
    for (UINT mip = 0; mip < levels && SUCCEEDED(hr); mip++)
    {
        D3DLOCKED_RECT rect;
        hr = staging->LockRect(mip, &rect, nullptr, 0);
        if (FAILED(hr))
            break;
        for (UINT y = 0; y < h; y++)
            memcpy((unsigned char *)rect.pBits + size_t(y) * rect.Pitch, level + size_t(y) * w * 4, size_t(w) * 4);
        staging->UnlockRect(mip);
        if (mip + 1 < levels)
        {
            UINT nw = w > 1 ? w / 2 : 1, nh = h > 1 ? h / 2 : 1;
            for (UINT y = 0; y < nh; y++)
                for (UINT x = 0; x < nw; x++)
                    for (UINT c = 0; c < 4; c++)
                    {
                        UINT sum = 0, count = 0;
                        for (UINT dy = 0; dy < 2; dy++)
                            for (UINT dx = 0; dx < 2; dx++)
                                if (2 * x + dx < w && 2 * y + dy < h)
                                {
                                    sum += level[(size_t(2 * y + dy) * w + 2 * x + dx) * 4 + c];
                                    count++;
                                }
                        level[(size_t(y) * nw + x) * 4 + c] = (unsigned char)(sum / count);
                    }
            w = nw;
            h = nh;
        }
    }
    free(level);
    if (SUCCEEDED(hr) && staging != texture)
        hr = device->UpdateTexture(staging, texture);
    if (staging != texture)
        staging->Release();
    if (FAILED(hr))
    {
        texture->Release();
        return hr;
    }
    *result = texture;
    return S_OK;
}
} // namespace

D3DX9API HRESULT WINAPI D3DXGetImageInfoFromFileInMemory(const void *bytes, UINT size, D3DXIMAGE_INFO *info)
{
    if (!info)
        return D3DERR_INVALIDCALL;
    Image image = {};
    HRESULT hr = DecodeImage(bytes, size, &image);
    if (SUCCEEDED(hr))
    {
        FillInfo(image, info);
        stbi_image_free(image.rgba);
    }
    return hr;
}
D3DX9API HRESULT WINAPI D3DXGetImageInfoFromFileA(LPCSTR path, D3DXIMAGE_INFO *info)
{
    if (!info)
        return D3DERR_INVALIDCALL;
    unsigned char *bytes = nullptr;
    DWORD size = 0;
    HRESULT hr = ReadFileA(path, &bytes, &size);
    if (SUCCEEDED(hr))
    {
        hr = D3DXGetImageInfoFromFileInMemory(bytes, size, info);
        free(bytes);
    }
    return hr;
}
D3DX9API HRESULT WINAPI D3DXGetImageInfoFromFileW(LPCWSTR path, D3DXIMAGE_INFO *info)
{
    if (!info)
        return D3DERR_INVALIDCALL;
    unsigned char *bytes = nullptr;
    DWORD size = 0;
    HRESULT hr = ReadFileW(path, &bytes, &size);
    if (SUCCEEDED(hr))
    {
        hr = D3DXGetImageInfoFromFileInMemory(bytes, size, info);
        free(bytes);
    }
    return hr;
}
D3DX9API HRESULT WINAPI D3DXCreateTexture(IDirect3DDevice9 *device, UINT w, UINT h, UINT mips, DWORD usage,
                                          D3DFORMAT format, D3DPOOL pool, IDirect3DTexture9 **out)
{
    if (!device || !out)
        return D3DERR_INVALIDCALL;
    *out = nullptr;
    if (!w || !h || w == D3DX_DEFAULT || h == D3DX_DEFAULT)
        return D3DERR_INVALIDCALL;
    if (!mips || mips == D3DX_DEFAULT)
        mips = FullMips(w, h);
    if (format == D3DFMT_UNKNOWN || (DWORD)format == D3DX_DEFAULT)
        format = D3DFMT_A8R8G8B8;
    HRESULT created = device->CreateTexture(w, h, mips, usage, format, pool, out, nullptr);
    if (FAILED(created))
        TraceFailure("CreateTexture", created, nullptr, 0, w, h);
    return created;
}
D3DX9API HRESULT WINAPI D3DXCreateCubeTexture(IDirect3DDevice9 *, UINT, UINT, DWORD, D3DFORMAT, D3DPOOL,
                                              IDirect3DCubeTexture9 **out)
{
    if (out)
        *out = nullptr;
    return D3DERR_NOTAVAILABLE;
}
D3DX9API HRESULT WINAPI D3DXCreateTextureFromFileInMemoryEx(IDirect3DDevice9 *device, const void *bytes, UINT size,
                                                            UINT w, UINT h, UINT mips, DWORD usage, D3DFORMAT format,
                                                            D3DPOOL pool, DWORD, DWORD, D3DCOLOR key,
                                                            D3DXIMAGE_INFO *info, PALETTEENTRY *,
                                                            IDirect3DTexture9 **out)
{
    if (!device || !out)
        return D3DERR_INVALIDCALL;
    *out = nullptr;
    Image image = {};
    HRESULT hr = DecodeImage(bytes, size, &image);
    if (SUCCEEDED(hr))
    {
        hr = CreateFromImage(device, image, w, h, mips, usage, format, pool, key, info, out);
        stbi_image_free(image.rgba);
    }
    if (FAILED(hr))
        TraceFailure("CreateTextureFromFileInMemoryEx", hr, bytes, size, w, h);
    return hr;
}
D3DX9API HRESULT WINAPI D3DXCreateTextureFromFileInMemory(IDirect3DDevice9 *device, const void *bytes, UINT size,
                                                          IDirect3DTexture9 **out)
{
    return D3DXCreateTextureFromFileInMemoryEx(device, bytes, size, D3DX_DEFAULT, D3DX_DEFAULT, D3DX_DEFAULT, 0,
                                               D3DFMT_UNKNOWN, D3DPOOL_MANAGED, D3DX_DEFAULT, D3DX_DEFAULT, 0, nullptr,
                                               nullptr, out);
}
D3DX9API HRESULT WINAPI D3DXCreateTextureFromFileExA(IDirect3DDevice9 *device, LPCSTR path, UINT w, UINT h, UINT mips,
                                                     DWORD usage, D3DFORMAT format, D3DPOOL pool, DWORD filter,
                                                     DWORD mipFilter, D3DCOLOR key, D3DXIMAGE_INFO *info,
                                                     PALETTEENTRY *palette, IDirect3DTexture9 **out)
{
    if (!out)
        return D3DERR_INVALIDCALL;
    *out = nullptr;
    unsigned char *bytes = nullptr;
    DWORD size = 0;
    HRESULT hr = ReadFileA(path, &bytes, &size);
    if (SUCCEEDED(hr))
    {
        hr = D3DXCreateTextureFromFileInMemoryEx(device, bytes, size, w, h, mips, usage, format, pool, filter,
                                                 mipFilter, key, info, palette, out);
        free(bytes);
    }
    return hr;
}
D3DX9API HRESULT WINAPI D3DXCreateTextureFromFileExW(IDirect3DDevice9 *device, LPCWSTR path, UINT w, UINT h, UINT mips,
                                                     DWORD usage, D3DFORMAT format, D3DPOOL pool, DWORD filter,
                                                     DWORD mipFilter, D3DCOLOR key, D3DXIMAGE_INFO *info,
                                                     PALETTEENTRY *palette, IDirect3DTexture9 **out)
{
    if (!out)
        return D3DERR_INVALIDCALL;
    *out = nullptr;
    unsigned char *bytes = nullptr;
    DWORD size = 0;
    HRESULT hr = ReadFileW(path, &bytes, &size);
    if (SUCCEEDED(hr))
    {
        hr = D3DXCreateTextureFromFileInMemoryEx(device, bytes, size, w, h, mips, usage, format, pool, filter,
                                                 mipFilter, key, info, palette, out);
        free(bytes);
    }
    return hr;
}
D3DX9API HRESULT WINAPI D3DXCreateTextureFromFileA(IDirect3DDevice9 *device, LPCSTR path, IDirect3DTexture9 **out)
{
    return D3DXCreateTextureFromFileExA(device, path, D3DX_DEFAULT, D3DX_DEFAULT, D3DX_DEFAULT, 0, D3DFMT_UNKNOWN,
                                        D3DPOOL_MANAGED, D3DX_DEFAULT, D3DX_DEFAULT, 0, nullptr, nullptr, out);
}
D3DX9API HRESULT WINAPI D3DXCreateTextureFromFileW(IDirect3DDevice9 *device, LPCWSTR path, IDirect3DTexture9 **out)
{
    return D3DXCreateTextureFromFileExW(device, path, D3DX_DEFAULT, D3DX_DEFAULT, D3DX_DEFAULT, 0, D3DFMT_UNKNOWN,
                                        D3DPOOL_MANAGED, D3DX_DEFAULT, D3DX_DEFAULT, 0, nullptr, nullptr, out);
}

namespace
{
HRESULT SaveTextureW(LPCWSTR path, int fileFormat, IDirect3DBaseTexture9 *base)
{
    if (!path || !base || base->GetType() != D3DRTYPE_TEXTURE ||
        (fileFormat != 0 && fileFormat != 2 && fileFormat != 3))
        return D3DERR_INVALIDCALL;
    IDirect3DTexture9 *tex = static_cast<IDirect3DTexture9 *>(base);
    D3DSURFACE_DESC desc;
    HRESULT hr = tex->GetLevelDesc(0, &desc);
    if (FAILED(hr))
        return hr;
    if (desc.Format != D3DFMT_A8R8G8B8 && desc.Format != D3DFMT_X8R8G8B8)
        return D3DERR_NOTAVAILABLE;
    IDirect3DDevice9 *device = nullptr;
    hr = tex->GetDevice(&device);
    if (FAILED(hr))
        return hr;
    IDirect3DSurface9 *source = nullptr;
    hr = tex->GetSurfaceLevel(0, &source);
    IDirect3DSurface9 *copy = nullptr;
    if (SUCCEEDED(hr))
        hr = device->CreateOffscreenPlainSurface(desc.Width, desc.Height, desc.Format, D3DPOOL_SYSTEMMEM, &copy,
                                                 nullptr);
    if (SUCCEEDED(hr))
        hr = device->GetRenderTargetData(source, copy);
    if (FAILED(hr) && source)
    {
        // Managed/system-memory surfaces can be locked directly.
        if (copy)
        {
            copy->Release();
            copy = nullptr;
        }
        if (desc.Pool != D3DPOOL_DEFAULT)
        {
            copy = source;
            copy->AddRef();
            hr = S_OK;
        }
    }
    D3DLOCKED_RECT rect = {};
    if (SUCCEEDED(hr))
        hr = copy->LockRect(&rect, nullptr, D3DLOCK_READONLY);
    if (SUCCEEDED(hr))
    {
        size_t count = size_t(desc.Width) * desc.Height;
        if (count > SIZE_MAX / 4)
            hr = E_OUTOFMEMORY;
        else
        {
            unsigned char *rgba = (unsigned char *)malloc(count * 4);
            if (!rgba)
                hr = E_OUTOFMEMORY;
            else
            {
                for (UINT y = 0; y < desc.Height; y++)
                    for (UINT x = 0; x < desc.Width; x++)
                    {
                        const unsigned char *p = (const unsigned char *)rect.pBits + size_t(y) * rect.Pitch + x * 4;
                        unsigned char *q = rgba + (size_t(y) * desc.Width + x) * 4;
                        q[0] = p[2];
                        q[1] = p[1];
                        q[2] = p[0];
                        q[3] = desc.Format == D3DFMT_X8R8G8B8 ? 255 : p[3];
                    }
                int needed = WideCharToMultiByte(CP_ACP, 0, path, -1, nullptr, 0, nullptr, nullptr);
                char *narrow = needed ? (char *)malloc(needed) : nullptr;
                if (!narrow)
                    hr = E_OUTOFMEMORY;
                else
                {
                    WideCharToMultiByte(CP_ACP, 0, path, -1, narrow, needed, nullptr, nullptr);
                    int ok = fileFormat == 3 ? stbi_write_png(narrow, desc.Width, desc.Height, 4, rgba, desc.Width * 4)
                             : fileFormat == 0 ? stbi_write_bmp(narrow, desc.Width, desc.Height, 4, rgba)
                                               : stbi_write_tga(narrow, desc.Width, desc.Height, 4, rgba);
                    hr = ok ? S_OK : D3DXERR_INVALIDDATA;
                    free(narrow);
                }
                free(rgba);
            }
        }
        copy->UnlockRect();
    }
    if (copy)
        copy->Release();
    if (source)
        source->Release();
    device->Release();
    return hr;
}
} // namespace
D3DX9API HRESULT WINAPI D3DXSaveTextureToFileW(LPCWSTR path, int format, IDirect3DBaseTexture9 *texture,
                                               const PALETTEENTRY *)
{
    return SaveTextureW(path, format, texture);
}
D3DX9API HRESULT WINAPI D3DXSaveTextureToFileA(LPCSTR path, int format, IDirect3DBaseTexture9 *texture,
                                               const PALETTEENTRY *palette)
{
    if (!path)
        return D3DERR_INVALIDCALL;
    int n = MultiByteToWideChar(CP_ACP, 0, path, -1, nullptr, 0);
    if (!n)
        return D3DERR_INVALIDCALL;
    wchar_t *wide = (wchar_t *)malloc(n * sizeof(wchar_t));
    if (!wide)
        return E_OUTOFMEMORY;
    MultiByteToWideChar(CP_ACP, 0, path, -1, wide, n);
    HRESULT hr = D3DXSaveTextureToFileW(wide, format, texture, palette);
    free(wide);
    return hr;
}

namespace
{
typedef HRESULT(WINAPI *CompileFn)(LPCVOID, SIZE_T, LPCSTR, const D3DXMACRO *, ID3DXInclude *, LPCSTR, LPCSTR, UINT,
                                   UINT, Blob **, Blob **);
typedef HRESULT(WINAPI *HostCompileFn)(LPCSTR, UINT, LPCSTR, const D3DXMACRO *, LPCSTR, LPCSTR, UINT, UINT,
                                       void *, UINT, UINT *, char *, UINT, UINT *);
bool CompileOnHost(const char *source, UINT length, const char *name, const D3DXMACRO *macros,
                   const char *entry, const char *profile, DWORD flags, ID3DXBuffer **shader,
                   ID3DXBuffer **errors, HRESULT *result)
{
    HMODULE module = LoadLibraryW(L"nativra_host.dll");
    if (!module)
        return false;
    HostCompileFn fn = (HostCompileFn)GetProcAddress(module, "HostD3DCompile");
    if (!fn)
    {
        FreeLibrary(module);
        return false;
    }
    UINT codeLength = 0, errorLength = 0;
    HRESULT hr = fn(source, length, name, macros, entry, profile, flags, 0,
                    nullptr, 0, &codeLength, nullptr, 0, &errorLength);
    const HRESULT tooSmall = HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
    if (hr != tooSmall)
    {
        *result = hr;
        FreeLibrary(module);
        return true;
    }
    void *code = malloc(codeLength ? codeLength : 1);
    char *messages = (char *)malloc(errorLength ? errorLength : 1);
    if (!code || !messages)
    {
        free(code);
        free(messages);
        *result = E_OUTOFMEMORY;
        FreeLibrary(module);
        return true;
    }
    UINT receivedCode = 0, receivedErrors = 0;
    hr = fn(source, length, name, macros, entry, profile, flags, 0, code, codeLength, &receivedCode,
            messages, errorLength, &receivedErrors);
    if (hr == tooSmall || receivedCode > codeLength || receivedErrors > errorLength)
        hr = tooSmall;
    if (hr != tooSmall)
    {
        if (receivedCode && shader)
        {
            Buffer *buffer = new (std::nothrow) Buffer(code, receivedCode);
            if (!buffer || !buffer->valid())
            {
                if (buffer) buffer->Release();
                hr = E_OUTOFMEMORY;
            }
            else *shader = buffer;
        }
        if (receivedErrors && errors)
        {
            Buffer *buffer = new (std::nothrow) Buffer(messages, receivedErrors);
            if (!buffer || !buffer->valid())
            {
                if (buffer) buffer->Release();
                if (shader && *shader) { (*shader)->Release(); *shader = nullptr; }
                hr = E_OUTOFMEMORY;
            }
            else *errors = buffer;
        }
    }
    free(code);
    free(messages);
    FreeLibrary(module);
    *result = hr;
    return true;
}
HRESULT Compile(const char *source, UINT length, const char *name, const D3DXMACRO *macros, ID3DXInclude *include,
                const char *entry, const char *profile, DWORD flags, ID3DXBuffer **shader, ID3DXBuffer **errors,
                void **table)
{
    if (shader)
        *shader = nullptr;
    if (errors)
        *errors = nullptr;
    if (table)
        *table = nullptr;
    if (!source || !length || !entry || !profile || !shader)
        return D3DERR_INVALIDCALL;
    HRESULT hostResult = D3DERR_NOTAVAILABLE;
    // A game that passes an include handler usually has no #include in the text (Super Meat Boy joins its
    // sources itself); the host compiler gets the first try either way and reports an unresolved include.
    if (CompileOnHost(source, length, name, macros, entry, profile, flags, shader, errors, &hostResult))
        return hostResult;
    HMODULE module = LoadLibraryW(L"d3dcompiler_43.dll");
    if (!module)
        return D3DERR_NOTAVAILABLE;
    CompileFn fn = (CompileFn)GetProcAddress(module, "D3DCompile");
    if (!fn)
    {
        FreeLibrary(module);
        return D3DERR_NOTAVAILABLE;
    }
    Blob *code = nullptr, *messages = nullptr;
    HRESULT hr = fn(source, length, name, macros, include, entry, profile, flags, 0, &code, &messages);
    HRESULT wrap = WrapBlob(code, shader);
    if (SUCCEEDED(hr) && FAILED(wrap))
        hr = wrap;
    wrap = WrapBlob(messages, errors);
    if (SUCCEEDED(hr) && FAILED(wrap))
        hr = wrap;
    FreeLibrary(module);
    return hr;
}
const char *ShaderProfile(IDirect3DDevice9 *device, bool pixel)
{
    if (!device)
        return nullptr;
    D3DCAPS9 caps = {};
    if (FAILED(device->GetDeviceCaps(&caps)))
        return nullptr;
    DWORD version = pixel ? caps.PixelShaderVersion : caps.VertexShaderVersion;
    UINT major = (version >> 8) & 255, minor = version & 255;
    if (major >= 3)
        return pixel ? "ps_3_0" : "vs_3_0";
    if (major == 2)
        return pixel ? "ps_2_0" : "vs_2_0";
    if (major == 1)
        return pixel ? (minor >= 4 ? "ps_1_4" : "ps_1_1") : "vs_1_1";
    return nullptr;
}
} // namespace
D3DX9API HRESULT WINAPI D3DXCompileShader(LPCSTR source, UINT length, const D3DXMACRO *macros, ID3DXInclude *include,
                                          LPCSTR entry, LPCSTR profile, DWORD flags, ID3DXBuffer **shader,
                                          ID3DXBuffer **errors, void **table)
{
    return Compile(source, length, nullptr, macros, include, entry, profile, flags, shader, errors, table);
}
D3DX9API HRESULT WINAPI D3DXCompileShaderFromFileA(LPCSTR path, const D3DXMACRO *macros, ID3DXInclude *include,
                                                   LPCSTR entry, LPCSTR profile, DWORD flags, ID3DXBuffer **shader,
                                                   ID3DXBuffer **errors, void **table)
{
    unsigned char *data = nullptr;
    DWORD length = 0;
    HRESULT hr = ReadFileA(path, &data, &length);
    if (FAILED(hr))
    {
        if (shader)
            *shader = nullptr;
        if (errors)
            *errors = nullptr;
        if (table)
            *table = nullptr;
        return hr;
    }
    hr = Compile((const char *)data, length, path, macros, include, entry, profile, flags, shader, errors, table);
    free(data);
    return hr;
}
D3DX9API HRESULT WINAPI D3DXCompileShaderFromFileW(LPCWSTR path, const D3DXMACRO *macros, ID3DXInclude *include,
                                                   LPCSTR entry, LPCSTR profile, DWORD flags, ID3DXBuffer **shader,
                                                   ID3DXBuffer **errors, void **table)
{
    unsigned char *data = nullptr;
    DWORD length = 0;
    HRESULT hr = ReadFileW(path, &data, &length);
    if (FAILED(hr))
    {
        if (shader)
            *shader = nullptr;
        if (errors)
            *errors = nullptr;
        if (table)
            *table = nullptr;
        return hr;
    }
    hr = Compile((const char *)data, length, nullptr, macros, include, entry, profile, flags, shader, errors, table);
    free(data);
    return hr;
}
D3DX9API LPCSTR WINAPI D3DXGetPixelShaderProfile(IDirect3DDevice9 *device)
{
    return ShaderProfile(device, true);
}
D3DX9API LPCSTR WINAPI D3DXGetVertexShaderProfile(IDirect3DDevice9 *device)
{
    return ShaderProfile(device, false);
}

namespace
{
class RenderToSurface final : public ID3DXRenderToSurface
{
    volatile LONG refs_;
    IDirect3DDevice9 *device_;
    D3DXRTS_DESC desc_;
    IDirect3DSurface9 *previousTarget_, *previousDepth_, *depth_;
    D3DVIEWPORT9 previousViewport_;
    bool active_;

  public:
    RenderToSurface(IDirect3DDevice9 *device, const D3DXRTS_DESC &desc)
        : refs_(1), device_(device), desc_(desc), previousTarget_(nullptr), previousDepth_(nullptr), depth_(nullptr),
          previousViewport_{}, active_(false)
    {
        device_->AddRef();
    }
    ~RenderToSurface()
    {
        if (depth_)
            depth_->Release();
        if (previousTarget_)
            previousTarget_->Release();
        if (previousDepth_)
            previousDepth_->Release();
        device_->Release();
    }
    HRESULT __stdcall QueryInterface(REFIID iid, void **out) override
    {
        if (!out)
            return E_POINTER;
        *out = nullptr;
        if (IsEqualGUID(iid, iidUnknown) || IsEqualGUID(iid, iidRenderToSurface))
        {
            *out = static_cast<ID3DXRenderToSurface *>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }
    ULONG __stdcall AddRef() override
    {
        return InterlockedIncrement(&refs_);
    }
    ULONG __stdcall Release() override
    {
        ULONG n = InterlockedDecrement(&refs_);
        if (!n)
            delete this;
        return n;
    }
    HRESULT __stdcall GetDevice(IDirect3DDevice9 **out) override
    {
        if (!out)
            return D3DERR_INVALIDCALL;
        *out = device_;
        device_->AddRef();
        return S_OK;
    }
    HRESULT __stdcall GetDesc(D3DXRTS_DESC *out) override
    {
        if (!out)
            return D3DERR_INVALIDCALL;
        *out = desc_;
        return S_OK;
    }
    HRESULT __stdcall BeginScene(IDirect3DSurface9 *surface, const D3DVIEWPORT9 *viewport) override
    {
        if (!surface || active_)
            return D3DERR_INVALIDCALL;
        D3DSURFACE_DESC target;
        HRESULT hr = surface->GetDesc(&target);
        if (FAILED(hr))
            return hr;
        if (target.Width != desc_.Width || target.Height != desc_.Height || target.Format != desc_.Format)
            return D3DERR_INVALIDCALL;
        hr = device_->GetRenderTarget(0, &previousTarget_);
        if (FAILED(hr))
            return hr;
        device_->GetDepthStencilSurface(&previousDepth_);
        hr = device_->GetViewport(&previousViewport_);
        if (SUCCEEDED(hr) && desc_.DepthStencil && !depth_)
            hr = device_->CreateDepthStencilSurface(desc_.Width, desc_.Height, desc_.DepthStencilFormat,
                                                    D3DMULTISAMPLE_NONE, 0, TRUE, &depth_, nullptr);
        if (SUCCEEDED(hr))
            hr = device_->SetRenderTarget(0, surface);
        if (SUCCEEDED(hr))
            hr = device_->SetDepthStencilSurface(desc_.DepthStencil ? depth_ : nullptr);
        D3DVIEWPORT9 view = {0, 0, desc_.Width, desc_.Height, 0.0f, 1.0f};
        if (viewport)
            view = *viewport;
        if (SUCCEEDED(hr))
            hr = device_->SetViewport(&view);
        if (SUCCEEDED(hr))
            hr = device_->BeginScene();
        if (FAILED(hr))
        {
            device_->SetRenderTarget(0, previousTarget_);
            device_->SetDepthStencilSurface(previousDepth_);
            device_->SetViewport(&previousViewport_);
            previousTarget_->Release();
            previousTarget_ = nullptr;
            if (previousDepth_)
            {
                previousDepth_->Release();
                previousDepth_ = nullptr;
            }
            return hr;
        }
        active_ = true;
        return S_OK;
    }
    HRESULT __stdcall EndScene(DWORD) override
    {
        if (!active_)
            return D3DERR_INVALIDCALL;
        HRESULT hr = device_->EndScene();
        HRESULT restore = device_->SetRenderTarget(0, previousTarget_);
        if (SUCCEEDED(hr))
            hr = restore;
        restore = device_->SetDepthStencilSurface(previousDepth_);
        if (SUCCEEDED(hr))
            hr = restore;
        restore = device_->SetViewport(&previousViewport_);
        if (SUCCEEDED(hr))
            hr = restore;
        previousTarget_->Release();
        previousTarget_ = nullptr;
        if (previousDepth_)
        {
            previousDepth_->Release();
            previousDepth_ = nullptr;
        }
        active_ = false;
        return hr;
    }
    HRESULT __stdcall OnLostDevice() override
    {
        if (active_)
            return D3DERR_INVALIDCALL;
        if (depth_)
        {
            depth_->Release();
            depth_ = nullptr;
        }
        return S_OK;
    }
    HRESULT __stdcall OnResetDevice() override
    {
        return S_OK;
    }
};
} // namespace
D3DX9API HRESULT WINAPI D3DXCreateRenderToSurface(IDirect3DDevice9 *device, UINT width, UINT height, D3DFORMAT format,
                                                  BOOL depthStencil, D3DFORMAT depthFormat, ID3DXRenderToSurface **out)
{
    if (!device || !out || !width || !height)
        return D3DERR_INVALIDCALL;
    *out = nullptr;
    D3DXRTS_DESC desc = {width, height, format, depthStencil, depthFormat};
    RenderToSurface *result = new (std::nothrow) RenderToSurface(device, desc);
    if (!result)
        return E_OUTOFMEMORY;
    *out = result;
    return S_OK;
}
