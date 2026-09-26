// XAudio 2.7 ABI, expressed for interop.
//
// Games from the DirectX SDK era were compiled against XAudio 2.7's headers
// and reach the engine either through COM (CoCreateInstance of CLSID_XAudio2,
// then IXAudio2::Initialize) or by linking XAudio2Create. The console only
// ships XAudio 2.9. This header declares the 2.7 COM shapes the shim must
// present; the 2.9 side is the platform's own <xaudio2.h>.
//
// These are ABI facts (vtable order, struct layout, published GUIDs), not
// copied SDK source. Everything the 2.7 and 2.9 versions share — the voice
// interfaces, callbacks, XAUDIO2_BUFFER, effect chains — is taken straight
// from <xaudio2.h> so there is exactly one definition of each.

#pragma once

#include <windows.h>
#include <mmreg.h>
#include <xaudio2.h>   // 2.9: IXAudio2, voices, callbacks, buffers, XAudio2Create

// ------------------------------------------------------------------ GUIDs

// CLSID_XAudio2 / CLSID_XAudio2_Debug and IID_IXAudio2 as published for 2.7.
// 2.9 has no COM class, so these never collide with the platform header.
extern "C" const GUID CLSID_XAudio2_27;
extern "C" const GUID CLSID_XAudio2_27_Debug;
extern "C" const GUID IID_IXAudio2_27;

// --------------------------------------------------------- 2.7-only structs

enum XAUDIO2_DEVICE_ROLE_27
{
    NotDefaultDevice = 0x0,
    DefaultConsoleDevice = 0x1,
    DefaultMultimediaDevice = 0x2,
    DefaultCommunicationsDevice = 0x4,
    DefaultGameDevice = 0x8,
    GlobalDefaultDevice = 0xF,
    InvalidDeviceRole = ~GlobalDefaultDevice,
};

// The 2.7 device record. 2.9 dropped device enumeration entirely; the shim
// synthesises exactly one entry from the default endpoint.
struct XAUDIO2_DEVICE_DETAILS_27
{
    WCHAR DeviceID[256];
    WCHAR DisplayName[256];
    XAUDIO2_DEVICE_ROLE_27 Role;
    WAVEFORMATEXTENSIBLE OutputFormat;
};

// ---------------------------------------------------- the 2.7 IXAudio2 vtable

// The interface order is what a 2.7-compiled game calls through. It differs
// from 2.9 only in the three device/Initialize methods at the front and in
// CreateMasteringVoice taking a device index instead of a device-id string.
struct IXAudio2_27 : public IUnknown
{
    STDMETHOD(GetDeviceCount)(THIS_ UINT32* pCount) PURE;

    STDMETHOD(GetDeviceDetails)(THIS_ UINT32 Index, XAUDIO2_DEVICE_DETAILS_27* pDeviceDetails) PURE;

    STDMETHOD(Initialize)(THIS_ UINT32 Flags, XAUDIO2_PROCESSOR XAudio2Processor) PURE;

    STDMETHOD(RegisterForCallbacks)(THIS_ IXAudio2EngineCallback* pCallback) PURE;

    STDMETHOD_(void, UnregisterForCallbacks)(THIS_ IXAudio2EngineCallback* pCallback) PURE;

    STDMETHOD(CreateSourceVoice)(THIS_ IXAudio2SourceVoice** ppSourceVoice,
        const WAVEFORMATEX* pSourceFormat, UINT32 Flags, float MaxFrequencyRatio,
        IXAudio2VoiceCallback* pCallback, const XAUDIO2_VOICE_SENDS* pSendList,
        const XAUDIO2_EFFECT_CHAIN* pEffectChain) PURE;

    STDMETHOD(CreateSubmixVoice)(THIS_ IXAudio2SubmixVoice** ppSubmixVoice,
        UINT32 InputChannels, UINT32 InputSampleRate, UINT32 Flags, UINT32 ProcessingStage,
        const XAUDIO2_VOICE_SENDS* pSendList, const XAUDIO2_EFFECT_CHAIN* pEffectChain) PURE;

    STDMETHOD(CreateMasteringVoice)(THIS_ IXAudio2MasteringVoice** ppMasteringVoice,
        UINT32 InputChannels, UINT32 InputSampleRate, UINT32 Flags, UINT32 DeviceIndex,
        const XAUDIO2_EFFECT_CHAIN* pEffectChain) PURE;

    STDMETHOD(StartEngine)(THIS) PURE;

    STDMETHOD_(void, StopEngine)(THIS) PURE;

    STDMETHOD(CommitChanges)(THIS_ UINT32 OperationSet) PURE;

    STDMETHOD_(void, GetPerformanceData)(THIS_ XAUDIO2_PERFORMANCE_DATA* pPerfData) PURE;

    STDMETHOD_(void, SetDebugConfiguration)(THIS_ const XAUDIO2_DEBUG_CONFIGURATION* pDebugConfiguration,
        void* pReserved) PURE;
};

// 2.7 default-processor value (2.9 renamed the constant but keeps the meaning).
#ifndef XAUDIO2_27_DEFAULT_PROCESSOR
#define XAUDIO2_27_DEFAULT_PROCESSOR 0x00000001  // Processor1
#endif

// A wrapper around a live 2.9 engine, presented through the 2.7 vtable. Passing
// a null 2.9 engine keeps the wrapper uninitialised (the COM path creates the
// real engine on Initialize); the tests pass a mock engine here.
IXAudio2_27* XAudio27_Wrap(IXAudio2* real29OrNull);
