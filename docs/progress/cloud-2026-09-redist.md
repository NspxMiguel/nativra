# Cloud work log — 2026-09 — DirectX redistributable

Cloud session (no console access). The console carries no DirectX June 2010
redistributable, so games that reach for its DLLs stop at a missing module.
The work here replaces the parts games actually use by forwarding to the
platform's own newer libraries, delivered as C++ DLLs built and tested on the
GitHub Actions Windows runner. This file records what was built and exactly
what to measure on the Xbox, since the runner has no audio device and is not
the console.

All of this lives under `native/directx-redist/` and builds through its own
workflow (`.github/workflows/directx-redist.yml`); it never touches the Xbox
package build.

Two goals, worked in order:

1. **XAudio 2.7 on XAudio 2.9** — so DirectX-SDK-era games (first target: Little
   Nightmares, UE4) reach the console's audio engine.
2. **Shader compiler and D3DX** — so games that compile HLSL at runtime
   (d3dcompiler_43/47) or load textures and maths through d3dx9/10/11 keep
   working.

---

## Goal A — XAudio 2.7 → 2.9

### Done

**`xaudio2_7.dll` (`native/directx-redist/xaudio2_7`).** Presents the XAudio 2.7
COM surface and forwards to the platform's XAudio 2.9.

- `xaudio2_7_abi.h` declares the 2.7 shapes that differ from 2.9: the
  `IXAudio2` vtable (device methods and `Initialize` at the front,
  `CreateMasteringVoice` taking a device index), the published CLSIDs/IID, and
  `XAUDIO2_DEVICE_DETAILS`. Everything 2.7 and 2.9 share — voices, callbacks,
  `XAUDIO2_BUFFER`, effect chains — comes from the platform `<xaudio2.h>`, so
  there is one definition of each and voices are handed back unwrapped (a 2.7
  caller only reaches methods 2.9 keeps at the same vtable slots).
- The wrapper synthesises the one default device for `GetDeviceCount` /
  `GetDeviceDetails`, creates the real 2.9 engine lazily on `Initialize` (the
  2.7 CoCreateInstance-then-Initialize split), and translates
  `CreateMasteringVoice`'s device index to a 2.9 null device id plus the game
  stream category. A COM class factory (`DllGetClassObject`) answers the 2.7
  CLSIDs so `CoCreateInstance` works.

**`x3daudio1_7.dll`, `xapofx1_5.dll`.** Thin forwarders to the platform's
X3DAudio and XAPOFX: `X3DAudioInitialize` (adapting the void-vs-HRESULT return),
`X3DAudioCalculate`, and `CreateFX` (forwarding the two parameters 2.9 added).

**Tests (`tests/test_xaudio2_7.cpp`).** No audio hardware needed — a mock 2.9
engine stands in and also lets the test watch every forwarded call:
- device-record field offsets;
- each 2.7 `IXAudio2` method reaches the matching 2.9 method (which pins the
  vtable order — a wrong slot would call the wrong mock method);
- `CreateMasteringVoice` device-index → null device id + game category;
- the game's voice callback is forwarded intact and fires in
  submit → start → end → stream-end order.

### To measure on the console

- Does `CoCreateInstance(CLSID_XAudio2 [2.7], IID_IXAudio2 [2.7])` followed by
  `Initialize` succeed once `xaudio2_7.dll` is mapped, and does a mastering
  voice reach the console speakers? (Human listen check; there is no recording.)
- Little Nightmares: does startup get past XAudio 2.7 creation, and is menu
  audio audible?
- Confirm `xaudio2_9.dll` (and the X3DAudio/XAPOFX exports it carries) are
  present on the console image; if a symbol is missing, note which.

### Loader wiring (one line, pending)

Games reach these DLLs two ways. Direct imports of `xaudio2_7.dll` /
`x3daudio1_7.dll` / `xapofx1_5.dll` resolve once the DLLs ship in the package
and the loader's `LoadPackagedLibrary` finds them. COM activation
(`CoCreateInstance` of the 2.7 CLSID) needs one hook so it routes to
`DllGetClassObject`; see the note next to the shim for the single registration
line to add in the loader.

---

## Goal B — shader compiler and D3DX

### Done

**`d3dcompiler_43.dll` (`native/directx-redist/d3dcompiler_43`).** A pure
export-forwarder DLL: the whole D3DCompile family (`D3DCompile`, `D3DCompile2`,
`D3DPreprocess`, `D3DReflect`, `D3DGetBlobPart`, `D3DCreateBlob`,
`D3DDisassemble`, `D3DStripShader`, the signature-blob getters, …) forwards to
the platform's `d3dcompiler_47.dll`, which is a Windows 10 system DLL. The API
is source-compatible across those versions, so a game that links the SDK-era
`d3dcompiler_43` keeps working with no code in between.

**Tests (`tests/test_d3dcompiler.cpp`).** Compiles a representative set of
shaders (VS/PS in SM4 and SM5, a compute shader) through the forwarder, and:
- checks each result is a valid `DXBC` container;
- checks it is **byte-for-byte identical** to what the real compiler produces
  directly (the forwarder reaches the same function);
- exercises the container helpers a game uses — `D3DGetBlobPart` pulls the
  input signature out of the vertex shaders, and `D3DReflect` returns a shader
  description.

**`d3dx9_43.dll` maths (`native/directx-redist/d3dx9_43`).** D3DX has no
forwarding target, so this is a real implementation of the maths games import
most: matrix multiply/transpose/inverse/determinant, translation/scaling and
every rotation form (X/Y/Z, axis, quaternion, yaw-pitch-roll), LookAt and
perspective/ortho projections, vector transforms and normalisation, and
quaternion rotation/multiply/normalize/slerp. Row-major, row-vector convention,
exported with clean x64 names. Its tests (`tests/test_d3dx9.cpp`) check
mathematical identities and hand-computed references, and were validated
locally under Wine as well as in the workflow.

**The probe (`uwp/Kiosk/Native/RedistProbe.cs`, new file).** On game load it
records, to `redist-probe.txt`, whether the console can load each redist
library on its own — `d3dcompiler_47` first, then the 2.9 audio engine and the
older redistributable names — through `LoadPackagedLibrary` and the already-
loaded-module check. Wired by a single line at the end of `LoaderStubs.Install`.
(The CI runner, Windows Server 2025, does carry `d3dcompiler_47.dll`: the
d3dcompiler test links and loads it. The console is expected to as well; this
confirms it there.)

### To do

- Wire XAudio 2.7 COM activation: one line in `ComStubs`' `CoCreateInstance`
  handler, next to the existing `AudioBridge.ClassFor` check, to route the 2.7
  CLSIDs to `xaudio2_7.dll`'s `DllGetClassObject`. Direct DLL-name imports of
  the shims need no code once the DLLs ship in the package (`LoadPackagedLibrary`
  finds them); they are added to `Kiosk.csproj` as `Content` alongside the
  TlsCarrier DLLs.
- D3DX texture loading (`D3DX11CreateTextureFromMemory` and friends:
  DDS/PNG/JPG/TGA/BMP) — needs a D3D11 device, so it is testable on the runner
  with WARP; deferred.
- A 32-bit (x86) build of the shims, once the x86 loader lands, for the many
  D3DX9-era games that are 32-bit.

### To measure on the console

- Does the console load `d3dcompiler_47.dll` by itself? The probe answers this;
  if yes, `d3dcompiler_43` forwards to it and Hades' runtime HLSL compilation
  works. If no, the redistributable `d3dcompiler_47.dll` must ship in the
  package (note which, from the probe).
- Hades: does startup get past the D3DCOMPILER_47 import and reach a frame?
