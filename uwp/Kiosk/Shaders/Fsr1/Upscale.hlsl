// Nativra's D3D11/SM5 wrapper around AMD's MIT-licensed FSR 1 implementation.
// SDR color only. EASU reconstruction and RCAS sharpening are separate passes.
#define A_GPU 1
#define A_HLSL 1
#include "ffx_a.h"

cbuffer UpscaleSettings : register(b0) {
  float2 InputSize;
  float2 OutputSize;
  float SharpnessStops;
  float3 Padding;
};

Texture2D<float4> InputColor : register(t0);
SamplerState LinearClamp : register(s0);
RWTexture2D<float4> OutputColor : register(u0);

#if PASS_EASU
#define FSR_EASU_F 1
AF4 FsrEasuRF(AF2 p) { return InputColor.GatherRed(LinearClamp, p); }
AF4 FsrEasuGF(AF2 p) { return InputColor.GatherGreen(LinearClamp, p); }
AF4 FsrEasuBF(AF2 p) { return InputColor.GatherBlue(LinearClamp, p); }
#else
#define FSR_RCAS_F 1
AF4 FsrRcasLoadF(ASU2 p) {
  return InputColor.Load(int3(clamp(p, int2(0, 0), int2(InputSize) - 1), 0));
}
void FsrRcasInputF(inout AF1 r, inout AF1 g, inout AF1 b){}
#endif

#include "ffx_fsr1.h"

[numthreads(8, 8, 1)] void Main(uint3 position : SV_DispatchThreadID) {
  if (any(position.xy >= uint2(OutputSize)))
    return;
  float3 color;
#if PASS_EASU
  uint4 con0, con1, con2, con3;
  FsrEasuCon(con0, con1, con2, con3, InputSize.x, InputSize.y, InputSize.x,
             InputSize.y, OutputSize.x, OutputSize.y);
  FsrEasuF(color, position.xy, con0, con1, con2, con3);
#else
  uint4 con;
  FsrRcasCon(con, SharpnessStops);
  FsrRcasF(color.r, color.g, color.b, position.xy, con);
#endif
  OutputColor[position.xy] = float4(color, 1.0);
}
