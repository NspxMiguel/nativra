// X3DAudio1_7.dll — the 2.7 positional-audio surface over the platform's own
// X3DAudio. The maths is identical across versions; only X3DAudioInitialize's
// return type changed (void in 1.7, HRESULT in 2.9), so it is adapted here and
// X3DAudioCalculate is a straight forward.
#include <windows.h>
#include <x3daudio.h>

extern "C" __declspec(dllexport)
void WINAPI X3DAudioInitialize_17(UINT32 speakerChannelMask, FLOAT32 speedOfSound, X3DAUDIO_HANDLE instance)
{
    // 2.9 returns an HRESULT; a 1.7 caller expects none. Ignore it — a bad
    // channel mask simply yields an instance that calculates to silence.
    (void)X3DAudioInitialize(speakerChannelMask, speedOfSound, instance);
}

extern "C" __declspec(dllexport)
void WINAPI X3DAudioCalculate_17(const X3DAUDIO_HANDLE instance, const X3DAUDIO_LISTENER* listener,
    const X3DAUDIO_EMITTER* emitter, UINT32 flags, X3DAUDIO_DSP_SETTINGS* settings)
{
    X3DAudioCalculate(instance, listener, emitter, flags, settings);
}

BOOL WINAPI DllMain(HINSTANCE, DWORD, LPVOID) { return TRUE; }
