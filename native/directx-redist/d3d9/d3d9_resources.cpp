// D3D9 resources on D3D11: images (texture storage), surfaces, textures,
// buffers, vertex declarations, shaders, queries, swap chain, state blocks.

#include "d3d9_objects.h"

#include <d3dcompiler.h>

#include <algorithm>

namespace d3d9 {

// --- private data ---------------------------------------------------------------

PrivateData::~PrivateData()
{
    for (auto& e : entries) if (e.second.unknown) e.second.unknown->Release();
}

HRESULT PrivateData::Set(REFGUID guid, const void* data, DWORD size, DWORD flags)
{
    if (!data && size) return D3DERR_INVALIDCALL;
    Free(guid);
    Entry e;
    if (flags & D3DSPD_IUNKNOWN) {
        if (size != sizeof(IUnknown*)) return D3DERR_INVALIDCALL;
        e.unknown = *static_cast<IUnknown* const*>(data);
        if (e.unknown) e.unknown->AddRef();
    } else {
        e.bytes.assign(static_cast<const uint8_t*>(data), static_cast<const uint8_t*>(data) + size);
    }
    entries[Key(guid)] = std::move(e);
    return D3D_OK;
}

HRESULT PrivateData::Get(REFGUID guid, void* data, DWORD* size)
{
    if (!size) return D3DERR_INVALIDCALL;
    auto it = entries.find(Key(guid));
    if (it == entries.end()) return D3DERR_NOTFOUND;
    const DWORD need = it->second.unknown ? static_cast<DWORD>(sizeof(IUnknown*))
                                          : static_cast<DWORD>(it->second.bytes.size());
    if (!data) { *size = need; return D3D_OK; }
    if (*size < need) { *size = need; return D3DERR_MOREDATA; }
    if (it->second.unknown) {
        it->second.unknown->AddRef();
        *static_cast<IUnknown**>(data) = it->second.unknown;
    } else if (need) {
        std::memcpy(data, it->second.bytes.data(), need);
    }
    *size = need;
    return D3D_OK;
}

HRESULT PrivateData::Free(REFGUID guid)
{
    auto it = entries.find(Key(guid));
    if (it == entries.end()) return D3DERR_NOTFOUND;
    if (it->second.unknown) it->second.unknown->Release();
    entries.erase(it);
    return D3D_OK;
}

// --- device children ------------------------------------------------------------

DeviceChild::DeviceChild(Device* device, bool startPublic)
    : RefCounted(startPublic), device(device)
{
    if (startPublic) device->AddRef();
}

void DeviceChild::OnPublicFirst() { device->AddRef(); }
void DeviceChild::OnPublicZero() { device->Release(); }

#define DEFINE_GET_DEVICE(Class)                                  \
    HRESULT Class::GetDevice(IDirect3DDevice9** out)              \
    {                                                             \
        if (!out) return D3DERR_INVALIDCALL;                      \
        device->AddRef();                                         \
        *out = device;                                            \
        return D3D_OK;                                            \
    }

DEFINE_GET_DEVICE(Surface)
DEFINE_GET_DEVICE(Texture)
DEFINE_GET_DEVICE(CubeTexture)
DEFINE_GET_DEVICE(Volume)
DEFINE_GET_DEVICE(VolumeTexture)
DEFINE_GET_DEVICE(VertexBuffer)
DEFINE_GET_DEVICE(IndexBuffer)
DEFINE_GET_DEVICE(VertexDeclaration)
DEFINE_GET_DEVICE(VertexShader)
DEFINE_GET_DEVICE(PixelShader)
DEFINE_GET_DEVICE(Query)
DEFINE_GET_DEVICE(StateBlock)
DEFINE_GET_DEVICE(SwapChain)

template <typename T>
static HRESULT ReturnInterface(T* object, void** out)
{
    object->AddRef();
    *out = object;
    return S_OK;
}

// --- images ---------------------------------------------------------------------

Image::Image(Device* device, UINT width, UINT height, UINT levels, UINT faces, D3DFORMAT format,
             DWORD usage, D3DPOOL pool, bool lockable)
    : device(device), format(format), width(width), height(height), levels(levels), faces(faces),
      usage(usage), pool(pool), lockable(lockable)
{
}

Image::~Image()
{
    for (auto& s : subs) LockMemoryFree(s.shadow);
    for (auto* v : srv) SafeRelease(v);
    for (auto& list : rtv) for (auto* v : list) if (v) v->Release();
    for (auto* v : dsv) if (v) v->Release();
    SafeRelease(staging);
    SafeRelease(texture);
}

HRESULT Image::Init()
{
    fmt = GetFormat(format);
    if (!fmt || width == 0 || height == 0) return D3DERR_INVALIDCALL;

    // Levels 0 asks for the whole chain; so does an auto-generated chain.
    UINT fullChain = 1;
    for (UINT s = std::max(width, height); s > 1; s >>= 1) fullChain++;
    if (levels == 0 || levels > fullChain || (usage & D3DUSAGE_AUTOGENMIPMAP)) levels = fullChain;

    subs.resize(static_cast<size_t>(faces) * levels);
    for (UINT f = 0; f < faces; f++) {
        for (UINT l = 0; l < levels; l++) {
            Subresource& s = subs[Sub(f, l)];
            s.width = std::max(1u, width >> l);
            s.height = std::max(1u, height >> l);
            s.pitch = (RowPitch(*fmt, s.width, true) + 3) & ~3u;
        }
    }
    for (auto& list : rtv) list.assign(subs.size(), nullptr);
    dsv.assign(subs.size(), nullptr);

    if (pool == D3DPOOL_SYSTEMMEM || pool == D3DPOOL_SCRATCH) return D3D_OK;   // CPU only

    D3D11_TEXTURE2D_DESC desc = {};
    desc.Width = width;
    desc.Height = height;
    desc.MipLevels = levels;
    desc.ArraySize = faces;
    desc.Format = fmt->typeless;
    desc.SampleDesc.Count = 1;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    if (usage & D3DUSAGE_RENDERTARGET) desc.BindFlags |= D3D11_BIND_RENDER_TARGET;
    if (usage & D3DUSAGE_DEPTHSTENCIL) desc.BindFlags |= D3D11_BIND_DEPTH_STENCIL;
    if ((usage & D3DUSAGE_AUTOGENMIPMAP) && !fmt->block && !IsDepth()) {
        desc.BindFlags |= D3D11_BIND_RENDER_TARGET;
        desc.MiscFlags |= D3D11_RESOURCE_MISC_GENERATE_MIPS;
    }
    if (faces == 6) desc.MiscFlags |= D3D11_RESOURCE_MISC_TEXTURECUBE;

    HRESULT hr = device->dev->CreateTexture2D(&desc, nullptr, &texture);
    if (FAILED(hr) && (desc.BindFlags & D3D11_BIND_SHADER_RESOURCE) && IsDepth()) {
        desc.BindFlags &= ~D3D11_BIND_SHADER_RESOURCE;   // some depth formats cannot be sampled
        hr = device->dev->CreateTexture2D(&desc, nullptr, &texture);
    }
    if (FAILED(hr)) {
        Log("CreateTexture2D %ux%u levels %u format %d failed: 0x%08lX", width, height, levels,
            static_cast<int>(format), static_cast<unsigned long>(hr));
        return hr == E_OUTOFMEMORY ? D3DERR_OUTOFVIDEOMEMORY : D3DERR_INVALIDCALL;
    }
    return D3D_OK;
}

uint8_t* Image::Shadow(UINT sub)
{
    Subresource& s = subs[sub];
    if (!s.shadow) {
        const size_t bytes = static_cast<size_t>(s.pitch) * RowCount(*fmt, s.height);
        s.shadow = static_cast<uint8_t*>(LockMemoryAlloc(bytes));
        if (!s.shadow) return nullptr;
        std::memset(s.shadow, 0, bytes);
        if (s.evicted) {
            // Dropped after its upload: the GPU copy is the contents.
            s.evicted = false;
            s.valid = false;
            ReadBack(sub);
            return s.shadow;
        }
        // A fresh CPU-only image, or one the GPU has never been given data
        // for, starts as zeros on both sides.
        s.valid = !texture || !(usage & (D3DUSAGE_RENDERTARGET | D3DUSAGE_DEPTHSTENCIL));
    }
    return s.shadow;
}

// A static texture's CPU copy is only needed while it is locked: once the
// data is on the GPU it goes, and comes back from the GPU if the game locks
// again. That keeps most textures in video memory only, inside the console's
// budget. Kept: dynamic textures (locked every frame), render targets and
// depth, system-memory and scratch pools (CPU only by definition), and
// formats converted on upload (the GPU copy is not in the D3D9 layout).
bool Image::Evictable() const
{
    return texture && !IsDepth() && fmt->convert == Convert::None &&
           !(usage & (D3DUSAGE_DYNAMIC | D3DUSAGE_RENDERTARGET | D3DUSAGE_DEPTHSTENCIL)) &&
           (pool == D3DPOOL_MANAGED || pool == D3DPOOL_DEFAULT);
}

HRESULT Image::Lock(UINT sub, D3DLOCKED_RECT* locked, const RECT* rect, DWORD flags)
{
    if (!locked || sub >= subs.size()) return D3DERR_INVALIDCALL;
    Subresource& s = subs[sub];
    if (s.locked) return D3DERR_INVALIDCALL;
    if (!Shadow(sub)) return E_OUTOFMEMORY;

    // Whatever the GPU drew into a render target has to be read back first.
    if (texture && (usage & (D3DUSAGE_RENDERTARGET | D3DUSAGE_DEPTHSTENCIL)) && !(flags & D3DLOCK_DISCARD))
        ReadBack(sub);

    UINT x = 0, y = 0;
    if (rect) {
        x = static_cast<UINT>(rect->left);
        y = static_cast<UINT>(rect->top);
    }
    const UINT rowOffset = fmt->block ? (y / 4) * s.pitch : y * s.pitch;
    const UINT colOffset = fmt->block ? (x / 4) * fmt->d3dBytes : x * fmt->d3dBytes;
    locked->pBits = s.shadow + rowOffset + colOffset;
    locked->Pitch = static_cast<INT>(s.pitch);
    s.locked = true;
    s.valid = s.valid || !(flags & D3DLOCK_READONLY);
    lockReadOnly = (flags & D3DLOCK_READONLY) != 0;
    return D3D_OK;
}

HRESULT Image::Unlock(UINT sub)
{
    if (sub >= subs.size() || !subs[sub].locked) return D3DERR_INVALIDCALL;
    subs[sub].locked = false;
    if (texture && !lockReadOnly) {
        Upload(sub);
        const UINT level = sub % levels;
        if (level == 0 && (usage & D3DUSAGE_AUTOGENMIPMAP) && Srv(false))
            device->ctx->GenerateMips(Srv(false));
    }
    if (Evictable()) {
        Subresource& s = subs[sub];
        LockMemoryFree(s.shadow);
        s.shadow = nullptr;
        s.evicted = true;
        s.valid = false;
    }
    return D3D_OK;
}

void Image::Upload(UINT sub)
{
    Subresource& s = subs[sub];
    if (!texture || !s.shadow || IsDepth()) return;   // D3D11 cannot UpdateSubresource a depth buffer
    const UINT rows = RowCount(*fmt, s.height);
    if (fmt->convert == Convert::None) {
        device->ctx->UpdateSubresource(texture, sub, nullptr, s.shadow, s.pitch, 0);
        return;
    }
    const UINT dstPitch = RowPitch(*fmt, s.width, false);
    std::vector<uint8_t> converted(static_cast<size_t>(dstPitch) * rows);
    ConvertRows(*fmt, s.shadow, s.pitch, converted.data(), dstPitch, s.width, rows);
    device->ctx->UpdateSubresource(texture, sub, nullptr, converted.data(), dstPitch, 0);
}

bool Image::ReadBack(UINT sub)
{
    Subresource& s = subs[sub];
    if (!texture || !Shadow(sub)) return false;

    D3D11_TEXTURE2D_DESC desc;
    texture->GetDesc(&desc);
    desc.Width = s.width;
    desc.Height = s.height;
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Usage = D3D11_USAGE_STAGING;
    desc.BindFlags = 0;
    desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    desc.MiscFlags = 0;
    ID3D11Texture2D* copy = nullptr;
    if (FAILED(device->dev->CreateTexture2D(&desc, nullptr, &copy))) return false;
    device->ctx->CopySubresourceRegion(copy, 0, 0, 0, 0, texture, sub, nullptr);

    D3D11_MAPPED_SUBRESOURCE mapped;
    const bool ok = SUCCEEDED(device->ctx->Map(copy, 0, D3D11_MAP_READ, 0, &mapped));
    if (ok) {
        const UINT rows = RowCount(*fmt, s.height);
        const UINT bytes = std::min(s.pitch, RowPitch(*fmt, s.width, false));
        for (UINT y = 0; y < rows; y++)
            std::memcpy(s.shadow + static_cast<size_t>(y) * s.pitch,
                        static_cast<const uint8_t*>(mapped.pData) + static_cast<size_t>(y) * mapped.RowPitch, bytes);
        device->ctx->Unmap(copy, 0);
        s.valid = true;
    }
    copy->Release();
    return ok;
}

ID3D11ShaderResourceView* Image::Srv(bool srgb)
{
    if (!texture) return nullptr;
    const bool useSrgb = srgb && fmt->srgb != DXGI_FORMAT_UNKNOWN;
    ID3D11ShaderResourceView*& view = srv[useSrgb ? 1 : 0];
    if (!view) {
        D3D11_SHADER_RESOURCE_VIEW_DESC desc = {};
        desc.Format = useSrgb ? fmt->srgb : fmt->view;
        if (faces == 6) {
            desc.ViewDimension = D3D11_SRV_DIMENSION_TEXTURECUBE;
            desc.TextureCube.MipLevels = static_cast<UINT>(-1);
        } else {
            desc.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
            desc.Texture2D.MipLevels = static_cast<UINT>(-1);
        }
        if (FAILED(device->dev->CreateShaderResourceView(texture, &desc, &view))) view = nullptr;
    }
    return view;
}

ID3D11RenderTargetView* Image::Rtv(UINT sub, bool srgb)
{
    if (!texture || sub >= subs.size() || IsDepth()) return nullptr;
    const bool useSrgb = srgb && fmt->srgb != DXGI_FORMAT_UNKNOWN;
    ID3D11RenderTargetView*& view = rtv[useSrgb ? 1 : 0][sub];
    if (!view) {
        D3D11_RENDER_TARGET_VIEW_DESC desc = {};
        desc.Format = useSrgb ? fmt->srgb : fmt->view;
        desc.ViewDimension = D3D11_RTV_DIMENSION_TEXTURE2DARRAY;
        desc.Texture2DArray.MipSlice = sub % levels;
        desc.Texture2DArray.FirstArraySlice = sub / levels;
        desc.Texture2DArray.ArraySize = 1;
        if (FAILED(device->dev->CreateRenderTargetView(texture, &desc, &view))) view = nullptr;
    }
    return view;
}

ID3D11DepthStencilView* Image::Dsv(UINT sub)
{
    if (!texture || sub >= subs.size() || !IsDepth()) return nullptr;
    ID3D11DepthStencilView*& view = dsv[sub];
    if (!view) {
        D3D11_DEPTH_STENCIL_VIEW_DESC desc = {};
        desc.Format = fmt->depth;
        desc.ViewDimension = D3D11_DSV_DIMENSION_TEXTURE2DARRAY;
        desc.Texture2DArray.MipSlice = sub % levels;
        desc.Texture2DArray.FirstArraySlice = sub / levels;
        desc.Texture2DArray.ArraySize = 1;
        if (FAILED(device->dev->CreateDepthStencilView(texture, &desc, &view))) view = nullptr;
    }
    return view;
}

// --- surfaces -------------------------------------------------------------------

Surface::Surface(Device* device, Image* image, UINT sub, IUnknown* container, RefCounted* containerRef,
                 bool ownsImage, bool startPublic)
    : DeviceChild(device, startPublic), image(image), sub(sub), container(container), containerRef(containerRef),
      ownsImage(ownsImage)
{
}

Surface::~Surface()
{
    if (ownsImage) delete image;
}

HRESULT Surface::QueryInterface(REFIID riid, void** out)
{
    if (!out) return E_POINTER;
    *out = nullptr;
    if (riid == __uuidof(IUnknown) || riid == __uuidof(IDirect3DResource9) || riid == __uuidof(IDirect3DSurface9))
        return ReturnInterface(static_cast<IDirect3DSurface9*>(this), out);
    return E_NOINTERFACE;
}

HRESULT Surface::GetContainer(REFIID riid, void** out)
{
    if (!out) return D3DERR_INVALIDCALL;
    if (container) return container->QueryInterface(riid, out);
    return device->QueryInterface(riid, out);
}

HRESULT Surface::GetDesc(D3DSURFACE_DESC* desc)
{
    if (!desc) return D3DERR_INVALIDCALL;
    const Subresource& s = image->subs[sub];
    desc->Format = image->format;
    desc->Type = D3DRTYPE_SURFACE;
    desc->Usage = image->usage;
    desc->Pool = image->pool;
    desc->MultiSampleType = multisample;
    desc->MultiSampleQuality = 0;
    desc->Width = s.width;
    desc->Height = s.height;
    return D3D_OK;
}

HRESULT Surface::LockRect(D3DLOCKED_RECT* locked, const RECT* rect, DWORD flags)
{
    std::lock_guard<std::recursive_mutex> guard(device->mutex);
    return image->Lock(sub, locked, rect, flags);
}

HRESULT Surface::UnlockRect()
{
    std::lock_guard<std::recursive_mutex> guard(device->mutex);
    return image->Unlock(sub);
}

HRESULT Surface::GetDC(HDC*) { return D3DERR_INVALIDCALL; }
HRESULT Surface::ReleaseDC(HDC) { return D3DERR_INVALIDCALL; }

// --- textures -------------------------------------------------------------------

Texture::Texture(Device* device, std::unique_ptr<Image> image)
    : DeviceChild(device, true), image(std::move(image))
{
}

Texture::~Texture()
{
    for (auto* s : surfaces) delete s;
}

HRESULT Texture::Init()
{
    const HRESULT hr = image->Init();
    if (FAILED(hr)) return hr;
    for (UINT l = 0; l < image->levels; l++)
        surfaces.push_back(new Surface(device, image.get(), image->Sub(0, l), static_cast<IDirect3DTexture9*>(this),
                                       this, false, false));
    return D3D_OK;
}

HRESULT Texture::QueryInterface(REFIID riid, void** out)
{
    if (!out) return E_POINTER;
    *out = nullptr;
    if (riid == __uuidof(IUnknown) || riid == __uuidof(IDirect3DResource9) ||
        riid == __uuidof(IDirect3DBaseTexture9) || riid == __uuidof(IDirect3DTexture9))
        return ReturnInterface(static_cast<IDirect3DTexture9*>(this), out);
    return E_NOINTERFACE;
}

void Texture::GenerateMipSubLevels()
{
    std::lock_guard<std::recursive_mutex> guard(device->mutex);
    if ((image->usage & D3DUSAGE_AUTOGENMIPMAP) && image->Srv(false)) device->ctx->GenerateMips(image->Srv(false));
}

HRESULT Texture::GetLevelDesc(UINT level, D3DSURFACE_DESC* desc)
{
    if (level >= surfaces.size()) return D3DERR_INVALIDCALL;
    return surfaces[level]->GetDesc(desc);
}

HRESULT Texture::GetSurfaceLevel(UINT level, IDirect3DSurface9** surface)
{
    if (!surface || level >= surfaces.size()) return D3DERR_INVALIDCALL;
    surfaces[level]->AddRef();
    *surface = surfaces[level];
    return D3D_OK;
}

HRESULT Texture::LockRect(UINT level, D3DLOCKED_RECT* locked, const RECT* rect, DWORD flags)
{
    if (level >= surfaces.size()) return D3DERR_INVALIDCALL;
    return surfaces[level]->LockRect(locked, rect, flags);
}

HRESULT Texture::UnlockRect(UINT level)
{
    if (level >= surfaces.size()) return D3DERR_INVALIDCALL;
    return surfaces[level]->UnlockRect();
}

CubeTexture::CubeTexture(Device* device, std::unique_ptr<Image> image)
    : DeviceChild(device, true), image(std::move(image))
{
}

CubeTexture::~CubeTexture()
{
    for (auto* s : surfaces) delete s;
}

HRESULT CubeTexture::Init()
{
    const HRESULT hr = image->Init();
    if (FAILED(hr)) return hr;
    for (UINT f = 0; f < 6; f++)
        for (UINT l = 0; l < image->levels; l++)
            surfaces.push_back(new Surface(device, image.get(), image->Sub(f, l),
                                           static_cast<IDirect3DCubeTexture9*>(this), this, false, false));
    return D3D_OK;
}

HRESULT CubeTexture::QueryInterface(REFIID riid, void** out)
{
    if (!out) return E_POINTER;
    *out = nullptr;
    if (riid == __uuidof(IUnknown) || riid == __uuidof(IDirect3DResource9) ||
        riid == __uuidof(IDirect3DBaseTexture9) || riid == __uuidof(IDirect3DCubeTexture9))
        return ReturnInterface(static_cast<IDirect3DCubeTexture9*>(this), out);
    return E_NOINTERFACE;
}

void CubeTexture::GenerateMipSubLevels()
{
    std::lock_guard<std::recursive_mutex> guard(device->mutex);
    if ((image->usage & D3DUSAGE_AUTOGENMIPMAP) && image->Srv(false)) device->ctx->GenerateMips(image->Srv(false));
}

HRESULT CubeTexture::GetLevelDesc(UINT level, D3DSURFACE_DESC* desc)
{
    if (level >= image->levels) return D3DERR_INVALIDCALL;
    return surfaces[level]->GetDesc(desc);
}

HRESULT CubeTexture::GetCubeMapSurface(D3DCUBEMAP_FACES face, UINT level, IDirect3DSurface9** surface)
{
    if (!surface || face > D3DCUBEMAP_FACE_NEGATIVE_Z || level >= image->levels) return D3DERR_INVALIDCALL;
    Surface* s = surfaces[face * image->levels + level];
    s->AddRef();
    *surface = s;
    return D3D_OK;
}

HRESULT CubeTexture::LockRect(D3DCUBEMAP_FACES face, UINT level, D3DLOCKED_RECT* locked, const RECT* rect, DWORD flags)
{
    if (face > D3DCUBEMAP_FACE_NEGATIVE_Z || level >= image->levels) return D3DERR_INVALIDCALL;
    return surfaces[face * image->levels + level]->LockRect(locked, rect, flags);
}

HRESULT CubeTexture::UnlockRect(D3DCUBEMAP_FACES face, UINT level)
{
    if (face > D3DCUBEMAP_FACE_NEGATIVE_Z || level >= image->levels) return D3DERR_INVALIDCALL;
    return surfaces[face * image->levels + level]->UnlockRect();
}

// --- volume images --------------------------------------------------------------

VolumeImage::VolumeImage(Device* device, UINT width, UINT height, UINT depth, UINT levels, D3DFORMAT format,
                         DWORD usage, D3DPOOL pool)
    : device(device), format(format), width(width), height(height), depth(depth), levels(levels), usage(usage),
      pool(pool)
{
}

VolumeImage::~VolumeImage()
{
    for (auto& m : mips) LockMemoryFree(m.shadow);
    for (auto* v : srv) SafeRelease(v);
    SafeRelease(texture);
}

HRESULT VolumeImage::Init()
{
    fmt = GetFormat(format);
    if (!fmt || fmt->depth != DXGI_FORMAT_UNKNOWN || width == 0 || height == 0 || depth == 0 ||
        (usage & (D3DUSAGE_RENDERTARGET | D3DUSAGE_DEPTHSTENCIL)))
        return D3DERR_INVALIDCALL;

    // Levels 0 asks for the whole chain, which halves all three dimensions.
    UINT fullChain = 1;
    for (UINT s = std::max({ width, height, depth }); s > 1; s >>= 1) fullChain++;
    if (levels == 0 || levels > fullChain || (usage & D3DUSAGE_AUTOGENMIPMAP)) levels = fullChain;

    mips.resize(levels);
    for (UINT l = 0; l < levels; l++) {
        VolumeLevel& m = mips[l];
        m.width = std::max(1u, width >> l);
        m.height = std::max(1u, height >> l);
        m.depth = std::max(1u, depth >> l);
        m.rowPitch = (RowPitch(*fmt, m.width, true) + 3) & ~3u;
        m.slicePitch = m.rowPitch * RowCount(*fmt, m.height);
        // A level's shadow is one allocation, addressed with 32-bit offsets.
        if (static_cast<uint64_t>(m.slicePitch) * m.depth > 0x7FFFFFFFu) return D3DERR_OUTOFVIDEOMEMORY;
    }

    if (pool == D3DPOOL_SYSTEMMEM || pool == D3DPOOL_SCRATCH) return D3D_OK;   // CPU only

    D3D11_TEXTURE3D_DESC desc = {};
    desc.Width = width;
    desc.Height = height;
    desc.Depth = depth;
    desc.MipLevels = levels;
    desc.Format = fmt->typeless;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    if ((usage & D3DUSAGE_AUTOGENMIPMAP) && !fmt->block) {
        // D3D9 does not promise this for volumes, and not every format can do
        // it in D3D11: try, and fall back to a texture without it.
        D3D11_TEXTURE3D_DESC generating = desc;
        generating.BindFlags |= D3D11_BIND_RENDER_TARGET;
        generating.MiscFlags |= D3D11_RESOURCE_MISC_GENERATE_MIPS;
        autoGen = SUCCEEDED(device->dev->CreateTexture3D(&generating, nullptr, &texture));
    }
    HRESULT hr = S_OK;
    if (!texture) hr = device->dev->CreateTexture3D(&desc, nullptr, &texture);
    if (FAILED(hr)) {
        Log("CreateTexture3D %ux%ux%u levels %u format %d failed: 0x%08lX", width, height, depth, levels,
            static_cast<int>(format), static_cast<unsigned long>(hr));
        return hr == E_OUTOFMEMORY ? D3DERR_OUTOFVIDEOMEMORY : D3DERR_INVALIDCALL;
    }
    return D3D_OK;
}

uint8_t* VolumeImage::Shadow(UINT level)
{
    VolumeLevel& m = mips[level];
    if (!m.shadow) {
        const size_t bytes = static_cast<size_t>(m.slicePitch) * m.depth;
        m.shadow = static_cast<uint8_t*>(LockMemoryAlloc(bytes));
        if (!m.shadow) return nullptr;
        std::memset(m.shadow, 0, bytes);
        if (m.evicted) {
            // Dropped after its upload: the GPU copy is the contents.
            m.evicted = false;
            ReadBack(level);
        }
    }
    return m.shadow;
}

// The same rule as Image::Evictable: a static volume's CPU copy is only needed
// while it is locked, which keeps big noise and lookup volumes out of system
// memory.
bool VolumeImage::Evictable() const
{
    return texture && fmt->convert == Convert::None && !(usage & D3DUSAGE_DYNAMIC) &&
           (pool == D3DPOOL_MANAGED || pool == D3DPOOL_DEFAULT);
}

// Where a texel sits in a level's shadow. Compressed formats address whole
// 4x4 blocks, so `left` and `top` are on block boundaries there.
uint8_t* VolumeImage::Texel(const VolumeLevel& m, UINT left, UINT top, UINT front) const
{
    const UINT rowOffset = fmt->block ? (top / 4) * m.rowPitch : top * m.rowPitch;
    const UINT colOffset = fmt->block ? (left / 4) * fmt->d3dBytes : left * fmt->d3dBytes;
    return m.shadow + static_cast<size_t>(front) * m.slicePitch + rowOffset + colOffset;
}

HRESULT VolumeImage::Lock(UINT level, D3DLOCKED_BOX* locked, const D3DBOX* box, DWORD flags)
{
    if (!locked || level >= mips.size()) return D3DERR_INVALIDCALL;
    VolumeLevel& m = mips[level];
    if (m.locked) return D3DERR_INVALIDCALL;

    const D3DBOX whole = { 0, 0, m.width, m.height, 0, m.depth };
    const D3DBOX& b = box ? *box : whole;
    if (b.Left >= b.Right || b.Top >= b.Bottom || b.Front >= b.Back || b.Right > m.width || b.Bottom > m.height ||
        b.Back > m.depth)
        return D3DERR_INVALIDCALL;
    // Compressed formats lock whole blocks: edges fall on a block boundary or
    // on the edge of the level.
    if (fmt->block && ((b.Left | b.Top) % 4 != 0 || (b.Right % 4 != 0 && b.Right != m.width) ||
                       (b.Bottom % 4 != 0 && b.Bottom != m.height)))
        return D3DERR_INVALIDCALL;
    if (!Shadow(level)) return E_OUTOFMEMORY;

    locked->pBits = Texel(m, b.Left, b.Top, b.Front);
    locked->RowPitch = static_cast<INT>(m.rowPitch);
    locked->SlicePitch = static_cast<INT>(m.slicePitch);
    m.locked = true;
    m.readOnly = (flags & D3DLOCK_READONLY) != 0;
    m.lockedBox = { b.Left, b.Top, b.Front, b.Right, b.Bottom, b.Back };
    return D3D_OK;
}

HRESULT VolumeImage::Unlock(UINT level)
{
    if (level >= mips.size() || !mips[level].locked) return D3DERR_INVALIDCALL;
    VolumeLevel& m = mips[level];
    m.locked = false;
    if (texture && !m.readOnly) {
        // Only the box that was locked goes up: a small edit of a big volume
        // must not cost the whole level.
        const D3D11_BOX& b = m.lockedBox;
        Write(level, b, Texel(m, b.left, b.top, b.front), m.rowPitch, m.slicePitch);
        if (level == 0) GenerateMips();
    }
    if (Evictable()) {
        LockMemoryFree(m.shadow);
        m.shadow = nullptr;
        m.evicted = true;
    }
    return D3D_OK;
}

// Puts a box of texels, laid out as D3D9 holds them, on the GPU. `texels` is
// the box's first texel. Formats the GPU holds differently are converted.
void VolumeImage::Write(UINT level, const D3D11_BOX& box, const uint8_t* texels, UINT rowPitch, UINT slicePitch)
{
    const VolumeLevel& m = mips[level];
    const bool whole = box.left == 0 && box.top == 0 && box.front == 0 && box.right == m.width &&
                       box.bottom == m.height && box.back == m.depth;
    const D3D11_BOX* region = whole ? nullptr : &box;
    if (fmt->convert == Convert::None) {
        device->ctx->UpdateSubresource(texture, level, region, texels, rowPitch, slicePitch);
        return;
    }
    const UINT w = box.right - box.left, h = box.bottom - box.top, d = box.back - box.front;
    const UINT rows = RowCount(*fmt, h);
    const UINT convertedRow = RowPitch(*fmt, w, false);
    const UINT convertedSlice = convertedRow * rows;
    std::vector<uint8_t> converted(static_cast<size_t>(convertedSlice) * d);
    for (UINT z = 0; z < d; z++)
        ConvertRows(*fmt, texels + static_cast<size_t>(z) * slicePitch, rowPitch,
                    converted.data() + static_cast<size_t>(z) * convertedSlice, convertedRow, w, rows);
    device->ctx->UpdateSubresource(texture, level, region, converted.data(), convertedRow, convertedSlice);
}

bool VolumeImage::ReadBack(UINT level)
{
    VolumeLevel& m = mips[level];
    if (!texture || !Shadow(level)) return false;

    D3D11_TEXTURE3D_DESC desc;
    texture->GetDesc(&desc);
    desc.Width = m.width;
    desc.Height = m.height;
    desc.Depth = m.depth;
    desc.MipLevels = 1;
    desc.Usage = D3D11_USAGE_STAGING;
    desc.BindFlags = 0;
    desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    desc.MiscFlags = 0;
    ID3D11Texture3D* copy = nullptr;
    if (FAILED(device->dev->CreateTexture3D(&desc, nullptr, &copy))) return false;
    device->ctx->CopySubresourceRegion(copy, 0, 0, 0, 0, texture, level, nullptr);

    D3D11_MAPPED_SUBRESOURCE mapped;
    const bool ok = SUCCEEDED(device->ctx->Map(copy, 0, D3D11_MAP_READ, 0, &mapped));
    if (ok) {
        const UINT rows = RowCount(*fmt, m.height);
        const UINT bytes = std::min(m.rowPitch, RowPitch(*fmt, m.width, false));
        for (UINT z = 0; z < m.depth; z++)
            for (UINT y = 0; y < rows; y++)
                std::memcpy(m.shadow + static_cast<size_t>(z) * m.slicePitch + static_cast<size_t>(y) * m.rowPitch,
                            static_cast<const uint8_t*>(mapped.pData) + static_cast<size_t>(z) * mapped.DepthPitch +
                                static_cast<size_t>(y) * mapped.RowPitch,
                            bytes);
        device->ctx->Unmap(copy, 0);
    }
    copy->Release();
    return ok;
}

void VolumeImage::GenerateMips()
{
    if (autoGen && Srv(false)) device->ctx->GenerateMips(Srv(false));
}

// UpdateTexture for volumes, system memory to the GPU: every level this
// volume has comes from the source level of the same size, so the source may
// carry larger levels above.
HRESULT VolumeImage::UpdateFrom(VolumeImage& source)
{
    if (source.format != format || !texture) return D3DERR_INVALIDCALL;
    UINT first = 0;
    while (first < source.levels && (source.mips[first].width != width || source.mips[first].height != height ||
                                     source.mips[first].depth != depth))
        first++;
    if (first == source.levels) return D3DERR_INVALIDCALL;

    for (UINT l = 0; l < levels && first + l < source.levels; l++) {
        const VolumeLevel& from = source.mips[first + l];
        const VolumeLevel& to = mips[l];
        if (!from.shadow) {
            // Dropped after its upload: the GPU has it, copy on the GPU.
            if (from.evicted && source.texture)
                device->ctx->CopySubresourceRegion(texture, l, 0, 0, 0, source.texture, first + l, nullptr);
            continue;   // otherwise never written
        }
        const D3D11_BOX whole = { 0, 0, 0, to.width, to.height, to.depth };
        Write(l, whole, from.shadow, from.rowPitch, from.slicePitch);
    }
    GenerateMips();
    return D3D_OK;
}

ID3D11ShaderResourceView* VolumeImage::Srv(bool srgb)
{
    if (!texture) return nullptr;
    const bool useSrgb = srgb && fmt->srgb != DXGI_FORMAT_UNKNOWN;
    ID3D11ShaderResourceView*& view = srv[useSrgb ? 1 : 0];
    if (!view) {
        D3D11_SHADER_RESOURCE_VIEW_DESC desc = {};
        desc.Format = useSrgb ? fmt->srgb : fmt->view;
        desc.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE3D;
        desc.Texture3D.MipLevels = static_cast<UINT>(-1);
        if (FAILED(device->dev->CreateShaderResourceView(texture, &desc, &view))) view = nullptr;
    }
    return view;
}

// --- volume textures --------------------------------------------------------------

Volume::Volume(Device* device, VolumeImage* image, UINT level, IUnknown* container, RefCounted* containerRef)
    : DeviceChild(device, false), image(image), level(level), container(container), containerRef(containerRef)
{
}

HRESULT Volume::QueryInterface(REFIID riid, void** out)
{
    if (!out) return E_POINTER;
    *out = nullptr;
    if (riid == __uuidof(IUnknown) || riid == __uuidof(IDirect3DVolume9))
        return ReturnInterface(static_cast<IDirect3DVolume9*>(this), out);
    return E_NOINTERFACE;
}

HRESULT Volume::GetContainer(REFIID riid, void** out)
{
    if (!out) return D3DERR_INVALIDCALL;
    return container->QueryInterface(riid, out);
}

HRESULT Volume::GetDesc(D3DVOLUME_DESC* desc)
{
    if (!desc) return D3DERR_INVALIDCALL;
    const VolumeLevel& m = image->mips[level];
    desc->Format = image->format;
    desc->Type = D3DRTYPE_VOLUME;
    desc->Usage = image->usage;
    desc->Pool = image->pool;
    desc->Width = m.width;
    desc->Height = m.height;
    desc->Depth = m.depth;
    return D3D_OK;
}

HRESULT Volume::LockBox(D3DLOCKED_BOX* locked, const D3DBOX* box, DWORD flags)
{
    std::lock_guard<std::recursive_mutex> guard(device->mutex);
    return image->Lock(level, locked, box, flags);
}

HRESULT Volume::UnlockBox()
{
    std::lock_guard<std::recursive_mutex> guard(device->mutex);
    return image->Unlock(level);
}

VolumeTexture::VolumeTexture(Device* device, std::unique_ptr<VolumeImage> image)
    : DeviceChild(device, true), image(std::move(image))
{
}

VolumeTexture::~VolumeTexture()
{
    for (auto* v : volumes) delete v;
}

HRESULT VolumeTexture::Init()
{
    const HRESULT hr = image->Init();
    if (FAILED(hr)) return hr;
    for (UINT l = 0; l < image->levels; l++)
        volumes.push_back(new Volume(device, image.get(), l, static_cast<IDirect3DVolumeTexture9*>(this), this));
    return D3D_OK;
}

HRESULT VolumeTexture::QueryInterface(REFIID riid, void** out)
{
    if (!out) return E_POINTER;
    *out = nullptr;
    if (riid == __uuidof(IUnknown) || riid == __uuidof(IDirect3DResource9) ||
        riid == __uuidof(IDirect3DBaseTexture9) || riid == __uuidof(IDirect3DVolumeTexture9))
        return ReturnInterface(static_cast<IDirect3DVolumeTexture9*>(this), out);
    return E_NOINTERFACE;
}

void VolumeTexture::GenerateMipSubLevels()
{
    std::lock_guard<std::recursive_mutex> guard(device->mutex);
    image->GenerateMips();
}

HRESULT VolumeTexture::GetLevelDesc(UINT level, D3DVOLUME_DESC* desc)
{
    if (level >= volumes.size()) return D3DERR_INVALIDCALL;
    return volumes[level]->GetDesc(desc);
}

HRESULT VolumeTexture::GetVolumeLevel(UINT level, IDirect3DVolume9** volume)
{
    if (!volume || level >= volumes.size()) return D3DERR_INVALIDCALL;
    volumes[level]->AddRef();
    *volume = volumes[level];
    return D3D_OK;
}

HRESULT VolumeTexture::LockBox(UINT level, D3DLOCKED_BOX* locked, const D3DBOX* box, DWORD flags)
{
    if (level >= volumes.size()) return D3DERR_INVALIDCALL;
    return volumes[level]->LockBox(locked, box, flags);
}

HRESULT VolumeTexture::UnlockBox(UINT level)
{
    if (level >= volumes.size()) return D3DERR_INVALIDCALL;
    return volumes[level]->UnlockBox();
}

// --- buffers --------------------------------------------------------------------

BufferData::BufferData(Device* device, UINT size, DWORD usage, D3DPOOL pool, UINT bind)
    : device(device), size(size), usage(usage), pool(pool), bind(bind)
{
}

BufferData::~BufferData()
{
    LockMemoryFree(shadow);
    SafeRelease(buffer);
}

HRESULT BufferData::Init()
{
    if (size == 0) return D3DERR_INVALIDCALL;
    // Index buffers keep a CPU copy for the triangle-fan expansion; vertex
    // buffers get one when first locked.
    if (bind == D3D11_BIND_INDEX_BUFFER) {
        shadow = static_cast<uint8_t*>(LockMemoryAlloc(size));
        if (!shadow) return E_OUTOFMEMORY;
        std::memset(shadow, 0, size);
    }

    D3D11_BUFFER_DESC desc = {};
    desc.ByteWidth = (size + 15) & ~15u;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = bind;
    if (FAILED(device->dev->CreateBuffer(&desc, nullptr, &buffer))) return D3DERR_OUTOFVIDEOMEMORY;
    return D3D_OK;
}

HRESULT BufferData::Lock(UINT offset, UINT bytes, void** data, DWORD flags)
{
    if (!data) return D3DERR_INVALIDCALL;
    if (offset > size) offset = size;
    if (bytes == 0 || offset + bytes > size) bytes = size - offset;
    if (!shadow) {
        shadow = static_cast<uint8_t*>(LockMemoryAlloc(size));
        if (!shadow) return E_OUTOFMEMORY;
        // Written before and dropped since: unless the lock throws the old
        // contents away, they come back from the GPU.
        if (uploaded && !(flags & D3DLOCK_DISCARD)) ReadBack();
        else std::memset(shadow, 0, size);
    }
    *data = shadow + offset;
    if (!(flags & D3DLOCK_READONLY)) {
        dirtyBegin = std::min(dirtyBegin, offset);
        dirtyEnd = std::max(dirtyEnd, offset + bytes);
    }
    locks++;
    return D3D_OK;
}

HRESULT BufferData::Unlock()
{
    if (locks == 0) return D3DERR_INVALIDCALL;
    if (--locks == 0) {
        std::lock_guard<std::recursive_mutex> guard(device->mutex);
        Flush();
        if (Evictable()) {
            LockMemoryFree(shadow);
            shadow = nullptr;
        }
    }
    return D3D_OK;
}

// A static vertex buffer is filled once and drawn from the GPU copy for the
// rest of its life, so its CPU copy goes after the upload. Dynamic buffers
// (refilled every frame), system-memory and scratch pools, and index
// buffers (read on the CPU for triangle fans) keep theirs.
bool BufferData::Evictable() const
{
    return buffer && bind != D3D11_BIND_INDEX_BUFFER && !(usage & D3DUSAGE_DYNAMIC) &&
           pool != D3DPOOL_SYSTEMMEM && pool != D3DPOOL_SCRATCH;
}

bool BufferData::ReadBack()
{
    if (!buffer || !shadow) return false;
    D3D11_BUFFER_DESC desc = {};
    buffer->GetDesc(&desc);
    desc.Usage = D3D11_USAGE_STAGING;
    desc.BindFlags = 0;
    desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    desc.MiscFlags = 0;
    ID3D11Buffer* copy = nullptr;
    if (FAILED(device->dev->CreateBuffer(&desc, nullptr, &copy))) return false;
    std::lock_guard<std::recursive_mutex> guard(device->mutex);
    device->ctx->CopyResource(copy, buffer);
    D3D11_MAPPED_SUBRESOURCE mapped;
    const bool ok = SUCCEEDED(device->ctx->Map(copy, 0, D3D11_MAP_READ, 0, &mapped));
    if (ok) {
        std::memcpy(shadow, mapped.pData, size);
        device->ctx->Unmap(copy, 0);
    }
    copy->Release();
    return ok;
}

void BufferData::Flush()
{
    if (dirtyBegin >= dirtyEnd || !buffer) return;
    D3D11_BOX box = { dirtyBegin, 0, 0, dirtyEnd, 1, 1 };
    device->ctx->UpdateSubresource(buffer, 0, &box, shadow + dirtyBegin, 0, 0);
    uploaded = true;
    dirtyBegin = UINT_MAX;
    dirtyEnd = 0;
}

VertexBuffer::VertexBuffer(Device* device, UINT size, DWORD usage, DWORD fvf, D3DPOOL pool)
    : DeviceChild(device, true), data_(device, size, usage, pool, D3D11_BIND_VERTEX_BUFFER), fvf(fvf)
{
}

HRESULT VertexBuffer::QueryInterface(REFIID riid, void** out)
{
    if (!out) return E_POINTER;
    *out = nullptr;
    if (riid == __uuidof(IUnknown) || riid == __uuidof(IDirect3DResource9) || riid == __uuidof(IDirect3DVertexBuffer9))
        return ReturnInterface(static_cast<IDirect3DVertexBuffer9*>(this), out);
    return E_NOINTERFACE;
}

HRESULT VertexBuffer::GetDesc(D3DVERTEXBUFFER_DESC* desc)
{
    if (!desc) return D3DERR_INVALIDCALL;
    desc->Format = D3DFMT_VERTEXDATA;
    desc->Type = D3DRTYPE_VERTEXBUFFER;
    desc->Usage = data_.usage;
    desc->Pool = data_.pool;
    desc->Size = data_.size;
    desc->FVF = fvf;
    return D3D_OK;
}

IndexBuffer::IndexBuffer(Device* device, UINT size, DWORD usage, D3DFORMAT format, D3DPOOL pool)
    : DeviceChild(device, true), data_(device, size, usage, pool, D3D11_BIND_INDEX_BUFFER), format(format)
{
}

HRESULT IndexBuffer::QueryInterface(REFIID riid, void** out)
{
    if (!out) return E_POINTER;
    *out = nullptr;
    if (riid == __uuidof(IUnknown) || riid == __uuidof(IDirect3DResource9) || riid == __uuidof(IDirect3DIndexBuffer9))
        return ReturnInterface(static_cast<IDirect3DIndexBuffer9*>(this), out);
    return E_NOINTERFACE;
}

HRESULT IndexBuffer::GetDesc(D3DINDEXBUFFER_DESC* desc)
{
    if (!desc) return D3DERR_INVALIDCALL;
    desc->Format = format;
    desc->Type = D3DRTYPE_INDEXBUFFER;
    desc->Usage = data_.usage;
    desc->Pool = data_.pool;
    desc->Size = data_.size;
    return D3D_OK;
}

// --- vertex declarations --------------------------------------------------------

VertexDeclaration::VertexDeclaration(Device* device, const D3DVERTEXELEMENT9* list, DWORD fvf)
    : DeviceChild(device, true), fvf(fvf), id(device->NextId())
{
    for (const D3DVERTEXELEMENT9* e = list; e && e->Stream != 0xFF; e++) elements.push_back(*e);
}

HRESULT VertexDeclaration::QueryInterface(REFIID riid, void** out)
{
    if (!out) return E_POINTER;
    *out = nullptr;
    if (riid == __uuidof(IUnknown) || riid == __uuidof(IDirect3DVertexDeclaration9))
        return ReturnInterface(static_cast<IDirect3DVertexDeclaration9*>(this), out);
    return E_NOINTERFACE;
}

HRESULT VertexDeclaration::GetDeclaration(D3DVERTEXELEMENT9* out, UINT* count)
{
    if (!count) return D3DERR_INVALIDCALL;
    *count = static_cast<UINT>(elements.size()) + 1;
    if (out) {
        std::copy(elements.begin(), elements.end(), out);
        const D3DVERTEXELEMENT9 end = D3DDECL_END();
        out[elements.size()] = end;
    }
    return D3D_OK;
}

// --- shaders --------------------------------------------------------------------

// A D3D9 shader carries no length; walk it to the END token.
static size_t ShaderLength(const DWORD* tokens)
{
    const DWORD version = tokens[0];
    const bool sm2 = ((version >> 8) & 0xFF) >= 2;
    size_t i = 1;
    for (;;) {
        const DWORD token = tokens[i];
        if (token == 0x0000FFFF) return i + 1;
        const DWORD op = token & 0xFFFF;
        if (op == 0xFFFE) { i += 1 + ((token >> 16) & 0x7FFF); continue; }
        if (sm2) { i += 1 + ((token >> 24) & 0xF); continue; }
        // SM1 carries no length. Parameters have bit 31 set; def's four
        // literals may not, so it is sized explicitly.
        i++;
        if (op == 81) { i += 5; continue; }
        while ((tokens[i] & 0x80000000u) != 0) i++;
    }
}

ShaderBase::ShaderBase(Device* device, const DWORD* code, dxso::Stage stage)
    : DeviceChild(device, true), stage(stage)
{
    const size_t n = ShaderLength(code);
    tokens.assign(code, code + n);
    base = dxso::Translate(tokens.data(), tokens.size());
    if (!base.ok) Log("shader translation failed: %s", base.error.c_str());
}

ShaderBase::~ShaderBase()
{
    for (auto& v : variants) {
        SafeRelease(v.second.shader);
        SafeRelease(v.second.bytecode);
    }
}

ShaderVariant* ShaderBase::Variant(const dxso::Options& options)
{
    if (!base.ok) return nullptr;
    // Every Options field is part of the translation, so every one is part of the key.
    std::string key(reinterpret_cast<const char*>(options.inputTypes), sizeof(options.inputTypes));
    key.append(reinterpret_cast<const char*>(options.samplerTypes), sizeof(options.samplerTypes));
    key.append(reinterpret_cast<const char*>(options.projectDivisor), sizeof(options.projectDivisor));
    auto it = variants.find(key);
    if (it != variants.end()) return it->second.shader ? &it->second : nullptr;

    ShaderVariant& v = variants[key];
    bool defaultOptions = true;
    for (auto t : options.inputTypes) if (t != dxso::InputType::Float) defaultOptions = false;
    for (auto t : options.samplerTypes) if (t != dxso::SamplerType::None) defaultOptions = false;
    for (auto d : options.projectDivisor) if (d != 0) defaultOptions = false;
    const dxso::Result translated = defaultOptions ? base : dxso::Translate(tokens.data(), tokens.size(), options);
    if (!translated.ok) return nullptr;
    v.bytecode = device->CompileHlsl(translated.hlsl, translated.profile);
    if (!v.bytecode) return nullptr;
    HRESULT hr;
    if (stage == dxso::Stage::Vertex) {
        ID3D11VertexShader* vs = nullptr;
        hr = device->dev->CreateVertexShader(v.bytecode->GetBufferPointer(), v.bytecode->GetBufferSize(), nullptr, &vs);
        v.shader = vs;
    } else {
        ID3D11PixelShader* ps = nullptr;
        hr = device->dev->CreatePixelShader(v.bytecode->GetBufferPointer(), v.bytecode->GetBufferSize(), nullptr, &ps);
        v.shader = ps;
    }
    if (FAILED(hr)) {
        SafeRelease(v.shader);
        return nullptr;
    }
    return &v;
}

HRESULT VertexShader::QueryInterface(REFIID riid, void** out)
{
    if (!out) return E_POINTER;
    *out = nullptr;
    if (riid == __uuidof(IUnknown) || riid == __uuidof(IDirect3DVertexShader9))
        return ReturnInterface(static_cast<IDirect3DVertexShader9*>(this), out);
    return E_NOINTERFACE;
}

HRESULT VertexShader::GetFunction(void* data, UINT* size)
{
    if (!size) return D3DERR_INVALIDCALL;
    const UINT bytes = static_cast<UINT>(tokens.size() * 4);
    if (!data) { *size = bytes; return D3D_OK; }
    if (*size < bytes) return D3DERR_INVALIDCALL;
    std::memcpy(data, tokens.data(), bytes);
    *size = bytes;
    return D3D_OK;
}

HRESULT PixelShader::QueryInterface(REFIID riid, void** out)
{
    if (!out) return E_POINTER;
    *out = nullptr;
    if (riid == __uuidof(IUnknown) || riid == __uuidof(IDirect3DPixelShader9))
        return ReturnInterface(static_cast<IDirect3DPixelShader9*>(this), out);
    return E_NOINTERFACE;
}

HRESULT PixelShader::GetFunction(void* data, UINT* size)
{
    if (!size) return D3DERR_INVALIDCALL;
    const UINT bytes = static_cast<UINT>(tokens.size() * 4);
    if (!data) { *size = bytes; return D3D_OK; }
    if (*size < bytes) return D3DERR_INVALIDCALL;
    std::memcpy(data, tokens.data(), bytes);
    *size = bytes;
    return D3D_OK;
}

// --- queries --------------------------------------------------------------------

bool Query::Supported(D3DQUERYTYPE type)
{
    switch (type) {
    case D3DQUERYTYPE_EVENT: case D3DQUERYTYPE_OCCLUSION: case D3DQUERYTYPE_TIMESTAMP:
    case D3DQUERYTYPE_TIMESTAMPDISJOINT: case D3DQUERYTYPE_TIMESTAMPFREQ:
        return true;
    default:
        return false;
    }
}

Query::Query(Device* device, D3DQUERYTYPE type) : DeviceChild(device, true), type(type) {}

Query::~Query()
{
    SafeRelease(query);
    SafeRelease(disjoint);
}

HRESULT Query::Init()
{
    D3D11_QUERY_DESC desc = {};
    switch (type) {
    case D3DQUERYTYPE_EVENT: desc.Query = D3D11_QUERY_EVENT; break;
    case D3DQUERYTYPE_OCCLUSION: desc.Query = D3D11_QUERY_OCCLUSION; break;
    case D3DQUERYTYPE_TIMESTAMP: desc.Query = D3D11_QUERY_TIMESTAMP; break;
    case D3DQUERYTYPE_TIMESTAMPDISJOINT: case D3DQUERYTYPE_TIMESTAMPFREQ: desc.Query = D3D11_QUERY_TIMESTAMP_DISJOINT; break;
    default: return D3DERR_NOTAVAILABLE;
    }
    return SUCCEEDED(device->dev->CreateQuery(&desc, &query)) ? D3D_OK : D3DERR_NOTAVAILABLE;
}

HRESULT Query::QueryInterface(REFIID riid, void** out)
{
    if (!out) return E_POINTER;
    *out = nullptr;
    if (riid == __uuidof(IUnknown) || riid == __uuidof(IDirect3DQuery9))
        return ReturnInterface(static_cast<IDirect3DQuery9*>(this), out);
    return E_NOINTERFACE;
}

DWORD Query::GetDataSize()
{
    switch (type) {
    case D3DQUERYTYPE_EVENT: case D3DQUERYTYPE_TIMESTAMPDISJOINT: return sizeof(BOOL);
    case D3DQUERYTYPE_OCCLUSION: return sizeof(DWORD);
    case D3DQUERYTYPE_TIMESTAMP: case D3DQUERYTYPE_TIMESTAMPFREQ: return sizeof(UINT64);
    default: return 0;
    }
}

HRESULT Query::Issue(DWORD flags)
{
    std::lock_guard<std::recursive_mutex> guard(device->mutex);
    const bool hasBegin = type == D3DQUERYTYPE_OCCLUSION || type == D3DQUERYTYPE_TIMESTAMPDISJOINT ||
                          type == D3DQUERYTYPE_TIMESTAMPFREQ;
    if (flags & D3DISSUE_BEGIN) {
        if (hasBegin) device->ctx->Begin(query);
    } else if (flags & D3DISSUE_END) {
        if (hasBegin && !issued) device->ctx->Begin(query);   // END without BEGIN
        device->ctx->End(query);
        issued = true;
    }
    return D3D_OK;
}

HRESULT Query::GetData(void* data, DWORD size, DWORD flags)
{
    std::lock_guard<std::recursive_mutex> guard(device->mutex);
    if (!issued) {
        if (data && size) std::memset(data, 0, size);
        return type == D3DQUERYTYPE_EVENT ? S_OK : S_FALSE;
    }
    const UINT getFlags = (flags & D3DGETDATA_FLUSH) ? 0 : D3D11_ASYNC_GETDATA_DONOTFLUSH;
    union {
        BOOL event;
        UINT64 u64;
        D3D11_QUERY_DATA_TIMESTAMP_DISJOINT disjointData;
    } result = {};
    const UINT resultSize = type == D3DQUERYTYPE_EVENT ? sizeof(BOOL)
                          : (type == D3DQUERYTYPE_TIMESTAMPDISJOINT || type == D3DQUERYTYPE_TIMESTAMPFREQ)
                          ? sizeof(D3D11_QUERY_DATA_TIMESTAMP_DISJOINT) : sizeof(UINT64);
    const HRESULT hr = device->ctx->GetData(query, &result, resultSize, getFlags);
    if (hr != S_OK) return S_FALSE;
    if (!data || size == 0) return S_OK;

    switch (type) {
    case D3DQUERYTYPE_EVENT: { BOOL v = TRUE; std::memcpy(data, &v, std::min<DWORD>(size, sizeof v)); break; }
    case D3DQUERYTYPE_OCCLUSION: {
        DWORD v = static_cast<DWORD>(std::min<UINT64>(result.u64, 0xFFFFFFFFu));
        std::memcpy(data, &v, std::min<DWORD>(size, sizeof v));
        break;
    }
    case D3DQUERYTYPE_TIMESTAMP: std::memcpy(data, &result.u64, std::min<DWORD>(size, sizeof(UINT64))); break;
    case D3DQUERYTYPE_TIMESTAMPDISJOINT: {
        BOOL v = result.disjointData.Disjoint;
        std::memcpy(data, &v, std::min<DWORD>(size, sizeof v));
        break;
    }
    case D3DQUERYTYPE_TIMESTAMPFREQ:
        std::memcpy(data, &result.disjointData.Frequency, std::min<DWORD>(size, sizeof(UINT64)));
        break;
    default: break;
    }
    return S_OK;
}

// --- swap chain -----------------------------------------------------------------

SwapChain::SwapChain(Device* device, const D3DPRESENT_PARAMETERS& params)
    : DeviceChild(device, false), params(params)
{
}

SwapChain::~SwapChain()
{
    delete backBuffer;
}

HRESULT SwapChain::Init()
{
    if (params.BackBufferFormat == D3DFMT_UNKNOWN) params.BackBufferFormat = D3DFMT_X8R8G8B8;
    if (!params.BackBufferWidth) params.BackBufferWidth = GetHost().width;
    if (!params.BackBufferHeight) params.BackBufferHeight = GetHost().height;
    if (params.BackBufferCount == 0) params.BackBufferCount = 1;

    Image* image = new Image(device, params.BackBufferWidth, params.BackBufferHeight, 1, 1, params.BackBufferFormat,
                             D3DUSAGE_RENDERTARGET, D3DPOOL_DEFAULT,
                             (params.Flags & D3DPRESENTFLAG_LOCKABLE_BACKBUFFER) != 0);
    const HRESULT hr = image->Init();
    if (FAILED(hr)) { delete image; return hr; }
    backBuffer = new Surface(device, image, 0, static_cast<IDirect3DSwapChain9*>(this), this, true, false);
    return D3D_OK;
}

HRESULT SwapChain::QueryInterface(REFIID riid, void** out)
{
    if (!out) return E_POINTER;
    *out = nullptr;
    if (riid == __uuidof(IUnknown) || riid == __uuidof(IDirect3DSwapChain9) || riid == __uuidof(IDirect3DSwapChain9Ex))
        return ReturnInterface(static_cast<IDirect3DSwapChain9Ex*>(this), out);
    return E_NOINTERFACE;
}

HRESULT SwapChain::Present(const RECT*, const RECT*, HWND, const RGNDATA*, DWORD)
{
    device->PresentBackBuffer(this);
    return D3D_OK;
}

HRESULT SwapChain::GetFrontBufferData(IDirect3DSurface9* dst)
{
    return device->GetRenderTargetData(backBuffer, dst);
}

HRESULT SwapChain::GetBackBuffer(UINT index, D3DBACKBUFFER_TYPE, IDirect3DSurface9** out)
{
    if (!out || index >= params.BackBufferCount) return D3DERR_INVALIDCALL;
    backBuffer->AddRef();
    *out = backBuffer;
    return D3D_OK;
}

HRESULT SwapChain::GetRasterStatus(D3DRASTER_STATUS* status)
{
    if (!status) return D3DERR_INVALIDCALL;
    status->InVBlank = FALSE;
    status->ScanLine = 0;
    return D3D_OK;
}

HRESULT SwapChain::GetDisplayMode(D3DDISPLAYMODE* mode)
{
    if (!mode) return D3DERR_INVALIDCALL;
    mode->Width = params.BackBufferWidth;
    mode->Height = params.BackBufferHeight;
    mode->RefreshRate = 60;
    mode->Format = D3DFMT_X8R8G8B8;
    return D3D_OK;
}

HRESULT SwapChain::GetPresentParameters(D3DPRESENT_PARAMETERS* out)
{
    if (!out) return D3DERR_INVALIDCALL;
    *out = params;
    return D3D_OK;
}

HRESULT SwapChain::GetLastPresentCount(UINT* count)
{
    if (!count) return D3DERR_INVALIDCALL;
    *count = presents;
    return D3D_OK;
}

HRESULT SwapChain::GetPresentStats(D3DPRESENTSTATS* stats)
{
    if (!stats) return D3DERR_INVALIDCALL;
    std::memset(stats, 0, sizeof *stats);
    stats->PresentCount = presents;
    return D3D_OK;
}

HRESULT SwapChain::GetDisplayModeEx(D3DDISPLAYMODEEX* mode, D3DDISPLAYROTATION* rotation)
{
    if (mode) {
        mode->Size = sizeof *mode;
        mode->Width = params.BackBufferWidth;
        mode->Height = params.BackBufferHeight;
        mode->RefreshRate = 60;
        mode->Format = D3DFMT_X8R8G8B8;
        mode->ScanLineOrdering = D3DSCANLINEORDERING_PROGRESSIVE;
    }
    if (rotation) *rotation = D3DDISPLAYROTATION_IDENTITY;
    return D3D_OK;
}

// --- state blocks -----------------------------------------------------------------

StateBlock::StateBlock(Device* device, const StateMask& mask) : DeviceChild(device, true), mask(mask)
{
    for (auto& m : state.transforms) std::memset(&m, 0, sizeof m);
}

StateBlock::~StateBlock()
{
    HoldReferences(false);
}

HRESULT StateBlock::QueryInterface(REFIID riid, void** out)
{
    if (!out) return E_POINTER;
    *out = nullptr;
    if (riid == __uuidof(IUnknown) || riid == __uuidof(IDirect3DStateBlock9))
        return ReturnInterface(static_cast<IDirect3DStateBlock9*>(this), out);
    return E_NOINTERFACE;
}

void StateBlock::HoldReferences(bool hold)
{
    auto touch = [hold](RefCounted* r) {
        if (!r) return;
        if (hold) r->PrivateAddRef(); else r->PrivateRelease();
    };
    for (UINT s = 0; s < kSamplers; s++) {
        if (!mask.textures[s] || !state.textures[s]) continue;
        auto* binding = dynamic_cast<TextureBinding*>(state.textures[s]);
        if (binding) touch(binding->Ref());
    }
    for (UINT s = 0; s < 16; s++)
        if (mask.streams[s]) touch(state.streams[s].buffer);
    if (mask.indices) touch(state.indices);
    if (mask.decl) touch(state.decl);
    if (mask.vs) touch(static_cast<ShaderBase*>(state.vs));
    if (mask.ps) touch(static_cast<ShaderBase*>(state.ps));
}

HRESULT StateBlock::Capture()
{
    std::lock_guard<std::recursive_mutex> guard(device->mutex);
    HoldReferences(false);
    device->CaptureInto(state, mask);
    HoldReferences(true);
    return D3D_OK;
}

HRESULT StateBlock::Apply()
{
    std::lock_guard<std::recursive_mutex> guard(device->mutex);
    device->ApplyFrom(state, mask);
    return D3D_OK;
}

} // namespace d3d9
