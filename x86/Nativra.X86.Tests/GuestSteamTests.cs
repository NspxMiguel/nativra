using System;
using System.IO;
using System.Text;
using Nativra.X86.Cpu;
using Nativra.X86.Loader;
using Xunit;

namespace Nativra.X86.Tests
{
    /// <summary>
    /// steam_api.dll for 32-bit games: the exports, the classic interfaces
    /// called as MSVC code calls them (thiscall, this in ECX, callee pops),
    /// callbacks and call results delivered to guest C++ objects.
    /// </summary>
    public sealed class GuestSteamTests : IDisposable
    {
        private const uint Code = 0x00600000, Data = 0x00601000;
        private readonly string work = Path.Combine(Path.GetTempPath(), "nativra-steam-" + Guid.NewGuid().ToString("N"));
        private readonly GuestProcess p;
        private readonly GuestKernel k;
        private readonly MemorySteamAccount account = new MemorySteamAccount { SteamId = 76561198000000042, AppId = 562260, PersonaName = "Ada" };
        private readonly GuestSteam steam;

        public GuestSteamTests()
        {
            Directory.CreateDirectory(work);
            p = new GuestProcess(new GuestMemory(native: true), useJit: false);
            k = new GuestKernel(p) { ExePath = "C:\\game\\game.exe", Files = new HostFolderFiles("C:\\game", work) };
            k.Install();
            steam = new GuestSteam(p, k, account);
            steam.Install();
            p.Memory.Map(Code, 0x3000);
        }

        public void Dispose()
        {
            p.Dispose();
            try { Directory.Delete(work, true); } catch (IOException) { }
        }

        private uint Api(string function, params uint[] args)
        {
            var result = p.Call(p.Imports.Bind("steam_api.dll", function, -1), out var eax, 10_000_000, args);
            Assert.True(result.Ok, $"{function} stopped as {result}");
            return eax;
        }

        /// <summary>Calls vtable slot <paramref name="slot"/> of <paramref name="obj"/> as thiscall.</summary>
        private GuestRunResult Method(uint obj, int slot, out uint eax, params uint[] args)
        {
            p.Cpu.Ecx = obj;
            return p.Call(p.Memory.Read32(p.Memory.Read32(obj) + (uint)slot * 4), out eax, 10_000_000, args);
        }

        private uint M(uint obj, int slot, params uint[] args)
        {
            var esp = p.Cpu.Esp;
            var result = Method(obj, slot, out var eax, args);
            Assert.True(result.Ok, $"slot {slot} stopped as {result}");
            Assert.Equal(esp, p.Cpu.Esp);   // the callee popped exactly its arguments
            return eax;
        }

        private uint Str(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            var at = k.Heap.Alloc((uint)bytes.Length + 1, zero: true);
            p.Memory.WriteBytes(at, bytes);
            return at;
        }

        [Fact]
        public void InitSafeThenInterfacesByVersionAnswerWithTheAccount()
        {
            Assert.Equal(0u, Api("SteamAPI_RestartAppIfNecessary", 562260));
            Assert.Equal(1u, Api("Steamworks_InitCEGLibrary"));
            Assert.Equal(1u, Api("SteamAPI_InitSafe"));
            var client = Api("SteamClient");
            var user = M(client, 5, 1, 1, Str("SteamUser019"));        // GetISteamUser(user, pipe, version)
            var utils = M(client, 9, 1, Str("SteamUtils009"));          // GetISteamUtils(pipe, version)
            var friends = M(client, 8, 1, 1, Str("SteamFriends015"));
            Assert.NotEqual(0u, user);
            Assert.Contains("SteamUser019", steam.Versions);

            var id = k.Heap.Alloc(8);
            Assert.Equal(id, M(user, 2, id));                           // GetSteamID: hidden result pointer
            Assert.Equal(account.SteamId, p.Memory.Read64(id));
            Assert.Equal(1u, M(user, 1));                                // BLoggedOn
            Assert.Equal(562260u, M(utils, 9));                          // GetAppID
            Assert.Equal("Ada", p.Memory.ReadAnsi(M(friends, 0)));       // GetPersonaName

            var apps = Api("SteamApps");
            Assert.Equal(1u, M(apps, 6, 562260));                        // BIsSubscribedApp: this game
            Assert.Equal(0u, M(apps, 6, 440));                           // and no other
        }

        [Fact]
        public void StatsAndTheUserStatsReceivedCallbackReachTheGuestObject()
        {
            // Callback object: vptr at +0, flags at +4, id at +8. Run(void*) is slot 1:
            // mov eax,[esp+4]; mov eax,[eax+8]; mov [0x601000],eax; ret 4
            p.Memory.WriteBytes(Code, new byte[] { 0x8B, 0x44, 0x24, 0x04, 0x8B, 0x40, 0x08, 0xA3, 0x00, 0x10, 0x60, 0x00, 0xC2, 0x04, 0x00 });
            var vtable = Data + 0x100;
            p.Memory.Write32(vtable + 4, Code);
            var callback = Data + 0x200;
            p.Memory.Write32(callback, vtable);
            Api("SteamAPI_RegisterCallback", callback, 1101);
            Assert.Equal(1101u, p.Memory.Read32(callback + 8));

            var stats = Api("SteamUserStats");
            Assert.Equal(1u, M(stats, 0));                                               // RequestCurrentStats
            Api("SteamAPI_RunCallbacks");
            Assert.Equal(1u, p.Memory.Read32(Data));                                     // EResult k_EResultOK

            Assert.Equal(1u, M(stats, 3, Str("distance"), (uint)BitConverter.SingleToInt32Bits(2.5f)));   // SetStat(float): slot 3
            Assert.Equal(1u, M(stats, 4, Str("deaths"), 7));                                               // SetStat(int32): slot 4
            Assert.Equal(2.5f, account.Floats["distance"]);
            Assert.Equal(7, account.Ints["deaths"]);
            var value = k.Heap.Alloc(4);
            M(stats, 2, Str("deaths"), value);                                                             // GetStat(int32): slot 2
            Assert.Equal(7u, p.Memory.Read32(value));
            Assert.Equal(1u, M(stats, 7, Str("ACH_WIN")));                                                  // SetAchievement
            Assert.True(account.Achievements.ContainsKey("ACH_WIN"));
            var got = k.Heap.Alloc(1);
            M(stats, 6, Str("ACH_WIN"), got);
            Assert.Equal(1, p.Memory.Read8(got));
            M(stats, 10);                                                                                  // StoreStats
            Assert.Equal(1, account.Stored);
        }

        [Fact]
        public void LeaderboardCallResultIsDeliveredToItsListener()
        {
            // Run(void*, bool, SteamAPICall_t) is slot 0: store the board handle's low half; ret 16
            p.Memory.WriteBytes(Code, new byte[] { 0x8B, 0x44, 0x24, 0x04, 0x8B, 0x00, 0xA3, 0x04, 0x10, 0x60, 0x00, 0xC2, 0x10, 0x00 });
            var vtable = Data + 0x100;
            p.Memory.Write32(vtable, Code);
            var listener = Data + 0x200;
            p.Memory.Write32(listener, vtable);

            var stats = Api("SteamUserStats");
            Method(stats, 22, out var low, Str("High Scores"), 2, 1);   // FindOrCreateLeaderboard -> SteamAPICall_t
            var call = ((ulong)p.Cpu.Edx << 32) | low;
            Assert.NotEqual(0UL, call);
            Api("SteamAPI_RegisterCallResult", listener, (uint)call, (uint)(call >> 32));
            Api("SteamAPI_RunCallbacks");
            var board = p.Memory.Read32(Data + 4);
            Assert.NotEqual(0u, board);
            Assert.Equal("High Scores", p.Memory.ReadAnsi(M(stats, 24, board, 0)));   // GetLeaderboardName
        }

        [Fact]
        public void CloudFilesLiveInTheGuestProfile()
        {
            var storage = Api("SteamRemoteStorage");
            var data = Str("save-bytes");
            Assert.Equal(1u, M(storage, 0, Str("slot1.sav"), data, 10));   // FileWrite
            Assert.Equal(1u, M(storage, 13, Str("slot1.sav")));             // FileExists
            Assert.Equal(10u, M(storage, 15, Str("slot1.sav")));            // GetFileSize
            var buffer = k.Heap.Alloc(16, zero: true);
            Assert.Equal(10u, M(storage, 1, Str("slot1.sav"), buffer, 16)); // FileRead
            Assert.Equal("save-bytes", p.Memory.ReadAnsi(buffer));
            Assert.Equal(1u, M(storage, 18));                                // GetFileCount
            Assert.True(File.Exists(Path.Combine(work, "nativra-user", "AppData", "Local", "Steam", "remote", "562260", "slot1.sav")));
        }

        [Fact]
        public void AnUnknownSlotStopsWithItsName()
        {
            var screenshots = Api("SteamScreenshots");
            var result = Method(screenshots, 0, out _);
            Assert.Equal(GuestStop.HostError, result.Stop);
            Assert.Contains("SteamScreenshots", result.ToString());
            Assert.Contains("slot 0", result.ToString());
        }
    }
}
