# DirectX guest shims

`build-x86.cmd` builds the 32-bit DLLs with the static MSVC runtime and checks every plain-name export listed in each `.def` file with `dumpbin`. The 32-bit test project can exercise built DLLs only on Windows CI; the macOS checkout cannot run them.

The `d3dx9_43` shim now supports D3DX9 math plus 2D texture creation and image information from PNG, JPEG, BMP, TGA, GIF, and DDS. DDS decoding accepts DXT1, DXT3, DXT5, A8R8G8B8, and X8R8G8B8; only the first DDS mip is decoded, and generated mips use a box filter. Texture output accepts A8R8G8B8 or X8R8G8B8. BMP, TGA, and PNG texture saving accepts 32-bit textures. Shader compilation uses the host-served `nativra_host.dll!HostD3DCompile` bridge for 32-bit guests and falls back to `d3dcompiler_43.dll` when the bridge is absent or an include handler is supplied. The bridge uses guest buffers and has no guest-visible COM objects; shader constant tables are not created. The render-to-surface COM object supports one active scene and restores the prior target, depth surface, and viewport. Cube texture creation reports `D3DERR_NOTAVAILABLE`.

The texture loader currently uses nearest-neighbor scaling for the base level regardless of the D3DX filter flags. Color-key matching is against RGB, and only decoded 2D images are supported. These compatibility limits should be checked against affected games on Windows CI.
