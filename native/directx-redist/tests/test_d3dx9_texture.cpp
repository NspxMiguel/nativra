// Exercises D3DX_FROM_FILE through the exported texture loader and the WARP-backed bridge.
#include <cstdint>
#include <cstdio>
#include <cwchar>
#include <d3d9.h>
#include <windows.h>

using LoadTexture = HRESULT(WINAPI *)(IDirect3DDevice9 *, const void *, UINT, UINT, UINT, UINT, DWORD, D3DFORMAT,
                                      D3DPOOL, DWORD, DWORD, D3DCOLOR, void *, void *, IDirect3DTexture9 **);

int main()
{
    wchar_t path[MAX_PATH];
    GetModuleFileNameW(nullptr, path, MAX_PATH);
    wchar_t *name = wcsrchr(path, L'\\');
    if (!name)
        return 1;
    wcscpy(name + 1, L"d3d9.dll");
    HMODULE d3dModule = LoadLibraryW(path);
    wcscpy(name + 1, L"d3dx9_43.dll");
    HMODULE d3dxModule = LoadLibraryW(path);
    if (!d3dModule || !d3dxModule)
        return 1;
    auto create = reinterpret_cast<IDirect3D9 *(WINAPI *)(UINT)>(GetProcAddress(d3dModule, "Direct3DCreate9"));
    auto warp = reinterpret_cast<void(WINAPI *)(BOOL)>(GetProcAddress(d3dModule, "NativraD3D9UseWarp"));
    auto load = reinterpret_cast<LoadTexture>(GetProcAddress(d3dxModule, "D3DXCreateTextureFromFileInMemoryEx"));
    if (!create || !warp || !load)
        return 1;
    warp(TRUE);
    IDirect3D9 *d3d = create(D3D_SDK_VERSION);
    if (!d3d)
        return 1;
    D3DPRESENT_PARAMETERS pp = {};
    pp.BackBufferWidth = pp.BackBufferHeight = 16;
    pp.BackBufferFormat = D3DFMT_X8R8G8B8;
    pp.BackBufferCount = 1;
    pp.SwapEffect = D3DSWAPEFFECT_DISCARD;
    pp.Windowed = TRUE;
    IDirect3DDevice9 *device = nullptr;
    if (FAILED(d3d->CreateDevice(0, D3DDEVTYPE_HAL, nullptr, D3DCREATE_HARDWARE_VERTEXPROCESSING, &pp, &device)))
        return 1;
    const unsigned char png[] = {
        0x89, 0x50, 0x4e, 0x47, 0xd,  0xa,  0x1a, 0xa,  0x0, 0x0,  0x0,  0xd,  0x49, 0x48, 0x44, 0x52, 0x0,  0x0,  0x0,
        0x3,  0x0,  0x0,  0x0,  0x2,  0x8,  0x6,  0x0,  0x0, 0x0,  0x9d, 0x74, 0x66, 0x1a, 0x0,  0x0,  0x0,  0x11, 0x49,
        0x44, 0x41, 0x54, 0x78, 0x9c, 0x63, 0x10, 0x32, 0x9, 0xab, 0x80, 0x61, 0x6,  0x64, 0xe,  0x0,  0x50, 0x36, 0x6,
        0x79, 0x49, 0x20, 0x77, 0xea, 0x0,  0x0,  0x0,  0x0, 0x49, 0x45, 0x4e, 0x44, 0xae, 0x42, 0x60, 0x82};
    const UINT fromFile = 0xfffffffdu, defaults = 0xffffffffu;
    int failures = 0;
    // Super Meat Boy requests default dimensions, one mip, FROM_FILE format and DEFAULT pool.
    // Also check exact source dimensions/mips and the managed-pool upload path.
    for (int mode = 0; mode < 2; ++mode)
    {
        IDirect3DTexture9 *texture = nullptr;
        HRESULT hr = load(device, png, sizeof png, mode ? fromFile : defaults, mode ? fromFile : defaults,
                          mode ? fromFile : 1, 0, static_cast<D3DFORMAT>(fromFile),
                          mode ? D3DPOOL_MANAGED : D3DPOOL_DEFAULT, defaults, defaults, 0, nullptr, nullptr, &texture);
        bool ok = SUCCEEDED(hr) && texture;
        if (texture)
        {
            D3DSURFACE_DESC desc = {};
            ok = ok && SUCCEEDED(texture->GetLevelDesc(0, &desc)) && desc.Width == (mode ? 3u : 4u) &&
                 desc.Height == 2 && desc.Format == D3DFMT_A8R8G8B8 && texture->GetLevelCount() == 1;
            // DEFAULT-pool textures cannot be read by locking their CPU shadow.
            // Copy the GPU texture to a render target, then read it back.
            IDirect3DSurface9 *source = nullptr, *target = nullptr, *readback = nullptr;
            HRESULT copied = texture->GetSurfaceLevel(0, &source);
            if (SUCCEEDED(copied))
                copied = device->CreateRenderTarget(desc.Width, desc.Height, desc.Format, D3DMULTISAMPLE_NONE, 0, FALSE,
                                                    &target, nullptr);
            if (SUCCEEDED(copied))
                copied = device->CreateOffscreenPlainSurface(desc.Width, desc.Height, desc.Format, D3DPOOL_SYSTEMMEM,
                                                             &readback, nullptr);
            if (SUCCEEDED(copied))
                copied = device->StretchRect(source, nullptr, target, nullptr, D3DTEXF_NONE);
            if (SUCCEEDED(copied))
                copied = device->GetRenderTargetData(target, readback);
            D3DLOCKED_RECT rect = {};
            HRESULT locked = SUCCEEDED(copied) ? readback->LockRect(&rect, nullptr, D3DLOCK_READONLY) : copied;
            ok = ok && SUCCEEDED(locked);
            if (SUCCEEDED(locked))
            {
                ok = ok && *static_cast<const uint32_t *>(rect.pBits) == 0x78123456u;
                readback->UnlockRect();
            }
            if (readback)
                readback->Release();
            if (target)
                target->Release();
            if (source)
                source->Release();
            texture->Release();
        }
        std::printf("%s FROM_FILE texture mode %d: 0x%08lX\n", ok ? "PASS" : "FAIL", mode,
                    static_cast<unsigned long>(hr));
        if (!ok)
            ++failures;
    }
    device->Release();
    d3d->Release();
    FreeLibrary(d3dxModule);
    FreeLibrary(d3dModule);
    return failures ? 1 : 0;
}
