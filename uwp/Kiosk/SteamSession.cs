using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Storage;

namespace Kiosk
{
    public sealed class SteamSignInRequiredException : Exception
    {
        public SteamSignInRequiredException() : base("Steam sign-in is required.") { }
    }

    /// <summary>
    /// What the console remembers about the signed-in Steam account. It lives in
    /// the app's own folder and never leaves the console.
    /// </summary>
    public sealed class SteamSession
    {
        private const string FileName = "steam.json";

        public string AccountName { get; set; }
        public string RefreshToken { get; set; }
        public string AccessToken { get; set; }
        public ulong SteamId { get; set; }

        public bool IsSignedIn => !string.IsNullOrEmpty(RefreshToken) && SteamId != 0;

        // One session for the whole app. Renewing can rotate the refresh token,
        // and Steam then refuses the old one: with a copy per screen, a download
        // that started while the home screen renewed logged on with the old token,
        // got eresult 5, and the app deleted a sign-in that was perfectly good.
        private static SteamSession current;
        private static readonly SemaphoreSlim renewing = new SemaphoreSlim(1, 1);

        public static async Task<SteamSession> LoadAsync()
        {
            var shared = current;
            if (shared != null && shared.IsSignedIn) return shared;
            var loaded = await ReadAsync();
            current = loaded;
            return loaded;
        }

        private static async Task<SteamSession> ReadAsync()
        {
            try
            {
                var file = await ApplicationData.Current.LocalFolder
                    .TryGetItemAsync(FileName) as StorageFile;
                if (file == null) return new SteamSession();

                var text = await FileIO.ReadTextAsync(file);
                if (!JsonObject.TryParse(text, out var root)) return new SteamSession();

                var session = new SteamSession
                {
                    AccountName = root.GetNamedString("account", string.Empty),
                    RefreshToken = root.GetNamedString("refresh", string.Empty),
                    AccessToken = root.GetNamedString("access", string.Empty),
                };
                var id = root.GetNamedString("steamid", string.Empty);
                if (ulong.TryParse(id, out var parsed)) session.SteamId = parsed;
                return session;
            }
            catch
            {
                return new SteamSession();
            }
        }

        public async Task SaveAsync()
        {
            current = this;
            try
            {
                var root = new JsonObject
                {
                    { "account", JsonValue.CreateStringValue(AccountName ?? string.Empty) },
                    { "refresh", JsonValue.CreateStringValue(RefreshToken ?? string.Empty) },
                    { "access", JsonValue.CreateStringValue(AccessToken ?? string.Empty) },
                    { "steamid", JsonValue.CreateStringValue(SteamId.ToString()) },
                };
                var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    FileName, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, root.Stringify());
            }
            catch
            {
                // Failing to persist must not undo a successful sign-in.
            }
        }

        public async Task ClearAsync()
        {
            if (current == this) current = null;
            AccountName = RefreshToken = AccessToken = null;
            SteamId = 0;
            try
            {
                var file = await ApplicationData.Current.LocalFolder
                    .TryGetItemAsync(FileName) as StorageFile;
                if (file != null) await file.DeleteAsync();
            }
            catch
            {
                // Nothing to do: the caller already treats us as signed out.
            }
        }

        /// <summary>
        /// Steam's tokens are JWTs whose "sub" claim is the steamid — reading it
        /// here saves a round trip and needs no publisher API key.
        /// </summary>
        public static ulong SteamIdFromToken(string token)
        {
            try
            {
                if (string.IsNullOrEmpty(token)) return 0;
                var parts = token.Split('.');
                if (parts.Length < 2) return 0;

                var payload = parts[1].Replace('-', '+').Replace('_', '/');
                switch (payload.Length % 4)
                {
                    case 2: payload += "=="; break;
                    case 3: payload += "="; break;
                }

                var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
                if (!JsonObject.TryParse(json, out var claims)) return 0;
                var sub = claims.GetNamedString("sub", string.Empty);
                return ulong.TryParse(sub, out var id) ? id : 0;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Returns an access token that is good right now, minting a fresh one
        /// when the stored one is missing or has been refused.
        /// </summary>
        public async Task<string> EnsureAccessTokenAsync(bool force = false)
        {
            if (!force && !string.IsNullOrEmpty(AccessToken)) return AccessToken;
            // One renewal at a time: two at once would each rotate the token and
            // the slower one would save a token Steam had already replaced.
            var asked = RefreshToken;
            await renewing.WaitAsync();
            try
            {
                // Someone renewed while this waited: theirs is the token to use.
                if (RefreshToken != asked && !string.IsNullOrEmpty(AccessToken)) return AccessToken;
                if (string.IsNullOrEmpty(RefreshToken) || SteamId == 0)
                    throw new SteamSignInRequiredException();

                var tokens = await SteamAuth.RenewTokensAsync(RefreshToken, SteamId);
                var fresh = tokens.Item1;
                if (string.IsNullOrEmpty(fresh)) throw new SteamSignInRequiredException();

                AccessToken = fresh;
                if (!string.IsNullOrEmpty(tokens.Item2)) RefreshToken = tokens.Item2;
                await SaveAsync();
                return fresh;
            }
            finally
            {
                renewing.Release();
            }
        }

        /// <summary>
        /// Called when Steam refused a log-on with this session. The refusal may
        /// have been for a token that was rotated a moment earlier, so the
        /// sign-in is forgotten only when renewing with the current refresh token
        /// also fails — Steam itself confirming the session is gone. True when it
        /// was forgotten; false when the session still works and the caller can
        /// simply try again.
        /// </summary>
        public async Task<bool> ForgetIfRefusedAsync()
        {
            try
            {
                await EnsureAccessTokenAsync(force: true);
                return false;
            }
            catch (SteamSignInRequiredException)
            {
                await ClearAsync();
                return true;
            }
            catch
            {
                // No answer from Steam is not a refusal: keep the sign-in.
                return false;
            }
        }
    }
}
