# LEGO Jurassic World — the null resource before the first frame

Steam 352400, `LEGOJurassicWorld_DX11.exe` (x64, 40,576,824 bytes, depot download
2026-09-27). On the console (build 305) the game starts, creates its window, D3D11
device and swap chain, then faults reading through a null pointer at `+0x3795DB`.

## What the fault is (static analysis, Capstone)

`+0x379557` fills an engine texture descriptor on the stack and asks the engine's
resource factory for it:

| field | value |
|---|---|
| `+0x00` engine format | `0x70` |
| `+0x04` width | `r12d` (same as the texture created just before) |
| `+0x08` height | `r13d` (same) |
| `+0x0C` | 0 |
| `+0x10` mip levels | 1 |
| `+0x18` flags | `0x101` (bit 0 = depth target) |

`call 0x14033EC90(factory, &out, &desc)` then stores `out` in `[rsi]` and, at
`+0x3795DB`, reads `[rsi]->+0x50` without a null check. The fault means the
factory returned null.

`0x14033EC90` validates first: width 0 returns a shared default object; height 0,
mip levels 0, a negative `+0x0C`, format 0 or ≥ `0x79`, or (with flag bit 0) a
format below `0x64` all take the failure path (`0x14033F09C`, null out). Format
`0x70` with flag bit 0 passes these checks, and the texture created just before
(`+0x379446`, format `0x75` or `r15d`, same size) does not fault, so the size is
not zero. The failure is further in: the creation path at `0x14033F600`.

## Why D3D11 is not the suspect yet

GraphicsBridge logs every failed `CreateTexture2D` and `CreateDepthStencilView`
with its HRESULT, and the build 305 run logged none. The engine therefore gives up
before asking D3D11, most likely in its format translation or a capability check
(`ID3D11Device::CheckFormatSupport`, vtable slot 29, is forwarded unlogged).

## Next console run

Log `CheckFormatSupport` / `CheckMultisampleQualityLevels` answers in
GraphicsBridge, run LEGO (`APPID=352400`), and read which DXGI format the engine
asks about just before the fault. Then disassemble `0x14033F600` down to the
failing branch.
