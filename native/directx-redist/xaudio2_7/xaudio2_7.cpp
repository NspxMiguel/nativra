// xaudio2_7.dll — the XAudio 2.7 surface, forwarding to XAudio 2.9.
//
// A 2.7 game gets a 2.7-vtable IXAudio2 whose device methods are synthesised
// (2.9 dropped device enumeration; the shim reports the one default endpoint)
// and whose voice creation is forwarded to the platform engine. The voice,
// callback, buffer and effect-chain shapes are identical between 2.7 and 2.9,
// so voices are handed back as-is: a 2.7 caller only ever reaches the methods
// that sit at the same vtable slots in 2.9 (2.9 only appended new ones).

#include "xaudio2_7_abi.h"
#include <new>

// Published 2.7 identifiers.
extern "C" const GUID CLSID_XAudio2_27 =
    { 0x5a508685, 0xa254, 0x4fba, { 0x9b, 0x82, 0x9a, 0x24, 0xb0, 0x03, 0x06, 0xaf } };
extern "C" const GUID CLSID_XAudio2_27_Debug =
    { 0xdb05ea35, 0x0329, 0x4d4b, { 0xa5, 0x3a, 0x6d, 0xea, 0xd0, 0x3d, 0x38, 0x52 } };
extern "C" const GUID IID_IXAudio2_27 =
    { 0x8bcf1f58, 0x9fe7, 0x4583, { 0x8a, 0xc6, 0xe2, 0xad, 0xc4, 0x65, 0xc8, 0xbb } };

namespace
{
    // Turns a 2.7 device index into the 2.9 device-id string. Index 0 (and the
    // 2.7 "default" of 0) is the default endpoint, which 2.9 names with a null
    // string; any other index would need a real enumeration the console does
    // not expose, so it also falls back to the default rather than failing.
    inline LPCWSTR DeviceIdFor(UINT32 index) { (void)index; return nullptr; }

    class Wrapper : public IXAudio2_27
    {
    public:
        explicit Wrapper(IXAudio2* real) : ref_(1), real_(real)
        {
            if (real_) real_->AddRef();
        }

        // ------------------------------------------------------------ IUnknown

        STDMETHOD(QueryInterface)(REFIID riid, void** ppv) override
        {
            if (!ppv) return E_POINTER;
            if (riid == IID_IXAudio2_27 || riid == IID_IUnknown)
            {
                *ppv = static_cast<IXAudio2_27*>(this);
                AddRef();
                return S_OK;
            }
            *ppv = nullptr;
            return E_NOINTERFACE;
        }

        STDMETHOD_(ULONG, AddRef)() override { return (ULONG)InterlockedIncrement(&ref_); }

        STDMETHOD_(ULONG, Release)() override
        {
            LONG n = InterlockedDecrement(&ref_);
            if (n == 0) delete this;
            return (ULONG)n;
        }

        // -------------------------------------------------- device enumeration

        STDMETHOD(GetDeviceCount)(UINT32* pCount) override
        {
            if (!pCount) return E_POINTER;
            *pCount = 1;                       // the default endpoint
            return S_OK;
        }

        STDMETHOD(GetDeviceDetails)(UINT32 index, XAUDIO2_DEVICE_DETAILS_27* details) override
        {
            if (!details) return E_POINTER;
            if (index != 0) return XAUDIO2_E_INVALID_CALL;
            ZeroMemory(details, sizeof(*details));
            wcscpy_s(details->DeviceID, L"nativra-default");
            wcscpy_s(details->DisplayName, L"Nativra default output");
            details->Role = GlobalDefaultDevice;

            // A 5.1, 48 kHz, 16-bit template: enough for a 2.7 game to size its
            // mastering voice. The real 2.9 mastering voice adopts the endpoint's
            // own mix format regardless of what is reported here.
            WAVEFORMATEXTENSIBLE& f = details->OutputFormat;
            f.Format.wFormatTag = WAVE_FORMAT_EXTENSIBLE;
            f.Format.nChannels = 6;
            f.Format.nSamplesPerSec = 48000;
            f.Format.wBitsPerSample = 16;
            f.Format.nBlockAlign = (WORD)(f.Format.nChannels * f.Format.wBitsPerSample / 8);
            f.Format.nAvgBytesPerSec = f.Format.nSamplesPerSec * f.Format.nBlockAlign;
            f.Format.cbSize = sizeof(WAVEFORMATEXTENSIBLE) - sizeof(WAVEFORMATEX);
            f.Samples.wValidBitsPerSample = 16;
            f.dwChannelMask = SPEAKER_FRONT_LEFT | SPEAKER_FRONT_RIGHT | SPEAKER_FRONT_CENTER |
                              SPEAKER_LOW_FREQUENCY | SPEAKER_BACK_LEFT | SPEAKER_BACK_RIGHT;
            f.SubFormat = KSDATAFORMAT_SUBTYPE_PCM;
            return S_OK;
        }

        STDMETHOD(Initialize)(UINT32 flags, XAUDIO2_PROCESSOR processor) override
        {
            // 2.7 split construction (CoCreateInstance) from Initialize; 2.9 does
            // both in XAudio2Create. If the engine was not injected, make it now.
            if (!real_)
            {
                HRESULT hr = XAudio2Create(&real_, flags,
                    processor ? processor : XAUDIO2_27_DEFAULT_PROCESSOR);
                if (FAILED(hr)) return hr;
            }
            return S_OK;
        }

        // ---------------------------------------------------------- callbacks

        STDMETHOD(RegisterForCallbacks)(IXAudio2EngineCallback* cb) override
        {
            return real_ ? real_->RegisterForCallbacks(cb) : XAUDIO2_E_INVALID_CALL;
        }

        STDMETHOD_(void, UnregisterForCallbacks)(IXAudio2EngineCallback* cb) override
        {
            if (real_) real_->UnregisterForCallbacks(cb);
        }

        // ------------------------------------------------------------- voices

        STDMETHOD(CreateSourceVoice)(IXAudio2SourceVoice** ppVoice, const WAVEFORMATEX* fmt,
            UINT32 flags, float maxFreqRatio, IXAudio2VoiceCallback* cb,
            const XAUDIO2_VOICE_SENDS* sends, const XAUDIO2_EFFECT_CHAIN* fx) override
        {
            if (!real_) return XAUDIO2_E_INVALID_CALL;
            return real_->CreateSourceVoice(ppVoice, fmt, flags, maxFreqRatio, cb, sends, fx);
        }

        STDMETHOD(CreateSubmixVoice)(IXAudio2SubmixVoice** ppVoice, UINT32 channels,
            UINT32 sampleRate, UINT32 flags, UINT32 stage,
            const XAUDIO2_VOICE_SENDS* sends, const XAUDIO2_EFFECT_CHAIN* fx) override
        {
            if (!real_) return XAUDIO2_E_INVALID_CALL;
            return real_->CreateSubmixVoice(ppVoice, channels, sampleRate, flags, stage, sends, fx);
        }

        STDMETHOD(CreateMasteringVoice)(IXAudio2MasteringVoice** ppVoice, UINT32 channels,
            UINT32 sampleRate, UINT32 flags, UINT32 deviceIndex,
            const XAUDIO2_EFFECT_CHAIN* fx) override
        {
            if (!real_) return XAUDIO2_E_INVALID_CALL;
            // The device index becomes a 2.9 device-id string; the stream
            // category is the game default.
            return real_->CreateMasteringVoice(ppVoice, channels, sampleRate, flags,
                DeviceIdFor(deviceIndex), fx, AudioCategory_GameEffects);
        }

        // --------------------------------------------------------- engine ops

        STDMETHOD(StartEngine)() override
        {
            return real_ ? real_->StartEngine() : XAUDIO2_E_INVALID_CALL;
        }

        STDMETHOD_(void, StopEngine)() override { if (real_) real_->StopEngine(); }

        STDMETHOD(CommitChanges)(UINT32 op) override
        {
            return real_ ? real_->CommitChanges(op) : XAUDIO2_E_INVALID_CALL;
        }

        STDMETHOD_(void, GetPerformanceData)(XAUDIO2_PERFORMANCE_DATA* perf) override
        {
            if (real_) real_->GetPerformanceData(perf);
            else if (perf) ZeroMemory(perf, sizeof(*perf));
        }

        STDMETHOD_(void, SetDebugConfiguration)(const XAUDIO2_DEBUG_CONFIGURATION* cfg,
            void* reserved) override
        {
            if (real_) real_->SetDebugConfiguration(cfg, reserved);
        }

    private:
        ~Wrapper() { if (real_) real_->Release(); }

        LONG ref_;
        IXAudio2* real_;
    };
}

IXAudio2_27* XAudio27_Wrap(IXAudio2* real29OrNull)
{
    return new (std::nothrow) Wrapper(real29OrNull);
}

// ------------------------------------------------------- COM class factory

namespace
{
    class Factory : public IClassFactory
    {
    public:
        Factory() : ref_(1) {}

        STDMETHOD(QueryInterface)(REFIID riid, void** ppv) override
        {
            if (!ppv) return E_POINTER;
            if (riid == IID_IUnknown || riid == IID_IClassFactory)
            {
                *ppv = static_cast<IClassFactory*>(this);
                AddRef();
                return S_OK;
            }
            *ppv = nullptr;
            return E_NOINTERFACE;
        }
        STDMETHOD_(ULONG, AddRef)() override { return (ULONG)InterlockedIncrement(&ref_); }
        STDMETHOD_(ULONG, Release)() override
        {
            LONG n = InterlockedDecrement(&ref_);
            if (n == 0) delete this;
            return (ULONG)n;
        }

        STDMETHOD(CreateInstance)(IUnknown* outer, REFIID riid, void** ppv) override
        {
            if (outer) return CLASS_E_NOAGGREGATION;
            if (!ppv) return E_POINTER;
            *ppv = nullptr;
            // 2.7 semantics: hand back an uninitialised engine; the game calls
            // Initialize next, which creates the real 2.9 engine.
            IXAudio2_27* wrapper = XAudio27_Wrap(nullptr);
            if (!wrapper) return E_OUTOFMEMORY;
            HRESULT hr = wrapper->QueryInterface(riid, ppv);
            wrapper->Release();
            return hr;
        }

        STDMETHOD(LockServer)(BOOL) override { return S_OK; }

    private:
        LONG ref_;
    };
}

STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, void** ppv)
{
    if (!ppv) return E_POINTER;
    *ppv = nullptr;
    if (rclsid == CLSID_XAudio2_27 || rclsid == CLSID_XAudio2_27_Debug)
    {
        Factory* factory = new (std::nothrow) Factory();
        if (!factory) return E_OUTOFMEMORY;
        HRESULT hr = factory->QueryInterface(riid, ppv);
        factory->Release();
        return hr;
    }
    return CLASS_E_CLASSNOTAVAILABLE;
}

STDAPI DllCanUnloadNow() { return S_FALSE; }

BOOL WINAPI DllMain(HINSTANCE, DWORD, LPVOID) { return TRUE; }
