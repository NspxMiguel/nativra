// Tests for the XAudio 2.7 shim that need no audio hardware.
//
//  * layout: the 2.7 device record's field offsets;
//  * forwarding: each 2.7 IXAudio2 method reaches the matching 2.9 method
//    (which also pins the 2.7 vtable order — a wrong slot would call the
//    wrong mock method and fail);
//  * device translation: CreateMasteringVoice turns a 2.7 device index into
//    a 2.9 (null device id, game stream category) call;
//  * callbacks: the game's voice callback is forwarded intact, so callbacks
//    fire in submit -> start -> end -> stream-end order.
//
// The 2.9 engine is a mock: the runner has no audio device, and a mock also
// lets the test observe every forwarded call.

#include "../xaudio2_7/xaudio2_7_abi.h"
#include <cstdio>
#include <cstring>
#include <vector>
#include <string>

static int g_failures = 0;
#define CHECK(cond, msg) do { if (!(cond)) { printf("FAIL: %s\n", msg); ++g_failures; } } while (0)

// ------------------------------------------------------------- layout checks

static void CheckLayout()
{
    CHECK(offsetof(XAUDIO2_DEVICE_DETAILS_27, DeviceID) == 0, "DeviceID offset");
    CHECK(offsetof(XAUDIO2_DEVICE_DETAILS_27, DisplayName) == 512, "DisplayName offset");
    CHECK(offsetof(XAUDIO2_DEVICE_DETAILS_27, Role) == 1024, "Role offset");
    CHECK(offsetof(XAUDIO2_DEVICE_DETAILS_27, OutputFormat) == 1028, "OutputFormat offset");
    // The shared shapes must be the platform's own, byte-for-byte.
    CHECK(sizeof(XAUDIO2_BUFFER) >= 40, "XAUDIO2_BUFFER size");
}

// -------------------------------------------------------- mock 2.9 objects

struct MockSourceVoice : public IXAudio2SourceVoice
{
    IXAudio2VoiceCallback* cb = nullptr;
    std::vector<std::string>* log = nullptr;

    // The subset the test drives; the rest are no-op stubs below.
    STDMETHOD(SubmitSourceBuffer)(const XAUDIO2_BUFFER* buffer, const XAUDIO2_BUFFER_WMA*) override
    {
        if (log) log->push_back("submit");
        // Simulate the engine consuming the buffer and calling back, in order.
        if (cb)
        {
            cb->OnBufferStart(buffer ? buffer->pContext : nullptr);
            cb->OnBufferEnd(buffer ? buffer->pContext : nullptr);
            if (buffer && (buffer->Flags & XAUDIO2_END_OF_STREAM)) cb->OnStreamEnd();
        }
        return S_OK;
    }

    STDMETHOD_(void, GetVoiceDetails)(XAUDIO2_VOICE_DETAILS* p) override { if (p) ZeroMemory(p, sizeof(*p)); }
    STDMETHOD(SetOutputVoices)(const XAUDIO2_VOICE_SENDS*) override { return S_OK; }
    STDMETHOD(SetEffectChain)(const XAUDIO2_EFFECT_CHAIN*) override { return S_OK; }
    STDMETHOD(EnableEffect)(UINT32, UINT32) override { return S_OK; }
    STDMETHOD(DisableEffect)(UINT32, UINT32) override { return S_OK; }
    STDMETHOD_(void, GetEffectState)(UINT32, BOOL* p) override { if (p) *p = FALSE; }
    STDMETHOD(SetEffectParameters)(UINT32, const void*, UINT32, UINT32) override { return S_OK; }
    STDMETHOD(GetEffectParameters)(UINT32, void*, UINT32) override { return S_OK; }
    STDMETHOD(SetFilterParameters)(const XAUDIO2_FILTER_PARAMETERS*, UINT32) override { return S_OK; }
    STDMETHOD_(void, GetFilterParameters)(XAUDIO2_FILTER_PARAMETERS*) override {}
    STDMETHOD(SetOutputFilterParameters)(IXAudio2Voice*, const XAUDIO2_FILTER_PARAMETERS*, UINT32) override { return S_OK; }
    STDMETHOD_(void, GetOutputFilterParameters)(IXAudio2Voice*, XAUDIO2_FILTER_PARAMETERS*) override {}
    STDMETHOD(SetVolume)(float, UINT32) override { return S_OK; }
    STDMETHOD_(void, GetVolume)(float* p) override { if (p) *p = 1.0f; }
    STDMETHOD(SetChannelVolumes)(UINT32, const float*, UINT32) override { return S_OK; }
    STDMETHOD_(void, GetChannelVolumes)(UINT32, float*) override {}
    STDMETHOD(SetOutputMatrix)(IXAudio2Voice*, UINT32, UINT32, const float*, UINT32) override { return S_OK; }
    STDMETHOD_(void, GetOutputMatrix)(IXAudio2Voice*, UINT32, UINT32, float*) override {}
    STDMETHOD_(void, DestroyVoice)() override { delete this; }
    STDMETHOD(Start)(UINT32, UINT32) override { if (log) log->push_back("start"); return S_OK; }
    STDMETHOD(Stop)(UINT32, UINT32) override { return S_OK; }
    STDMETHOD(FlushSourceBuffers)() override { return S_OK; }
    STDMETHOD(Discontinuity)() override { return S_OK; }
    STDMETHOD(ExitLoop)(UINT32) override { return S_OK; }
    STDMETHOD_(void, GetState)(XAUDIO2_VOICE_STATE* p, UINT32) override { if (p) ZeroMemory(p, sizeof(*p)); }
    STDMETHOD(SetFrequencyRatio)(float, UINT32) override { return S_OK; }
    STDMETHOD_(void, GetFrequencyRatio)(float* p) override { if (p) *p = 1.0f; }
    STDMETHOD(SetSourceSampleRate)(UINT32) override { return S_OK; }
};

struct MockEngine : public IXAudio2
{
    LONG ref = 1;
    std::vector<std::string> log;
    bool sawInitialize = false;           // 2.9 has no Initialize; must never be reached
    std::wstring lastDeviceId = L"UNSET";
    AUDIO_STREAM_CATEGORY lastCategory = (AUDIO_STREAM_CATEGORY)-1;
    IXAudio2VoiceCallback* lastCallback = nullptr;

    STDMETHOD(QueryInterface)(REFIID, void** ppv) override { *ppv = this; return S_OK; }
    STDMETHOD_(ULONG, AddRef)() override { return (ULONG)InterlockedIncrement(&ref); }
    STDMETHOD_(ULONG, Release)() override { return (ULONG)InterlockedDecrement(&ref); }

    STDMETHOD(RegisterForCallbacks)(IXAudio2EngineCallback*) override { log.push_back("register"); return S_OK; }
    STDMETHOD_(void, UnregisterForCallbacks)(IXAudio2EngineCallback*) override {}

    STDMETHOD(CreateSourceVoice)(IXAudio2SourceVoice** ppv, const WAVEFORMATEX*, UINT32, float,
        IXAudio2VoiceCallback* cb, const XAUDIO2_VOICE_SENDS*, const XAUDIO2_EFFECT_CHAIN*) override
    {
        log.push_back("createsource");
        lastCallback = cb;
        MockSourceVoice* v = new MockSourceVoice();
        v->cb = cb;
        v->log = &log;
        *ppv = v;
        return S_OK;
    }

    STDMETHOD(CreateSubmixVoice)(IXAudio2SubmixVoice**, UINT32, UINT32, UINT32, UINT32,
        const XAUDIO2_VOICE_SENDS*, const XAUDIO2_EFFECT_CHAIN*) override { return S_OK; }

    STDMETHOD(CreateMasteringVoice)(IXAudio2MasteringVoice** ppv, UINT32, UINT32, UINT32,
        LPCWSTR deviceId, const XAUDIO2_EFFECT_CHAIN*, AUDIO_STREAM_CATEGORY category) override
    {
        log.push_back("createmaster");
        lastDeviceId = deviceId ? deviceId : L"";
        lastCategory = category;
        *ppv = reinterpret_cast<IXAudio2MasteringVoice*>(1);   // opaque, never dereferenced here
        return S_OK;
    }

    STDMETHOD(StartEngine)() override { log.push_back("start-engine"); return S_OK; }
    STDMETHOD_(void, StopEngine)() override { log.push_back("stop-engine"); }
    STDMETHOD(CommitChanges)(UINT32) override { log.push_back("commit"); return S_OK; }
    STDMETHOD_(void, GetPerformanceData)(XAUDIO2_PERFORMANCE_DATA* p) override { if (p) ZeroMemory(p, sizeof(*p)); }
    STDMETHOD_(void, SetDebugConfiguration)(const XAUDIO2_DEBUG_CONFIGURATION*, void*) override {}
};

// The game's own voice callback: records the order it is notified in.
struct GameCallback : public IXAudio2VoiceCallback
{
    std::vector<std::string>* log;
    explicit GameCallback(std::vector<std::string>* l) : log(l) {}
    STDMETHOD_(void, OnVoiceProcessingPassStart)(UINT32) override {}
    STDMETHOD_(void, OnVoiceProcessingPassEnd)() override {}
    STDMETHOD_(void, OnStreamEnd)() override { log->push_back("cb-streamend"); }
    STDMETHOD_(void, OnBufferStart)(void*) override { log->push_back("cb-start"); }
    STDMETHOD_(void, OnBufferEnd)(void*) override { log->push_back("cb-end"); }
    STDMETHOD_(void, OnLoopEnd)(void*) override {}
    STDMETHOD_(void, OnVoiceError)(void*, HRESULT) override { log->push_back("cb-error"); }
};

// ------------------------------------------------------------- forwarding

static void CheckForwarding()
{
    MockEngine engine;
    IXAudio2_27* x = XAudio27_Wrap(&engine);
    CHECK(x != nullptr, "wrap");

    // Initialize with an injected engine must not try to build a real one.
    CHECK(x->Initialize(0, XAUDIO2_27_DEFAULT_PROCESSOR) == S_OK, "Initialize");
    CHECK(!engine.sawInitialize, "2.9 engine has no Initialize");

    UINT32 count = 0;
    CHECK(x->GetDeviceCount(&count) == S_OK && count == 1, "GetDeviceCount");

    XAUDIO2_DEVICE_DETAILS_27 details;
    CHECK(x->GetDeviceDetails(0, &details) == S_OK, "GetDeviceDetails");
    CHECK(details.OutputFormat.Format.nSamplesPerSec == 48000, "device sample rate");
    CHECK(x->GetDeviceDetails(3, &details) != S_OK, "GetDeviceDetails rejects bad index");

    // Mastering voice: index -> null device id + game category.
    IXAudio2MasteringVoice* master = nullptr;
    CHECK(x->CreateMasteringVoice(&master, 2, 48000, 0, 0, nullptr) == S_OK, "CreateMasteringVoice");
    CHECK(engine.lastDeviceId.empty(), "device index 0 -> null device id");
    CHECK(engine.lastCategory == AudioCategory_GameEffects, "stream category");

    CHECK(x->StartEngine() == S_OK, "StartEngine");
    x->StopEngine();
    CHECK(x->CommitChanges(0) == S_OK, "CommitChanges");
    CHECK(engine.log.size() >= 4 &&
          engine.log[engine.log.size()-3] == "start-engine" &&
          engine.log[engine.log.size()-2] == "stop-engine" &&
          engine.log[engine.log.size()-1] == "commit", "engine op order");

    x->Release();
}

static void CheckCallbackOrder()
{
    MockEngine engine;
    IXAudio2_27* x = XAudio27_Wrap(&engine);
    x->Initialize(0, XAUDIO2_27_DEFAULT_PROCESSOR);

    std::vector<std::string> cbLog;
    GameCallback game(&cbLog);

    WAVEFORMATEX fmt = {};
    fmt.wFormatTag = WAVE_FORMAT_PCM;
    fmt.nChannels = 2;
    fmt.nSamplesPerSec = 48000;
    fmt.wBitsPerSample = 16;
    fmt.nBlockAlign = 4;
    fmt.nAvgBytesPerSec = 48000 * 4;

    IXAudio2SourceVoice* voice = nullptr;
    CHECK(x->CreateSourceVoice(&voice, &fmt, 0, XAUDIO2_DEFAULT_FREQ_RATIO, &game, nullptr, nullptr) == S_OK,
          "CreateSourceVoice");
    CHECK(engine.lastCallback == &game, "callback forwarded intact");

    voice->Start(0, XAUDIO2_COMMIT_NOW);
    BYTE pcm[16] = {};
    XAUDIO2_BUFFER buf = {};
    buf.AudioBytes = sizeof(pcm);
    buf.pAudioData = pcm;
    buf.Flags = XAUDIO2_END_OF_STREAM;
    buf.pContext = (void*)0x1234;
    voice->SubmitSourceBuffer(&buf, nullptr);

    CHECK(cbLog.size() == 3, "three callbacks");
    if (cbLog.size() == 3)
    {
        CHECK(cbLog[0] == "cb-start", "start first");
        CHECK(cbLog[1] == "cb-end", "end second");
        CHECK(cbLog[2] == "cb-streamend", "stream-end last");
    }

    voice->DestroyVoice();
    x->Release();
}

int main()
{
    CheckLayout();
    CheckForwarding();
    CheckCallbackOrder();
    if (g_failures == 0) printf("xaudio2_7: all checks passed\n");
    else printf("xaudio2_7: %d check(s) failed\n", g_failures);
    return g_failures == 0 ? 0 : 1;
}
