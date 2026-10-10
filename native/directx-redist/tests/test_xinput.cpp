#include "../xinput/xinput.h"
#include <assert.h>
#include <string.h>

static int enabled;
static uint32_t state(uint32_t index, void *output) {
  if (index >= 4 || !output)
    return 160;
  if (index != 0)
    return 1167;
  memset(output, 0, 16);
  static_cast<uint8_t *>(output)[5] = 0x10;
  return 0;
}
static uint32_t query(uint32_t index, uint32_t flags, void *output) {
  assert(index == 0 && flags == 3 && output);
  return 29;
}
static void enable(int value) { enabled = value; }
static uint32_t guids(uint32_t index, void *render, void *capture) {
  assert(index == 0 && render && capture);
  return 31;
}
static uint32_t ids(uint32_t index, void *render, void *renderCount,
                    void *capture, void *captureCount) {
  assert(index == 0 && render && renderCount && capture && captureCount);
  return 37;
}
int main() {
  uint8_t output[24];
  memset(output, 0xCD, sizeof(output));
  assert(XInputGetState(0, output) == 1167);
  void *callbacks[] = {
      reinterpret_cast<void *>(state), reinterpret_cast<void *>(state),
      reinterpret_cast<void *>(query), reinterpret_cast<void *>(enable),
      reinterpret_cast<void *>(query), reinterpret_cast<void *>(query),
      reinterpret_cast<void *>(guids), reinterpret_cast<void *>(ids)};
  assert(!NativraBindXInput(nullptr, 8));
  assert(!NativraBindXInput(callbacks, 7));
  assert(NativraBindXInput(callbacks, 8));
  assert(XInputGetState(0, output) == 0);
  assert(output[4] == 0 && output[5] == 0x10);
  for (int i = 16; i < 24; ++i)
    assert(output[i] == 0xCD);
  assert(XInputGetState(1, output) == 1167);
  assert(XInputGetState(4, output) == 160);
  assert(XInputGetState(0, nullptr) == 160);
  assert(XInputSetState(0, output) == 0);
  assert(XInputGetCapabilities(0, 3, output) == 29);
  assert(XInputGetBatteryInformation(0, 3, output) == 29);
  assert(XInputGetKeystroke(0, 3, output) == 29);
  XInputEnable(1);
  assert(enabled == 1);
  assert(XInputGetDSoundAudioDeviceGuids(0, output, output) == 31);
  assert(XInputGetAudioDeviceIds(0, output, output, output, output) == 37);
  callbacks[0] = nullptr;
  assert(!NativraBindXInput(callbacks, 8));
  assert(XInputGetState(0, output) == 0);
  assert(NativraXInputUnsupported() == 120);
}
