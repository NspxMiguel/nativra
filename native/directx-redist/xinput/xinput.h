#pragma once
#include <stdint.h>
#ifdef _WIN32
#define XINPUT_CALL __stdcall
#else
#define XINPUT_CALL
#endif
#ifdef __cplusplus
extern "C" {
#endif
int XINPUT_CALL NativraBindXInput(void *const *callbacks, uint32_t count);
uint32_t XINPUT_CALL XInputGetState(uint32_t index, void *state);
uint32_t XINPUT_CALL XInputSetState(uint32_t index, void *vibration);
uint32_t XINPUT_CALL XInputGetCapabilities(uint32_t index, uint32_t flags,
                                           void *caps);
void XINPUT_CALL XInputEnable(int enabled);
uint32_t XINPUT_CALL XInputGetBatteryInformation(uint32_t index, uint32_t type,
                                                 void *battery);
uint32_t XINPUT_CALL XInputGetKeystroke(uint32_t index, uint32_t flags,
                                        void *key);
uint32_t XINPUT_CALL XInputGetDSoundAudioDeviceGuids(uint32_t index,
                                                     void *render,
                                                     void *capture);
uint32_t XINPUT_CALL XInputGetAudioDeviceIds(uint32_t index, void *render,
                                             void *renderCount, void *capture,
                                             void *captureCount);
uint32_t XINPUT_CALL NativraXInputUnsupported(void);
#ifdef __cplusplus
}
#endif
