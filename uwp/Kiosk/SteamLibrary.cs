using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Windows.Data.Json;

namespace Kiosk
{
    public sealed class OwnedGame
    {
        public uint AppId { get; set; }
        public string Name { get; set; }
        public int MinutesPlayed { get; set; }

        /// <summary>The wide capsule Steam itself uses in its library grid.</summary>
        public string ArtUrl =>
            $"https://cdn.cloudflare.steamstatic.com/steam/apps/{AppId}/header.jpg";

        /// <summary>
        /// Built here rather than bound as a string: x:Bind does not run the
        /// string-to-ImageSource conversion that XAML markup does.
        /// </summary>
        public Windows.UI.Xaml.Media.ImageSource Art =>
            new Windows.UI.Xaml.Media.Imaging.BitmapImage(new Uri(ArtUrl));

        /// <summary>Null until someone has actually run it on a console.</summary>
        public bool Tested { get; set; }

        /// <summary>Came from a family library rather than from his own.</summary>
        public bool Shared { get; set; }

        /// <summary>Set by the page: he can turn the titles off.</summary>
        public static bool ShowTitles = true;

        public Windows.UI.Xaml.Visibility TitleShown =>
            ShowTitles ? Windows.UI.Xaml.Visibility.Visible : Windows.UI.Xaml.Visibility.Collapsed;

        public Windows.UI.Xaml.Media.Brush BadgeBrush =>
            new Windows.UI.Xaml.Media.SolidColorBrush(
                Tested
                    ? Windows.UI.Color.FromArgb(255, 59, 224, 129)
                    : Windows.UI.Color.FromArgb(90, 245, 245, 247));

        public string Played =>
            MinutesPlayed >= 60
                ? Texts.Get("steam.hours", MinutesPlayed / 60)
                : MinutesPlayed > 0 ? Texts.Get("steam.minutes", MinutesPlayed)
                : Texts.Get("steam.neverplayed");
    }

    /// <summary>
    /// Reads the account's own library. The access token from the QR sign-in is
    /// the whole authorisation — no publisher API key is involved.
    /// </summary>
    public static class SteamLibrary
    {
        private static readonly HttpClient Http = new HttpClient();

        public static async Task<List<OwnedGame>> OwnedAsync(SteamSession session)
        {
            var token = await session.EnsureAccessTokenAsync();
            if (string.IsNullOrEmpty(token)) throw new Exception("no access token");

            var games = await FetchAsync(session.SteamId, token);
            if (games != null) return games;

            // A stored token that Steam has since expired looks exactly like a
            // refusal, so mint a new one and ask once more before giving up.
            token = await session.EnsureAccessTokenAsync(force: true);
            if (string.IsNullOrEmpty(token)) throw new Exception("token renewal refused");

            games = await FetchAsync(session.SteamId, token);
            if (games == null) throw new Exception("Steam refused the library request");
            return games;
        }

        /// <summary>
        /// Games shared through a Steam family. They appear in his client and are
        /// not in GetOwnedGames, which is why the console's list looked short.
        /// </summary>
        public static async Task<List<OwnedGame>> FamilyAsync(SteamSession session)
        {
            var games = new List<OwnedGame>();
            try
            {
                var token = await session.EnsureAccessTokenAsync();
                if (string.IsNullOrEmpty(token)) return games;

                const string Base = "https://api.steampowered.com/IFamilyGroupsService";
                var groupText = await Http.GetStringAsync(
                    $"{Base}/GetFamilyGroupForUser/v1/?access_token={Uri.EscapeDataString(token)}"
                    + $"&steamid={session.SteamId}");
                if (!JsonObject.TryParse(groupText, out var groupRoot)) return games;

                var familyId = groupRoot.GetNamedObject("response", new JsonObject())
                    .GetNamedString("family_groupid", string.Empty);
                if (string.IsNullOrEmpty(familyId)) return games;

                var sharedText = await Http.GetStringAsync(
                    $"{Base}/GetSharedLibraryApps/v1/?access_token={Uri.EscapeDataString(token)}"
                    + $"&family_groupid={familyId}&include_own=true&include_excluded=true&include_free=true");
                if (!JsonObject.TryParse(sharedText, out var sharedRoot)) return games;

                var payload = sharedRoot.GetNamedObject("response", new JsonObject());
                if (!payload.ContainsKey("apps")) return games;

                foreach (var value in payload.GetNamedArray("apps"))
                {
                    var item = value.GetObject();
                    var name = item.GetNamedString("name", string.Empty);
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    games.Add(new OwnedGame
                    {
                        AppId = (uint)item.GetNamedNumber("appid", 0),
                        Name = name,
                        Shared = true,
                    });
                }
            }
            catch
            {
                // No family, or a refusal: the owned library still stands on its own.
            }
            return games;
        }

        /// <summary>Null means Steam refused the token; an empty list means no games.</summary>
        private static async Task<List<OwnedGame>> FetchAsync(ulong steamId, string token)
        {
            var url = "https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/"
                + "?access_token=" + Uri.EscapeDataString(token)
                + "&steamid=" + steamId
                + "&include_appinfo=true&include_played_free_games=true";

            var response = await Http.GetAsync(url);
            if ((int)response.StatusCode == 401 || (int)response.StatusCode == 403) return null;
            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"HTTP {(int)response.StatusCode}");
            }

            var text = await response.Content.ReadAsStringAsync();
            if (!JsonObject.TryParse(text, out var root)) return null;

            var payload = root.GetNamedObject("response", new JsonObject());
            var games = new List<OwnedGame>();
            if (!payload.ContainsKey("games"))
            {
                // Only an explicit zero count proves the library is empty.
                // An empty response must not hide a refused access token.
                return payload.ContainsKey("game_count") &&
                    payload.GetNamedNumber("game_count", -1) == 0 ? games : null;
            }

            foreach (var value in payload.GetNamedArray("games"))
            {
                var item = value.GetObject();
                var name = item.GetNamedString("name", string.Empty);
                if (string.IsNullOrWhiteSpace(name)) continue;
                games.Add(new OwnedGame
                {
                    AppId = (uint)item.GetNamedNumber("appid", 0),
                    Name = name,
                    MinutesPlayed = (int)item.GetNamedNumber("playtime_forever", 0),
                });
            }

            // Most played first: the console's home screen should open on what
            // the account actually plays.
            games.Sort((a, b) => b.MinutesPlayed.CompareTo(a.MinutesPlayed));
            return games;
        }
    }
}
