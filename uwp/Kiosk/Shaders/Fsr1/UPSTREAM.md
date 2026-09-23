# AMD FidelityFX Super Resolution 1

`ffx_a.h`, `ffx_fsr1.h` and `LICENSE.txt` come from
[GPUOpen-Effects/FidelityFX-FSR](https://github.com/GPUOpen-Effects/FidelityFX-FSR),
pinned to commit `a21ffb8f6c13233ba336352bdff293894c706575`.
The upstream headers retain their copyright and license notices. The MIT license
for these files is independent of Nativra's own noncommercial license.

`Upscale.hlsl` is Nativra's wrapper, targeting D3D11 Shader Model 5 with FP32
EASU and RCAS passes. No machine-learning reconstruction, temporal FSR or frame
generation is implied. Output is opaque SDR color; HDR is outside this prototype.

Status: shader compilation/package preparation only. The runtime GPU pass,
resource/state restoration, resolution controls and Xbox image/performance
validation are not implemented yet. No user-facing FSR toggle is enabled.

The current CPU-readback presentation path makes output resolution a significant
cost. Before enabling FSR, compare native rendering against reduced-resolution
rendering plus the real upscaler, including readback cost, text/HUD quality,
frame pacing and input latency. Do not claim increased performance from shader
compilation alone. Temporal FSR needs engine depth/motion/jitter inputs that the
generic final-color bridge does not currently provide.
