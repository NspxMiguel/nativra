// DirectInput 8 keyboard and mouse shim for PE32 games. Input comes from the
// guest's user32 bridge; force feedback and game controllers are not exposed.
#define DIRECTINPUT_VERSION 0x0800
#define INITGUID
#include <cstring>
#include <dinput.h>
#include <new>
#include <windows.h>

// Newer SDKs dropped this flag from dinput.h; it is the high bit of the object-type word.
#ifndef DIDFT_OPTIONAL
#define DIDFT_OPTIONAL 0x80000000
#endif

namespace {
constexpr DWORD kMaxObjects = 256;
constexpr DWORD kMaxEvents = 512;
const GUID kUnknownInterface = {0, 0, 0, {0xC0, 0, 0, 0, 0, 0, 0, 0x46}};

bool Same(REFGUID a, REFGUID b) { return IsEqualGUID(a, b) != 0; }
bool Keyboard(REFGUID id) {
  return Same(id, GUID_SysKeyboard) || Same(id, GUID_SysKeyboardEm) ||
         Same(id, GUID_SysKeyboardEm2);
}
bool Mouse(REFGUID id) {
  return Same(id, GUID_SysMouse) || Same(id, GUID_SysMouseEm) ||
         Same(id, GUID_SysMouseEm2);
}

// These are set-1 DirectInput scan codes, including the extended-key prefix.
struct KeyMap {
  BYTE vk, dik;
};
constexpr KeyMap kKeys[] = {
    {VK_ESCAPE, 0x01},     {'1', 0x02},         {'2', 0x03},
    {'3', 0x04},           {'4', 0x05},         {'5', 0x06},
    {'6', 0x07},           {'7', 0x08},         {'8', 0x09},
    {'9', 0x0A},           {'0', 0x0B},         {VK_OEM_MINUS, 0x0C},
    {VK_OEM_PLUS, 0x0D},   {VK_BACK, 0x0E},     {VK_TAB, 0x0F},
    {'Q', 0x10},           {'W', 0x11},         {'E', 0x12},
    {'R', 0x13},           {'T', 0x14},         {'Y', 0x15},
    {'U', 0x16},           {'I', 0x17},         {'O', 0x18},
    {'P', 0x19},           {VK_OEM_4, 0x1A},    {VK_OEM_6, 0x1B},
    {VK_RETURN, 0x1C},     {VK_LCONTROL, 0x1D}, {'A', 0x1E},
    {'S', 0x1F},           {'D', 0x20},         {'F', 0x21},
    {'G', 0x22},           {'H', 0x23},         {'J', 0x24},
    {'K', 0x25},           {'L', 0x26},         {VK_OEM_1, 0x27},
    {VK_OEM_7, 0x28},      {VK_OEM_3, 0x29},    {VK_LSHIFT, 0x2A},
    {VK_OEM_5, 0x2B},      {'Z', 0x2C},         {'X', 0x2D},
    {'C', 0x2E},           {'V', 0x2F},         {'B', 0x30},
    {'N', 0x31},           {'M', 0x32},         {VK_OEM_COMMA, 0x33},
    {VK_OEM_PERIOD, 0x34}, {VK_OEM_2, 0x35},    {VK_RSHIFT, 0x36},
    {VK_MULTIPLY, 0x37},   {VK_LMENU, 0x38},    {VK_SPACE, 0x39},
    {VK_CAPITAL, 0x3A},    {VK_F1, 0x3B},       {VK_F2, 0x3C},
    {VK_F3, 0x3D},         {VK_F4, 0x3E},       {VK_F5, 0x3F},
    {VK_F6, 0x40},         {VK_F7, 0x41},       {VK_F8, 0x42},
    {VK_F9, 0x43},         {VK_F10, 0x44},      {VK_NUMLOCK, 0x45},
    {VK_SCROLL, 0x46},     {VK_NUMPAD7, 0x47},  {VK_NUMPAD8, 0x48},
    {VK_NUMPAD9, 0x49},    {VK_SUBTRACT, 0x4A}, {VK_NUMPAD4, 0x4B},
    {VK_NUMPAD5, 0x4C},    {VK_NUMPAD6, 0x4D},  {VK_ADD, 0x4E},
    {VK_NUMPAD1, 0x4F},    {VK_NUMPAD2, 0x50},  {VK_NUMPAD3, 0x51},
    {VK_NUMPAD0, 0x52},    {VK_DECIMAL, 0x53},  {VK_F11, 0x57},
    {VK_F12, 0x58},        {VK_RCONTROL, 0x9D}, {VK_DIVIDE, 0xB5},
    {VK_RMENU, 0xB8},      {VK_HOME, 0xC7},     {VK_UP, 0xC8},
    {VK_PRIOR, 0xC9},      {VK_LEFT, 0xCB},     {VK_RIGHT, 0xCD},
    {VK_END, 0xCF},        {VK_DOWN, 0xD0},     {VK_NEXT, 0xD1},
    {VK_INSERT, 0xD2},     {VK_DELETE, 0xD3},   {VK_LWIN, 0xDB},
    {VK_RWIN, 0xDC},       {VK_APPS, 0xDD}};

void ReadKeyboard(BYTE *keys) {
  std::memset(keys, 0, 256);
  for (const auto &key : kKeys)
    if (GetAsyncKeyState(key.vk) & 0x8000)
      keys[key.dik] = 0x80;
  if (GetAsyncKeyState(VK_CONTROL) & 0x8000)
    keys[0x1D] = 0x80;
  if (GetAsyncKeyState(VK_SHIFT) & 0x8000)
    keys[0x2A] = 0x80;
  if (GetAsyncKeyState(VK_MENU) & 0x8000)
    keys[0x38] = 0x80;
}

DIOBJECTDATAFORMAT keyboardObjects[256];
DIOBJECTDATAFORMAT mouseObjects[7];
DIOBJECTDATAFORMAT mouse2Objects[11];
DIOBJECTDATAFORMAT joystickObjects[44];
DIOBJECTDATAFORMAT joystick2Objects[164];

void FillMouse(DIOBJECTDATAFORMAT *objects, DWORD count) {
  const GUID *axes[] = {&GUID_XAxis, &GUID_YAxis, &GUID_ZAxis};
  for (DWORD i = 0; i < 3; ++i)
    objects[i] = {axes[i], i * 4,
                  DIDFT_AXIS | DIDFT_ANYINSTANCE | DIDFT_OPTIONAL,
                  DIDOI_ASPECTPOSITION};
  for (DWORD i = 3; i < count; ++i)
    objects[i] = {&GUID_Button, 12 + i - 3,
                  DIDFT_BUTTON | DIDFT_MAKEINSTANCE(i - 3) | DIDFT_OPTIONAL, 0};
}
void FillJoystick(DIOBJECTDATAFORMAT *objects, DWORD count) {
  const GUID *axes[] = {&GUID_XAxis,  &GUID_YAxis,  &GUID_ZAxis,  &GUID_RxAxis,
                        &GUID_RyAxis, &GUID_RzAxis, &GUID_Slider, &GUID_Slider};
  for (DWORD i = 0; i < 8; ++i)
    objects[i] = {axes[i], i * 4,
                  DIDFT_AXIS | DIDFT_ANYINSTANCE | DIDFT_OPTIONAL,
                  DIDOI_ASPECTPOSITION};
  for (DWORD i = 0; i < 4; ++i)
    objects[8 + i] = {&GUID_POV, 32 + i * 4,
                      DIDFT_POV | DIDFT_ANYINSTANCE | DIDFT_OPTIONAL, 0};
  for (DWORD i = 12; i < count && i < (count == 44 ? 44u : 140u); ++i)
    objects[i] = {&GUID_Button, 48 + i - 12,
                  DIDFT_BUTTON | DIDFT_ANYINSTANCE | DIDFT_OPTIONAL, 0};
  if (count == 164) {
    const GUID *extra[] = {&GUID_XAxis,  &GUID_YAxis,  &GUID_ZAxis,
                           &GUID_RxAxis, &GUID_RyAxis, &GUID_RzAxis,
                           &GUID_Slider, &GUID_Slider};
    for (DWORD i = 0; i < 24; ++i)
      objects[140 + i] = {extra[i % 8], 176 + i * 4,
                          DIDFT_AXIS | DIDFT_ANYINSTANCE | DIDFT_OPTIONAL,
                          static_cast<DWORD>(i < 8    ? DIDOI_ASPECTVELOCITY
                                             : i < 16 ? DIDOI_ASPECTACCEL
                                                      : DIDOI_ASPECTFORCE)};
  }
}

void InitializeFormats() {
  for (DWORD i = 0; i < 256; ++i)
    keyboardObjects[i] = {
        &GUID_Key, i, DIDFT_OPTIONAL | DIDFT_BUTTON | DIDFT_MAKEINSTANCE(i), 0};
  FillMouse(mouseObjects, 7);
  FillMouse(mouse2Objects, 11);
  FillJoystick(joystickObjects, 44);
  FillJoystick(joystick2Objects, 164);
}

// DIPROP_* values are pseudo-GUID references with addresses 1, 2, ... .
ULONG_PTR PropertyId(REFGUID id) { return reinterpret_cast<ULONG_PTR>(&id); }

template <bool Wide> struct Traits;
template <> struct Traits<false> {
  using Input = IDirectInput8A;
  using Device = IDirectInputDevice8A;
  using Instance = DIDEVICEINSTANCEA;
  using Object = DIDEVICEOBJECTINSTANCEA;
  using InstanceDx3 = DIDEVICEINSTANCE_DX3A;
  using ObjectDx3 = DIDEVICEOBJECTINSTANCE_DX3A;
  using DeviceCallback = LPDIENUMDEVICESCALLBACKA;
  using ObjectCallback = LPDIENUMDEVICEOBJECTSCALLBACKA;
  using SemanticCallback = LPDIENUMDEVICESBYSEMANTICSCBA;
  using Configure = LPDICONFIGUREDEVICESPARAMSA;
  using Action = LPDIACTIONFORMATA;
  using EffectInfo = LPDIEFFECTINFOA;
  using EffectCallback = LPDIENUMEFFECTSCALLBACKA;
  using FileCallback = LPDIENUMEFFECTSINFILECALLBACK;
  using ImageInfo = LPDIDEVICEIMAGEINFOHEADERA;
  using Char = char;
  static REFIID InputId() { return IID_IDirectInput8A; }
  static REFIID DeviceId() { return IID_IDirectInputDevice8A; }
};
template <> struct Traits<true> {
  using Input = IDirectInput8W;
  using Device = IDirectInputDevice8W;
  using Instance = DIDEVICEINSTANCEW;
  using Object = DIDEVICEOBJECTINSTANCEW;
  using InstanceDx3 = DIDEVICEINSTANCE_DX3W;
  using ObjectDx3 = DIDEVICEOBJECTINSTANCE_DX3W;
  using DeviceCallback = LPDIENUMDEVICESCALLBACKW;
  using ObjectCallback = LPDIENUMDEVICEOBJECTSCALLBACKW;
  using SemanticCallback = LPDIENUMDEVICESBYSEMANTICSCBW;
  using Configure = LPDICONFIGUREDEVICESPARAMSW;
  using Action = LPDIACTIONFORMATW;
  using EffectInfo = LPDIEFFECTINFOW;
  using EffectCallback = LPDIENUMEFFECTSCALLBACKW;
  using FileCallback = LPDIENUMEFFECTSINFILECALLBACK;
  using ImageInfo = LPDIDEVICEIMAGEINFOHEADERW;
  using Char = wchar_t;
  static REFIID InputId() { return IID_IDirectInput8W; }
  static REFIID DeviceId() { return IID_IDirectInputDevice8W; }
};

template <typename Char>
void CopyName(Char *out, DWORD capacity, const char *name) {
  DWORD i = 0;
  for (; i + 1 < capacity && name[i]; ++i)
    out[i] = static_cast<Char>(name[i]);
  if (capacity)
    out[i] = 0;
}
template <typename Char>
bool NameMatches(const Char *actual, const char *expected) {
  if (!actual)
    return false;
  for (; *expected; ++actual, ++expected) {
    Char value = *actual;
    if (value >= 'A' && value <= 'Z')
      value += 'a' - 'A';
    char target = *expected;
    if (target >= 'A' && target <= 'Z')
      target += 'a' - 'A';
    if (value != static_cast<Char>(target))
      return false;
  }
  return *actual == 0;
}
template <bool Wide>
void DeviceInfo(typename Traits<Wide>::Instance *info, bool keyboard) {
  const DWORD size = info->dwSize;
  std::memset(info, 0, size < sizeof(*info) ? size : sizeof(*info));
  info->dwSize = size;
  info->guidInstance = keyboard ? GUID_SysKeyboard : GUID_SysMouse;
  info->guidProduct = info->guidInstance;
  info->dwDevType = keyboard ? DI8DEVTYPE_KEYBOARD : DI8DEVTYPE_MOUSE;
  CopyName(info->tszInstanceName, MAX_PATH,
           keyboard ? "System Keyboard" : "System Mouse");
  CopyName(info->tszProductName, MAX_PATH,
           keyboard ? "System Keyboard" : "System Mouse");
  if (size >= sizeof(*info)) {
    info->wUsagePage = 1;
    info->wUsage = keyboard ? 6 : 2;
  }
}

struct Snapshot {
  BYTE keys[256];
  BYTE buttons[8];
  LONG x, y, z;
};

template <bool Wide> class InputDevice final : public Traits<Wide>::Device {
  using T = Traits<Wide>;
  ULONG references = 1;
  bool keyboard, acquired = false, haveCursor = false, havePrevious = false,
                 overflow = false;
  POINT lastCursor = {};
  LONG motionX = 0, motionY = 0;
  Snapshot previous = {};
  DIOBJECTDATAFORMAT objects[kMaxObjects] = {};
  GUID objectGuids[kMaxObjects] = {};
  DWORD objectCount = 0, dataSize = 0, bufferSize = 0, head = 0, count = 0,
        sequence = 0, axisMode = DIPROPAXISMODE_REL;
  DIPROPRANGE range = {
      {sizeof(DIPROPRANGE), sizeof(DIPROPHEADER), 0, DIPH_DEVICE}, 0, 65535};
  DWORD deadzone = 0;
  HANDLE event = nullptr;
  DIDEVICEOBJECTDATA events[kMaxEvents] = {};

  void Push(DWORD offset, DWORD value) {
    if (!bufferSize)
      return;
    if (count == bufferSize) {
      head = (head + 1) % kMaxEvents;
      --count;
      overflow = true;
    }
    auto &item = events[(head + count) % kMaxEvents];
    item.dwOfs = offset;
    item.dwData = value;
    item.dwTimeStamp = GetTickCount();
    item.dwSequence = ++sequence;
    item.uAppData = 0;
    ++count;
    if (event)
      SetEvent(event);
  }
  void Sample(Snapshot &sample, bool queue) {
    std::memset(&sample, 0, sizeof(sample));
    if (keyboard)
      ReadKeyboard(sample.keys);
    else {
      POINT cursor = {};
      if (GetCursorPos(&cursor)) {
        if (haveCursor) {
          LONG dx = cursor.x - lastCursor.x, dy = cursor.y - lastCursor.y;
          // A game may warp the pointer back to the center after each frame.
          if (dx > 1000 || dx < -1000)
            dx = 0;
          if (dy > 1000 || dy < -1000)
            dy = 0;
          sample.x = dx;
          sample.y = dy;
          motionX += dx;
          motionY += dy;
        }
        lastCursor = cursor;
        haveCursor = true;
      }
      const int buttons[] = {VK_LBUTTON, VK_RBUTTON, VK_MBUTTON, VK_XBUTTON1,
                             VK_XBUTTON2};
      for (int i = 0; i < 5; ++i)
        sample.buttons[i] = (GetAsyncKeyState(buttons[i]) & 0x8000) ? 0x80 : 0;
      // The user32 bridge currently has no wheel accumulator; z remains zero.
    }
    if (queue && havePrevious) {
      for (DWORD i = 0; i < objectCount; ++i) {
        const auto &object = objects[i];
        DWORD before = 0, after = 0;
        if (keyboard) {
          DWORD key = DIDFT_GETINSTANCE(object.dwType);
          if (key >= 256)
            continue;
          before = previous.keys[key];
          after = sample.keys[key];
        } else if (object.pguid && Same(*object.pguid, GUID_XAxis)) {
          before = 0;
          after = static_cast<DWORD>(sample.x);
        } else if (object.pguid && Same(*object.pguid, GUID_YAxis)) {
          before = 0;
          after = static_cast<DWORD>(sample.y);
        } else if (object.pguid && Same(*object.pguid, GUID_ZAxis)) {
          before = 0;
          after = static_cast<DWORD>(sample.z);
        } else if (DIDFT_GETTYPE(object.dwType) & DIDFT_BUTTON) {
          DWORD button = DIDFT_GETINSTANCE(object.dwType);
          if (button >= 8)
            continue;
          before = previous.buttons[button];
          after = sample.buttons[button];
        }
        if (before != after)
          Push(object.dwOfs, after);
      }
    }
    previous = sample;
    havePrevious = true;
  }

public:
  explicit InputDevice(bool isKeyboard) : keyboard(isKeyboard) {}
  HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id, void **out) override {
    if (!out)
      return E_POINTER;
    *out = nullptr;
    if (!Same(id, kUnknownInterface) && !Same(id, T::DeviceId()))
      return E_NOINTERFACE;
    *out = static_cast<typename T::Device *>(this);
    AddRef();
    return S_OK;
  }
  ULONG STDMETHODCALLTYPE AddRef() override { return ++references; }
  ULONG STDMETHODCALLTYPE Release() override {
    ULONG left = --references;
    if (!left)
      delete this;
    return left;
  }
  HRESULT STDMETHODCALLTYPE GetCapabilities(LPDIDEVCAPS caps) override {
    if (!caps || caps->dwSize < sizeof(DIDEVCAPS_DX3))
      return DIERR_INVALIDPARAM;
    DWORD size = caps->dwSize;
    std::memset(caps, 0, size < sizeof(*caps) ? size : sizeof(*caps));
    caps->dwSize = size;
    caps->dwFlags = DIDC_ATTACHED;
    caps->dwDevType = keyboard ? DI8DEVTYPE_KEYBOARD : DI8DEVTYPE_MOUSE;
    caps->dwAxes = keyboard ? 0 : 3;
    caps->dwButtons = keyboard ? 256 : 8;
    return S_OK;
  }
  HRESULT STDMETHODCALLTYPE EnumObjects(typename T::ObjectCallback callback,
                                        LPVOID context, DWORD flags) override {
    if (!callback)
      return DIERR_INVALIDPARAM;
    DWORD total = keyboard ? 256 : 11;
    for (DWORD i = 0; i < total; ++i) {
      DWORD type = keyboard ? DIDFT_BUTTON
                   : i < 3  ? DIDFT_RELAXIS
                            : DIDFT_BUTTON;
      if (flags && !(flags & type))
        continue;
      typename T::Object info = {};
      info.dwSize = sizeof(info);
      info.guidType = keyboard ? GUID_Key
                      : i == 0 ? GUID_XAxis
                      : i == 1 ? GUID_YAxis
                      : i == 2 ? GUID_ZAxis
                               : GUID_Button;
      info.dwOfs = keyboard ? i : i < 3 ? i * 4 : 12 + i - 3;
      info.dwType = type | DIDFT_MAKEINSTANCE(keyboard ? i : i < 3 ? i : i - 3);
      CopyName(info.tszName, MAX_PATH,
               keyboard ? "Key"
               : i < 3  ? "Axis"
                        : "Button");
      if (callback(&info, context) == DIENUM_STOP)
        break;
    }
    return S_OK;
  }
  HRESULT STDMETHODCALLTYPE GetProperty(REFGUID property,
                                        LPDIPROPHEADER header) override {
    if (!header || header->dwSize < sizeof(DIPROPHEADER))
      return DIERR_INVALIDPARAM;
    ULONG_PTR id = PropertyId(property);
    if (id == 4 && header->dwSize >= sizeof(DIPROPRANGE)) {
      auto *out = reinterpret_cast<DIPROPRANGE *>(header);
      out->lMin = range.lMin;
      out->lMax = range.lMax;
      return S_OK;
    }
    if (header->dwSize < sizeof(DIPROPDWORD))
      return DIERR_INVALIDPARAM;
    DWORD *value = &reinterpret_cast<DIPROPDWORD *>(header)->dwData;
    if (id == 1)
      *value = bufferSize;
    else if (id == 2)
      *value = axisMode;
    else if (id == 5)
      *value = deadzone;
    else
      return DIERR_UNSUPPORTED;
    return S_OK;
  }
  HRESULT STDMETHODCALLTYPE SetProperty(REFGUID property,
                                        LPCDIPROPHEADER header) override {
    if (!header || header->dwSize < sizeof(DIPROPHEADER))
      return DIERR_INVALIDPARAM;
    ULONG_PTR id = PropertyId(property);
    if (id == 4 && header->dwSize >= sizeof(DIPROPRANGE)) {
      range = *reinterpret_cast<const DIPROPRANGE *>(header);
      return S_OK;
    }
    if (header->dwSize < sizeof(DIPROPDWORD))
      return DIERR_INVALIDPARAM;
    DWORD value = reinterpret_cast<const DIPROPDWORD *>(header)->dwData;
    if (id == 1) {
      bufferSize = value < kMaxEvents ? value : kMaxEvents;
      head = count = 0;
      overflow = false;
    } else if (id == 2)
      axisMode = value;
    else if (id == 5)
      deadzone = value;
    else
      return DIERR_UNSUPPORTED;
    return S_OK;
  }
  HRESULT STDMETHODCALLTYPE Acquire() override {
    if (acquired)
      return S_FALSE;
    acquired = true;
    havePrevious = false;
    haveCursor = false;
    motionX = motionY = 0;
    Snapshot initial;
    Sample(initial, false);
    return S_OK;
  }
  HRESULT STDMETHODCALLTYPE Unacquire() override {
    if (!acquired)
      return S_FALSE;
    acquired = false;
    return S_OK;
  }
  HRESULT STDMETHODCALLTYPE GetDeviceState(DWORD size, LPVOID data) override {
    if (!acquired)
      return DIERR_NOTACQUIRED;
    if (!data || !dataSize || size != dataSize)
      return DIERR_INVALIDPARAM;
    Snapshot sample;
    Sample(sample, true);
    if (!keyboard) {
      sample.x = motionX;
      sample.y = motionY;
      motionX = motionY = 0;
    }
    std::memset(data, 0, size);
    for (DWORD i = 0; i < objectCount; ++i) {
      const auto &object = objects[i];
      if (keyboard) {
        DWORD key = DIDFT_GETINSTANCE(object.dwType);
        if (key < 256 && object.dwOfs < size)
          reinterpret_cast<BYTE *>(data)[object.dwOfs] = sample.keys[key];
      } else {
        DWORD value = 0;
        bool axis = false;
        if (object.pguid && Same(*object.pguid, GUID_XAxis)) {
          value = static_cast<DWORD>(sample.x);
          axis = true;
        } else if (object.pguid && Same(*object.pguid, GUID_YAxis)) {
          value = static_cast<DWORD>(sample.y);
          axis = true;
        } else if (object.pguid && Same(*object.pguid, GUID_ZAxis)) {
          value = static_cast<DWORD>(sample.z);
          axis = true;
        }
        if (axis && object.dwOfs + 4 <= size)
          std::memcpy(reinterpret_cast<BYTE *>(data) + object.dwOfs, &value, 4);
        else if (!axis && (DIDFT_GETTYPE(object.dwType) & DIDFT_BUTTON) &&
                 object.dwOfs < size) {
          DWORD button = DIDFT_GETINSTANCE(object.dwType);
          if (button < 8)
            reinterpret_cast<BYTE *>(data)[object.dwOfs] =
                sample.buttons[button];
        }
      }
    }
    return S_OK;
  }
  HRESULT STDMETHODCALLTYPE GetDeviceData(DWORD itemSize,
                                          LPDIDEVICEOBJECTDATA output,
                                          LPDWORD inOut, DWORD flags) override {
    if (!acquired)
      return DIERR_NOTACQUIRED;
    if (!inOut || itemSize < sizeof(DIDEVICEOBJECTDATA_DX3) ||
        itemSize > sizeof(DIDEVICEOBJECTDATA))
      return DIERR_INVALIDPARAM;
    if (!bufferSize)
      return DIERR_NOTBUFFERED;
    Snapshot sample;
    Sample(sample, true);
    HRESULT result = overflow ? DI_BUFFEROVERFLOW : S_OK;
    if (!output) {
      *inOut = count;
      return result;
    }
    DWORD available = count, taken = *inOut < available ? *inOut : available;
    if (*inOut == INFINITE)
      taken = available;
    for (DWORD i = 0; i < taken; ++i)
      std::memcpy(reinterpret_cast<BYTE *>(output) + i * itemSize,
                  &events[(head + i) % kMaxEvents], itemSize);
    *inOut = taken;
    if (!(flags & DIGDD_PEEK)) {
      head = (head + taken) % kMaxEvents;
      count -= taken;
      overflow = false;
    }
    return result;
  }
  HRESULT STDMETHODCALLTYPE SetDataFormat(LPCDIDATAFORMAT format) override {
    if (!format || format->dwSize != sizeof(DIDATAFORMAT) ||
        format->dwObjSize != sizeof(DIOBJECTDATAFORMAT) || !format->rgodf ||
        format->dwNumObjs > kMaxObjects || !format->dwDataSize ||
        format->dwDataSize > 4096)
      return DIERR_INVALIDPARAM;
    if (acquired)
      return DIERR_ACQUIRED;
    if (keyboard && format->dwDataSize != 256)
      return DIERR_INVALIDPARAM;
    if (!keyboard && format->dwDataSize < sizeof(DIMOUSESTATE))
      return DIERR_INVALIDPARAM;
    objectCount = format->dwNumObjs;
    dataSize = format->dwDataSize;
    std::memcpy(objects, format->rgodf,
                objectCount * sizeof(DIOBJECTDATAFORMAT));
    for (DWORD i = 0; i < objectCount; ++i) {
      if (objects[i].pguid) {
        objectGuids[i] = *objects[i].pguid;
        objects[i].pguid = &objectGuids[i];
      }
    }
    return S_OK;
  }
  HRESULT STDMETHODCALLTYPE SetEventNotification(HANDLE handle) override {
    event = handle;
    return S_OK;
  }
  HRESULT STDMETHODCALLTYPE SetCooperativeLevel(HWND, DWORD flags) override {
    if (!(flags & (DISCL_FOREGROUND | DISCL_BACKGROUND)) ||
        !(flags & (DISCL_EXCLUSIVE | DISCL_NONEXCLUSIVE)))
      return DIERR_INVALIDPARAM;
    return S_OK;
  }
  HRESULT STDMETHODCALLTYPE GetObjectInfo(typename T::Object *info,
                                          DWORD object, DWORD how) override {
    if (!info || info->dwSize < sizeof(typename T::ObjectDx3))
      return DIERR_INVALIDPARAM;
    DWORD index = kMaxObjects;
    if (how == DIPH_BYOFFSET || how == DIPH_BYID) {
      for (DWORD i = 0; i < objectCount; ++i)
        if ((how == DIPH_BYOFFSET && objects[i].dwOfs == object) ||
            (how == DIPH_BYID && (objects[i].dwType & ~DIDFT_OPTIONAL) ==
                                     (object & ~DIDFT_OPTIONAL))) {
          index = i;
          break;
        }
      if (index == kMaxObjects) {
        if (how == DIPH_BYOFFSET)
          index = keyboard                         ? object
                  : object < 12 && object % 4 == 0 ? object / 4
                  : object >= 12 && object < 20    ? object - 9
                                                   : kMaxObjects;
        else if (DIDFT_GETTYPE(object) & DIDFT_BUTTON)
          index = keyboard ? DIDFT_GETINSTANCE(object)
                           : 3 + DIDFT_GETINSTANCE(object);
        else if (!keyboard && (DIDFT_GETTYPE(object) & DIDFT_AXIS))
          index = DIDFT_GETINSTANCE(object);
      }
    }
    if (index >= (keyboard ? 256u : 11u))
      return DIERR_OBJECTNOTFOUND;
    DWORD size = info->dwSize;
    std::memset(info, 0, size < sizeof(*info) ? size : sizeof(*info));
    info->dwSize = size;
    info->guidType = keyboard     ? GUID_Key
                     : index == 0 ? GUID_XAxis
                     : index == 1 ? GUID_YAxis
                     : index == 2 ? GUID_ZAxis
                                  : GUID_Button;
    info->dwOfs = keyboard ? index : index < 3 ? index * 4 : 12 + index - 3;
    info->dwType = (keyboard || index >= 3 ? DIDFT_BUTTON : DIDFT_RELAXIS) |
                   DIDFT_MAKEINSTANCE(keyboard    ? index
                                      : index < 3 ? index
                                                  : index - 3);
    CopyName(info->tszName, MAX_PATH,
             keyboard    ? "Key"
             : index < 3 ? "Axis"
                         : "Button");
    return S_OK;
  }
  HRESULT STDMETHODCALLTYPE GetDeviceInfo(typename T::Instance *info) override {
    if (!info || info->dwSize < sizeof(typename T::InstanceDx3))
      return DIERR_INVALIDPARAM;
    DeviceInfo<Wide>(info, keyboard);
    return S_OK;
  }
  HRESULT STDMETHODCALLTYPE RunControlPanel(HWND, DWORD) override {
    return S_OK;
  }
  HRESULT STDMETHODCALLTYPE Initialize(HINSTANCE, DWORD, REFGUID id) override {
    return (keyboard ? Keyboard(id) : Mouse(id)) ? S_OK : DIERR_DEVICENOTREG;
  }
  HRESULT STDMETHODCALLTYPE CreateEffect(REFGUID, LPCDIEFFECT,
                                         LPDIRECTINPUTEFFECT *out,
                                         LPUNKNOWN) override {
    if (out)
      *out = nullptr;
    return E_NOTIMPL;
  }
  HRESULT STDMETHODCALLTYPE EnumEffects(typename T::EffectCallback, LPVOID,
                                        DWORD) override {
    return S_OK;
  }
  HRESULT STDMETHODCALLTYPE GetEffectInfo(typename T::EffectInfo,
                                          REFGUID) override {
    return E_NOTIMPL;
  }
  HRESULT STDMETHODCALLTYPE GetForceFeedbackState(LPDWORD value) override {
    if (value)
      *value = 0;
    return E_NOTIMPL;
  }
  HRESULT STDMETHODCALLTYPE SendForceFeedbackCommand(DWORD) override {
    return E_NOTIMPL;
  }
  HRESULT STDMETHODCALLTYPE EnumCreatedEffectObjects(
      LPDIENUMCREATEDEFFECTOBJECTSCALLBACK, LPVOID, DWORD) override {
    return S_OK;
  }
  HRESULT STDMETHODCALLTYPE Escape(LPDIEFFESCAPE) override { return E_NOTIMPL; }
  HRESULT STDMETHODCALLTYPE Poll() override {
    if (!acquired)
      return DIERR_NOTACQUIRED;
    Snapshot sample;
    Sample(sample, true);
    return S_OK;
  }
  HRESULT STDMETHODCALLTYPE SendDeviceData(DWORD, LPCDIDEVICEOBJECTDATA,
                                           LPDWORD, DWORD) override {
    return E_NOTIMPL;
  }
  HRESULT STDMETHODCALLTYPE EnumEffectsInFile(const typename T::Char *,
                                              typename T::FileCallback, LPVOID,
                                              DWORD) override {
    return E_NOTIMPL;
  }
  HRESULT STDMETHODCALLTYPE WriteEffectToFile(const typename T::Char *, DWORD,
                                              LPDIFILEEFFECT, DWORD) override {
    return E_NOTIMPL;
  }
  HRESULT STDMETHODCALLTYPE BuildActionMap(typename T::Action,
                                           const typename T::Char *,
                                           DWORD) override {
    return E_NOTIMPL;
  }
  HRESULT STDMETHODCALLTYPE SetActionMap(typename T::Action,
                                         const typename T::Char *,
                                         DWORD) override {
    return E_NOTIMPL;
  }
  HRESULT STDMETHODCALLTYPE GetImageInfo(typename T::ImageInfo) override {
    return E_NOTIMPL;
  }
};

template <bool Wide> class DirectInput final : public Traits<Wide>::Input {
  using T = Traits<Wide>;
  ULONG references = 1;

public:
  HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id, void **out) override {
    if (!out)
      return E_POINTER;
    *out = nullptr;
    if (!Same(id, kUnknownInterface) && !Same(id, T::InputId()))
      return E_NOINTERFACE;
    *out = static_cast<typename T::Input *>(this);
    AddRef();
    return S_OK;
  }
  ULONG STDMETHODCALLTYPE AddRef() override { return ++references; }
  ULONG STDMETHODCALLTYPE Release() override {
    ULONG left = --references;
    if (!left)
      delete this;
    return left;
  }
  HRESULT STDMETHODCALLTYPE CreateDevice(REFGUID id, typename T::Device **out,
                                         LPUNKNOWN outer) override {
    if (!out)
      return DIERR_INVALIDPARAM;
    *out = nullptr;
    if (outer)
      return DIERR_NOAGGREGATION;
    if (!Keyboard(id) && !Mouse(id))
      return DIERR_DEVICENOTREG;
    *out = new (std::nothrow) InputDevice<Wide>(Keyboard(id));
    return *out ? S_OK : E_OUTOFMEMORY;
  }
  HRESULT STDMETHODCALLTYPE EnumDevices(DWORD type,
                                        typename T::DeviceCallback callback,
                                        LPVOID context, DWORD) override {
    if (!callback)
      return DIERR_INVALIDPARAM;
    const DWORD types[] = {DI8DEVTYPE_KEYBOARD, DI8DEVTYPE_MOUSE};
    const DWORD classes[] = {DI8DEVCLASS_KEYBOARD, DI8DEVCLASS_POINTER};
    for (int i = 0; i < 2; ++i) {
      if (type != DI8DEVCLASS_ALL && type != DI8DEVCLASS_DEVICE &&
          type != classes[i] && type != types[i])
        continue;
      typename T::Instance info = {};
      info.dwSize = sizeof(info);
      DeviceInfo<Wide>(&info, i == 0);
      if (callback(&info, context) == DIENUM_STOP)
        break;
    }
    return S_OK;
  }
  HRESULT STDMETHODCALLTYPE GetDeviceStatus(REFGUID id) override {
    return Keyboard(id) || Mouse(id) ? S_OK : DIERR_DEVICENOTREG;
  }
  HRESULT STDMETHODCALLTYPE RunControlPanel(HWND, DWORD) override {
    return S_OK;
  }
  HRESULT STDMETHODCALLTYPE Initialize(HINSTANCE, DWORD) override {
    return S_OK;
  }
  HRESULT STDMETHODCALLTYPE FindDevice(REFGUID, const typename T::Char *name,
                                       LPGUID out) override {
    if (!name || !out)
      return DIERR_INVALIDPARAM;
    if (NameMatches(name, "System Keyboard")) {
      *out = GUID_SysKeyboard;
      return S_OK;
    }
    if (NameMatches(name, "System Mouse")) {
      *out = GUID_SysMouse;
      return S_OK;
    }
    return DIERR_DEVICENOTREG;
  }
  HRESULT STDMETHODCALLTYPE EnumDevicesBySemantics(const typename T::Char *,
                                                   typename T::Action,
                                                   typename T::SemanticCallback,
                                                   LPVOID, DWORD) override {
    return S_OK;
  }
  HRESULT STDMETHODCALLTYPE ConfigureDevices(LPDICONFIGUREDEVICESCALLBACK,
                                             typename T::Configure, DWORD,
                                             LPVOID) override {
    return E_NOTIMPL;
  }
};
} // namespace

extern "C" {
// The PE export table marks these as DATA. Games import their addresses through
// the IAT and then pass them directly to SetDataFormat.
const DIDATAFORMAT c_dfDIKeyboard = {sizeof(DIDATAFORMAT),
                                     sizeof(DIOBJECTDATAFORMAT),
                                     DIDF_RELAXIS,
                                     sizeof(BYTE) * 256,
                                     256,
                                     keyboardObjects};
const DIDATAFORMAT c_dfDIMouse = {sizeof(DIDATAFORMAT),
                                  sizeof(DIOBJECTDATAFORMAT),
                                  DIDF_RELAXIS,
                                  sizeof(DIMOUSESTATE),
                                  7,
                                  mouseObjects};
const DIDATAFORMAT c_dfDIMouse2 = {sizeof(DIDATAFORMAT),
                                   sizeof(DIOBJECTDATAFORMAT),
                                   DIDF_RELAXIS,
                                   sizeof(DIMOUSESTATE2),
                                   11,
                                   mouse2Objects};
const DIDATAFORMAT c_dfDIJoystick = {sizeof(DIDATAFORMAT),
                                     sizeof(DIOBJECTDATAFORMAT),
                                     DIDF_ABSAXIS,
                                     sizeof(DIJOYSTATE),
                                     44,
                                     joystickObjects};
const DIDATAFORMAT c_dfDIJoystick2 = {sizeof(DIDATAFORMAT),
                                      sizeof(DIOBJECTDATAFORMAT),
                                      DIDF_ABSAXIS,
                                      sizeof(DIJOYSTATE2),
                                      164,
                                      joystick2Objects};

HRESULT WINAPI DirectInput8Create(HINSTANCE, DWORD version, REFIID id,
                                  LPVOID *out, LPUNKNOWN outer) {
  if (!out)
    return DIERR_INVALIDPARAM;
  *out = nullptr;
  if (outer)
    return DIERR_NOAGGREGATION;
  if (version < 0x0800)
    return DIERR_OLDDIRECTINPUTVERSION;
  if (Same(id, IID_IDirectInput8A)) {
    *out = new (std::nothrow) DirectInput<false>();
    return *out ? S_OK : E_OUTOFMEMORY;
  }
  if (Same(id, IID_IDirectInput8W)) {
    *out = new (std::nothrow) DirectInput<true>();
    return *out ? S_OK : E_OUTOFMEMORY;
  }
  return E_NOINTERFACE;
}
BOOL WINAPI DllMain(HINSTANCE, DWORD reason, LPVOID) {
  if (reason == DLL_PROCESS_ATTACH)
    InitializeFormats();
  return TRUE;
}
}
