// Independent synthetic MSVC caller. No Steam SDK headers or game code.
#include <cstdint>

struct UserId {
  std::uint64_t value;
  UserId()
      : value(0) {} // A class return requires MSVC's hidden result pointer.
};

struct User016 {
  virtual int handle() = 0;
  virtual bool loggedOn() = 0;
  virtual UserId id() = 0;
  virtual int connect(void *, int, UserId, unsigned, unsigned short, bool) = 0;
  virtual void disconnect(unsigned, unsigned short) = 0;
  virtual void usage(UserId, int, const char *) = 0;
  virtual bool folder(char *, int) = 0;
  virtual void startVoice() = 0;
  virtual void stopVoice() = 0;
  virtual int available(unsigned *, unsigned *, unsigned) = 0;
  virtual int voice(bool, void *, unsigned, unsigned *, bool, void *, unsigned,
                    unsigned *, unsigned) = 0;
  virtual int decompress(const void *, unsigned, void *, unsigned, unsigned *,
                         unsigned) = 0;
  virtual unsigned sampleRate() = 0;
  virtual unsigned ticket(void *, int, unsigned *) = 0;
  virtual int beginAuth(const void *, int, UserId) = 0;
  virtual void endAuth(UserId) = 0;
  virtual void cancelAuth(unsigned) = 0;
  virtual int license(UserId, unsigned) = 0;
  virtual bool behindNat() = 0;
  virtual void advertise(UserId, unsigned, unsigned short) = 0;
  virtual std::uint64_t requestEncrypted(void *, int) = 0;
  virtual bool encrypted(void *, int, unsigned *) = 0;
};

struct Count {
  unsigned before = 0xA5A5A5A5;
  unsigned value = 0xFFFFFFFF;
  unsigned after = 0x5A5A5A5A;
  bool cleared() const {
    return before == 0xA5A5A5A5 && value == 0 && after == 0x5A5A5A5A;
  }
};

#define CHECK(condition)                                                       \
  do {                                                                         \
    if (!(condition))                                                          \
      return __LINE__;                                                         \
  } while (0)

extern "C"
    __declspec(dllexport) int __cdecl probe_user016(User016 *user,
                                                    std::uint64_t expected) {
  static_assert(sizeof(UserId) == 8, "SteamID storage");
  CHECK(user != nullptr);
  CHECK(user->handle() == 1);
  CHECK(!user->loggedOn());
  struct {
    std::uint64_t before = 0xAAAAAAAAAAAAAAAA;
    UserId result;
    std::uint64_t after = 0xBBBBBBBBBBBBBBBB;
  } id;
  id.result = user->id();
  CHECK(id.result.value == expected);
  CHECK(id.before == 0xAAAAAAAAAAAAAAAA && id.after == 0xBBBBBBBBBBBBBBBB);
  CHECK(user->connect(nullptr, 0, id.result, 0, 0, false) == 0);
  user->disconnect(0, 0);
  user->usage(id.result, 0, "synthetic");
  char folder[] = {'X', 'Y'};
  CHECK(!user->folder(folder, 1));
  CHECK(folder[0] == 0 && folder[1] == 'Y');
  user->startVoice();
  user->stopVoice();
  Count compressed, uncompressed;
  CHECK(user->available(&compressed.value, &uncompressed.value, 48000) == 1);
  CHECK(compressed.cleared() && uncompressed.cleared());
  compressed.value = uncompressed.value = 42;
  CHECK(user->voice(true, nullptr, 0, &compressed.value, true, nullptr, 0,
                    &uncompressed.value, 48000) == 1);
  CHECK(compressed.cleared() && uncompressed.cleared());
  Count written;
  CHECK(user->decompress(nullptr, 0, nullptr, 0, &written.value, 48000) == 1);
  CHECK(written.cleared());
  CHECK(user->sampleRate() == 0);
  written.value = 42;
  CHECK(user->ticket(nullptr, 0, &written.value) == 0);
  CHECK(written.cleared());
  CHECK(user->beginAuth(nullptr, 0, id.result) == 1);
  user->endAuth(id.result);
  user->cancelAuth(0);
  CHECK(user->license(id.result, 42) == 2);
  CHECK(!user->behindNat());
  user->advertise(id.result, 0, 0);
  CHECK(user->requestEncrypted(nullptr, 0) == 0);
  written.value = 42;
  CHECK(!user->encrypted(nullptr, 0, &written.value));
  CHECK(written.cleared());
  return 0;
}

// Exercise all 27 SteamClient012 slots using independent native signatures.
static int probe_client012(void *client, void *expectedUser) {
  CHECK(client != nullptr);
  auto **slots = *static_cast<void ***>(client);
  using Integer = int(__fastcall *)(void *);
  using Boolean = bool(__fastcall *)(void *);
  using PipeBoolean = bool(__fastcall *)(void *, int);
  using Connect = int(__fastcall *)(void *, int);
  using Local = int(__fastcall *)(void *, int *, int);
  using Release = void(__fastcall *)(void *, int, int);
  using Binding = void(__fastcall *)(void *, unsigned, unsigned short);
  using Getter = void *(__fastcall *)(void *, int, int, const char *);
  using Utils = void *(__fastcall *)(void *, int, const char *);
  using Nothing = void(__fastcall *)(void *);
  using Hook = void(__fastcall *)(void *, void *);
  CHECK(reinterpret_cast<Integer>(slots[0])(client) == 1);
  CHECK(reinterpret_cast<PipeBoolean>(slots[1])(client, 1));
  CHECK(reinterpret_cast<Connect>(slots[2])(client, 1) == 1);
  Count pipe;
  CHECK(reinterpret_cast<Local>(slots[3])(
            client, reinterpret_cast<int *>(&pipe.value), 1) == 0);
  CHECK(pipe.cleared());
  reinterpret_cast<Release>(slots[4])(client, 1, 1);
  CHECK(reinterpret_cast<Getter>(slots[5])(client, 1, 1, "SteamUser016") ==
        expectedUser);
  CHECK(reinterpret_cast<Getter>(slots[5])(client, 1, 1, "SteamUser999") ==
        nullptr);
  CHECK(reinterpret_cast<Getter>(slots[5])(client, 1, 1, "SteamFriends011") ==
        nullptr);
  reinterpret_cast<Binding>(slots[7])(client, 0, 0);
  CHECK(reinterpret_cast<Utils>(slots[9])(client, 1, "SteamUtils005") !=
        nullptr);
  const int getters[] = {6,  8,  10, 11, 12, 13, 14, 15,
                         16, 17, 18, 23, 24, 25, 26};
  for (int slot : getters) {
    const char *version = "Unsupported999";
    if (slot == 8)
      version = "SteamFriends011";
    if (slot == 13)
      version = "STEAMUSERSTATS_INTERFACE_VERSION010";
    if (slot == 15)
      version = "STEAMAPPS_INTERFACE_VERSION005";
    if (slot == 17)
      version = "STEAMREMOTESTORAGE_INTERFACE_VERSION006";
    auto *result = reinterpret_cast<Getter>(slots[slot])(client, 1, 1, version);
    CHECK((result != nullptr) ==
          (slot == 8 || slot == 13 || slot == 15 || slot == 17));
  }
  reinterpret_cast<Nothing>(slots[19])(client);
  CHECK(reinterpret_cast<Integer>(slots[20])(client) == 0);
  reinterpret_cast<Hook>(slots[21])(client, nullptr);
  CHECK(!reinterpret_cast<Boolean>(slots[22])(client));
  return 0;
}

using Factory = void *(__cdecl *)(const char *);
using FindUser = void *(__cdecl *)(int, const char *);
using FlatId = std::uint64_t(__cdecl *)(void *);
using Accessor = void *(__cdecl *)();
extern "C" __declspec(dllexport) int __cdecl
probe_factories(Factory create, FindUser find, FlatId flat, Accessor classic,
                std::uint64_t expected) {
  auto *user = create("SteamUser016");
  CHECK(user != nullptr && user == find(1, "SteamUser016"));
  CHECK(user == classic());
  CHECK(create("SteamUser999") == nullptr);
  CHECK(find(1, "SteamUser016suffix") == nullptr);
  CHECK(flat(user) == expected);
  CHECK(probe_client012(create("SteamClient012"), user) == 0);
  return probe_user016(static_cast<User016 *>(user), expected);
}
