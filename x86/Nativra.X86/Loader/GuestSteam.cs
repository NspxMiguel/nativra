using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Nativra.X86.Cpu;

namespace Nativra.X86.Loader
{
    /// <summary>What the signed-in Steam account says, for the guest's steam_api.dll.</summary>
    public interface IGuestSteamAccount
    {
        ulong SteamId { get; }
        uint AppId { get; }
        string PersonaName { get; }
        string Language { get; }
        string Country { get; }
        bool TryGetAchievement(string name, out uint unlockTime);
        void SetAchievement(string name);
        void ClearAchievement(string name);
        bool TryGetStat(string name, out int value);
        void SetStat(string name, int value);
        bool TryGetStat(string name, out float value);
        void SetStat(string name, float value);
        /// <summary>The game asked for its stats and achievements to reach Steam.</summary>
        void StoreStats();
    }

    /// <summary>An account kept in memory: the tests' backend, and a template for the app's.</summary>
    public sealed class MemorySteamAccount : IGuestSteamAccount
    {
        public ulong SteamId { get; set; } = 76561197960287930;
        public uint AppId { get; set; } = 480;
        public string PersonaName { get; set; } = "Player";
        public string Language { get; set; } = "english";
        public string Country { get; set; } = "US";
        public readonly Dictionary<string, uint> Achievements = new Dictionary<string, uint>();
        public readonly Dictionary<string, int> Ints = new Dictionary<string, int>();
        public readonly Dictionary<string, float> Floats = new Dictionary<string, float>();
        public int Stored;
        public bool TryGetAchievement(string name, out uint unlockTime) => Achievements.TryGetValue(name, out unlockTime);
        public void SetAchievement(string name) { if (!Achievements.ContainsKey(name)) Achievements[name] = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds(); }
        public void ClearAchievement(string name) => Achievements.Remove(name);
        public bool TryGetStat(string name, out int value) => Ints.TryGetValue(name, out value);
        public void SetStat(string name, int value) => Ints[name] = value;
        public bool TryGetStat(string name, out float value) => Floats.TryGetValue(name, out value);
        public void SetStat(string name, float value) => Floats[name] = value;
        public void StoreStats() => Stored++;
    }

    /// <summary>
    /// steam_api.dll for 32-bit games, answered on the host the way the Steam
    /// client would answer a game the signed-in account owns, as the 64-bit
    /// side's SteamBridge/SteamClassic do. The game's own steam_api.dll is not
    /// mapped: its exports are handlers here, and the classic C++ interfaces
    /// (ISteamUser, ISteamFriends, ISteamUtils, ISteamApps, ISteamUserStats,
    /// ISteamRemoteStorage, ISteamClient) are guest objects whose vtables are
    /// thiscall sentinels, laid out per the version string the game asks for,
    /// with MSVC's reversed order for same-name overloads.
    ///
    /// A thiscall callee pops its arguments, so a slot served with the wrong
    /// argument count would corrupt the game's stack: a slot outside the
    /// known layouts, or an interface this does not serve, stops the run with
    /// its interface and slot named instead. Nothing here touches DRM: the
    /// CEG entry point only says the library started, as it does under Steam.
    /// Leaderboards are local (the console has no Steam network session for
    /// the game): the player's own scores, no one else's.
    /// </summary>
    public sealed class GuestSteam
    {
        private const string Api = "steam_api.dll";
        private const string Objects = "nativra-steam.dll";
        private const int Slots = 128;
        private const uint HSteamUser = 1, HSteamPipe = 1;

        private readonly GuestProcess process;
        private readonly GuestKernel kernel;
        private readonly GuestMemory memory;
        private readonly IGuestSteamAccount account;
        private readonly Dictionary<string, uint> objects = new Dictionary<string, uint>(StringComparer.Ordinal);
        private readonly Dictionary<string, uint> strings = new Dictionary<string, uint>(StringComparer.Ordinal);
        private readonly List<uint> callbacks = new List<uint>();
        private readonly Queue<KeyValuePair<int, byte[]>> posted = new Queue<KeyValuePair<int, byte[]>>();
        private readonly Dictionary<ulong, KeyValuePair<int, byte[]>> results = new Dictionary<ulong, KeyValuePair<int, byte[]>>();
        private readonly Dictionary<ulong, uint> resultListeners = new Dictionary<ulong, uint>();
        private readonly Dictionary<ulong, string> boards = new Dictionary<ulong, string>();
        private readonly Dictionary<ulong, int> bestScores = new Dictionary<ulong, int>();
        private readonly Dictionary<ulong, ulong> entrySets = new Dictionary<ulong, ulong>();
        private readonly Dictionary<ulong, byte[]> asyncReads = new Dictionary<ulong, byte[]>();
        private ulong nextCall = 0x1000;

        /// <summary>Interface versions the game asked for, in order: what the report shows.</summary>
        public List<string> Versions { get; } = new List<string>();

        /// <summary>Calls answered, by interface and method.</summary>
        public Dictionary<string, long> Calls { get; } = new Dictionary<string, long>();

        /// <summary>Where Steam Cloud files live, as a guest path.</summary>
        public string CloudFolder { get; set; }

        public GuestSteam(GuestProcess process, GuestKernel kernel, IGuestSteamAccount account)
        {
            this.process = process;
            this.kernel = kernel;
            memory = process.Memory;
            this.account = account;
            CloudFolder = GuestKernel.GuestProfile + "\\AppData\\Local\\Steam\\remote\\" + account.AppId;
        }

        private void Count(string name)
        {
            Calls.TryGetValue(name, out var n);
            Calls[name] = n + 1;
        }

        private uint Text(string text)
        {
            text = text ?? "";
            if (strings.TryGetValue(text, out var at)) return at;
            var bytes = Encoding.UTF8.GetBytes(text);
            at = kernel.Heap.Alloc((uint)bytes.Length + 1, zero: true);
            memory.WriteBytes(at, bytes);
            strings[text] = at;
            return at;
        }

        private string Read(uint p)
        {
            if (p == 0) return "";
            var bytes = new List<byte>();
            for (var b = memory.Read8(p); b != 0; b = memory.Read8(++p)) bytes.Add(b);
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        private void WriteString(uint buffer, uint capacity, string text)
        {
            if (buffer == 0 || capacity == 0) return;
            var bytes = Encoding.UTF8.GetBytes(text ?? "");
            var n = (int)Math.Min((uint)bytes.Length, capacity - 1);
            memory.WriteBytes(buffer, bytes, 0, n);
            memory.Write8(buffer + (uint)n, 0);
        }

        // --- installation -----------------------------------------------------------

        public void Install()
        {
            var i = process.Imports;
            void E(string name, int args, HostCall body)
            {
                i.Register(Api, name, CallConv.Cdecl, args, c => { Count(name); return body(c); });
            }
            E("SteamAPI_Init", 0, c => 1);
            E("SteamAPI_InitSafe", 0, c => 1);
            E("SteamAPI_Shutdown", 0, c => 0);
            E("SteamAPI_RestartAppIfNecessary", 1, c => 0);   // already running as that app
            E("SteamAPI_IsSteamRunning", 0, c => 1);
            E("SteamAPI_GetHSteamUser", 0, c => HSteamUser);
            E("SteamAPI_GetHSteamPipe", 0, c => HSteamPipe);
            E("SteamAPI_RunCallbacks", 0, c => { RunCallbacks(); return 0; });
            E("SteamAPI_RegisterCallback", 2, c => { RegisterCallback(c.Arg(0), (int)c.Arg(1)); return 0; });
            E("SteamAPI_UnregisterCallback", 1, c => { UnregisterCallback(c.Arg(0)); return 0; });
            E("SteamAPI_RegisterCallResult", 3, c => { if (c.Arg(0) != 0) resultListeners[c.Arg64(1)] = c.Arg(0); return 0; });
            E("SteamAPI_UnregisterCallResult", 3, c => { resultListeners.Remove(c.Arg64(1)); return 0; });
            E("SteamAPI_SetMiniDumpComment", 1, c => 0);
            E("SteamAPI_WriteMiniDump", 3, c => 0);
            E("SteamAPI_UseBreakpadCrashHandler", 6, c => 0);
            E("SteamAPI_SetBreakpadAppID", 1, c => 0);
            E("SteamAPI_SetTryCatchCallbacks", 1, c => 0);
            E("SteamAPI_ReleaseCurrentThreadMemory", 0, c => 0);
            E("SteamAPI_GetSteamInstallPath", 0, c => Text("C:\\Program Files (x86)\\Steam"));
            // The CEG library's start-up call: it reports that it started, as under
            // the Steam client. No check of the executable is made or skipped here.
            E("Steamworks_InitCEGLibrary", 0, c => 1);
            E("Steamworks_TermCEGLibrary", 0, c => 1);
            E("Steamworks_RegisterThread", 0, c => 1);

            E("SteamClient", 0, c => Interface("SteamClient017"));
            E("SteamUser", 0, c => Interface("SteamUser019"));
            E("SteamFriends", 0, c => Interface("SteamFriends015"));
            E("SteamUtils", 0, c => Interface("SteamUtils009"));
            E("SteamApps", 0, c => Interface("STEAMAPPS_INTERFACE_VERSION008"));
            E("SteamUserStats", 0, c => Interface("STEAMUSERSTATS_INTERFACE_VERSION011"));
            E("SteamRemoteStorage", 0, c => Interface("STEAMREMOTESTORAGE_INTERFACE_VERSION014"));
            foreach (var other in new[] { "SteamScreenshots", "SteamHTTP", "SteamMatchmaking", "SteamMatchmakingServers", "SteamNetworking",
                                          "SteamUGC", "SteamController", "SteamMusic", "SteamMusicRemote", "SteamInventory", "SteamVideo",
                                          "SteamAppList", "SteamHTMLSurface", "SteamParentalSettings", "SteamUnifiedMessages" })
            {
                var name = other;
                E(name, 0, c => Interface(name));
            }
            E("SteamInternal_CreateInterface", 1, c => Interface(Read(c.Arg(0))));
            E("SteamInternal_FindOrCreateUserInterface", 2, c => Interface(Read(c.Arg(1))));
            E("SteamInternal_FindOrCreateGameServerInterface", 2, c => 0);
            E("SteamInternal_ContextInit", 1, c =>
            {
                // { void (*init)(void* context); uintptr counter; context... }: the SDK's
                // inline accessors fill their context once, through the game's own init.
                var p = c.Arg(0);
                if (memory.Read32(p + 4) != 1)
                {
                    memory.Write32(p + 4, 1);
                    kernel.CallGuestCdecl(memory.Read32(p), p + 8);
                }
                return p + 8;
            });
            E("SteamGameServer_Init", 6, c => 0);
            E("SteamGameServer_Shutdown", 0, c => 0);
            E("SteamGameServer_RunCallbacks", 0, c => 0);
        }

        // --- callbacks and call results --------------------------------------------------
        // CCallbackBase (x86): vptr, uint8 flags at +4, int callback id at +8. Its
        // vtable, overloads reversed: [0] Run(void*, bool, SteamAPICall_t),
        // [1] Run(void*), [2] GetCallbackSizeBytes().

        private void RegisterCallback(uint callback, int id)
        {
            if (callback == 0) return;
            memory.Write8(callback + 4, (byte)(memory.Read8(callback + 4) | 1));
            memory.Write32(callback + 8, (uint)id);
            if (!callbacks.Contains(callback)) callbacks.Add(callback);
        }

        private void UnregisterCallback(uint callback)
        {
            if (callback == 0) return;
            memory.Write8(callback + 4, (byte)(memory.Read8(callback + 4) & ~1));
            callbacks.Remove(callback);
        }

        private void Post(int id, byte[] payload) => posted.Enqueue(new KeyValuePair<int, byte[]>(id, payload));

        private ulong Result(int id, byte[] payload)
        {
            var call = nextCall++;
            results[call] = new KeyValuePair<int, byte[]>(id, payload);
            return call;
        }

        private void RunCallbacks()
        {
            while (posted.Count > 0)
            {
                var message = posted.Dequeue();
                var buffer = kernel.Heap.Alloc((uint)Math.Max(1, message.Value.Length));
                memory.WriteBytes(buffer, message.Value);
                foreach (var callback in callbacks.ToArray())
                {
                    if ((int)memory.Read32(callback + 8) != message.Key) continue;
                    kernel.CallGuestThis(memory.Read32(memory.Read32(callback) + 4), callback, buffer);
                }
                kernel.Heap.Free(buffer);
            }
            foreach (var call in new List<ulong>(resultListeners.Keys))
            {
                if (!results.TryGetValue(call, out var result) || !resultListeners.TryGetValue(call, out var listener)) continue;
                resultListeners.Remove(call);
                var buffer = kernel.Heap.Alloc((uint)Math.Max(1, result.Value.Length));
                memory.WriteBytes(buffer, result.Value);
                kernel.CallGuestThis(memory.Read32(memory.Read32(listener)), listener, buffer, 0, (uint)call, (uint)(call >> 32));
                kernel.Heap.Free(buffer);
            }
        }

        // --- interfaces ---------------------------------------------------------------

        private delegate ulong Method(GuestCall c);

        /// <summary>A layout entry: name and stack argument DWORDs (after this); 'S' returns a class through a hidden pointer, 'Q' a 64-bit value.</summary>
        private struct Slot
        {
            public string Name;
            public int Args;
            public char Kind;
        }

        private static Slot[] Layout(params string[] spec)
        {
            var slots = new Slot[spec.Length];
            for (var n = 0; n < spec.Length; n++)
            {
                var parts = spec[n].Split(':');
                slots[n] = new Slot { Name = parts[0], Args = int.Parse(parts[1], CultureInfo.InvariantCulture), Kind = parts.Length > 2 ? parts[2][0] : ' ' };
                if (slots[n].Kind == 'S') slots[n].Args++;   // the hidden result pointer
            }
            return slots;
        }

        private static int VersionNumber(string version)
        {
            var n = version.Length;
            while (n > 0 && char.IsDigit(version[n - 1])) n--;
            return n < version.Length ? int.Parse(version.Substring(n), CultureInfo.InvariantCulture) : 0;
        }

        /// <summary>The guest object for an interface version, built once.</summary>
        private uint Interface(string version)
        {
            if (objects.TryGetValue(version, out var known)) return known;
            Versions.Add(version);
            kernel.Log?.Invoke("x86: steam interface " + version);

            Slot[] layout;
            Dictionary<string, Method> methods;
            string family;
            if (version.StartsWith("SteamClient", StringComparison.Ordinal)) { family = "ISteamClient"; layout = ClientLayout; methods = ClientMethods(); }
            else if (version.StartsWith("SteamUser0", StringComparison.Ordinal)) { family = "ISteamUser"; layout = UserLayout; methods = UserMethods(); }
            else if (version.StartsWith("SteamFriends", StringComparison.Ordinal)) { family = "ISteamFriends"; layout = FriendsLayout(VersionNumber(version)); methods = FriendsMethods(); }
            else if (version.StartsWith("SteamUtils", StringComparison.Ordinal)) { family = "ISteamUtils"; layout = UtilsLayout; methods = UtilsMethods(); }
            else if (version.StartsWith("STEAMAPPS_INTERFACE_VERSION", StringComparison.Ordinal)) { family = "ISteamApps"; layout = AppsLayout; methods = AppsMethods(); }
            else if (version.StartsWith("STEAMUSERSTATS_INTERFACE_VERSION", StringComparison.Ordinal)) { family = "ISteamUserStats"; layout = StatsLayout; methods = StatsMethods(); }
            else if (version.StartsWith("STEAMREMOTESTORAGE_INTERFACE_VERSION", StringComparison.Ordinal)) { family = "ISteamRemoteStorage"; layout = RemoteLayout; methods = RemoteMethods(VersionNumber(version)); }
            else { family = version; layout = new Slot[0]; methods = new Dictionary<string, Method>(); }

            var table = kernel.Heap.Alloc(Slots * 4, zero: true);
            for (var n = 0; n < Slots; n++)
            {
                var key = $"{version}#{n}";
                if (n < layout.Length)
                {
                    var slot = layout[n];
                    var label = family + "::" + slot.Name;
                    methods.TryGetValue(slot.Name, out var body);
                    var kind = slot.Kind;
                    var name = slot.Name;
                    process.Imports.Register(Objects, key, CallConv.Thiscall, slot.Args, c =>
                    {
                        Count(label);
                        if (body != null) return body(c);
                        return Default(c, kind, name);
                    });
                }
                else
                {
                    var where = $"{family} ({version}) slot {n}";
                    process.Imports.Register(Objects, key, CallConv.Thiscall, 0, c =>
                        throw new NotSupportedException("Steam " + where + " is not served yet"));
                }
                memory.Write32(table + (uint)n * 4, process.Imports.Bind(Objects, key, -1));
            }
            var obj = kernel.Heap.Alloc(16, zero: true);
            memory.Write32(obj, table);
            objects[version] = obj;
            return obj;
        }

        /// <summary>What an unserved method answers: nothing, false, an empty string, or a zero CSteamID.</summary>
        private ulong Default(GuestCall c, char kind, string name)
        {
            if (kind == 'S')
            {
                memory.Write64(c.Arg(0), 0);
                return c.Arg(0);
            }
            if (name.StartsWith("Get", StringComparison.Ordinal) &&
                (name.EndsWith("Name", StringComparison.Ordinal) || name.EndsWith("Language", StringComparison.Ordinal) ||
                 name.EndsWith("Country", StringComparison.Ordinal) || name.Contains("RichPresence") || name.EndsWith("Tag", StringComparison.Ordinal)))
                return Text("");
            return 0;
        }

        private string Name(GuestCall c, int arg) => Read(c.Arg(arg));

        // --- ISteamClient -------------------------------------------------------------

        private static readonly Slot[] ClientLayout = Layout(
            "CreateSteamPipe:0", "BReleaseSteamPipe:1", "ConnectToGlobalUser:1", "CreateLocalUser:2", "ReleaseUser:2",
            "GetISteamUser:3", "GetISteamGameServer:3", "SetLocalIPBinding:2", "GetISteamFriends:3", "GetISteamUtils:2",
            "GetISteamMatchmaking:3", "GetISteamMatchmakingServers:3", "GetISteamGenericInterface:3", "GetISteamUserStats:3",
            "GetISteamGameServerStats:3", "GetISteamApps:3", "GetISteamNetworking:3", "GetISteamRemoteStorage:3",
            "GetISteamScreenshots:3", "RunFrame:0", "GetIPCCallCount:0", "SetWarningMessageHook:1", "BShutdownIfAllPipesClosed:0",
            "GetISteamHTTP:3", "GetISteamUnifiedMessages:3", "GetISteamController:3", "GetISteamUGC:3", "GetISteamAppList:3",
            "GetISteamMusic:3", "GetISteamMusicRemote:3", "GetISteamHTMLSurface:3", "Set_SteamAPI_CPostAPIResultInProcess:1",
            "Remove_SteamAPI_CPostAPIResultInProcess:1", "Set_SteamAPI_CCheckCallbackRegisteredInProcess:1",
            "GetISteamInventory:3", "GetISteamVideo:3", "GetISteamParentalSettings:3");

        private Dictionary<string, Method> ClientMethods()
        {
            var m = new Dictionary<string, Method>
            {
                ["CreateSteamPipe"] = c => HSteamPipe,
                ["BReleaseSteamPipe"] = c => 1,
                ["ConnectToGlobalUser"] = c => HSteamUser,
                ["CreateLocalUser"] = c => { if (c.Arg(0) != 0) memory.Write32(c.Arg(0), HSteamPipe); return HSteamUser; },
                ["GetISteamUtils"] = c => Interface(Read(c.Arg(1))),
                ["BShutdownIfAllPipesClosed"] = c => 0,
            };
            foreach (var slot in ClientLayout)
                if (slot.Name.StartsWith("GetISteam", StringComparison.Ordinal) && slot.Name != "GetISteamUtils")
                    m[slot.Name] = slot.Name == "GetISteamGameServer" || slot.Name == "GetISteamGameServerStats"
                        ? (Method)(c => 0)
                        : c => Interface(Read(c.Arg(2)));
            return m;
        }

        // --- ISteamUser (016-019) ------------------------------------------------------

        private static readonly Slot[] UserLayout = Layout(
            "GetHSteamUser:0", "BLoggedOn:0", "GetSteamID:0:S", "InitiateGameConnection:7", "TerminateGameConnection:2",
            "TrackAppUsageEvent:4", "GetUserDataFolder:2", "StartVoiceRecording:0", "StopVoiceRecording:0", "GetAvailableVoice:3",
            "GetVoice:9", "DecompressVoice:6", "GetVoiceOptimalSampleRate:0", "GetAuthSessionTicket:3", "BeginAuthSession:4",
            "EndAuthSession:2", "CancelAuthTicket:1", "UserHasLicenseForApp:3", "BIsBehindNAT:0", "AdvertiseGame:4",
            "RequestEncryptedAppTicket:2:Q", "GetEncryptedAppTicket:3", "GetGameBadgeLevel:2", "GetPlayerSteamLevel:0",
            "RequestStoreAuthURL:1:Q", "BIsPhoneVerified:0", "BIsTwoFactorEnabled:0", "BIsPhoneIdentifying:0",
            "BIsPhoneRequiringVerification:0");

        private Dictionary<string, Method> UserMethods() => new Dictionary<string, Method>
        {
            ["GetHSteamUser"] = c => HSteamUser,
            ["BLoggedOn"] = c => 1,
            ["GetSteamID"] = c => { memory.Write64(c.Arg(0), account.SteamId); return c.Arg(0); },
            ["GetUserDataFolder"] = c =>
            {
                WriteString(c.Arg(0), c.Arg(1), GuestKernel.GuestProfile + "\\AppData\\Local\\Steam\\userdata\\" + account.AppId);
                return 1;
            },
            ["GetAvailableVoice"] = c => 2,   // k_EVoiceResultNotRecording
            ["GetVoice"] = c => 2,
            ["DecompressVoice"] = c => 2,
            ["UserHasLicenseForApp"] = c => c.Arg(2) == account.AppId ? 0u : 1u,   // HasLicense / DoesNotHaveLicense
            ["GetPlayerSteamLevel"] = c => 1,
        };

        // --- ISteamFriends (014-017) ----------------------------------------------------

        private static Slot[] FriendsLayout(int version) => Layout(
            "GetPersonaName:0", "SetPersonaName:1:Q", "GetPersonaState:0", "GetFriendCount:1", "GetFriendByIndex:2:S",
            "GetFriendRelationship:2", "GetFriendPersonaState:2", "GetFriendPersonaName:2", "GetFriendGamePlayed:3",
            "GetFriendPersonaNameHistory:3", "GetFriendSteamLevel:2", "GetPlayerNickname:2", "GetFriendsGroupCount:0",
            "GetFriendsGroupIDByIndex:1", "GetFriendsGroupName:1", "GetFriendsGroupMembersCount:1", "GetFriendsGroupMembersList:3",
            "HasFriend:3", "GetClanCount:0", "GetClanByIndex:1:S", "GetClanName:2", "GetClanTag:2", "GetClanActivityCounts:5",
            "DownloadClanActivityCounts:2:Q", "GetFriendCountFromSource:2", "GetFriendFromSourceByIndex:3:S", "IsUserInSource:4",
            "SetInGameVoiceSpeaking:3", "ActivateGameOverlay:1", "ActivateGameOverlayToUser:3",
            version >= 16 ? "ActivateGameOverlayToWebPage:2" : "ActivateGameOverlayToWebPage:1",
            "ActivateGameOverlayToStore:2", "SetPlayedWith:2", "ActivateGameOverlayInviteDialog:2", "GetSmallFriendAvatar:2",
            "GetMediumFriendAvatar:2", "GetLargeFriendAvatar:2", "RequestUserInformation:3", "RequestClanOfficerList:2:Q",
            "GetClanOwner:2:S", "GetClanOfficerCount:2", "GetClanOfficerByIndex:3:S", "GetUserRestrictions:0", "SetRichPresence:2",
            "ClearRichPresence:0", "GetFriendRichPresence:3", "GetFriendRichPresenceKeyCount:2", "GetFriendRichPresenceKeyByIndex:3",
            "RequestFriendRichPresence:2", "InviteUserToGame:3", "GetCoplayFriendCount:0", "GetCoplayFriend:1:S",
            "GetFriendCoplayTime:2", "GetFriendCoplayGame:2", "JoinClanChatRoom:2:Q", "LeaveClanChatRoom:2", "GetClanChatMemberCount:2",
            "GetChatMemberByIndex:3:S", "SendClanChatMessage:3", "GetClanChatMessage:7", "IsClanChatAdmin:4",
            "IsClanChatWindowOpenInSteam:2", "OpenClanChatWindowInSteam:2", "CloseClanChatWindowInSteam:2",
            "SetListenForFriendsMessages:1", "ReplyToFriendMessage:3", "GetFriendMessage:6", "GetFollowerCount:2:Q",
            "IsFollowing:2:Q", "EnumerateFollowingList:1:Q", "IsClanPublic:2", "IsClanOfficialGameGroup:2");

        private Dictionary<string, Method> FriendsMethods() => new Dictionary<string, Method>
        {
            ["GetPersonaName"] = c => Text(account.PersonaName),
            ["GetPersonaState"] = c => 1,   // online
            ["GetFriendPersonaName"] = c => Text(((ulong)c.Arg(1) << 32 | c.Arg(0)) == account.SteamId ? account.PersonaName : ""),
            ["GetFriendPersonaState"] = c => ((ulong)c.Arg(1) << 32 | c.Arg(0)) == account.SteamId ? 1u : 0u,
            ["SetRichPresence"] = c => 1,
            ["RequestUserInformation"] = c => 0,   // already known
        };

        // --- ISteamUtils (007-009) -------------------------------------------------------

        private static readonly Slot[] UtilsLayout = Layout(
            "GetSecondsSinceAppActive:0", "GetSecondsSinceComputerActive:0", "GetConnectedUniverse:0", "GetServerRealTime:0",
            "GetIPCountry:0", "GetImageSize:3", "GetImageRGBA:3", "GetCSERIPPort:2", "GetCurrentBatteryPower:0", "GetAppID:0",
            "SetOverlayNotificationPosition:1", "IsAPICallCompleted:3", "GetAPICallFailureReason:2", "GetAPICallResult:6",
            "RunFrame:0", "GetIPCCallCount:0", "SetWarningMessageHook:1", "IsOverlayEnabled:0", "BOverlayNeedsPresent:0",
            "CheckFileSignature:1:Q", "ShowGamepadTextInput:5", "GetEnteredGamepadTextLength:0", "GetEnteredGamepadTextInput:2",
            "GetSteamUILanguage:0", "IsSteamRunningInVR:0", "SetOverlayNotificationInset:2", "IsSteamInBigPictureMode:0",
            "StartVRDashboard:0", "IsVRHeadsetStreamingEnabled:0", "SetVRHeadsetStreamingEnabled:1");

        private readonly System.Diagnostics.Stopwatch uptime = System.Diagnostics.Stopwatch.StartNew();

        private Dictionary<string, Method> UtilsMethods() => new Dictionary<string, Method>
        {
            ["GetSecondsSinceAppActive"] = c => (uint)uptime.Elapsed.TotalSeconds,
            ["GetSecondsSinceComputerActive"] = c => (uint)uptime.Elapsed.TotalSeconds,
            ["GetConnectedUniverse"] = c => 1,   // public
            ["GetServerRealTime"] = c => (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["GetIPCountry"] = c => Text(account.Country),
            ["GetCurrentBatteryPower"] = c => 255,   // on AC power
            ["GetAppID"] = c => account.AppId,
            ["IsAPICallCompleted"] = c =>
            {
                var done = results.ContainsKey(c.Arg64(0));
                if (c.Arg(2) != 0) memory.Write8(c.Arg(2), 0);
                return done ? 1u : 0u;
            },
            ["GetAPICallFailureReason"] = c => results.ContainsKey(c.Arg64(0)) ? 0xFFFFFFFF : 2u,   // none / invalid handle
            ["GetAPICallResult"] = c =>
            {
                // (call, buffer, size, expected callback, failed*)
                if (c.Arg(5) != 0) memory.Write8(c.Arg(5), 0);
                if (!results.TryGetValue(c.Arg64(0), out var r) || r.Key != (int)c.Arg(4)) return 0;
                memory.WriteBytes(c.Arg(2), r.Value, 0, Math.Min(r.Value.Length, (int)c.Arg(3)));
                results.Remove(c.Arg64(0));
                return 1;
            },
            ["IsOverlayEnabled"] = c => 0,
            ["GetSteamUILanguage"] = c => Text(account.Language),
        };

        // --- ISteamApps (006-008) ---------------------------------------------------------

        private static readonly Slot[] AppsLayout = Layout(
            "BIsSubscribed:0", "BIsLowViolence:0", "BIsCybercafe:0", "BIsVACBanned:0", "GetCurrentGameLanguage:0",
            "GetAvailableGameLanguages:0", "BIsSubscribedApp:1", "BIsDlcInstalled:1", "GetEarliestPurchaseUnixTime:1",
            "BIsSubscribedFromFreeWeekend:0", "GetDLCCount:0", "BGetDLCDataByIndex:5", "InstallDLC:1", "UninstallDLC:1",
            "RequestAppProofOfPurchaseKey:1", "GetCurrentBetaName:2", "MarkContentCorrupt:1", "GetInstalledDepots:3",
            "GetAppInstallDir:3", "BIsAppInstalled:1", "GetAppOwner:0:S", "GetLaunchQueryParam:1", "GetDlcDownloadProgress:3",
            "GetAppBuildId:0", "RequestAllProofOfPurchaseKeys:0", "GetFileDetails:1:Q");

        private Dictionary<string, Method> AppsMethods() => new Dictionary<string, Method>
        {
            // Ownership only ever for the one game running under the account's own license.
            ["BIsSubscribed"] = c => 1,
            ["GetCurrentGameLanguage"] = c => Text(account.Language),
            ["GetAvailableGameLanguages"] = c => Text(account.Language),
            ["BIsSubscribedApp"] = c => c.Arg(0) == account.AppId ? 1u : 0u,
            ["BIsAppInstalled"] = c => c.Arg(0) == account.AppId ? 1u : 0u,
            ["GetAppOwner"] = c => { memory.Write64(c.Arg(0), account.SteamId); return c.Arg(0); },
            ["GetAppInstallDir"] = c =>
            {
                if (c.Arg(0) != account.AppId) return 0;
                var folder = kernel.ExePath.Substring(0, Math.Max(0, kernel.ExePath.LastIndexOf('\\')));
                WriteString(c.Arg(1), c.Arg(2), folder);
                return (uint)Encoding.UTF8.GetByteCount(folder);
            },
            ["GetLaunchQueryParam"] = c => Text(""),
        };

        // --- ISteamUserStats (010-011) ------------------------------------------------------
        // Same-name overloads are laid out in reverse declaration order by MSVC.

        private static readonly Slot[] StatsLayout = Layout(
            "RequestCurrentStats:0", "GetStatFloat:2", "GetStatInt:2", "SetStatFloat:2", "SetStatInt:2", "UpdateAvgRateStat:4",
            "GetAchievement:2", "SetAchievement:1", "ClearAchievement:1", "GetAchievementAndUnlockTime:3", "StoreStats:0",
            "GetAchievementIcon:1", "GetAchievementDisplayAttribute:2", "IndicateAchievementProgress:3", "GetNumAchievements:0",
            "GetAchievementName:1", "RequestUserStats:2:Q", "GetUserStatFloat:4", "GetUserStatInt:4", "GetUserAchievement:4",
            "GetUserAchievementAndUnlockTime:5", "ResetAllStats:1", "FindOrCreateLeaderboard:3:Q", "FindLeaderboard:1:Q",
            "GetLeaderboardName:2", "GetLeaderboardEntryCount:2", "GetLeaderboardSortMethod:2", "GetLeaderboardDisplayType:2",
            "DownloadLeaderboardEntries:5:Q", "DownloadLeaderboardEntriesForUsers:4:Q", "GetDownloadedLeaderboardEntry:6",
            "UploadLeaderboardScore:6:Q", "AttachLeaderboardUGC:4:Q", "GetNumberOfCurrentPlayers:0:Q",
            "RequestGlobalAchievementPercentages:0:Q", "GetMostAchievedAchievementInfo:4", "GetNextMostAchievedAchievementInfo:5",
            "GetAchievementAchievedPercent:2", "RequestGlobalStats:1:Q", "GetGlobalStatDouble:2", "GetGlobalStatInt64:2",
            "GetGlobalStatHistoryDouble:3", "GetGlobalStatHistoryInt64:3");

        private byte[] StatsReceived(ulong user)
        {
            // UserStatsReceived_t: game id, EResult, CSteamID (8-byte packing).
            var b = new byte[24];
            BitConverter.GetBytes((ulong)account.AppId).CopyTo(b, 0);
            BitConverter.GetBytes(1).CopyTo(b, 8);
            BitConverter.GetBytes(user).CopyTo(b, 16);
            return b;
        }

        private ulong BoardHandle(string name)
        {
            foreach (var pair in boards) if (pair.Value == name) return pair.Key;
            var handle = (ulong)(boards.Count + 1) * 0x10;
            boards[handle] = name;
            return handle;
        }

        private Dictionary<string, Method> StatsMethods() => new Dictionary<string, Method>
        {
            ["RequestCurrentStats"] = c => { Post(1101, StatsReceived(account.SteamId)); return 1; },
            ["GetStatFloat"] = c =>
            {
                account.TryGetStat(Name(c, 0), out float f);
                memory.Write32(c.Arg(1), (uint)Bits.SingleToInt32Bits(f));
                return 1;
            },
            ["GetStatInt"] = c => { account.TryGetStat(Name(c, 0), out int n); memory.Write32(c.Arg(1), (uint)n); return 1; },
            ["SetStatFloat"] = c => { account.SetStat(Name(c, 0), Bits.Int32BitsToSingle((int)c.Arg(1))); return 1; },
            ["SetStatInt"] = c => { account.SetStat(Name(c, 0), (int)c.Arg(1)); return 1; },
            ["UpdateAvgRateStat"] = c => 1,
            ["GetAchievement"] = c =>
            {
                var got = account.TryGetAchievement(Name(c, 0), out _);
                if (c.Arg(1) != 0) memory.Write8(c.Arg(1), (byte)(got ? 1 : 0));
                return 1;
            },
            ["SetAchievement"] = c =>
            {
                var name = Name(c, 0);
                var fresh = !account.TryGetAchievement(name, out _);
                account.SetAchievement(name);
                if (fresh)
                {
                    // UserAchievementStored_t: game id, group flag, name[128], progress.
                    var b = new byte[152];
                    BitConverter.GetBytes((ulong)account.AppId).CopyTo(b, 0);
                    var bytes = Encoding.UTF8.GetBytes(name);
                    Array.Copy(bytes, 0, b, 9, Math.Min(bytes.Length, 127));
                    Post(1103, b);
                }
                return 1;
            },
            ["ClearAchievement"] = c => { account.ClearAchievement(Name(c, 0)); return 1; },
            ["GetAchievementAndUnlockTime"] = c =>
            {
                var got = account.TryGetAchievement(Name(c, 0), out var when);
                if (c.Arg(1) != 0) memory.Write8(c.Arg(1), (byte)(got ? 1 : 0));
                if (c.Arg(2) != 0) memory.Write32(c.Arg(2), got ? when : 0);
                return 1;
            },
            ["StoreStats"] = c =>
            {
                account.StoreStats();
                var b = new byte[16];   // UserStatsStored_t
                BitConverter.GetBytes((ulong)account.AppId).CopyTo(b, 0);
                BitConverter.GetBytes(1).CopyTo(b, 8);
                Post(1102, b);
                return 1;
            },
            ["GetAchievementDisplayAttribute"] = c => Text(""),
            ["IndicateAchievementProgress"] = c => 1,
            ["GetAchievementName"] = c => Text(""),
            ["RequestUserStats"] = c => Result(1101, StatsReceived(c.Arg64(0))),
            ["GetUserAchievement"] = c =>
            {
                var mine = c.Arg64(0) == account.SteamId && account.TryGetAchievement(Name(c, 2), out _);
                if (c.Arg(3) != 0) memory.Write8(c.Arg(3), (byte)(mine ? 1 : 0));
                return c.Arg64(0) == account.SteamId ? 1u : 0u;
            },
            ["FindOrCreateLeaderboard"] = c => FindBoard(Name(c, 0)),
            ["FindLeaderboard"] = c => FindBoard(Name(c, 0)),
            ["GetLeaderboardName"] = c => Text(boards.TryGetValue(c.Arg64(0), out var name) ? name : ""),
            ["GetLeaderboardEntryCount"] = c => bestScores.ContainsKey(c.Arg64(0)) ? 1u : 0u,
            ["GetLeaderboardSortMethod"] = c => 2,    // descending
            ["GetLeaderboardDisplayType"] = c => 1,   // numeric
            ["UploadLeaderboardScore"] = c =>
            {
                // (board, method, score, details, count): LeaderboardScoreUploaded_t.
                var board = c.Arg64(0);
                var score = (int)c.Arg(3);
                var had = bestScores.TryGetValue(board, out var best);
                var changed = !had || c.Arg(2) == 2 || score > best;   // ForceUpdate, or better (KeepBest)
                if (changed) bestScores[board] = score;
                var b = new byte[32];
                b[0] = 1;
                BitConverter.GetBytes(board).CopyTo(b, 8);
                BitConverter.GetBytes(score).CopyTo(b, 16);
                b[20] = (byte)(changed ? 1 : 0);
                BitConverter.GetBytes(1).CopyTo(b, 24);
                BitConverter.GetBytes(had ? 1 : 0).CopyTo(b, 28);
                return Result(1106, b);
            },
            ["DownloadLeaderboardEntries"] = c => DownloadBoard(c.Arg64(0)),
            ["DownloadLeaderboardEntriesForUsers"] = c => DownloadBoard(c.Arg64(0)),
            ["GetDownloadedLeaderboardEntry"] = c =>
            {
                // (entries, index, LeaderboardEntry_t*, details*, max): only the player's own score, at index 0.
                var entry = c.Arg(3);
                if (entry != 0) memory.WriteBytes(entry, new byte[32]);
                if (entry == 0 || c.Arg(2) != 0 || !entrySets.TryGetValue(c.Arg64(0), out var board) ||
                    !bestScores.TryGetValue(board, out var score)) return 0;
                memory.Write64(entry, account.SteamId);
                memory.Write32(entry + 8, 1);                 // global rank
                memory.Write32(entry + 12, (uint)score);
                return 1;
            },
            ["GetNumberOfCurrentPlayers"] = c => 0,
        };

        private ulong FindBoard(string name)
        {
            var b = new byte[16];   // LeaderboardFindResult_t
            BitConverter.GetBytes(BoardHandle(name)).CopyTo(b, 0);
            b[8] = 1;
            return Result(1104, b);
        }

        private ulong DownloadBoard(ulong board)
        {
            var entries = (ulong)(entrySets.Count + 1) * 0x10 + 0x100000;
            entrySets[entries] = board;
            var b = new byte[24];   // LeaderboardScoresDownloaded_t
            BitConverter.GetBytes(board).CopyTo(b, 0);
            BitConverter.GetBytes(entries).CopyTo(b, 8);
            BitConverter.GetBytes(bestScores.ContainsKey(board) ? 1 : 0).CopyTo(b, 16);
            return Result(1105, b);
        }

        // --- ISteamRemoteStorage (012-014) ---------------------------------------------------

        private static readonly Slot[] RemoteLayout = Layout(
            "FileWrite:3", "FileRead:3", "FileWriteAsync:3:Q", "FileReadAsync:3:Q", "FileReadAsyncComplete:4", "FileForget:1",
            "FileDelete:1", "FileShare:1:Q", "SetSyncPlatforms:2", "FileWriteStreamOpen:1:Q", "FileWriteStreamWriteChunk:4",
            "FileWriteStreamClose:2", "FileWriteStreamCancel:2", "FileExists:1", "FilePersisted:1", "GetFileSize:1",
            "GetFileTimestamp:1:Q", "GetSyncPlatforms:1", "GetFileCount:0", "GetFileNameAndSize:2", "GetQuota:2",
            "IsCloudEnabledForAccount:0", "IsCloudEnabledForApp:0", "SetCloudEnabledForApp:1");

        private string CloudPath(string name) => CloudFolder + "\\" + name.Replace('/', '\\');

        private byte[] ReadCloud(string name)
        {
            try
            {
                using (var stream = kernel.Files.Open(CloudPath(name), FileMode.Open, FileAccess.Read))
                {
                    var bytes = new byte[stream.Length];
                    var read = 0;
                    while (read < bytes.Length) { var n = stream.Read(bytes, read, bytes.Length - read); if (n <= 0) break; read += n; }
                    return bytes;
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return null; }
        }

        private bool WriteCloud(string name, byte[] data)
        {
            var path = CloudPath(name);
            // Every folder on the way, then the file.
            for (var at = path.IndexOf('\\', 3); at > 0; at = path.IndexOf('\\', at + 1))
                if (kernel.Files.Stat(path.Substring(0, at)) == null) kernel.Files.CreateDirectory(path.Substring(0, at));
            try
            {
                using (var stream = kernel.Files.Open(path, FileMode.Create, FileAccess.Write)) stream.Write(data, 0, data.Length);
                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return false; }
        }

        private List<GuestFileEntry> CloudFiles()
        {
            var list = new List<GuestFileEntry>();
            var entries = kernel.Files.List(CloudFolder);
            if (entries != null) foreach (var e in entries) if ((e.Attributes & FileAttributes.Directory) == 0) list.Add(e);
            return list;
        }

        private Dictionary<string, Method> RemoteMethods(int version) => new Dictionary<string, Method>
        {
            ["FileWrite"] = c => WriteCloud(Name(c, 0), memory.ReadBytes(c.Arg(1), (int)c.Arg(2))) ? 1u : 0u,
            ["FileRead"] = c =>
            {
                var data = ReadCloud(Name(c, 0));
                if (data == null) return 0;
                var n = Math.Min(data.Length, (int)c.Arg(2));
                memory.WriteBytes(c.Arg(1), data, 0, n);
                return (uint)n;
            },
            ["FileWriteAsync"] = c =>
            {
                var ok = WriteCloud(Name(c, 0), memory.ReadBytes(c.Arg(1), (int)c.Arg(2)));
                return Result(1331, BitConverter.GetBytes(ok ? 1 : 2));   // RemoteStorageFileWriteAsyncComplete_t
            },
            ["FileReadAsync"] = c =>
            {
                // (name, offset, bytes): RemoteStorageFileReadAsyncComplete_t, data kept for ...Complete.
                var data = ReadCloud(Name(c, 0)) ?? new byte[0];
                var offset = (int)Math.Min(c.Arg(1), (uint)data.Length);
                var count = (int)Math.Min(c.Arg(2), (uint)(data.Length - offset));
                var chunk = new byte[count];
                Array.Copy(data, offset, chunk, 0, count);
                var b = new byte[24];
                var call = nextCall;
                BitConverter.GetBytes(call).CopyTo(b, 0);
                BitConverter.GetBytes(data.Length > 0 ? 1 : 2).CopyTo(b, 8);
                BitConverter.GetBytes(offset).CopyTo(b, 12);
                BitConverter.GetBytes(count).CopyTo(b, 16);
                asyncReads[call] = chunk;
                return Result(1332, b);
            },
            ["FileReadAsyncComplete"] = c =>
            {
                if (!asyncReads.TryGetValue(c.Arg64(0), out var chunk)) return 0;
                memory.WriteBytes(c.Arg(2), chunk, 0, (int)Math.Min((uint)chunk.Length, c.Arg(3)));
                asyncReads.Remove(c.Arg64(0));
                return 1;
            },
            ["FileForget"] = c => 1,
            ["FileDelete"] = c => kernel.Files.Delete(CloudPath(Name(c, 0))) ? 1u : 0u,
            ["FileExists"] = c => kernel.Files.Stat(CloudPath(Name(c, 0))) != null ? 1u : 0u,
            ["FilePersisted"] = c => kernel.Files.Stat(CloudPath(Name(c, 0))) != null ? 1u : 0u,
            ["GetFileSize"] = c => (uint)(kernel.Files.Stat(CloudPath(Name(c, 0)))?.Size ?? 0),
            ["GetFileTimestamp"] = c =>
            {
                var e = kernel.Files.Stat(CloudPath(Name(c, 0)));
                return e == null ? 0 : (ulong)new DateTimeOffset(e.WriteTimeUtc).ToUnixTimeSeconds();
            },
            ["GetSyncPlatforms"] = c => 0xFFFFFFFF,   // all
            ["GetFileCount"] = c => (uint)CloudFiles().Count,
            ["GetFileNameAndSize"] = c =>
            {
                var files = CloudFiles();
                if (c.Arg(0) >= files.Count) { if (c.Arg(1) != 0) memory.Write32(c.Arg(1), 0); return Text(""); }
                var e = files[(int)c.Arg(0)];
                if (c.Arg(1) != 0) memory.Write32(c.Arg(1), (uint)e.Size);
                return Text(e.Name);
            },
            ["GetQuota"] = c =>
            {
                // 013 and later take uint64 pointers, older versions int32 ones.
                const ulong Total = 100UL << 20;
                ulong used = 0;
                foreach (var e in CloudFiles()) used += (ulong)e.Size;
                if (version >= 13) { memory.Write64(c.Arg(0), Total); memory.Write64(c.Arg(1), Total - Math.Min(used, Total)); }
                else { memory.Write32(c.Arg(0), (uint)Total); memory.Write32(c.Arg(1), (uint)(Total - Math.Min(used, Total))); }
                return 1;
            },
            ["IsCloudEnabledForAccount"] = c => 1,
            ["IsCloudEnabledForApp"] = c => 1,
        };
    }
}
