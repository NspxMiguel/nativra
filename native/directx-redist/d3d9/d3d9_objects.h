// The D3D9 objects: device, resources, shaders, declarations, queries, state
// blocks and the implicit swap chain.

#pragma once

#include "d3d9_common.h"
#include "dxso.h"

#include <bitset>
#include <map>
#include <memory>
#include <string>
#include <unordered_map>
#include <vector>

namespace d3d9 {

class Device;
class Surface;

// --- private data (SetPrivateData/GetPrivateData) ---------------------------

class PrivateData {
public:
    ~PrivateData();
    HRESULT Set(REFGUID guid, const void* data, DWORD size, DWORD flags);
    HRESULT Get(REFGUID guid, void* data, DWORD* size);
    HRESULT Free(REFGUID guid);

private:
    struct Entry { std::vector<uint8_t> bytes; IUnknown* unknown = nullptr; };
    std::map<std::string, Entry> entries;
    static std::string Key(REFGUID guid) { return std::string(reinterpret_cast<const char*>(&guid), sizeof(GUID)); }
};

// Common behaviour of everything that has a parent device: the public count
// holds the device, GetDevice, private data, priority.
class DeviceChild : public RefCounted {
public:
    DeviceChild(Device* device, bool startPublic);
    Device* Parent() const { return device; }

protected:
    void OnPublicFirst() override;
    void OnPublicZero() override;

    Device* device;
    PrivateData privateData;
    DWORD priority = 0;
};

#define D3D9_UNKNOWN_METHODS                                                        \
    STDMETHODIMP_(ULONG) AddRef() override { return PublicAddRef(); }               \
    STDMETHODIMP_(ULONG) Release() override { return PublicRelease(); }

#define D3D9_RESOURCE_METHODS(type)                                                  \
    STDMETHODIMP GetDevice(IDirect3DDevice9** out) override;                        \
    STDMETHODIMP SetPrivateData(REFGUID g, const void* d, DWORD s, DWORD f) override \
    { return privateData.Set(g, d, s, f); }                                          \
    STDMETHODIMP GetPrivateData(REFGUID g, void* d, DWORD* s) override               \
    { return privateData.Get(g, d, s); }                                             \
    STDMETHODIMP FreePrivateData(REFGUID g) override { return privateData.Free(g); } \
    STDMETHODIMP_(DWORD) SetPriority(DWORD p) override { DWORD o = priority; priority = p; return o; } \
    STDMETHODIMP_(DWORD) GetPriority() override { return priority; }                \
    STDMETHODIMP_(void) PreLoad() override {}                                       \
    STDMETHODIMP_(D3DRESOURCETYPE) GetType() override { return type; }

// --- images (texture storage) -------------------------------------------------

struct Subresource {
    UINT width = 0, height = 0;
    uint8_t* shadow = nullptr;   // D3D9-layout copy the game locks
    UINT pitch = 0;              // D3D9 row pitch of the shadow
    bool locked = false;
    bool valid = false;          // shadow holds current contents
    bool evicted = false;        // shadow dropped after upload; the GPU copy is the contents
};

// A D3D11 texture plus everything D3D9 layers on top: CPU shadows for
// locking, lazily created views, format conversion on upload.
class Image {
public:
    Image(Device* device, UINT width, UINT height, UINT levels, UINT faces, D3DFORMAT format,
          DWORD usage, D3DPOOL pool, bool lockable);
    ~Image();
    HRESULT Init();

    UINT Sub(UINT face, UINT level) const { return face * levels + level; }
    HRESULT Lock(UINT sub, D3DLOCKED_RECT* locked, const RECT* rect, DWORD flags);
    HRESULT Unlock(UINT sub);
    void Upload(UINT sub);          // shadow -> GPU
    bool ReadBack(UINT sub);        // GPU -> shadow
    uint8_t* Shadow(UINT sub);      // allocates on demand (reading an evicted one back)
    bool Evictable() const;         // the shadow can go once uploaded

    ID3D11ShaderResourceView* Srv(bool srgb);
    ID3D11RenderTargetView* Rtv(UINT sub, bool srgb);
    ID3D11DepthStencilView* Dsv(UINT sub);

    bool IsDepth() const { return fmt && fmt->depth != DXGI_FORMAT_UNKNOWN; }
    bool OnGpu() const { return texture != nullptr; }

    Device* device;
    ID3D11Texture2D* texture = nullptr;
    const FormatInfo* fmt = nullptr;
    D3DFORMAT format;
    UINT width, height, levels, faces;
    DWORD usage;
    D3DPOOL pool;
    bool lockable;
    std::vector<Subresource> subs;

private:
    bool lockReadOnly = false;
    ID3D11ShaderResourceView* srv[2] = {};
    std::vector<ID3D11RenderTargetView*> rtv[2];
    std::vector<ID3D11DepthStencilView*> dsv;
    ID3D11Texture2D* staging = nullptr;
};

// Anything SetTexture accepts.
class TextureBinding {
public:
    virtual ~TextureBinding() = default;
    virtual Image* GetImage() = 0;
    virtual RefCounted* Ref() = 0;
};

// --- surfaces -----------------------------------------------------------------

class Surface final : public IDirect3DSurface9, public DeviceChild {
public:
    // A surface owns its image when standalone; a texture's or swap chain's
    // surface borrows the container's image and shares its reference count.
    Surface(Device* device, Image* image, UINT sub, IUnknown* container, RefCounted* containerRef,
            bool ownsImage, bool startPublic);
    ~Surface() override;

    // A contained surface's counts are its container's.
    ULONG PublicAddRef() override { return containerRef ? containerRef->PublicAddRef() : DeviceChild::PublicAddRef(); }
    ULONG PublicRelease() override { return containerRef ? containerRef->PublicRelease() : DeviceChild::PublicRelease(); }
    void PrivateAddRef() override { if (containerRef) containerRef->PrivateAddRef(); else DeviceChild::PrivateAddRef(); }
    void PrivateRelease() override { if (containerRef) containerRef->PrivateRelease(); else DeviceChild::PrivateRelease(); }

    STDMETHODIMP QueryInterface(REFIID riid, void** out) override;
    STDMETHODIMP_(ULONG) AddRef() override { return PublicAddRef(); }
    STDMETHODIMP_(ULONG) Release() override { return PublicRelease(); }
    D3D9_RESOURCE_METHODS(D3DRTYPE_SURFACE)
    STDMETHODIMP GetContainer(REFIID riid, void** out) override;
    STDMETHODIMP GetDesc(D3DSURFACE_DESC* desc) override;
    STDMETHODIMP LockRect(D3DLOCKED_RECT* locked, const RECT* rect, DWORD flags) override;
    STDMETHODIMP UnlockRect() override;
    STDMETHODIMP GetDC(HDC* dc) override;
    STDMETHODIMP ReleaseDC(HDC dc) override;

    Image* image;
    UINT sub;
    IUnknown* container;        // not owned
    RefCounted* containerRef;   // the same object, for reference counting
    bool ownsImage;
    D3DMULTISAMPLE_TYPE multisample = D3DMULTISAMPLE_NONE;
};

// --- textures -------------------------------------------------------------------

class Texture final : public IDirect3DTexture9, public DeviceChild, public TextureBinding {
public:
    Texture(Device* device, std::unique_ptr<Image> image);
    ~Texture() override;
    HRESULT Init();

    STDMETHODIMP QueryInterface(REFIID riid, void** out) override;
    D3D9_UNKNOWN_METHODS
    D3D9_RESOURCE_METHODS(D3DRTYPE_TEXTURE)
    STDMETHODIMP_(DWORD) SetLOD(DWORD lod) override { DWORD o = lod_; lod_ = lod; return o; }
    STDMETHODIMP_(DWORD) GetLOD() override { return lod_; }
    STDMETHODIMP_(DWORD) GetLevelCount() override { return image->levels; }
    STDMETHODIMP SetAutoGenFilterType(D3DTEXTUREFILTERTYPE f) override { filter = f; return D3D_OK; }
    STDMETHODIMP_(D3DTEXTUREFILTERTYPE) GetAutoGenFilterType() override { return filter; }
    STDMETHODIMP_(void) GenerateMipSubLevels() override;
    STDMETHODIMP GetLevelDesc(UINT level, D3DSURFACE_DESC* desc) override;
    STDMETHODIMP GetSurfaceLevel(UINT level, IDirect3DSurface9** surface) override;
    STDMETHODIMP LockRect(UINT level, D3DLOCKED_RECT* locked, const RECT* rect, DWORD flags) override;
    STDMETHODIMP UnlockRect(UINT level) override;
    STDMETHODIMP AddDirtyRect(const RECT*) override { return D3D_OK; }

    Image* GetImage() override { return image.get(); }
    RefCounted* Ref() override { return this; }

private:
    std::unique_ptr<Image> image;
    std::vector<Surface*> surfaces;
    DWORD lod_ = 0;
    D3DTEXTUREFILTERTYPE filter = D3DTEXF_LINEAR;
};

class CubeTexture final : public IDirect3DCubeTexture9, public DeviceChild, public TextureBinding {
public:
    CubeTexture(Device* device, std::unique_ptr<Image> image);
    ~CubeTexture() override;
    HRESULT Init();

    STDMETHODIMP QueryInterface(REFIID riid, void** out) override;
    D3D9_UNKNOWN_METHODS
    D3D9_RESOURCE_METHODS(D3DRTYPE_CUBETEXTURE)
    STDMETHODIMP_(DWORD) SetLOD(DWORD lod) override { DWORD o = lod_; lod_ = lod; return o; }
    STDMETHODIMP_(DWORD) GetLOD() override { return lod_; }
    STDMETHODIMP_(DWORD) GetLevelCount() override { return image->levels; }
    STDMETHODIMP SetAutoGenFilterType(D3DTEXTUREFILTERTYPE f) override { filter = f; return D3D_OK; }
    STDMETHODIMP_(D3DTEXTUREFILTERTYPE) GetAutoGenFilterType() override { return filter; }
    STDMETHODIMP_(void) GenerateMipSubLevels() override;
    STDMETHODIMP GetLevelDesc(UINT level, D3DSURFACE_DESC* desc) override;
    STDMETHODIMP GetCubeMapSurface(D3DCUBEMAP_FACES face, UINT level, IDirect3DSurface9** surface) override;
    STDMETHODIMP LockRect(D3DCUBEMAP_FACES face, UINT level, D3DLOCKED_RECT* locked, const RECT* rect, DWORD flags) override;
    STDMETHODIMP UnlockRect(D3DCUBEMAP_FACES face, UINT level) override;
    STDMETHODIMP AddDirtyRect(D3DCUBEMAP_FACES, const RECT*) override { return D3D_OK; }

    Image* GetImage() override { return image.get(); }
    RefCounted* Ref() override { return this; }

private:
    std::unique_ptr<Image> image;
    std::vector<Surface*> surfaces;
    DWORD lod_ = 0;
    D3DTEXTUREFILTERTYPE filter = D3DTEXF_LINEAR;
};

// --- buffers --------------------------------------------------------------------

class BufferData {
public:
    BufferData(Device* device, UINT size, DWORD usage, D3DPOOL pool, UINT bind);
    ~BufferData();
    HRESULT Init();
    HRESULT Lock(UINT offset, UINT size, void** data, DWORD flags);
    HRESULT Unlock();
    void Flush();              // upload the dirty range
    bool ReadBack();           // GPU -> a fresh shadow
    bool Evictable() const;
    bool uploaded = false;     // the GPU buffer holds data the game wrote

    Device* device;
    ID3D11Buffer* buffer = nullptr;
    uint8_t* shadow = nullptr;
    UINT size;
    DWORD usage;
    D3DPOOL pool;
    UINT bind;
    UINT dirtyBegin = UINT_MAX, dirtyEnd = 0;
    int locks = 0;
};

class VertexBuffer final : public IDirect3DVertexBuffer9, public DeviceChild {
public:
    VertexBuffer(Device* device, UINT size, DWORD usage, DWORD fvf, D3DPOOL pool);

    STDMETHODIMP QueryInterface(REFIID riid, void** out) override;
    D3D9_UNKNOWN_METHODS
    D3D9_RESOURCE_METHODS(D3DRTYPE_VERTEXBUFFER)
    STDMETHODIMP Lock(UINT offset, UINT size, void** data, DWORD flags) override { return data_.Lock(offset, size, data, flags); }
    STDMETHODIMP Unlock() override { return data_.Unlock(); }
    STDMETHODIMP GetDesc(D3DVERTEXBUFFER_DESC* desc) override;

    BufferData data_;
    DWORD fvf;
};

class IndexBuffer final : public IDirect3DIndexBuffer9, public DeviceChild {
public:
    IndexBuffer(Device* device, UINT size, DWORD usage, D3DFORMAT format, D3DPOOL pool);

    STDMETHODIMP QueryInterface(REFIID riid, void** out) override;
    D3D9_UNKNOWN_METHODS
    D3D9_RESOURCE_METHODS(D3DRTYPE_INDEXBUFFER)
    STDMETHODIMP Lock(UINT offset, UINT size, void** data, DWORD flags) override { return data_.Lock(offset, size, data, flags); }
    STDMETHODIMP Unlock() override { return data_.Unlock(); }
    STDMETHODIMP GetDesc(D3DINDEXBUFFER_DESC* desc) override;

    BufferData data_;
    D3DFORMAT format;
};

// --- declarations and shaders ---------------------------------------------------

class VertexDeclaration final : public IDirect3DVertexDeclaration9, public DeviceChild {
public:
    VertexDeclaration(Device* device, const D3DVERTEXELEMENT9* elements, DWORD fvf);

    STDMETHODIMP QueryInterface(REFIID riid, void** out) override;
    D3D9_UNKNOWN_METHODS
    STDMETHODIMP GetDevice(IDirect3DDevice9** out) override;
    STDMETHODIMP GetDeclaration(D3DVERTEXELEMENT9* elements, UINT* count) override;

    std::vector<D3DVERTEXELEMENT9> elements;   // without the END marker
    DWORD fvf;                                  // the FVF it came from, or 0
    uint64_t id;                                // unique, for input-layout caching
};

struct ShaderVariant {
    ID3D11DeviceChild* shader = nullptr;   // ID3D11VertexShader / ID3D11PixelShader
    ID3DBlob* bytecode = nullptr;
};

class ShaderBase : public DeviceChild {
public:
    ShaderBase(Device* device, const DWORD* tokens, dxso::Stage stage);
    ~ShaderBase() override;
    bool Valid() const { return base.ok; }

    // The compiled shader for a set of vertex-input fetch types (vertex
    // shaders only; pixel shaders have one variant).
    ShaderVariant* Variant(const dxso::Options& options);

    std::vector<uint32_t> tokens;
    dxso::Result base;
    dxso::Stage stage;

private:
    std::map<std::string, ShaderVariant> variants;
};

class VertexShader final : public IDirect3DVertexShader9, public ShaderBase {
public:
    VertexShader(Device* device, const DWORD* tokens) : ShaderBase(device, tokens, dxso::Stage::Vertex) {}
    STDMETHODIMP QueryInterface(REFIID riid, void** out) override;
    D3D9_UNKNOWN_METHODS
    STDMETHODIMP GetDevice(IDirect3DDevice9** out) override;
    STDMETHODIMP GetFunction(void* data, UINT* size) override;
};

class PixelShader final : public IDirect3DPixelShader9, public ShaderBase {
public:
    PixelShader(Device* device, const DWORD* tokens) : ShaderBase(device, tokens, dxso::Stage::Pixel) {}
    STDMETHODIMP QueryInterface(REFIID riid, void** out) override;
    D3D9_UNKNOWN_METHODS
    STDMETHODIMP GetDevice(IDirect3DDevice9** out) override;
    STDMETHODIMP GetFunction(void* data, UINT* size) override;
};

// --- queries --------------------------------------------------------------------

class Query final : public IDirect3DQuery9, public DeviceChild {
public:
    Query(Device* device, D3DQUERYTYPE type);
    ~Query() override;
    HRESULT Init();

    STDMETHODIMP QueryInterface(REFIID riid, void** out) override;
    D3D9_UNKNOWN_METHODS
    STDMETHODIMP GetDevice(IDirect3DDevice9** out) override;
    STDMETHODIMP_(D3DQUERYTYPE) GetType() override { return type; }
    STDMETHODIMP_(DWORD) GetDataSize() override;
    STDMETHODIMP Issue(DWORD flags) override;
    STDMETHODIMP GetData(void* data, DWORD size, DWORD flags) override;

    static bool Supported(D3DQUERYTYPE type);

private:
    D3DQUERYTYPE type;
    ID3D11Query* query = nullptr;
    ID3D11Query* disjoint = nullptr;
    bool issued = false;
};

// --- device state ---------------------------------------------------------------

constexpr UINT kSamplers = 21;         // 0..15 pixel, 16 = displacement, 17..20 vertex
constexpr UINT kMaxRenderState = 256;
constexpr UINT kMaxTss = 33;
constexpr UINT kMaxSs = 14;
constexpr UINT kTransforms = 512;

inline int SamplerSlot(DWORD sampler)
{
    if (sampler < 16) return static_cast<int>(sampler);
    if (sampler == D3DDMAPSAMPLER) return 16;
    if (sampler >= D3DVERTEXTEXTURESAMPLER0 && sampler <= D3DVERTEXTEXTURESAMPLER3)
        return 17 + static_cast<int>(sampler - D3DVERTEXTEXTURESAMPLER0);
    return -1;
}

struct Stream {
    VertexBuffer* buffer = nullptr;
    UINT offset = 0, stride = 0;
    UINT frequency = 1;
};

struct Light {
    D3DLIGHT9 light;
    bool enabled = false;
};

// Everything a state block can capture. Pointers are captured by value and
// the block holds its own references.
struct DeviceState {
    DWORD rs[kMaxRenderState] = {};
    DWORD tss[8][kMaxTss] = {};
    DWORD ss[kSamplers][kMaxSs] = {};
    D3DMATRIX transforms[kTransforms];
    D3DVIEWPORT9 viewport = {};
    RECT scissor = {};
    D3DMATERIAL9 material = {};
    std::map<DWORD, Light> lights;
    float clipPlanes[6][4] = {};
    float vsF[256][4] = {};
    int vsI[16][4] = {};
    BOOL vsB[16] = {};
    float psF[224][4] = {};
    int psI[16][4] = {};
    BOOL psB[16] = {};
    Stream streams[16];
    IndexBuffer* indices = nullptr;
    VertexDeclaration* decl = nullptr;
    DWORD fvf = 0;
    VertexShader* vs = nullptr;
    PixelShader* ps = nullptr;
    IDirect3DBaseTexture9* textures[kSamplers] = {};
};

// Which parts of DeviceState a state block covers.
struct StateMask {
    std::bitset<kMaxRenderState> rs;
    std::bitset<8 * kMaxTss> tss;
    std::bitset<kSamplers * kMaxSs> ss;
    std::bitset<kTransforms> transforms;
    bool viewport = false, scissor = false, material = false;
    std::map<DWORD, bool> lights;         // light index -> also captures enable
    bool allLights = false;
    std::bitset<6> clipPlanes;
    std::bitset<256> vsF;
    std::bitset<16> vsI, vsB;
    std::bitset<224> psF;
    std::bitset<16> psI, psB;
    std::bitset<16> streams, streamFreq;
    bool indices = false, decl = false, vs = false, ps = false;
    std::bitset<kSamplers> textures;
};

class StateBlock final : public IDirect3DStateBlock9, public DeviceChild {
public:
    StateBlock(Device* device, const StateMask& mask);
    ~StateBlock() override;

    STDMETHODIMP QueryInterface(REFIID riid, void** out) override;
    D3D9_UNKNOWN_METHODS
    STDMETHODIMP GetDevice(IDirect3DDevice9** out) override;
    STDMETHODIMP Capture() override;
    STDMETHODIMP Apply() override;

    StateMask mask;
    DeviceState state;

private:
    void HoldReferences(bool hold);
};

// --- swap chain -----------------------------------------------------------------

class SwapChain final : public IDirect3DSwapChain9Ex, public DeviceChild {
public:
    SwapChain(Device* device, const D3DPRESENT_PARAMETERS& params);
    ~SwapChain() override;
    HRESULT Init();

    STDMETHODIMP QueryInterface(REFIID riid, void** out) override;
    D3D9_UNKNOWN_METHODS
    STDMETHODIMP Present(const RECT* src, const RECT* dst, HWND window, const RGNDATA* dirty, DWORD flags) override;
    STDMETHODIMP GetFrontBufferData(IDirect3DSurface9* dst) override;
    STDMETHODIMP GetBackBuffer(UINT index, D3DBACKBUFFER_TYPE type, IDirect3DSurface9** out) override;
    STDMETHODIMP GetRasterStatus(D3DRASTER_STATUS* status) override;
    STDMETHODIMP GetDisplayMode(D3DDISPLAYMODE* mode) override;
    STDMETHODIMP GetDevice(IDirect3DDevice9** out) override;
    STDMETHODIMP GetPresentParameters(D3DPRESENT_PARAMETERS* params) override;
    STDMETHODIMP GetLastPresentCount(UINT* count) override;
    STDMETHODIMP GetPresentStats(D3DPRESENTSTATS* stats) override;
    STDMETHODIMP GetDisplayModeEx(D3DDISPLAYMODEEX* mode, D3DDISPLAYROTATION* rotation) override;

    D3DPRESENT_PARAMETERS params;
    Surface* backBuffer = nullptr;   // private reference
    UINT presents = 0;
};

// --- the device -------------------------------------------------------------------

class Direct3D9;

class Device final : public IDirect3DDevice9Ex, public RefCounted {
public:
    Device(Direct3D9* parent, UINT adapter, D3DDEVTYPE type, HWND focus, DWORD flags, bool ex);
    ~Device() override;
    HRESULT Init(D3DPRESENT_PARAMETERS* params);

    // IUnknown
    STDMETHODIMP QueryInterface(REFIID riid, void** out) override;
    STDMETHODIMP_(ULONG) AddRef() override { return PublicAddRef(); }
    STDMETHODIMP_(ULONG) Release() override { return PublicRelease(); }

    // IDirect3DDevice9
    STDMETHODIMP TestCooperativeLevel() override;
    STDMETHODIMP_(UINT) GetAvailableTextureMem() override;
    STDMETHODIMP EvictManagedResources() override;
    STDMETHODIMP GetDirect3D(IDirect3D9** d3d9) override;
    STDMETHODIMP GetDeviceCaps(D3DCAPS9* caps) override;
    STDMETHODIMP GetDisplayMode(UINT swapchain, D3DDISPLAYMODE* mode) override;
    STDMETHODIMP GetCreationParameters(D3DDEVICE_CREATION_PARAMETERS* params) override;
    STDMETHODIMP SetCursorProperties(UINT x, UINT y, IDirect3DSurface9* bitmap) override;
    STDMETHODIMP_(void) SetCursorPosition(int x, int y, DWORD flags) override;
    STDMETHODIMP_(BOOL) ShowCursor(BOOL show) override;
    STDMETHODIMP CreateAdditionalSwapChain(D3DPRESENT_PARAMETERS* params, IDirect3DSwapChain9** swapchain) override;
    STDMETHODIMP GetSwapChain(UINT index, IDirect3DSwapChain9** swapchain) override;
    STDMETHODIMP_(UINT) GetNumberOfSwapChains() override;
    STDMETHODIMP Reset(D3DPRESENT_PARAMETERS* params) override;
    STDMETHODIMP Present(const RECT* src, const RECT* dst, HWND window, const RGNDATA* dirty) override;
    STDMETHODIMP GetBackBuffer(UINT swapchain, UINT index, D3DBACKBUFFER_TYPE type, IDirect3DSurface9** out) override;
    STDMETHODIMP GetRasterStatus(UINT swapchain, D3DRASTER_STATUS* status) override;
    STDMETHODIMP SetDialogBoxMode(BOOL enable) override;
    STDMETHODIMP_(void) SetGammaRamp(UINT swapchain, DWORD flags, const D3DGAMMARAMP* ramp) override;
    STDMETHODIMP_(void) GetGammaRamp(UINT swapchain, D3DGAMMARAMP* ramp) override;
    STDMETHODIMP CreateTexture(UINT width, UINT height, UINT levels, DWORD usage, D3DFORMAT format, D3DPOOL pool,
                               IDirect3DTexture9** texture, HANDLE* shared) override;
    STDMETHODIMP CreateVolumeTexture(UINT width, UINT height, UINT depth, UINT levels, DWORD usage, D3DFORMAT format,
                                     D3DPOOL pool, IDirect3DVolumeTexture9** texture, HANDLE* shared) override;
    STDMETHODIMP CreateCubeTexture(UINT edge, UINT levels, DWORD usage, D3DFORMAT format, D3DPOOL pool,
                                   IDirect3DCubeTexture9** texture, HANDLE* shared) override;
    STDMETHODIMP CreateVertexBuffer(UINT size, DWORD usage, DWORD fvf, D3DPOOL pool,
                                    IDirect3DVertexBuffer9** buffer, HANDLE* shared) override;
    STDMETHODIMP CreateIndexBuffer(UINT size, DWORD usage, D3DFORMAT format, D3DPOOL pool,
                                   IDirect3DIndexBuffer9** buffer, HANDLE* shared) override;
    STDMETHODIMP CreateRenderTarget(UINT width, UINT height, D3DFORMAT format, D3DMULTISAMPLE_TYPE ms,
                                    DWORD quality, BOOL lockable, IDirect3DSurface9** surface, HANDLE* shared) override;
    STDMETHODIMP CreateDepthStencilSurface(UINT width, UINT height, D3DFORMAT format, D3DMULTISAMPLE_TYPE ms,
                                           DWORD quality, BOOL discard, IDirect3DSurface9** surface, HANDLE* shared) override;
    STDMETHODIMP UpdateSurface(IDirect3DSurface9* src, const RECT* srcRect, IDirect3DSurface9* dst, const POINT* dstPoint) override;
    STDMETHODIMP UpdateTexture(IDirect3DBaseTexture9* src, IDirect3DBaseTexture9* dst) override;
    STDMETHODIMP GetRenderTargetData(IDirect3DSurface9* rt, IDirect3DSurface9* dst) override;
    STDMETHODIMP GetFrontBufferData(UINT swapchain, IDirect3DSurface9* dst) override;
    STDMETHODIMP StretchRect(IDirect3DSurface9* src, const RECT* srcRect, IDirect3DSurface9* dst, const RECT* dstRect,
                             D3DTEXTUREFILTERTYPE filter) override;
    STDMETHODIMP ColorFill(IDirect3DSurface9* surface, const RECT* rect, D3DCOLOR color) override;
    STDMETHODIMP CreateOffscreenPlainSurface(UINT width, UINT height, D3DFORMAT format, D3DPOOL pool,
                                             IDirect3DSurface9** surface, HANDLE* shared) override;
    STDMETHODIMP SetRenderTarget(DWORD index, IDirect3DSurface9* surface) override;
    STDMETHODIMP GetRenderTarget(DWORD index, IDirect3DSurface9** surface) override;
    STDMETHODIMP SetDepthStencilSurface(IDirect3DSurface9* surface) override;
    STDMETHODIMP GetDepthStencilSurface(IDirect3DSurface9** surface) override;
    STDMETHODIMP BeginScene() override;
    STDMETHODIMP EndScene() override;
    STDMETHODIMP Clear(DWORD count, const D3DRECT* rects, DWORD flags, D3DCOLOR color, float z, DWORD stencil) override;
    STDMETHODIMP SetTransform(D3DTRANSFORMSTATETYPE state, const D3DMATRIX* matrix) override;
    STDMETHODIMP GetTransform(D3DTRANSFORMSTATETYPE state, D3DMATRIX* matrix) override;
    STDMETHODIMP MultiplyTransform(D3DTRANSFORMSTATETYPE state, const D3DMATRIX* matrix) override;
    STDMETHODIMP SetViewport(const D3DVIEWPORT9* viewport) override;
    STDMETHODIMP GetViewport(D3DVIEWPORT9* viewport) override;
    STDMETHODIMP SetMaterial(const D3DMATERIAL9* material) override;
    STDMETHODIMP GetMaterial(D3DMATERIAL9* material) override;
    STDMETHODIMP SetLight(DWORD index, const D3DLIGHT9* light) override;
    STDMETHODIMP GetLight(DWORD index, D3DLIGHT9* light) override;
    STDMETHODIMP LightEnable(DWORD index, BOOL enable) override;
    STDMETHODIMP GetLightEnable(DWORD index, BOOL* enable) override;
    STDMETHODIMP SetClipPlane(DWORD index, const float* plane) override;
    STDMETHODIMP GetClipPlane(DWORD index, float* plane) override;
    STDMETHODIMP SetRenderState(D3DRENDERSTATETYPE state, DWORD value) override;
    STDMETHODIMP GetRenderState(D3DRENDERSTATETYPE state, DWORD* value) override;
    STDMETHODIMP CreateStateBlock(D3DSTATEBLOCKTYPE type, IDirect3DStateBlock9** block) override;
    STDMETHODIMP BeginStateBlock() override;
    STDMETHODIMP EndStateBlock(IDirect3DStateBlock9** block) override;
    STDMETHODIMP SetClipStatus(const D3DCLIPSTATUS9* status) override;
    STDMETHODIMP GetClipStatus(D3DCLIPSTATUS9* status) override;
    STDMETHODIMP GetTexture(DWORD stage, IDirect3DBaseTexture9** texture) override;
    STDMETHODIMP SetTexture(DWORD stage, IDirect3DBaseTexture9* texture) override;
    STDMETHODIMP GetTextureStageState(DWORD stage, D3DTEXTURESTAGESTATETYPE type, DWORD* value) override;
    STDMETHODIMP SetTextureStageState(DWORD stage, D3DTEXTURESTAGESTATETYPE type, DWORD value) override;
    STDMETHODIMP GetSamplerState(DWORD sampler, D3DSAMPLERSTATETYPE type, DWORD* value) override;
    STDMETHODIMP SetSamplerState(DWORD sampler, D3DSAMPLERSTATETYPE type, DWORD value) override;
    STDMETHODIMP ValidateDevice(DWORD* passes) override;
    STDMETHODIMP SetPaletteEntries(UINT palette, const PALETTEENTRY* entries) override;
    STDMETHODIMP GetPaletteEntries(UINT palette, PALETTEENTRY* entries) override;
    STDMETHODIMP SetCurrentTexturePalette(UINT palette) override;
    STDMETHODIMP GetCurrentTexturePalette(UINT* palette) override;
    STDMETHODIMP SetScissorRect(const RECT* rect) override;
    STDMETHODIMP GetScissorRect(RECT* rect) override;
    STDMETHODIMP SetSoftwareVertexProcessing(BOOL software) override;
    STDMETHODIMP_(BOOL) GetSoftwareVertexProcessing() override;
    STDMETHODIMP SetNPatchMode(float segments) override;
    STDMETHODIMP_(float) GetNPatchMode() override;
    STDMETHODIMP DrawPrimitive(D3DPRIMITIVETYPE type, UINT start, UINT count) override;
    STDMETHODIMP DrawIndexedPrimitive(D3DPRIMITIVETYPE type, INT baseVertex, UINT minIndex, UINT vertices,
                                      UINT startIndex, UINT count) override;
    STDMETHODIMP DrawPrimitiveUP(D3DPRIMITIVETYPE type, UINT count, const void* data, UINT stride) override;
    STDMETHODIMP DrawIndexedPrimitiveUP(D3DPRIMITIVETYPE type, UINT minIndex, UINT vertices, UINT count,
                                        const void* indices, D3DFORMAT format, const void* data, UINT stride) override;
    STDMETHODIMP ProcessVertices(UINT srcStart, UINT dstIndex, UINT count, IDirect3DVertexBuffer9* dst,
                                 IDirect3DVertexDeclaration9* decl, DWORD flags) override;
    STDMETHODIMP CreateVertexDeclaration(const D3DVERTEXELEMENT9* elements, IDirect3DVertexDeclaration9** decl) override;
    STDMETHODIMP SetVertexDeclaration(IDirect3DVertexDeclaration9* decl) override;
    STDMETHODIMP GetVertexDeclaration(IDirect3DVertexDeclaration9** decl) override;
    STDMETHODIMP SetFVF(DWORD fvf) override;
    STDMETHODIMP GetFVF(DWORD* fvf) override;
    STDMETHODIMP CreateVertexShader(const DWORD* tokens, IDirect3DVertexShader9** shader) override;
    STDMETHODIMP SetVertexShader(IDirect3DVertexShader9* shader) override;
    STDMETHODIMP GetVertexShader(IDirect3DVertexShader9** shader) override;
    STDMETHODIMP SetVertexShaderConstantF(UINT reg, const float* data, UINT count) override;
    STDMETHODIMP GetVertexShaderConstantF(UINT reg, float* data, UINT count) override;
    STDMETHODIMP SetVertexShaderConstantI(UINT reg, const int* data, UINT count) override;
    STDMETHODIMP GetVertexShaderConstantI(UINT reg, int* data, UINT count) override;
    STDMETHODIMP SetVertexShaderConstantB(UINT reg, const BOOL* data, UINT count) override;
    STDMETHODIMP GetVertexShaderConstantB(UINT reg, BOOL* data, UINT count) override;
    STDMETHODIMP SetStreamSource(UINT stream, IDirect3DVertexBuffer9* buffer, UINT offset, UINT stride) override;
    STDMETHODIMP GetStreamSource(UINT stream, IDirect3DVertexBuffer9** buffer, UINT* offset, UINT* stride) override;
    STDMETHODIMP SetStreamSourceFreq(UINT stream, UINT frequency) override;
    STDMETHODIMP GetStreamSourceFreq(UINT stream, UINT* frequency) override;
    STDMETHODIMP SetIndices(IDirect3DIndexBuffer9* buffer) override;
    STDMETHODIMP GetIndices(IDirect3DIndexBuffer9** buffer) override;
    STDMETHODIMP CreatePixelShader(const DWORD* tokens, IDirect3DPixelShader9** shader) override;
    STDMETHODIMP SetPixelShader(IDirect3DPixelShader9* shader) override;
    STDMETHODIMP GetPixelShader(IDirect3DPixelShader9** shader) override;
    STDMETHODIMP SetPixelShaderConstantF(UINT reg, const float* data, UINT count) override;
    STDMETHODIMP GetPixelShaderConstantF(UINT reg, float* data, UINT count) override;
    STDMETHODIMP SetPixelShaderConstantI(UINT reg, const int* data, UINT count) override;
    STDMETHODIMP GetPixelShaderConstantI(UINT reg, int* data, UINT count) override;
    STDMETHODIMP SetPixelShaderConstantB(UINT reg, const BOOL* data, UINT count) override;
    STDMETHODIMP GetPixelShaderConstantB(UINT reg, BOOL* data, UINT count) override;
    STDMETHODIMP DrawRectPatch(UINT handle, const float* segments, const D3DRECTPATCH_INFO* info) override;
    STDMETHODIMP DrawTriPatch(UINT handle, const float* segments, const D3DTRIPATCH_INFO* info) override;
    STDMETHODIMP DeletePatch(UINT handle) override;
    STDMETHODIMP CreateQuery(D3DQUERYTYPE type, IDirect3DQuery9** query) override;

    // IDirect3DDevice9Ex
    STDMETHODIMP SetConvolutionMonoKernel(UINT width, UINT height, float* rows, float* columns) override;
    STDMETHODIMP ComposeRects(IDirect3DSurface9* src, IDirect3DSurface9* dst, IDirect3DVertexBuffer9* srcRects,
                              UINT count, IDirect3DVertexBuffer9* dstRects, D3DCOMPOSERECTSOP op, int x, int y) override;
    STDMETHODIMP PresentEx(const RECT* src, const RECT* dst, HWND window, const RGNDATA* dirty, DWORD flags) override;
    STDMETHODIMP GetGPUThreadPriority(INT* priority) override;
    STDMETHODIMP SetGPUThreadPriority(INT priority) override;
    STDMETHODIMP WaitForVBlank(UINT swapchain) override;
    STDMETHODIMP CheckResourceResidency(IDirect3DResource9** resources, UINT32 count) override;
    STDMETHODIMP SetMaximumFrameLatency(UINT latency) override;
    STDMETHODIMP GetMaximumFrameLatency(UINT* latency) override;
    STDMETHODIMP CheckDeviceState(HWND window) override;
    STDMETHODIMP CreateRenderTargetEx(UINT width, UINT height, D3DFORMAT format, D3DMULTISAMPLE_TYPE ms, DWORD quality,
                                      BOOL lockable, IDirect3DSurface9** surface, HANDLE* shared, DWORD usage) override;
    STDMETHODIMP CreateOffscreenPlainSurfaceEx(UINT width, UINT height, D3DFORMAT format, D3DPOOL pool,
                                               IDirect3DSurface9** surface, HANDLE* shared, DWORD usage) override;
    STDMETHODIMP CreateDepthStencilSurfaceEx(UINT width, UINT height, D3DFORMAT format, D3DMULTISAMPLE_TYPE ms,
                                             DWORD quality, BOOL discard, IDirect3DSurface9** surface, HANDLE* shared,
                                             DWORD usage) override;
    STDMETHODIMP ResetEx(D3DPRESENT_PARAMETERS* params, D3DDISPLAYMODEEX* mode) override;
    STDMETHODIMP GetDisplayModeEx(UINT swapchain, D3DDISPLAYMODEEX* mode, D3DDISPLAYROTATION* rotation) override;

    // --- internals shared with the objects ---
    ID3D11Device* dev = nullptr;
    ID3D11DeviceContext* ctx = nullptr;
    ID3D11DeviceContext1* ctx1 = nullptr;
    std::recursive_mutex mutex;
    uint64_t NextId() { return ++idCounter; }
    ID3DBlob* CompileHlsl(const std::string& hlsl, const char* profile);
    void PresentBackBuffer(SwapChain* chain);

    // State-block recording and application.
    StateBlock* recording = nullptr;
    DeviceState state;
    void CaptureInto(DeviceState& dst, const StateMask& mask);
    void ApplyFrom(const DeviceState& src, const StateMask& mask);
    static StateMask MaskFor(D3DSTATEBLOCKTYPE type);

private:
    HRESULT CreateSurface(UINT width, UINT height, D3DFORMAT format, DWORD usage, D3DPOOL pool, bool lockable,
                          D3DMULTISAMPLE_TYPE ms, IDirect3DSurface9** out);
    HRESULT ResetState(D3DPRESENT_PARAMETERS* params);
    void SetDefaultStates();
    bool PrepareDraw(UINT* instances);
    bool BindShaders(UINT instanceMask);
    ID3D11InputLayout* InputLayout(ShaderVariant* vs, const VertexDeclaration* decl, UINT instanceMask,
                                   const std::vector<dxso::InputDecl>& inputs);
    VertexDeclaration* FvfDeclaration(DWORD fvf);
    void FlushConstants();
    void BindOutputs();
    void BindStates();
    void BindTextures();
    ID3D11SamplerState* Sampler(UINT slot);
    HRESULT DrawFan(bool indexed, INT baseVertex, UINT start, UINT count, const void* upIndices, D3DFORMAT upFormat);
    HRESULT UploadUp(const void* data, UINT bytes, UINT* offset, ID3D11Buffer** buffer, bool index);
    Image* ImageOf(IDirect3DBaseTexture9* texture);
    void ResetViewport();
    bool InitBlitter();
    bool Blit(Image* src, UINT srcSub, const RECT& srcRect, Image* dst, UINT dstSub, const RECT& dstRect, bool linear);

    // Fixed-function pipeline (d3d9_ff.cpp): shaders generated from state.
    ShaderVariant* FixedVertexShader(const VertexDeclaration* decl, std::vector<dxso::InputDecl>* inputs);
    ShaderVariant* FixedPixelShader();
    void UploadFixedConstants();

    Direct3D9* parent;
    UINT adapter;
    D3DDEVTYPE deviceType;
    HWND focusWindow;
    DWORD behaviorFlags;
    bool ex;

    SwapChain* swapChain = nullptr;          // implicit swap chain (private reference)
    Surface* autoDepth = nullptr;            // automatic depth-stencil (private reference)
    Bound<Surface> renderTargets[4];
    Bound<Surface> depthStencil;

    // Bindings with private references (the state struct holds raw pointers).
    Bound<RefCounted> texRefs[kSamplers];
    Bound<VertexBuffer> streamRefs[16];
    Bound<IndexBuffer> indexRef;
    Bound<VertexDeclaration> declRef;
    Bound<VertexShader> vsRef;
    Bound<PixelShader> psRef;

    // D3D11 objects.
    ID3D11Buffer* cbVs[4] = {};                  // float, int, bool, fixup
    ID3D11Buffer* cbPs[4] = {};
    bool vsConstDirty = true, psConstDirty = true;
    ID3D11Buffer* upVertices = nullptr;          // DrawPrimitiveUP ring
    ID3D11Buffer* upIndices = nullptr;
    UINT upVertexCursor = 0, upIndexCursor = 0;
    UINT upVertexCapacity = 0, upIndexCapacity = 0;
    struct { ID3D11Buffer* buffer; UINT offset; UINT stride; } upStream = {};   // stream 0 during a UP draw
    float lastVsFix[16] = { -1e30f };
    float lastPsFix[16] = { -1e30f };
    D3DFORMAT boundDepthFormat = D3DFMT_UNKNOWN;
    bool warnedDepthSize = false;
    bool warnedPartialDepthClear = false;

    // Fixed-function shaders by state key, and their constants (b4).
    struct FixedVertex { ShaderVariant variant; std::vector<dxso::InputDecl> inputs; };
    std::unordered_map<std::string, FixedVertex> fixedVs;
    std::unordered_map<std::string, ShaderVariant> fixedPs;
    ID3D11Buffer* cbFixed = nullptr;
    bool usingFixed = false;

    // StretchRect's scaling / converting path.
    ID3D11VertexShader* blitVs = nullptr;
    ID3D11PixelShader* blitPs = nullptr;
    ID3D11SamplerState* blitSampler[2] = {};
    ID3D11Buffer* blitConstants = nullptr;
    ID3D11RasterizerState* blitRaster = nullptr;
    ID3D11BlendState* blitBlend = nullptr;
    ID3D11DepthStencilState* blitDepth = nullptr;
    ID3D11Buffer* zeroBuffer = nullptr;          // feeds shader inputs the declaration lacks
    std::unordered_map<uint64_t, ID3D11BlendState*> blendStates;
    std::unordered_map<uint64_t, ID3D11DepthStencilState*> depthStates;
    std::unordered_map<std::string, ID3D11RasterizerState*> rasterStates;   // key carries float bias bits
    std::unordered_map<std::string, ID3D11SamplerState*> samplerStates;
    std::map<std::pair<uint64_t, ID3D11DeviceChild*>, ID3D11InputLayout*> inputLayouts;
    std::map<DWORD, VertexDeclaration*> fvfDecls;          // private references
    std::unordered_map<std::string, ID3DBlob*> compiled;   // HLSL -> bytecode
    UINT stencilRef = 0;
    ShaderVariant* currentVs = nullptr;
    ShaderVariant* currentPs = nullptr;

    uint64_t idCounter = 0;
    bool inScene = false;
    bool warnedFixedFunction = false;
    D3DGAMMARAMP gamma = {};
    UINT maxLatency = 3;
    INT gpuPriority = 0;
};

// --- the interface object ---------------------------------------------------------

class Direct3D9 final : public IDirect3D9Ex, public RefCounted {
public:
    explicit Direct3D9(bool ex) : ex(ex) {}

    STDMETHODIMP QueryInterface(REFIID riid, void** out) override;
    STDMETHODIMP_(ULONG) AddRef() override { return PublicAddRef(); }
    STDMETHODIMP_(ULONG) Release() override { return PublicRelease(); }

    STDMETHODIMP RegisterSoftwareDevice(void*) override { return D3D_OK; }
    STDMETHODIMP_(UINT) GetAdapterCount() override { return 1; }
    STDMETHODIMP GetAdapterIdentifier(UINT adapter, DWORD flags, D3DADAPTER_IDENTIFIER9* id) override;
    STDMETHODIMP_(UINT) GetAdapterModeCount(UINT adapter, D3DFORMAT format) override;
    STDMETHODIMP EnumAdapterModes(UINT adapter, D3DFORMAT format, UINT mode, D3DDISPLAYMODE* out) override;
    STDMETHODIMP GetAdapterDisplayMode(UINT adapter, D3DDISPLAYMODE* mode) override;
    STDMETHODIMP CheckDeviceType(UINT adapter, D3DDEVTYPE type, D3DFORMAT display, D3DFORMAT backbuffer, BOOL windowed) override;
    STDMETHODIMP CheckDeviceFormat(UINT adapter, D3DDEVTYPE type, D3DFORMAT adapterFormat, DWORD usage,
                                   D3DRESOURCETYPE resource, D3DFORMAT format) override;
    STDMETHODIMP CheckDeviceMultiSampleType(UINT adapter, D3DDEVTYPE type, D3DFORMAT format, BOOL windowed,
                                            D3DMULTISAMPLE_TYPE ms, DWORD* quality) override;
    STDMETHODIMP CheckDepthStencilMatch(UINT adapter, D3DDEVTYPE type, D3DFORMAT adapterFormat, D3DFORMAT rt,
                                        D3DFORMAT ds) override;
    STDMETHODIMP CheckDeviceFormatConversion(UINT adapter, D3DDEVTYPE type, D3DFORMAT src, D3DFORMAT dst) override;
    STDMETHODIMP GetDeviceCaps(UINT adapter, D3DDEVTYPE type, D3DCAPS9* caps) override;
    STDMETHODIMP_(HMONITOR) GetAdapterMonitor(UINT adapter) override;
    STDMETHODIMP CreateDevice(UINT adapter, D3DDEVTYPE type, HWND focus, DWORD flags,
                              D3DPRESENT_PARAMETERS* params, IDirect3DDevice9** device) override;

    STDMETHODIMP_(UINT) GetAdapterModeCountEx(UINT adapter, const D3DDISPLAYMODEFILTER* filter) override;
    STDMETHODIMP EnumAdapterModesEx(UINT adapter, const D3DDISPLAYMODEFILTER* filter, UINT mode, D3DDISPLAYMODEEX* out) override;
    STDMETHODIMP GetAdapterDisplayModeEx(UINT adapter, D3DDISPLAYMODEEX* mode, D3DDISPLAYROTATION* rotation) override;
    STDMETHODIMP CreateDeviceEx(UINT adapter, D3DDEVTYPE type, HWND focus, DWORD flags, D3DPRESENT_PARAMETERS* params,
                                D3DDISPLAYMODEEX* mode, IDirect3DDevice9Ex** device) override;
    STDMETHODIMP GetAdapterLUID(UINT adapter, LUID* luid) override;

    static void FillCaps(D3DCAPS9* caps);

private:
    bool ex;
};

} // namespace d3d9
