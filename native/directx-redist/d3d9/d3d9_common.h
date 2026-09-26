// Shared pieces of the Direct3D 9 on Direct3D 11 implementation.
//
// d3d9.dll presents the D3D9 API to a game and draws with D3D11: shaders go
// through dxso (D3D9 bytecode -> SM5 HLSL -> d3dcompiler_47), render states
// become cached D3D11 state objects, and resources are D3D11 resources with a
// CPU shadow wherever D3D9 lets the game lock them.
//
// Presentation does not touch a DXGI swap chain: the host application (the
// Nativra loader) installs a callback and receives the finished back buffer as
// an ID3D11Texture2D on its own device, the same way FakeSwapChain hands frames
// to FrameMirror. Memory the game reads or writes through a pointer (every
// Lock) comes from a replaceable allocator, so a 32-bit guest can be given
// pointers inside its own address space.

#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX   // windows.h's min/max macros break std::min/std::max under MSVC
#endif
#include <windows.h>
#include <d3d9.h>
#include <d3d11_1.h>

#include <atomic>
#include <cstdint>
#include <cstring>
#include <mutex>

namespace d3d9 {

// --- host hooks (exported; see d3d9.def) ------------------------------------

using AllocFn = void* (*)(size_t size);
using FreeFn = void (*)(void* p);
using PresentFn = void (*)(void* context, ID3D11Texture2D* backBuffer, UINT width, UINT height);
using LogFn = void (*)(const char* line);

struct Host {
    ID3D11Device* device = nullptr;      // shared with the app when it supplies one
    PresentFn present = nullptr;
    void* presentContext = nullptr;
    AllocFn alloc = nullptr;
    FreeFn free = nullptr;
    LogFn log = nullptr;
    bool warp = false;                   // tests: create a WARP device
    // Back-buffer size when the game asks for "the window's size". The DLL
    // reads no window itself: it must not import user32, which the console's
    // app container does not give a natively loaded DLL.
    UINT width = 1280, height = 720;
};

Host& GetHost();

void* LockMemoryAlloc(size_t size);
void LockMemoryFree(void* p);

void Log(const char* format, ...);

// --- refcounting ------------------------------------------------------------

// D3D9 objects carry two counts: the public one the game sees through
// AddRef/Release, and a private one for references the device holds (bound
// textures, buffers, render targets). An object dies when both reach zero, so
// Release() returns what the game expects while bindings keep it alive.
class RefCounted {
public:
    // Objects the game creates start with one public reference; objects the
    // device makes for itself (back buffer, automatic depth buffer) start with
    // none and live on a private reference.
    explicit RefCounted(bool startPublic = true) : publicRefs(startPublic ? 1 : 0) {}
    virtual ~RefCounted() = default;

    // Virtual so a texture's surface can forward every count to the texture
    // that owns it: binding such a surface must keep the texture alive.
    virtual ULONG PublicAddRef()
    {
        const ULONG n = ++publicRefs;
        if (n == 1) OnPublicFirst();
        return n;
    }
    virtual ULONG PublicRelease()
    {
        const ULONG left = --publicRefs;
        if (left == 0) {
            // Hold a private reference across the hook: releasing the device
            // there can cascade back into this object's private count.
            PrivateAddRef();
            OnPublicZero();
            PrivateRelease();
        }
        return left;
    }
    virtual void PrivateAddRef() { ++privateRefs; }
    virtual void PrivateRelease()
    {
        if (--privateRefs == 0 && publicRefs == 0) delete this;
    }
    ULONG PublicCount() const { return publicRefs; }

protected:
    virtual void OnPublicFirst() {}
    virtual void OnPublicZero() {}

private:
    std::atomic<ULONG> publicRefs;
    std::atomic<ULONG> privateRefs{ 0 };
};

// A private reference held by the device on a bound object.
template <typename T>
class Bound {
public:
    Bound() = default;
    Bound(const Bound&) = delete;
    Bound& operator=(const Bound&) = delete;
    ~Bound() { Set(nullptr); }

    void Set(T* p)
    {
        if (p == ptr) return;
        if (p) p->PrivateAddRef();
        if (ptr) ptr->PrivateRelease();
        ptr = p;
    }
    T* Get() const { return ptr; }
    operator T*() const { return ptr; }
    T* operator->() const { return ptr; }

private:
    T* ptr = nullptr;
};

template <typename T>
void SafeRelease(T*& p)
{
    if (p) { p->Release(); p = nullptr; }
}

// --- formats ------------------------------------------------------------------

enum class Convert : uint8_t {
    None,         // memcpy
    X8ToA8,       // X8R8G8B8/X8B8G8R8: force alpha to 0xFF
    L8,           // L8 -> RGBA8 (L, L, L, 255)
    A8L8,         // A8L8 -> RGBA8 (L, L, L, A)
    L16,          // L16 -> RGBA16 (L, L, L, 65535)
    A4L4,         // A4L4 -> RGBA8
    X1R5G5B5,     // force the 1-bit alpha on
    R8G8B8,       // 24-bit -> BGRA8
};

struct FormatInfo {
    D3DFORMAT d3d;
    DXGI_FORMAT typeless;    // resource format (typeless when there is an sRGB view)
    DXGI_FORMAT view;        // SRV/RTV format
    DXGI_FORMAT srgb;        // sRGB view format, or UNKNOWN
    DXGI_FORMAT depth;       // DSV format for depth formats, else UNKNOWN
    UINT d3dBytes;           // bytes per pixel as D3D9 lays it out (per block for BC)
    UINT dxgiBytes;          // bytes per pixel in the D3D11 resource (per block for BC)
    bool block;              // 4x4 compressed
    Convert convert;
};

// Null when D3D9 format has no D3D11 home.
const FormatInfo* GetFormat(D3DFORMAT format);

inline UINT RowPitch(const FormatInfo& f, UINT width, bool d3dLayout)
{
    const UINT bytes = d3dLayout ? f.d3dBytes : f.dxgiBytes;
    return f.block ? ((width + 3) / 4) * bytes : width * bytes;
}

inline UINT RowCount(const FormatInfo& f, UINT height)
{
    return f.block ? (height + 3) / 4 : height;
}

// Copies `rows` rows from D3D9 layout into the D3D11 layout, converting.
void ConvertRows(const FormatInfo& f, const uint8_t* src, UINT srcPitch, uint8_t* dst, UINT dstPitch,
                 UINT width, UINT rows);

// Vertex element type (D3DDECLTYPE) to a D3D11 input format; `integer` is set
// when the shader must receive the value as an integer (see dxso::InputType).
DXGI_FORMAT DeclTypeFormat(BYTE type, int* integer);
UINT DeclTypeSize(BYTE type);

} // namespace d3d9
