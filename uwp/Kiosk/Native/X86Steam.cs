using System;
using Nativra.X86.Loader;

namespace Kiosk.Native
{
    /// <summary>
    /// The signed-in account for a 32-bit game's steam_api.dll: the same
    /// account, achievements and stats SteamBridge keeps for 64-bit games, so
    /// what a game earns reaches Steam the same way whatever its bitness.
    /// </summary>
    internal sealed class X86SteamAccount : IGuestSteamAccount
    {
        public ulong SteamId => SteamBridge.SteamId;
        public uint AppId => SteamBridge.AppId;
        public string PersonaName => Steam.SteamStats.PersonaName ?? SteamBridge.PersonaName;
        public string Language => SteamBridge.Language;
        public string Country => "BR";

        public bool TryGetAchievement(string name, out uint unlockTime)
        {
            var achieved = SteamBridge.Achieved;
            lock (achieved)
            {
                var got = achieved.TryGetValue(name, out var when);
                unlockTime = (uint)when;
                return got;
            }
        }

        public void SetAchievement(string name)
        {
            var achieved = SteamBridge.Achieved;
            lock (achieved)
            {
                if (achieved.ContainsKey(name)) return;
                achieved[name] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            }
            SteamBridge.Save();
        }

        public void ClearAchievement(string name)
        {
            var achieved = SteamBridge.Achieved;
            lock (achieved) achieved.Remove(name);
            SteamBridge.Save();
        }

        public bool TryGetStat(string name, out int value)
        {
            var numbers = SteamBridge.Numbers;
            lock (numbers) return numbers.TryGetValue(name, out value);
        }

        public void SetStat(string name, int value)
        {
            var numbers = SteamBridge.Numbers;
            lock (numbers) numbers[name] = value;
        }

        public bool TryGetStat(string name, out float value)
        {
            var floats = SteamBridge.FloatStats;
            lock (floats) return floats.TryGetValue(name, out value);
        }

        public void SetStat(string name, float value)
        {
            var floats = SteamBridge.FloatStats;
            lock (floats) floats[name] = value;
        }

        public void StoreStats()
        {
            SteamBridge.Save();
            SteamBridge.Changed?.Invoke();
        }
    }
}
