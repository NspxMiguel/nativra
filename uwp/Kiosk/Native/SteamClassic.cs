using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Kiosk.Native
{
    /// <summary>
    /// Answers the CLASSIC (pre-flat) Steamworks C++ API: the vtable-based
    /// interfaces declared in the 2012-era SDK (ISteamUser016, ISteamFriends011,
    /// ISteamUtils005, ISteamApps005, ISteamUserStats010, ISteamRemoteStorage006),
    /// reached through the SteamUser()/SteamFriends()/... global accessor
    /// functions and driven by SteamAPI_RunCallbacks/RegisterCallback rather
    /// than the ManualDispatch model Steamworks.NET uses.
    ///
    /// SteamBridge answers the FLAT SteamAPI_ISteamX_Method exports; this
    /// answers the older games that call the vtable directly (LEGO Jurassic
    /// World links steam_api64.dll statically and never goes through the flat
    /// wrapper at all). Both are backed by the same signed-in account and the
    /// same local save data, so an achievement set through either path is the
    /// same achievement. Ownership is only ever reported for SteamBridge.AppId,
    /// the one game actually running under the console's own Steam license --
    /// never for any other app id a game happens to ask about.
    ///
    /// Every interface here is a hand-built vtable: an unmanaged pointer to an
    /// array of function pointers, one per virtual method, in the exact order
    /// the header declares them (MSVC lays out a C++ vtable in declaration
    /// order, except that adjacent overloads of the same name are laid out in
    /// REVERSE declaration order -- GetStat/SetStat/GetUserStat/GetGlobalStat/
    /// GetGlobalStatHistory all have this, see BuildUserStats below).
    ///
    /// A class or struct returned BY VALUE from an x64 MSVC member function
    /// (CSteamID, CGameID) is never actually returned in a register: the
    /// caller passes a hidden pointer as the SECOND argument (right after
    /// 'this') for the callee to write the result into, and the callee
    /// returns that same pointer in RAX. GetSteamID() with zero declared
    /// arguments is therefore really GetSteamID(this, CSteamID* result).
    /// </summary>
    internal static class SteamClassic
    {
        // One shape covers every vtable slot in every interface here: 'this'
        // plus up to nine more IntPtr-sized arguments, which is as wide as
        // the widest real method (ISteamUser::GetVoice takes nine). A real
        // call that passes fewer arguments simply leaves the extra positions
        // holding whatever was already in that register or stack slot; slots
        // that ignore their arguments never look at them, so the extra width
        // is harmless. Anything passed in a floating-point register (a bare
        // float/double parameter, not a pointer to one) needs its own shape,
        // since x64 puts those in XMM registers rather than the integer ones
        // this delegate reads -- see WithFloat.
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr Fn(IntPtr a0, IntPtr a1, IntPtr a2, IntPtr a3, IntPtr a4,
            IntPtr a5, IntPtr a6, IntPtr a7, IntPtr a8, IntPtr a9);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr WithFloat(IntPtr self, IntPtr name, float value);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void RunCallback(IntPtr self, IntPtr pvParam);

        private static readonly List<Delegate> keep = new List<Delegate>();
        private static readonly object gate = new object();

        private static IntPtr True => new IntPtr(1);
        private static IntPtr Zero => IntPtr.Zero;

        private static IntPtr Keep(Delegate function)
        {
            lock (keep) keep.Add(function);
            return Marshal.GetFunctionPointerForDelegate(function);
        }

        /// <summary>A slot that ignores every argument and always answers the same value.</summary>
        private static IntPtr Stub(IntPtr value)
        {
            Fn fn = (a0, a1, a2, a3, a4, a5, a6, a7, a8, a9) => value;
            return Keep(fn);
        }

        private static readonly IntPtr stubZero = Stub(IntPtr.Zero);
        private static readonly IntPtr stubTrue = Stub(new IntPtr(1));
        private static readonly IntPtr stubMinusOne = Stub(new IntPtr(-1));

        /// <summary>
        /// A hidden-pointer-return slot that writes zero (no such friend, no
        /// such clan, ...) into *result and hands the same pointer back, per
        /// the ABI note above. 'result' is always argument position two.
        /// </summary>
        private static readonly IntPtr hiddenZero = Keep(new Fn((self, result, a2, a3, a4, a5, a6, a7, a8, a9) =>
        {
            if (result != IntPtr.Zero) Marshal.WriteInt64(result, 0, 0);
            return result;
        }));

        private static string Text(IntPtr pointer) => SteamBridge.Text(pointer);
        private static IntPtr Utf8(string text) => SteamBridge.Utf8(text);

        private static void WriteCString(IntPtr buffer, int capacity, string text)
        {
            if (buffer == IntPtr.Zero || capacity <= 0) return;
            var bytes = Encoding.UTF8.GetBytes(text ?? string.Empty);
            var count = Math.Min(bytes.Length, capacity - 1);
            if (count > 0) Marshal.Copy(bytes, 0, buffer, count);
            Marshal.WriteByte(buffer, count, 0);
        }

        /// <summary>Builds a vtable object: an IntPtr to a table of function pointers.</summary>
        private static IntPtr Table(params IntPtr[] slots)
        {
            var table = Marshal.AllocHGlobal(IntPtr.Size * slots.Length);
            for (var i = 0; i < slots.Length; i++) Marshal.WriteIntPtr(table, i * IntPtr.Size, slots[i]);
            var obj = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(obj, table);
            return obj;
        }

        private static readonly HashSet<string> accessorNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "SteamUser", "SteamFriends", "SteamApps", "SteamUserStats", "SteamUtils",
            "SteamRemoteStorage", "SteamMatchmaking", "SteamNetworking", "SteamScreenshots", "SteamHTTP",
        };

        /// <summary>Bare classic accessor names, which carry no SteamAPI_/SteamInternal_ prefix.</summary>
        public static bool IsAccessorName(string export) => export != null && accessorNames.Contains(export);

        // ---- object caches -------------------------------------------------

        private static IntPtr userObj, friendsObj, utilsObj, appsObj, statsObj, storageObj, emptyObj, clientObj;

        private static IntPtr User()
        {
            lock (gate) return userObj != IntPtr.Zero ? userObj : (userObj = BuildUser());
        }

        private static IntPtr Friends()
        {
            lock (gate) return friendsObj != IntPtr.Zero ? friendsObj : (friendsObj = BuildFriends());
        }

        private static IntPtr Utils()
        {
            lock (gate) return utilsObj != IntPtr.Zero ? utilsObj : (utilsObj = BuildUtils());
        }

        private static IntPtr Apps()
        {
            lock (gate) return appsObj != IntPtr.Zero ? appsObj : (appsObj = BuildApps());
        }

        private static IntPtr UserStats()
        {
            lock (gate) return statsObj != IntPtr.Zero ? statsObj : (statsObj = BuildUserStats());
        }

        private static IntPtr RemoteStorage()
        {
            lock (gate) return storageObj != IntPtr.Zero ? storageObj : (storageObj = BuildRemoteStorage());
        }

        /// <summary>
        /// A generously wide object (matchmaking, networking, screenshots,
        /// HTTP, and the parts of ISteamClient012 nothing here implements)
        /// that answers every slot with zero. Real interfaces top out well
        /// under 80 virtual methods, so this is never short.
        /// </summary>
        private static IntPtr Empty()
        {
            lock (gate)
            {
                if (emptyObj != IntPtr.Zero) return emptyObj;
                var slots = new IntPtr[80];
                for (var i = 0; i < slots.Length; i++) slots[i] = stubZero;
                return emptyObj = Table(slots);
            }
        }

        private static IntPtr Client()
        {
            lock (gate) return clientObj != IntPtr.Zero ? clientObj : (clientObj = BuildClient());
        }

        // ---- SteamAPI_* flat exports classic games call directly -----------

        private static readonly IntPtr exportInit = Stub(new IntPtr(1));
        private static IntPtr exportRunCallbacks;
        private static IntPtr exportRegisterCallback;
        private static IntPtr exportUnregisterCallback;

        public static IntPtr Resolve(string export)
        {
            if (!SteamBridge.Active || string.IsNullOrEmpty(export)) return IntPtr.Zero;
            switch (export)
            {
                case "SteamUser": return User();
                case "SteamFriends": return Friends();
                case "SteamApps": return Apps();
                case "SteamUserStats": return UserStats();
                case "SteamUtils": return Utils();
                case "SteamRemoteStorage": return RemoteStorage();
                case "SteamMatchmaking":
                case "SteamNetworking":
                case "SteamScreenshots":
                case "SteamHTTP":
                    return Empty();
                case "SteamClient":
                    return Client();

                // SteamAPI_Init/GetHSteamUser/GetHSteamPipe/RestartAppIfNecessary
                // are already answered by SteamBridge's flat table with the
                // same values, so they never reach here.
                case "SteamAPI_InitSafe":
                    return exportInit;
                case "SteamAPI_Shutdown":
                case "SteamAPI_RegisterCallResult":
                case "SteamAPI_UnregisterCallResult":
                    return stubZero;
                case "SteamAPI_RunCallbacks":
                    lock (gate) return exportRunCallbacks != IntPtr.Zero
                        ? exportRunCallbacks
                        : (exportRunCallbacks = Keep(new Fn((a0, a1, a2, a3, a4, a5, a6, a7, a8, a9) =>
                        {
                            RunPendingCallbacks();
                            return IntPtr.Zero;
                        })));
                case "SteamAPI_RegisterCallback":
                    lock (gate) return exportRegisterCallback != IntPtr.Zero
                        ? exportRegisterCallback
                        : (exportRegisterCallback = Keep(new Fn((cb, iCallback, a2, a3, a4, a5, a6, a7, a8, a9) =>
                        {
                            RegisterCallback(cb, (int)iCallback.ToInt64());
                            return IntPtr.Zero;
                        })));
                case "SteamAPI_UnregisterCallback":
                    lock (gate) return exportUnregisterCallback != IntPtr.Zero
                        ? exportUnregisterCallback
                        : (exportUnregisterCallback = Keep(new Fn((cb, a1, a2, a3, a4, a5, a6, a7, a8, a9) =>
                        {
                            UnregisterCallback(cb);
                            return IntPtr.Zero;
                        })));
                default:
                    return IntPtr.Zero;
            }
        }

        // ---- callback dispatch ----------------------------------------------
        //
        // CCallbackBase layout in this SDK: vptr (8 bytes), uint8
        // m_nCallbackFlags at +8, int m_iCallback at +12. Its vtable, with the
        // MSVC same-name-overload reversal applied: [0] Run(void*, bool,
        // SteamAPICall_t) for call results, [1] Run(void*) for plain
        // callbacks, [2] GetCallbackSizeBytes(). RunCallbacks only ever
        // delivers plain callbacks (slot 1): nothing here issues a real
        // SteamAPICall_t, so no CCallResult ever has something to complete.

        private const byte CallbackFlagRegistered = 1;
        private const int CallbackFlagsOffset = 8;
        private const int CallbackIdOffset = 12;
        private const int RunSlotIndex = 1;

        private static readonly List<IntPtr> registered = new List<IntPtr>();
        private static readonly Queue<KeyValuePair<int, byte[]>> pending = new Queue<KeyValuePair<int, byte[]>>();

        private static void RegisterCallback(IntPtr callback, int iCallback)
        {
            if (callback == IntPtr.Zero) return;
            var flags = Marshal.ReadByte(callback, CallbackFlagsOffset);
            Marshal.WriteByte(callback, CallbackFlagsOffset, (byte)(flags | CallbackFlagRegistered));
            Marshal.WriteInt32(callback, CallbackIdOffset, iCallback);
            lock (registered) if (!registered.Contains(callback)) registered.Add(callback);
        }

        private static void UnregisterCallback(IntPtr callback)
        {
            if (callback == IntPtr.Zero) return;
            var flags = Marshal.ReadByte(callback, CallbackFlagsOffset);
            Marshal.WriteByte(callback, CallbackFlagsOffset, (byte)(flags & ~CallbackFlagRegistered));
            lock (registered) registered.Remove(callback);
        }

        private static void Post(int callbackId, byte[] payload)
        {
            lock (pending) pending.Enqueue(new KeyValuePair<int, byte[]>(callbackId, payload));
        }

        private static void RunPendingCallbacks()
        {
            List<KeyValuePair<int, byte[]>> batch;
            lock (pending)
            {
                if (pending.Count == 0) return;
                batch = new List<KeyValuePair<int, byte[]>>(pending);
                pending.Clear();
            }
            List<IntPtr> targets;
            lock (registered) targets = new List<IntPtr>(registered);
            foreach (var message in batch)
            {
                var length = Math.Max(message.Value.Length, 1);
                var buffer = Marshal.AllocHGlobal(length);
                Marshal.Copy(message.Value, 0, buffer, message.Value.Length);
                foreach (var callback in targets)
                {
                    int iCallback;
                    try { iCallback = Marshal.ReadInt32(callback, CallbackIdOffset); }
                    catch { continue; }
                    if (iCallback != message.Key) continue;
                    try
                    {
                        var vtable = Marshal.ReadIntPtr(callback);
                        var slot = Marshal.ReadIntPtr(vtable, RunSlotIndex * IntPtr.Size);
                        var run = Marshal.GetDelegateForFunctionPointer<RunCallback>(slot);
                        run(callback, buffer);
                    }
                    catch
                    {
                        // A misbehaving callback object must not take the rest
                        // of the delivery, or the game itself, down with it.
                    }
                }
                Marshal.FreeHGlobal(buffer);
            }
        }

        private const int UserStatsReceived = 1101;
        private const int UserStatsStored = 1102;
        private const int UserAchievementStored = 1103;

        // ---- ISteamUser016 ---------------------------------------------------
        // 22 slots: GetHSteamUser, BLoggedOn, GetSteamID, InitiateGameConnection,
        // TerminateGameConnection, TrackAppUsageEvent, GetUserDataFolder,
        // StartVoiceRecording, StopVoiceRecording, GetAvailableVoice, GetVoice,
        // DecompressVoice, GetVoiceOptimalSampleRate, GetAuthSessionTicket,
        // BeginAuthSession, EndAuthSession, CancelAuthTicket,
        // UserHasLicenseForApp, BIsBehindNAT, AdvertiseGame,
        // RequestEncryptedAppTicket, GetEncryptedAppTicket.

        private static IntPtr BuildUser()
        {
            Fn getSteamId = (self, result, a2, a3, a4, a5, a6, a7, a8, a9) =>
            {
                if (result != IntPtr.Zero) Marshal.WriteInt64(result, 0, (long)SteamBridge.SteamId);
                return result;
            };
            Fn getUserDataFolder = (self, buffer, capacity, a3, a4, a5, a6, a7, a8, a9) =>
            {
                try
                {
                    var root = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
                    var path = System.IO.Path.Combine(root, "profile", "steam-userdata", SteamBridge.AppId.ToString());
                    System.IO.Directory.CreateDirectory(path);
                    WriteCString(buffer, (int)capacity.ToInt64(), path);
                    return True;
                }
                catch { return Zero; }
            };

            return Table(
                /* 0  GetHSteamUser              */ stubTrue,
                /* 1  BLoggedOn                   */ stubTrue,
                /* 2  GetSteamID                  */ Keep(getSteamId),
                /* 3  InitiateGameConnection      */ stubZero,
                /* 4  TerminateGameConnection     */ stubZero,
                /* 5  TrackAppUsageEvent          */ stubZero,
                /* 6  GetUserDataFolder           */ Keep(getUserDataFolder),
                /* 7  StartVoiceRecording         */ stubZero,
                /* 8  StopVoiceRecording          */ stubZero,
                /* 9  GetAvailableVoice           */ Stub(new IntPtr(1)), // k_EVoiceResultNotInitialized
                /* 10 GetVoice                    */ Stub(new IntPtr(1)),
                /* 11 DecompressVoice             */ Stub(new IntPtr(1)),
                /* 12 GetVoiceOptimalSampleRate   */ stubZero,
                /* 13 GetAuthSessionTicket        */ stubZero,
                /* 14 BeginAuthSession            */ stubZero,
                /* 15 EndAuthSession              */ stubZero,
                /* 16 CancelAuthTicket            */ stubZero,
                /* 17 UserHasLicenseForApp        */ stubZero,
                /* 18 BIsBehindNAT                */ stubZero,
                /* 19 AdvertiseGame               */ stubZero,
                /* 20 RequestEncryptedAppTicket   */ stubZero,
                /* 21 GetEncryptedAppTicket       */ stubZero
            );
        }

        // ---- ISteamFriends011 -------------------------------------------------
        // 63 slots, in header declaration order (no reversed overloads here).

        private static IntPtr BuildFriends()
        {
            Fn getPersonaName = (self, a1, a2, a3, a4, a5, a6, a7, a8, a9) =>
                Utf8(Steam.SteamStats.PersonaName ?? SteamBridge.PersonaName ?? string.Empty);
            Fn setRichPresence = (self, key, value, a3, a4, a5, a6, a7, a8, a9) => True;

            var emptyString = Utf8(string.Empty);

            return Table(
                /* 0  GetPersonaName                     */ Keep(getPersonaName),
                /* 1  SetPersonaName                      */ stubZero,
                /* 2  GetPersonaState                     */ stubTrue, // 1 = online
                /* 3  GetFriendCount                      */ stubZero,
                /* 4  GetFriendByIndex                    */ hiddenZero,
                /* 5  GetFriendRelationship               */ stubZero,
                /* 6  GetFriendPersonaState                */ stubZero,
                /* 7  GetFriendPersonaName                 */ Stub(emptyString),
                /* 8  GetFriendGamePlayed                  */ stubZero,
                /* 9  GetFriendPersonaNameHistory           */ Stub(emptyString),
                /* 10 HasFriend                            */ stubZero,
                /* 11 GetClanCount                         */ stubZero,
                /* 12 GetClanByIndex                       */ hiddenZero,
                /* 13 GetClanName                          */ Stub(emptyString),
                /* 14 GetClanTag                           */ Stub(emptyString),
                /* 15 GetClanActivityCounts                 */ stubZero,
                /* 16 DownloadClanActivityCounts            */ stubZero,
                /* 17 GetFriendCountFromSource              */ stubZero,
                /* 18 GetFriendFromSourceByIndex            */ hiddenZero,
                /* 19 IsUserInSource                       */ stubZero,
                /* 20 SetInGameVoiceSpeaking                */ stubZero,
                /* 21 ActivateGameOverlay                   */ stubZero,
                /* 22 ActivateGameOverlayToUser             */ stubZero,
                /* 23 ActivateGameOverlayToWebPage          */ stubZero,
                /* 24 ActivateGameOverlayToStore            */ stubZero,
                /* 25 SetPlayedWith                        */ stubZero,
                /* 26 ActivateGameOverlayInviteDialog       */ stubZero,
                /* 27 GetSmallFriendAvatar                  */ stubZero,
                /* 28 GetMediumFriendAvatar                 */ stubZero,
                /* 29 GetLargeFriendAvatar                  */ stubZero,
                /* 30 RequestUserInformation                */ stubZero,
                /* 31 RequestClanOfficerList                */ stubZero,
                /* 32 GetClanOwner                         */ hiddenZero,
                /* 33 GetClanOfficerCount                   */ stubZero,
                /* 34 GetClanOfficerByIndex                 */ hiddenZero,
                /* 35 GetUserRestrictions                   */ stubZero,
                /* 36 SetRichPresence                      */ Keep(setRichPresence),
                /* 37 ClearRichPresence                     */ stubZero,
                /* 38 GetFriendRichPresence                 */ Stub(emptyString),
                /* 39 GetFriendRichPresenceKeyCount         */ stubZero,
                /* 40 GetFriendRichPresenceKeyByIndex       */ Stub(emptyString),
                /* 41 RequestFriendRichPresence             */ stubZero,
                /* 42 InviteUserToGame                     */ stubZero,
                /* 43 GetCoplayFriendCount                  */ stubZero,
                /* 44 GetCoplayFriend                      */ hiddenZero,
                /* 45 GetFriendCoplayTime                   */ stubZero,
                /* 46 GetFriendCoplayGame                   */ stubZero,
                /* 47 JoinClanChatRoom                     */ stubZero,
                /* 48 LeaveClanChatRoom                     */ stubZero,
                /* 49 GetClanChatMemberCount                */ stubZero,
                /* 50 GetChatMemberByIndex                  */ hiddenZero,
                /* 51 SendClanChatMessage                   */ stubZero,
                /* 52 GetClanChatMessage                    */ stubZero,
                /* 53 IsClanChatAdmin                      */ stubZero,
                /* 54 IsClanChatWindowOpenInSteam           */ stubZero,
                /* 55 OpenClanChatWindowInSteam             */ stubZero,
                /* 56 CloseClanChatWindowInSteam            */ stubZero,
                /* 57 SetListenForFriendsMessages           */ stubZero,
                /* 58 ReplyToFriendMessage                  */ stubZero,
                /* 59 GetFriendMessage                      */ stubZero,
                /* 60 GetFollowerCount                      */ stubZero,
                /* 61 IsFollowing                          */ stubZero,
                /* 62 EnumerateFollowingList                */ stubZero
            );
        }

        // ---- ISteamUtils005 ---------------------------------------------------
        // 23 slots.

        private static readonly System.Diagnostics.Stopwatch uptime = System.Diagnostics.Stopwatch.StartNew();

        private static IntPtr BuildUtils()
        {
            Fn secondsAppActive = (self, a1, a2, a3, a4, a5, a6, a7, a8, a9) =>
                new IntPtr((long)uptime.Elapsed.TotalSeconds);
            Fn serverTime = (self, a1, a2, a3, a4, a5, a6, a7, a8, a9) =>
                new IntPtr(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            var countryString = Utf8("BR");
            Fn appId = (self, a1, a2, a3, a4, a5, a6, a7, a8, a9) => new IntPtr(SteamBridge.AppId);
            // No real asynchronous call is ever issued (every SteamAPICall_t
            // this bridge hands out is k_uAPICallInvalid, 0), so a poll on any
            // handle is answered as already finished, and failed, rather than
            // leaving a caller spinning forever waiting for it to complete.
            Fn isCompleted = (self, hCall, pbFailed, a3, a4, a5, a6, a7, a8, a9) =>
            {
                if (pbFailed != IntPtr.Zero) Marshal.WriteByte(pbFailed, 1);
                return True;
            };
            Fn callResult = (self, hCall, pCallback, cub, iExpected, pbFailed, a5, a6, a7, a8) =>
            {
                if (pbFailed != IntPtr.Zero) Marshal.WriteByte(pbFailed, 1);
                return Zero;
            };

            return Table(
                /* 0  GetSecondsSinceAppActive      */ Keep(secondsAppActive),
                /* 1  GetSecondsSinceComputerActive */ Keep(secondsAppActive),
                /* 2  GetConnectedUniverse          */ stubTrue, // k_EUniversePublic = 1
                /* 3  GetServerRealTime             */ Keep(serverTime),
                /* 4  GetIPCountry                  */ Stub(countryString),
                /* 5  GetImageSize                  */ stubZero,
                /* 6  GetImageRGBA                  */ stubZero,
                /* 7  GetCSERIPPort                 */ stubZero,
                /* 8  GetCurrentBatteryPower        */ Stub(new IntPtr(255)),
                /* 9  GetAppID                      */ Keep(appId),
                /* 10 SetOverlayNotificationPosition*/ stubZero,
                /* 11 IsAPICallCompleted            */ Keep(isCompleted),
                /* 12 GetAPICallFailureReason       */ Stub(new IntPtr(1)),
                /* 13 GetAPICallResult              */ Keep(callResult),
                /* 14 RunFrame                      */ stubZero,
                /* 15 GetIPCCallCount               */ stubZero,
                /* 16 SetWarningMessageHook         */ stubZero,
                /* 17 IsOverlayEnabled              */ stubZero,
                /* 18 BOverlayNeedsPresent          */ stubZero,
                /* 19 CheckFileSignature            */ stubZero,
                /* 20 ShowGamepadTextInput          */ stubZero,
                /* 21 GetEnteredGamepadTextLength   */ stubZero,
                /* 22 GetEnteredGamepadTextInput    */ stubZero
            );
        }

        // ---- ISteamApps005 -----------------------------------------------------
        // 20 slots.

        private static IntPtr BuildApps()
        {
            Fn subscribedApp = (self, appId, a2, a3, a4, a5, a6, a7, a8, a9) =>
                (uint)appId.ToInt64() == SteamBridge.AppId ? True : Zero;
            Fn installed = (self, appId, a2, a3, a4, a5, a6, a7, a8, a9) =>
                (uint)appId.ToInt64() == SteamBridge.AppId ? True : Zero;
            Fn installDir = (self, appId, folder, size, a4, a5, a6, a7, a8, a9) =>
            {
                if ((uint)appId.ToInt64() != SteamBridge.AppId || LoaderStubs.GameFolder == null) return Zero;
                WriteCString(folder, (int)size.ToInt64(), LoaderStubs.GameFolder);
                return new IntPtr(Encoding.UTF8.GetByteCount(LoaderStubs.GameFolder));
            };

            return Table(
                /* 0  BIsSubscribed                  */ stubTrue,
                /* 1  BIsLowViolence                 */ stubZero,
                /* 2  BIsCybercafe                   */ stubZero,
                /* 3  BIsVACBanned                   */ stubZero,
                /* 4  GetCurrentGameLanguage         */ Stub(Utf8(SteamBridge.Language ?? "english")),
                /* 5  GetAvailableGameLanguages      */ Stub(Utf8(SteamBridge.Language ?? "english")),
                /* 6  BIsSubscribedApp               */ Keep(subscribedApp),
                /* 7  BIsDlcInstalled                */ stubZero,
                /* 8  GetEarliestPurchaseUnixTime    */ stubZero,
                /* 9  BIsSubscribedFromFreeWeekend   */ stubZero,
                /* 10 GetDLCCount                    */ stubZero,
                /* 11 BGetDLCDataByIndex             */ stubZero,
                /* 12 InstallDLC                     */ stubZero,
                /* 13 UninstallDLC                   */ stubZero,
                /* 14 RequestAppProofOfPurchaseKey   */ stubZero,
                /* 15 GetCurrentBetaName             */ stubZero,
                /* 16 MarkContentCorrupt             */ stubZero,
                /* 17 GetInstalledDepots             */ stubZero,
                /* 18 GetAppInstallDir               */ Keep(installDir),
                /* 19 BIsAppInstalled                */ Keep(installed)
            );
        }

        // ---- ISteamUserStats010 -------------------------------------------------
        // 41 slots. GetStat/SetStat/GetUserStat/GetGlobalStat/GetGlobalStatHistory
        // are each declared as (int32-ish) then (float/double-ish) in the header
        // under the MSVC branch, but MSVC lays adjacent overloads of the same
        // name out in REVERSE declaration order, so the float/double overload
        // gets the earlier vtable slot in every one of those pairs.

        private static IntPtr BuildUserStats()
        {
            Fn requestCurrentStats = (self, a1, a2, a3, a4, a5, a6, a7, a8, a9) =>
            {
                Post(UserStatsReceived, SteamBridge.StatsReceived());
                return True;
            };
            Fn getStatFloat = (self, name, value, a3, a4, a5, a6, a7, a8, a9) =>
            {
                float kept;
                lock (SteamBridge.FloatStats) SteamBridge.FloatStats.TryGetValue(Text(name), out kept);
                if (value != IntPtr.Zero)
                    Marshal.WriteInt32(value, BitConverter.ToInt32(BitConverter.GetBytes(kept), 0));
                return True;
            };
            Fn getStatInt32 = (self, name, value, a3, a4, a5, a6, a7, a8, a9) =>
            {
                int kept;
                lock (SteamBridge.Numbers) SteamBridge.Numbers.TryGetValue(Text(name), out kept);
                if (value != IntPtr.Zero) Marshal.WriteInt32(value, kept);
                return True;
            };
            WithFloat setStatFloat = (self, name, value) =>
            {
                lock (SteamBridge.FloatStats) SteamBridge.FloatStats[Text(name)] = value;
                return True;
            };
            Fn setStatInt32 = (self, name, value, a3, a4, a5, a6, a7, a8, a9) =>
            {
                lock (SteamBridge.Numbers) SteamBridge.Numbers[Text(name)] = (int)value.ToInt64();
                return True;
            };
            Fn getAchievement = (self, name, achievedOut, a3, a4, a5, a6, a7, a8, a9) =>
            {
                bool got;
                lock (SteamBridge.Achieved) got = SteamBridge.Achieved.ContainsKey(Text(name));
                if (achievedOut != IntPtr.Zero) Marshal.WriteByte(achievedOut, (byte)(got ? 1 : 0));
                return True;
            };
            Fn setAchievement = (self, name, a2, a3, a4, a5, a6, a7, a8, a9) =>
            {
                var key = Text(name);
                bool fresh;
                lock (SteamBridge.Achieved)
                {
                    fresh = !SteamBridge.Achieved.ContainsKey(key);
                    if (fresh) SteamBridge.Achieved[key] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                }
                if (fresh)
                {
                    SteamBridge.Save();
                    Post(UserAchievementStored, SteamBridge.AchievementStored(key));
                }
                return True;
            };
            Fn clearAchievement = (self, name, a2, a3, a4, a5, a6, a7, a8, a9) =>
            {
                lock (SteamBridge.Achieved) SteamBridge.Achieved.Remove(Text(name));
                SteamBridge.Save();
                return True;
            };
            Fn getAchievementAndUnlockTime = (self, name, achievedOut, time, a4, a5, a6, a7, a8, a9) =>
            {
                long when;
                bool got;
                lock (SteamBridge.Achieved) got = SteamBridge.Achieved.TryGetValue(Text(name), out when);
                if (achievedOut != IntPtr.Zero) Marshal.WriteByte(achievedOut, (byte)(got ? 1 : 0));
                if (time != IntPtr.Zero) Marshal.WriteInt32(time, got ? (int)when : 0);
                return True;
            };
            Fn storeStats = (self, a1, a2, a3, a4, a5, a6, a7, a8, a9) =>
            {
                SteamBridge.Save();
                SteamBridge.Changed?.Invoke();
                Post(UserStatsStored, SteamBridge.StatsStored());
                return True;
            };
            var emptyString = Utf8(string.Empty);

            return Table(
                /* 0  RequestCurrentStats             */ Keep(requestCurrentStats),
                /* 1  GetStat(float*)                 */ Keep(getStatFloat),
                /* 2  GetStat(int32*)                 */ Keep(getStatInt32),
                /* 3  SetStat(float)                  */ Keep(setStatFloat),
                /* 4  SetStat(int32)                  */ Keep(setStatInt32),
                /* 5  UpdateAvgRateStat               */ stubTrue,
                /* 6  GetAchievement                  */ Keep(getAchievement),
                /* 7  SetAchievement                  */ Keep(setAchievement),
                /* 8  ClearAchievement                */ Keep(clearAchievement),
                /* 9  GetAchievementAndUnlockTime     */ Keep(getAchievementAndUnlockTime),
                /* 10 StoreStats                      */ Keep(storeStats),
                /* 11 GetAchievementIcon              */ stubZero,
                /* 12 GetAchievementDisplayAttribute  */ Stub(emptyString),
                /* 13 IndicateAchievementProgress     */ stubTrue,
                /* 14 RequestUserStats                */ stubZero,
                /* 15 GetUserStat(float*)             */ stubZero,
                /* 16 GetUserStat(int32*)             */ stubZero,
                /* 17 GetUserAchievement              */ stubZero,
                /* 18 GetUserAchievementAndUnlockTime */ stubZero,
                /* 19 ResetAllStats                   */ stubZero,
                /* 20 FindOrCreateLeaderboard         */ stubZero,
                /* 21 FindLeaderboard                 */ stubZero,
                /* 22 GetLeaderboardName              */ Stub(emptyString),
                /* 23 GetLeaderboardEntryCount        */ stubZero,
                /* 24 GetLeaderboardSortMethod        */ stubZero,
                /* 25 GetLeaderboardDisplayType       */ stubZero,
                /* 26 DownloadLeaderboardEntries      */ stubZero,
                /* 27 DownloadLeaderboardEntriesForUsers */ stubZero,
                /* 28 GetDownloadedLeaderboardEntry   */ stubZero,
                /* 29 UploadLeaderboardScore          */ stubZero,
                /* 30 AttachLeaderboardUGC            */ stubZero,
                /* 31 GetNumberOfCurrentPlayers       */ stubZero,
                /* 32 RequestGlobalAchievementPercentages */ stubZero,
                /* 33 GetMostAchievedAchievementInfo  */ stubMinusOne,
                /* 34 GetNextMostAchievedAchievementInfo */ stubMinusOne,
                /* 35 GetAchievementAchievedPercent   */ stubZero,
                /* 36 RequestGlobalStats              */ stubZero,
                /* 37 GetGlobalStat(double*)          */ stubZero,
                /* 38 GetGlobalStat(int64*)           */ stubZero,
                /* 39 GetGlobalStatHistory(double*)   */ stubZero,
                /* 40 GetGlobalStatHistory(int64*)    */ stubZero
            );
        }

        // ---- ISteamRemoteStorage006 ---------------------------------------------
        // 47 slots. Local-only cloud: files live under the app's own LocalState,
        // never actually leave the console, but behave like a synced cloud to
        // the game (writes persist, reads see them back, quota looks generous).

        private static string CloudDir()
        {
            var root = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
            var dir = System.IO.Path.Combine(root, "profile", "steam-cloud", SteamBridge.AppId.ToString());
            System.IO.Directory.CreateDirectory(dir);
            return dir;
        }

        private static string CloudPath(string file)
        {
            var name = (file ?? string.Empty).Trim().ToLowerInvariant()
                .Replace('\\', '_').Replace('/', '_');
            return System.IO.Path.Combine(CloudDir(), name.Length == 0 ? "_" : name);
        }

        private static IntPtr BuildRemoteStorage()
        {
            Fn fileWrite = (self, name, data, size, a4, a5, a6, a7, a8, a9) =>
            {
                try
                {
                    var count = Math.Max((int)size.ToInt64(), 0);
                    var bytes = new byte[count];
                    if (count > 0) Marshal.Copy(data, bytes, 0, count);
                    System.IO.File.WriteAllBytes(CloudPath(Text(name)), bytes);
                    return True;
                }
                catch { return Zero; }
            };
            Fn fileRead = (self, name, data, size, a4, a5, a6, a7, a8, a9) =>
            {
                try
                {
                    var path = CloudPath(Text(name));
                    if (!System.IO.File.Exists(path)) return Zero;
                    var bytes = System.IO.File.ReadAllBytes(path);
                    var count = Math.Min(bytes.Length, Math.Max((int)size.ToInt64(), 0));
                    if (count > 0) Marshal.Copy(bytes, 0, data, count);
                    return new IntPtr(count);
                }
                catch { return Zero; }
            };
            Fn fileDelete = (self, name, a2, a3, a4, a5, a6, a7, a8, a9) =>
            {
                try
                {
                    var path = CloudPath(Text(name));
                    if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                    return True;
                }
                catch { return Zero; }
            };
            Fn fileExists = (self, name, a2, a3, a4, a5, a6, a7, a8, a9) =>
                System.IO.File.Exists(CloudPath(Text(name))) ? True : Zero;
            Fn fileSize = (self, name, a2, a3, a4, a5, a6, a7, a8, a9) =>
            {
                try
                {
                    var path = CloudPath(Text(name));
                    return System.IO.File.Exists(path) ? new IntPtr(new System.IO.FileInfo(path).Length) : Zero;
                }
                catch { return Zero; }
            };
            Fn fileTimestamp = (self, name, a2, a3, a4, a5, a6, a7, a8, a9) =>
            {
                try
                {
                    var path = CloudPath(Text(name));
                    if (!System.IO.File.Exists(path)) return Zero;
                    var when = new DateTimeOffset(System.IO.File.GetLastWriteTimeUtc(path)).ToUnixTimeSeconds();
                    return new IntPtr(when);
                }
                catch { return Zero; }
            };
            Fn fileCount = (self, a1, a2, a3, a4, a5, a6, a7, a8, a9) =>
            {
                try { return new IntPtr(System.IO.Directory.GetFiles(CloudDir()).Length); }
                catch { return Zero; }
            };
            Fn fileNameAndSize = (self, iFile, sizeOut, a3, a4, a5, a6, a7, a8, a9) =>
            {
                try
                {
                    var files = System.IO.Directory.GetFiles(CloudDir());
                    var index = (int)iFile.ToInt64();
                    if (index < 0 || index >= files.Length)
                    {
                        if (sizeOut != IntPtr.Zero) Marshal.WriteInt32(sizeOut, 0);
                        return Utf8(string.Empty);
                    }
                    var info = new System.IO.FileInfo(files[index]);
                    if (sizeOut != IntPtr.Zero) Marshal.WriteInt32(sizeOut, (int)info.Length);
                    return Utf8(System.IO.Path.GetFileName(files[index]));
                }
                catch
                {
                    if (sizeOut != IntPtr.Zero) Marshal.WriteInt32(sizeOut, 0);
                    return Utf8(string.Empty);
                }
            };
            Fn getQuota = (self, totalOut, availableOut, a3, a4, a5, a6, a7, a8, a9) =>
            {
                const int totalBytes = 200 * 1024 * 1024; // matches Steam's usual per-app cloud quota order of magnitude
                var used = 0L;
                try
                {
                    foreach (var file in System.IO.Directory.GetFiles(CloudDir()))
                        used += new System.IO.FileInfo(file).Length;
                }
                catch { /* quota still answers something usable */ }
                if (totalOut != IntPtr.Zero) Marshal.WriteInt32(totalOut, totalBytes);
                if (availableOut != IntPtr.Zero)
                    Marshal.WriteInt32(availableOut, (int)Math.Max(totalBytes - used, 0));
                return True;
            };

            return Table(
                /* 0  FileWrite                        */ Keep(fileWrite),
                /* 1  FileRead                         */ Keep(fileRead),
                /* 2  FileForget                       */ stubTrue,
                /* 3  FileDelete                       */ Keep(fileDelete),
                /* 4  FileShare                        */ stubZero,
                /* 5  SetSyncPlatforms                 */ stubTrue,
                /* 6  FileExists                       */ Keep(fileExists),
                /* 7  FilePersisted                    */ Keep(fileExists),
                /* 8  GetFileSize                      */ Keep(fileSize),
                /* 9  GetFileTimestamp                 */ Keep(fileTimestamp),
                /* 10 GetSyncPlatforms                 */ stubMinusOne, // k_ERemoteStoragePlatformAll = 0xffffffff
                /* 11 GetFileCount                     */ Keep(fileCount),
                /* 12 GetFileNameAndSize               */ Keep(fileNameAndSize),
                /* 13 GetQuota                         */ Keep(getQuota),
                /* 14 IsCloudEnabledForAccount         */ stubTrue,
                /* 15 IsCloudEnabledForApp             */ stubTrue,
                /* 16 SetCloudEnabledForApp            */ stubZero,
                /* 17 UGCDownload                      */ stubZero,
                /* 18 GetUGCDownloadProgress           */ stubZero,
                /* 19 GetUGCDetails                    */ stubZero,
                /* 20 UGCRead                          */ stubZero,
                /* 21 GetCachedUGCCount                */ stubZero,
                /* 22 GetCachedUGCHandle               */ stubZero,
                /* 23 PublishWorkshopFile              */ stubZero,
                /* 24 CreatePublishedFileUpdateRequest */ stubZero,
                /* 25 UpdatePublishedFileFile          */ stubZero,
                /* 26 UpdatePublishedFilePreviewFile   */ stubZero,
                /* 27 UpdatePublishedFileTitle         */ stubZero,
                /* 28 UpdatePublishedFileDescription   */ stubZero,
                /* 29 UpdatePublishedFileVisibility    */ stubZero,
                /* 30 UpdatePublishedFileTags          */ stubZero,
                /* 31 CommitPublishedFileUpdate        */ stubZero,
                /* 32 GetPublishedFileDetails          */ stubZero,
                /* 33 DeletePublishedFile              */ stubZero,
                /* 34 EnumerateUserPublishedFiles      */ stubZero,
                /* 35 SubscribePublishedFile           */ stubZero,
                /* 36 EnumerateUserSubscribedFiles     */ stubZero,
                /* 37 UnsubscribePublishedFile         */ stubZero,
                /* 38 UpdatePublishedFileSetChangeDescription */ stubZero,
                /* 39 GetPublishedItemVoteDetails      */ stubZero,
                /* 40 UpdateUserPublishedItemVote      */ stubZero,
                /* 41 GetUserPublishedItemVoteDetails  */ stubZero,
                /* 42 EnumerateUserSharedWorkshopFiles */ stubZero,
                /* 43 PublishVideo                     */ stubZero,
                /* 44 SetUserPublishedFileAction       */ stubZero,
                /* 45 EnumeratePublishedFilesByUserAction */ stubZero,
                /* 46 EnumeratePublishedWorkshopFiles  */ stubZero
            );
        }

        // ---- ISteamClient012 -----------------------------------------------------
        // 26 slots. Not in LEGO Jurassic World's static import table (it calls
        // SteamUser()/SteamFriends()/... directly), built for completeness and
        // for any classic game that goes through SteamInternal_CreateInterface
        // instead. The Get*() accessors ignore hSteamUser/hSteamPipe/pchVersion
        // and hand back the same singleton objects the direct accessors do.

        private static IntPtr BuildClient()
        {
            Fn getUser = (self, a1, a2, a3, a4, a5, a6, a7, a8, a9) => User();
            Fn getFriends = (self, a1, a2, a3, a4, a5, a6, a7, a8, a9) => Friends();
            Fn getUtils = (self, a1, a2, a3, a4, a5, a6, a7, a8, a9) => Utils();
            Fn getApps = (self, a1, a2, a3, a4, a5, a6, a7, a8, a9) => Apps();
            Fn getUserStats = (self, a1, a2, a3, a4, a5, a6, a7, a8, a9) => UserStats();
            Fn getRemoteStorage = (self, a1, a2, a3, a4, a5, a6, a7, a8, a9) => RemoteStorage();
            Fn getEmpty = (self, a1, a2, a3, a4, a5, a6, a7, a8, a9) => Empty();

            return Table(
                /* 0  CreateSteamPipe               */ stubTrue,
                /* 1  BReleaseSteamPipe             */ stubTrue,
                /* 2  ConnectToGlobalUser           */ stubTrue,
                /* 3  CreateLocalUser               */ stubZero,
                /* 4  ReleaseUser                   */ stubZero,
                /* 5  GetISteamUser                 */ Keep(getUser),
                /* 6  GetISteamGameServer           */ Keep(getEmpty),
                /* 7  SetLocalIPBinding             */ stubZero,
                /* 8  GetISteamFriends              */ Keep(getFriends),
                /* 9  GetISteamUtils                */ Keep(getUtils),
                /* 10 GetISteamMatchmaking          */ Keep(getEmpty),
                /* 11 GetISteamMatchmakingServers   */ Keep(getEmpty),
                /* 12 GetISteamGenericInterface     */ Keep(getEmpty),
                /* 13 GetISteamUserStats            */ Keep(getUserStats),
                /* 14 GetISteamGameServerStats      */ Keep(getEmpty),
                /* 15 GetISteamApps                 */ Keep(getApps),
                /* 16 GetISteamNetworking           */ Keep(getEmpty),
                /* 17 GetISteamRemoteStorage        */ Keep(getRemoteStorage),
                /* 18 GetISteamScreenshots          */ Keep(getEmpty),
                /* 19 RunFrame                      */ stubZero,
                /* 20 GetIPCCallCount               */ stubZero,
                /* 21 SetWarningMessageHook         */ stubZero,
                /* 22 BShutdownIfAllPipesClosed     */ stubTrue,
                /* 23 GetISteamHTTP                 */ Keep(getEmpty),
                /* 24 GetISteamUnifiedMessages      */ Keep(getEmpty),
                /* 25 GetISteamController           */ Keep(getEmpty)
            );
        }
    }
}
