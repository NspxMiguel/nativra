using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Kiosk.Native
{
    /// <summary>
    /// Answers the game's Steamworks calls with the signed-in account.
    ///
    /// A Steam game looks for a running Steam client through steam_api64.dll.
    /// There is no client on the console, so SteamAPI_Init fails and the game
    /// runs as if offline: no name, no achievements. Steamworks.NET reaches
    /// every Steam feature through the DLL's flat exports, which the game looks
    /// up by name through our loader — so the answers can come from here,
    /// backed by the session the application already holds.
    ///
    /// Anything not implemented answers zero (false, null, empty), which is
    /// what the SDK itself returns when a feature is unavailable. Nothing is
    /// ever forwarded to the real DLL once the bridge is active: the real
    /// functions would dereference our stand-in interface pointers.
    /// </summary>
    internal static class SteamBridge
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr Any(IntPtr a, IntPtr b, IntPtr c, IntPtr d);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr WithFloat(IntPtr a, IntPtr b, float value, IntPtr d);

        public static bool Active;
        public static ulong SteamId;
        public static uint AppId;
        public static string PersonaName = string.Empty;
        public static string Language = "english";
        public static string SavePath;

        /// <summary>Raised when the game stores stats, so they can reach Steam.</summary>
        public static Action Changed;

        internal static IDictionary<string, long> Achieved => achieved;
        internal static IDictionary<string, int> Numbers => intStats;

        /// <summary>Calls the bridge answered, by export name, for the report.</summary>
        public static readonly Dictionary<string, long> Calls = new Dictionary<string, long>();

        private static readonly Dictionary<string, IntPtr> answers = new Dictionary<string, IntPtr>();
        private static readonly List<Delegate> keep = new List<Delegate>();
        private static readonly Dictionary<string, IntPtr> strings = new Dictionary<string, IntPtr>();
        private static readonly Dictionary<string, IntPtr> interfaces = new Dictionary<string, IntPtr>();
        private static readonly Queue<KeyValuePair<int, byte[]>> pending = new Queue<KeyValuePair<int, byte[]>>();
        private static readonly Dictionary<string, long> achieved = new Dictionary<string, long>();
        private static readonly Dictionary<string, int> intStats = new Dictionary<string, int>();
        private static readonly Dictionary<string, float> floatStats = new Dictionary<string, float>();
        private static IntPtr delivered;
        private static IntPtr zero;
        private static IntPtr empty;

        private const int UserStatsReceived = 1101;
        private const int UserStatsStored = 1102;
        private const int UserAchievementStored = 1103;

        public static bool Serves(string module) =>
            Active && module != null && module.StartsWith("steam_api64", StringComparison.OrdinalIgnoreCase);

        private static void Count(string name)
        {
            lock (Calls)
            {
                Calls.TryGetValue(name, out var seen);
                Calls[name] = seen + 1;
            }
        }

        private static IntPtr Utf8(string text)
        {
            lock (strings)
            {
                if (strings.TryGetValue(text, out var kept)) return kept;
                var bytes = Encoding.UTF8.GetBytes(text + "\0");
                var memory = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, memory, bytes.Length);
                strings[text] = memory;
                return memory;
            }
        }

        private static string Text(IntPtr pointer)
        {
            if (pointer == IntPtr.Zero) return string.Empty;
            var length = 0;
            while (Marshal.ReadByte(pointer, length) != 0) length++;
            var bytes = new byte[length];
            Marshal.Copy(pointer, bytes, 0, length);
            return Encoding.UTF8.GetString(bytes);
        }

        /// <summary>A distinct, readable stand-in for an interface object.</summary>
        private static IntPtr Interface(string name)
        {
            lock (interfaces)
            {
                if (interfaces.TryGetValue(name, out var kept)) return kept;
                var memory = Marshal.AllocHGlobal(64);
                for (var i = 0; i < 64; i += 8) Marshal.WriteInt64(memory, i, 0);
                interfaces[name] = memory;
                return memory;
            }
        }

        private static void Post(int id, byte[] payload)
        {
            lock (pending) pending.Enqueue(new KeyValuePair<int, byte[]>(id, payload));
        }

        private static byte[] StatsReceived()
        {
            var body = new byte[24];
            BitConverter.GetBytes((ulong)AppId).CopyTo(body, 0);
            BitConverter.GetBytes(1).CopyTo(body, 8); // k_EResultOK
            BitConverter.GetBytes(SteamId).CopyTo(body, 16);
            return body;
        }

        private static byte[] StatsStored()
        {
            var body = new byte[16];
            BitConverter.GetBytes((ulong)AppId).CopyTo(body, 0);
            BitConverter.GetBytes(1).CopyTo(body, 8);
            return body;
        }

        private static byte[] AchievementStored(string name)
        {
            var body = new byte[152];
            BitConverter.GetBytes((ulong)AppId).CopyTo(body, 0);
            var bytes = Encoding.UTF8.GetBytes(name);
            Array.Copy(bytes, 0, body, 9, Math.Min(bytes.Length, 127));
            return body;
        }

        private static void Answer(string name, Any body)
        {
            Any counted = (a, b, c, d) =>
            {
                Count(name);
                try { return body(a, b, c, d); }
                catch { return IntPtr.Zero; }
            };
            keep.Add(counted);
            answers[name] = Marshal.GetFunctionPointerForDelegate(counted);
        }

        private static IntPtr True => new IntPtr(1);

        /// <summary>The address to hand the game for one steam_api64 export.</summary>
        public static IntPtr Resolve(string export)
        {
            if (!Active || string.IsNullOrEmpty(export)) return IntPtr.Zero;
            lock (answers)
            {
                if (answers.Count == 0) Build();
                if (answers.TryGetValue(export, out var known)) return known;

                // Interface getters must never answer null: Steamworks.NET
                // refuses to initialise when any of them does.
                if (export.StartsWith("SteamAPI_ISteamClient_GetISteam", StringComparison.Ordinal))
                {
                    var name = export.Substring("SteamAPI_ISteamClient_Get".Length);
                    Answer(export, (a, b, c, d) => Interface(name));
                    return answers[export];
                }

                // Names and text come back as an empty string rather than
                // null, which managed wrappers turn into exceptions.
                if (export.Contains("Name") || export.Contains("Language") ||
                    export.Contains("Country") || export.Contains("RichPresence") ||
                    export.Contains("String") || export.Contains("Path"))
                {
                    Answer(export, (a, b, c, d) => empty);
                    return answers[export];
                }

                Answer(export, (a, b, c, d) => IntPtr.Zero);
                return answers[export];
            }
        }

        private static void Build()
        {
            zero = Marshal.AllocHGlobal(16);
            for (var i = 0; i < 16; i += 8) Marshal.WriteInt64(zero, i, 0);
            empty = zero;
            delivered = IntPtr.Zero;
            Load();

            Answer("SteamAPI_RestartAppIfNecessary", (a, b, c, d) => IntPtr.Zero);
            Answer("SteamAPI_Init", (a, b, c, d) => True);
            Answer("SteamAPI_IsSteamRunning", (a, b, c, d) => True);
            Answer("SteamAPI_GetHSteamPipe", (a, b, c, d) => True);
            Answer("SteamAPI_GetHSteamUser", (a, b, c, d) => True);
            Answer("SteamClient", (a, b, c, d) => Interface("SteamClient"));
            Answer("SteamInternal_CreateInterface", (a, b, c, d) => Interface(Text(a)));
            Answer("SteamInternal_FindOrCreateUserInterface", (a, b, c, d) => Interface(Text(b)));
            Answer("SteamInternal_ContextInit", (a, b, c, d) => a);
            Answer("SteamAPI_ISteamClient_CreateSteamPipe", (a, b, c, d) => True);
            Answer("SteamAPI_ISteamClient_ConnectToGlobalUser", (a, b, c, d) => True);

            // Callbacks, the way Steamworks.NET 20 collects them.
            Answer("SteamAPI_ManualDispatch_GetNextCallback", (pipe, message, c, d) =>
            {
                if (message == IntPtr.Zero) return IntPtr.Zero;
                KeyValuePair<int, byte[]> next;
                lock (pending)
                {
                    if (pending.Count == 0) return IntPtr.Zero;
                    next = pending.Dequeue();
                }
                if (delivered != IntPtr.Zero) Marshal.FreeHGlobal(delivered);
                delivered = Marshal.AllocHGlobal(next.Value.Length);
                Marshal.Copy(next.Value, 0, delivered, next.Value.Length);
                // CallbackMsg_t: user, callback id, parameter, parameter size.
                Marshal.WriteInt32(message, 0, 1);
                Marshal.WriteInt32(message, 4, next.Key);
                Marshal.WriteIntPtr(message, 8, delivered);
                Marshal.WriteInt32(message, 16, next.Value.Length);
                return True;
            });

            // Who is playing.
            Answer("SteamAPI_ISteamUser_GetSteamID", (a, b, c, d) => new IntPtr((long)SteamId));
            Answer("SteamAPI_ISteamUser_BLoggedOn", (a, b, c, d) => True);
            Answer("SteamAPI_ISteamUser_GetHSteamUser", (a, b, c, d) => True);
            Answer("SteamAPI_ISteamUser_GetPlayerSteamLevel", (a, b, c, d) => IntPtr.Zero);
            Answer("SteamAPI_ISteamFriends_GetPersonaName", (a, b, c, d) => Utf8(PersonaName));
            Answer("SteamAPI_ISteamFriends_GetPersonaState", (a, b, c, d) => True); // online

            // Which game, and that he owns it: the download already proved it.
            Answer("SteamAPI_ISteamUtils_GetAppID", (a, b, c, d) => new IntPtr(AppId));
            Answer("SteamAPI_ISteamUtils_GetIPCountry", (a, b, c, d) => Utf8("BR"));
            Answer("SteamAPI_ISteamUtils_IsOverlayEnabled", (a, b, c, d) => IntPtr.Zero);
            Answer("SteamAPI_ISteamUtils_GetConnectedUniverse", (a, b, c, d) => True); // public
            Answer("SteamAPI_ISteamUtils_GetServerRealTime", (a, b, c, d) =>
                new IntPtr(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            Answer("SteamAPI_ISteamApps_BIsSubscribed", (a, b, c, d) => True);
            Answer("SteamAPI_ISteamApps_BIsSubscribedApp", (a, b, c, d) =>
                (uint)b.ToInt64() == AppId ? True : IntPtr.Zero);
            Answer("SteamAPI_ISteamApps_BIsAppInstalled", (a, b, c, d) =>
                (uint)b.ToInt64() == AppId ? True : IntPtr.Zero);
            Answer("SteamAPI_ISteamApps_GetCurrentGameLanguage", (a, b, c, d) => Utf8(Language));
            Answer("SteamAPI_ISteamApps_GetAvailableGameLanguages", (a, b, c, d) => Utf8(Language));
            Answer("SteamAPI_ISteamApps_GetAppOwner", (a, b, c, d) => new IntPtr((long)SteamId));

            // Stats and achievements, kept in the app until they reach Steam.
            Answer("SteamAPI_ISteamUserStats_RequestCurrentStats", (a, b, c, d) =>
            {
                Post(UserStatsReceived, StatsReceived());
                return True;
            });
            Answer("SteamAPI_ISteamUserStats_GetAchievement", (self, name, achievedOut, d) =>
            {
                bool got;
                lock (achieved) got = achieved.ContainsKey(Text(name));
                if (achievedOut != IntPtr.Zero) Marshal.WriteByte(achievedOut, (byte)(got ? 1 : 0));
                return True;
            });
            Answer("SteamAPI_ISteamUserStats_GetAchievementAndUnlockTime", (self, name, achievedOut, time) =>
            {
                long when;
                bool got;
                lock (achieved) got = achieved.TryGetValue(Text(name), out when);
                if (achievedOut != IntPtr.Zero) Marshal.WriteByte(achievedOut, (byte)(got ? 1 : 0));
                if (time != IntPtr.Zero) Marshal.WriteInt32(time, got ? (int)when : 0);
                return True;
            });
            Answer("SteamAPI_ISteamUserStats_SetAchievement", (self, name, c, d) =>
            {
                var key = Text(name);
                bool fresh;
                lock (achieved)
                {
                    fresh = !achieved.ContainsKey(key);
                    if (fresh) achieved[key] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                }
                if (fresh)
                {
                    Save();
                    Post(UserAchievementStored, AchievementStored(key));
                }
                return True;
            });
            Answer("SteamAPI_ISteamUserStats_ClearAchievement", (self, name, c, d) =>
            {
                lock (achieved) achieved.Remove(Text(name));
                Save();
                return True;
            });
            Answer("SteamAPI_ISteamUserStats_GetStatInt32", (self, name, value, d) =>
            {
                int kept;
                lock (intStats) intStats.TryGetValue(Text(name), out kept);
                if (value != IntPtr.Zero) Marshal.WriteInt32(value, kept);
                return True;
            });
            Answer("SteamAPI_ISteamUserStats_SetStatInt32", (self, name, value, d) =>
            {
                lock (intStats) intStats[Text(name)] = (int)value.ToInt64();
                return True;
            });
            Answer("SteamAPI_ISteamUserStats_GetStatFloat", (self, name, value, d) =>
            {
                float kept;
                lock (floatStats) floatStats.TryGetValue(Text(name), out kept);
                if (value != IntPtr.Zero)
                    Marshal.WriteInt32(value, BitConverter.ToInt32(BitConverter.GetBytes(kept), 0));
                return True;
            });
            // The float arrives in a vector register, so it needs its own shape.
            WithFloat setFloat = (self, name, value, d) =>
            {
                Count("SteamAPI_ISteamUserStats_SetStatFloat");
                lock (floatStats) floatStats[Text(name)] = value;
                return True;
            };
            keep.Add(setFloat);
            answers["SteamAPI_ISteamUserStats_SetStatFloat"] = Marshal.GetFunctionPointerForDelegate(setFloat);
            Answer("SteamAPI_ISteamUserStats_StoreStats", (a, b, c, d) =>
            {
                Save();
                Changed?.Invoke();
                Post(UserStatsStored, StatsStored());
                return True;
            });
        }

        /// <summary>Achievements and stats earned, as they will be sent to Steam.</summary>
        public static string Summary()
        {
            lock (achieved) return achieved.Count + " achievements, " + (intStats.Count + floatStats.Count) + " stats";
        }

        private static void Load()
        {
            try
            {
                if (SavePath == null || !System.IO.File.Exists(SavePath)) return;
                foreach (var line in System.IO.File.ReadAllLines(SavePath))
                {
                    var parts = line.Split('\t');
                    if (parts.Length != 3) continue;
                    if (parts[0] == "a" && long.TryParse(parts[2], out var when)) achieved[parts[1]] = when;
                    else if (parts[0] == "i" && int.TryParse(parts[2], out var number)) intStats[parts[1]] = number;
                    else if (parts[0] == "f" && float.TryParse(parts[2],
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var real)) floatStats[parts[1]] = real;
                }
            }
            catch
            {
                // A broken record starts empty; the game asks again.
            }
        }

        private static void Save()
        {
            if (SavePath == null) return;
            try
            {
                var lines = new List<string>();
                lock (achieved) foreach (var pair in achieved) lines.Add("a\t" + pair.Key + "\t" + pair.Value);
                lock (intStats) foreach (var pair in intStats) lines.Add("i\t" + pair.Key + "\t" + pair.Value);
                lock (floatStats)
                    foreach (var pair in floatStats)
                        lines.Add("f\t" + pair.Key + "\t" +
                            pair.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                System.IO.File.WriteAllLines(SavePath, lines);
            }
            catch
            {
                // Kept in memory; written on the next change.
            }
        }
    }
}
