// XAPOFX1_5.dll — the 2.7 built-in audio effects (reverb, EQ, echo, mastering
// limiter) over the platform's XAPOFX. CreateFX gained two parameters in 2.9;
// a 1.5 caller passes neither, so they are forwarded as null/zero.
#include <windows.h>
#include <xapofx.h>

extern "C" __declspec(dllexport)
HRESULT WINAPI CreateFX_15(REFCLSID clsid, IUnknown** pEffect)
{
    return CreateFX(clsid, pEffect, nullptr, 0);
}

BOOL WINAPI DllMain(HINSTANCE, DWORD, LPVOID) { return TRUE; }
