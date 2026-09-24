using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Kiosk.Steam
{
    /// <summary>
    /// Keeps the account "in game" while a game runs, and stores what the game
    /// unlocks on the account.
    ///
    /// The Steam client does both over its connection manager session: it
    /// reports the game being played, which is what friends see, and it writes
    /// achievements as bits in the game's stats. The same session is opened
    /// here with the saved sign-in, so the console does what the client would.
    /// </summary>
    public static class SteamStats
    {
        private const int EMsgGamesPlayed = 742;
        private const int EMsgGetUserStats = 818;
        private const int EMsgStoreUserStats2 = 5466;
        private const string TypeInt = "1";
        private const string TypeAchievements = "4";

        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
        private static SteamCm connection;
        private static SteamSession account;
        private static uint playing;

        /// <summary>The last thing that happened, for the report.</summary>
        public static string Note = "idle";

        /// <summary>Achievements confirmed on the account.</summary>
        public static int Stored;

        private static async Task<SteamCm> ConnectedAsync()
        {
            if (connection != null) return connection;
            var endpoints = await SteamCm.EndpointsAsync();
            if (endpoints.Count == 0) throw new Exception("Steam listed no connection managers");
            var cm = new SteamCm();
            try
            {
                await cm.ConnectAsync(endpoints[0]);
                await cm.LogOnAsync(account.SteamId, account.RefreshToken);
            }
            catch
            {
                cm.Dispose();
                throw;
            }
            connection = cm;
            return cm;
        }

        private static void Drop()
        {
            try { connection?.Dispose(); } catch { }
            connection = null;
        }

        /// <summary>Shows the account as playing this game.</summary>
        public static async Task PlayAsync(SteamSession session, uint appId)
        {
            account = session;
            playing = appId;
            await Gate.WaitAsync();
            try
            {
                var cm = await ConnectedAsync();
                await cm.NotifyAsync(EMsgGamesPlayed, GamesPlayed(appId));
                Note = "playing " + appId;
            }
            catch (Exception error)
            {
                Note = "play: " + error.Message;
                Drop();
            }
            finally
            {
                Gate.Release();
            }
        }

        private static byte[] GamesPlayed(uint appId)
        {
            var writer = new ProtoWriter();
            if (appId != 0)
            {
                writer.Message(1, new ProtoWriter().Fixed64(2, appId).String(7, "Nativra"));
            }
            return writer.Finish();
        }

        /// <summary>Clears the playing state when the game ends.</summary>
        public static async Task StopAsync()
        {
            await Gate.WaitAsync();
            try
            {
                if (connection != null) await connection.NotifyAsync(EMsgGamesPlayed, GamesPlayed(0));
            }
            catch
            {
                // The session ends with the connection either way.
            }
            finally
            {
                Drop();
                playing = 0;
                Gate.Release();
            }
        }

        /// <summary>
        /// Writes unlocked achievements and integer stats to the account.
        /// Only bits and values that change are sent; Steam rejects a store
        /// whose checksum does not match the stats it last handed out.
        /// </summary>
        public static async Task SyncAsync(
            IDictionary<string, long> achieved, IDictionary<string, int> numbers)
        {
            if (account == null || playing == 0) return;
            await Gate.WaitAsync();
            try
            {
                var cm = await ConnectedAsync();
                var answer = await cm.RequestAsync(EMsgGetUserStats, new ProtoWriter()
                    .Fixed64(1, playing)
                    .Uint(2, 0)
                    .Uint(3, ulong.MaxValue) // schema_local_version -1: send the schema
                    .Fixed64(4, account.SteamId)
                    .Finish());
                var eresult = (int)answer.Num(2, 2);
                if (eresult != 1)
                {
                    Note = "stats refused: eresult " + eresult;
                    return;
                }
                var crc = (uint)answer.Num(3);
                var values = new Dictionary<uint, uint>();
                foreach (var stat in answer.List(5)) values[(uint)stat.Num(1)] = (uint)stat.Num(2);

                var schema = answer.Raw(4);
                if (schema == null)
                {
                    Note = "no stats schema";
                    return;
                }
                var stats = Find(KeyValue.Parse(schema), "stats", 3);
                if (stats == null)
                {
                    Note = "schema has no stats";
                    return;
                }

                var changed = new Dictionary<uint, uint>();
                var unlocked = 0;
                foreach (var pair in stats.Children)
                {
                    if (!uint.TryParse(pair.Key, out var id)) continue;
                    var node = pair.Value;
                    values.TryGetValue(id, out var now);
                    var next = now;
                    var type = node.Text("type") ?? node.Text("type_int");
                    if (type == TypeAchievements || type == "ACHIEVEMENTS")
                    {
                        var bits = node["bits"];
                        if (bits == null) continue;
                        foreach (var bit in bits.Children)
                        {
                            var name = bit.Value.Text("name");
                            if (name == null || !uint.TryParse(bit.Value.Text("bit") ?? bit.Key, out var index)) continue;
                            bool got;
                            lock (achieved) got = achieved.ContainsKey(name);
                            if (!got || index > 31) continue;
                            next |= 1u << (int)index;
                        }
                    }
                    else if (type == TypeInt || type == "INT")
                    {
                        var name = node.Text("name");
                        var value = 0;
                        bool got;
                        lock (numbers) got = name != null && numbers.TryGetValue(name, out value);
                        if (got) next = unchecked((uint)value);
                    }
                    if (next != now)
                    {
                        changed[id] = next;
                        if (type == TypeAchievements || type == "ACHIEVEMENTS")
                            unlocked += Count(next) - Count(now);
                    }
                }

                if (changed.Count == 0)
                {
                    Note = "stats in step (crc " + crc + ")";
                    return;
                }

                var store = new ProtoWriter()
                    .Fixed64(1, playing)
                    .Fixed64(2, account.SteamId)
                    .Fixed64(3, account.SteamId)
                    .Uint(4, crc)
                    .Bool(5, false);
                foreach (var pair in changed)
                    store.Message(6, new ProtoWriter().Uint(1, pair.Key).Uint(2, pair.Value));
                var stored = await cm.RequestAsync(EMsgStoreUserStats2, store.Finish());
                var result = (int)stored.Num(2, 2);
                if (result == 1) Stored += unlocked;
                Note = "stored " + changed.Count + " stats, " + unlocked + " achievements: eresult " + result;
            }
            catch (Exception error)
            {
                Note = "sync: " + error.Message;
                Drop();
            }
            finally
            {
                Gate.Release();
            }
        }

        private static int Count(uint value)
        {
            var count = 0;
            for (; value != 0; value &= value - 1) count++;
            return count;
        }

        private static KeyValue Find(KeyValue node, string key, int depth)
        {
            if (node == null || depth < 0) return null;
            var direct = node[key];
            if (direct != null) return direct;
            foreach (var child in node.Children.Values)
            {
                var found = Find(child, key, depth - 1);
                if (found != null) return found;
            }
            return null;
        }
    }
}
