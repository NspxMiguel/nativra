#include "xinput.h"

// A real PE export surface for runtimes whose DLL discovery bypasses the guest
// loader. State and policy remain in PadBridge; bind before starting guest
// code.
static void *handlers[8];
using State = uint32_t(XINPUT_CALL *)(uint32_t, void *);
using Query = uint32_t(XINPUT_CALL *)(uint32_t, uint32_t, void *);
using Enable = void(XINPUT_CALL *)(int);
using AudioGuids = uint32_t(XINPUT_CALL *)(uint32_t, void *, void *);
using AudioIds = uint32_t(XINPUT_CALL *)(uint32_t, void *, void *, void *,
                                         void *);

int XINPUT_CALL NativraBindXInput(void *const *callbacks, uint32_t count) {
  if (!callbacks || count != 8)
    return 0;
  for (uint32_t i = 0; i < count; ++i)
    if (!callbacks[i])
      return 0;
  for (uint32_t i = 0; i < count; ++i)
    handlers[i] = callbacks[i];
  return 1;
}
uint32_t XINPUT_CALL XInputGetState(uint32_t index, void *state) {
  return handlers[0] ? reinterpret_cast<State>(handlers[0])(index, state)
                     : 1167;
}
uint32_t XINPUT_CALL XInputSetState(uint32_t index, void *vibration) {
  return handlers[1] ? reinterpret_cast<State>(handlers[1])(index, vibration)
                     : 1167;
}
uint32_t XINPUT_CALL XInputGetCapabilities(uint32_t index, uint32_t flags,
                                           void *caps) {
  return handlers[2] ? reinterpret_cast<Query>(handlers[2])(index, flags, caps)
                     : 1167;
}
void XINPUT_CALL XInputEnable(int enabled) {
  if (handlers[3])
    reinterpret_cast<Enable>(handlers[3])(enabled);
}
uint32_t XINPUT_CALL XInputGetBatteryInformation(uint32_t index, uint32_t type,
                                                 void *battery) {
  return handlers[4]
             ? reinterpret_cast<Query>(handlers[4])(index, type, battery)
             : 1167;
}
uint32_t XINPUT_CALL XInputGetKeystroke(uint32_t index, uint32_t flags,
                                        void *key) {
  return handlers[5] ? reinterpret_cast<Query>(handlers[5])(index, flags, key)
                     : 1167;
}
uint32_t XINPUT_CALL XInputGetDSoundAudioDeviceGuids(uint32_t index,
                                                     void *render,
                                                     void *capture) {
  return handlers[6]
             ? reinterpret_cast<AudioGuids>(handlers[6])(index, render, capture)
             : 1167;
}
uint32_t XINPUT_CALL XInputGetAudioDeviceIds(uint32_t index, void *render,
                                             void *renderCount, void *capture,
                                             void *captureCount) {
  return handlers[7] ? reinterpret_cast<AudioIds>(handlers[7])(
                           index, render, renderCount, capture, captureCount)
                     : 1167;
}
uint32_t XINPUT_CALL NativraXInputUnsupported(void) { return 120; }
