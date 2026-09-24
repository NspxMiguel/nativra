using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace Kiosk
{
    /// <summary>
    /// Steam's QR sign-in, spoken directly. This is the same public
    /// IAuthenticationService flow the official client uses: ask for a
    /// challenge, show it as a QR, poll until the phone approves. No password
    /// ever passes through here — approval happens on the phone.
    /// </summary>
    public sealed class QrSession
    {
        public ulong ClientId;
        public string ChallengeUrl;
        public byte[] RequestId;
        public double Interval = 5.0;
    }

    public sealed class LoginResult
    {
        public string AccountName;
        public string RefreshToken;
        public string AccessToken;
        public bool HadRemoteInteraction;
    }

    /// <summary>Just enough protobuf to speak this one service.</summary>
    internal static class Proto
    {
        public static void WriteVarint(Stream s, ulong value)
        {
            while (true)
            {
                var b = (byte)(value & 0x7F);
                value >>= 7;
                if (value != 0) b |= 0x80;
                s.WriteByte(b);
                if (value == 0) break;
            }
        }

        public static void WriteTag(Stream s, int field, int wire) =>
            WriteVarint(s, (ulong)((field << 3) | wire));

        public static void WriteUInt64(Stream s, int field, ulong value)
        {
            WriteTag(s, field, 0);
            WriteVarint(s, value);
        }

        /// <summary>A steamid travels as fixed64, not as a varint.</summary>
        public static void WriteFixed64(Stream s, int field, ulong value)
        {
            WriteTag(s, field, 1);
            s.Write(BitConverter.GetBytes(value), 0, 8);
        }

        public static void WriteString(Stream s, int field, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            WriteBytes(s, field, bytes);
        }

        public static void WriteBytes(Stream s, int field, byte[] value)
        {
            WriteTag(s, field, 2);
            WriteVarint(s, (ulong)value.Length);
            s.Write(value, 0, value.Length);
        }

        /// <summary>Field number to raw value; later repeats overwrite.</summary>
        public static Dictionary<int, object> Read(byte[] buf)
        {
            var fields = new Dictionary<int, object>();
            var i = 0;
            while (i < buf.Length)
            {
                ulong tag = 0;
                var shift = 0;
                while (i < buf.Length)
                {
                    var b = buf[i++];
                    tag |= (ulong)(b & 0x7F) << shift;
                    shift += 7;
                    if ((b & 0x80) == 0) break;
                }
                var field = (int)(tag >> 3);
                var wire = (int)(tag & 7);

                if (wire == 0)
                {
                    ulong val = 0;
                    shift = 0;
                    while (i < buf.Length)
                    {
                        var b = buf[i++];
                        val |= (ulong)(b & 0x7F) << shift;
                        shift += 7;
                        if ((b & 0x80) == 0) break;
                    }
                    fields[field] = val;
                }
                else if (wire == 2)
                {
                    ulong len = 0;
                    shift = 0;
                    while (i < buf.Length)
                    {
                        var b = buf[i++];
                        len |= (ulong)(b & 0x7F) << shift;
                        shift += 7;
                        if ((b & 0x80) == 0) break;
                    }
                    var data = new byte[len];
                    Array.Copy(buf, i, data, 0, (int)len);
                    i += (int)len;
                    fields[field] = data;
                }
                else if (wire == 5)
                {
                    fields[field] = BitConverter.ToSingle(buf, i);
                    i += 4;
                }
                else if (wire == 1)
                {
                    fields[field] = BitConverter.ToUInt64(buf, i);
                    i += 8;
                }
                else
                {
                    break;
                }
            }
            return fields;
        }

        public static string AsString(object value) =>
            value is byte[] b ? Encoding.UTF8.GetString(b) : null;
    }

    public static class SteamAuth
    {
        private const string Base = "https://api.steampowered.com/IAuthenticationService/";
        private static readonly HttpClient Http = new HttpClient();

        private static async Task<Dictionary<int, object>> CallAsync(string method, byte[] request)
        {
            var body = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>(
                    "input_protobuf_encoded", Convert.ToBase64String(request)),
            });
            var response = await Http.PostAsync(Base + method + "/v1/", body);
            var bytes = await response.Content.ReadAsByteArrayAsync();

            // Steam reports its own result code in a header, separate from HTTP.
            if (response.Headers.TryGetValues("x-eresult", out var values))
            {
                foreach (var v in values)
                {
                    if (v != "1") throw new Exception($"Steam eresult {v}");
                    break;
                }
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"HTTP {(int)response.StatusCode}");
            }
            return Proto.Read(bytes);
        }

        public static async Task<QrSession> BeginAsync(string deviceName)
        {
            using (var ms = new MemoryStream())
            {
                Proto.WriteString(ms, 1, deviceName);
                Proto.WriteUInt64(ms, 2, 1); // platform: Steam client
                var fields = await CallAsync("BeginAuthSessionViaQR", ms.ToArray());

                var session = new QrSession();
                if (fields.TryGetValue(1, out var cid) && cid is ulong id) session.ClientId = id;
                if (fields.TryGetValue(2, out var url)) session.ChallengeUrl = Proto.AsString(url);
                if (fields.TryGetValue(3, out var rid) && rid is byte[] r) session.RequestId = r;
                if (fields.TryGetValue(4, out var iv) && iv is float f && f > 0) session.Interval = f;

                if (string.IsNullOrEmpty(session.ChallengeUrl))
                {
                    throw new Exception("Steam returned no challenge");
                }
                return session;
            }
        }

        /// <summary>
        /// One poll. Returns the login once the phone approves; until then it may
        /// hand back a rotated challenge, and the QR on screen has to follow it.
        /// </summary>
        public static async Task<LoginResult> PollAsync(QrSession session)
        {
            using (var ms = new MemoryStream())
            {
                Proto.WriteUInt64(ms, 1, session.ClientId);
                Proto.WriteBytes(ms, 2, session.RequestId ?? new byte[0]);
                var fields = await CallAsync("PollAuthSessionStatus", ms.ToArray());

                if (fields.TryGetValue(1, out var newId) && newId is ulong nid && nid != 0)
                {
                    session.ClientId = nid;
                }
                if (fields.TryGetValue(2, out var newUrl))
                {
                    var url = Proto.AsString(newUrl);
                    if (!string.IsNullOrEmpty(url)) session.ChallengeUrl = url;
                }

                var refresh = fields.TryGetValue(3, out var rt) ? Proto.AsString(rt) : null;
                var access = fields.TryGetValue(4, out var at) ? Proto.AsString(at) : null;
                var account = fields.TryGetValue(6, out var an) ? Proto.AsString(an) : null;

                if (string.IsNullOrEmpty(refresh) && string.IsNullOrEmpty(access)) return null;

                return new LoginResult
                {
                    RefreshToken = refresh,
                    AccessToken = access,
                    AccountName = account,
                    HadRemoteInteraction =
                        fields.TryGetValue(5, out var hr) && hr is ulong h && h != 0,
                };
            }
        }

        /// <summary>
        /// The access token from sign-in lasts about a day; the refresh token
        /// lasts months and mints a new one. This is what keeps the console
        /// signed in without asking for the phone again.
        /// </summary>
        /// <summary>Steam results that mean the saved sign-in is no longer valid.</summary>
        public static bool MeansSignedOut(Exception error)
        {
            if (error is SteamSignInRequiredException) return true;
            return error is Steam.SteamLogOnException logOn &&
                (logOn.Result == 5 || logOn.Result == 15 || logOn.Result == 21 ||
                 logOn.Result == 26 || logOn.Result == 27);
        }

        public static async Task<string> RenewAccessTokenAsync(string refreshToken, ulong steamId)
        {
            // SteamClient tokens must be renewed over an authenticated CM
            // connection. The public HTTP endpoint can reject a valid session.
            var endpoints = await Steam.SteamCm.EndpointsAsync();
            if (endpoints.Count == 0) throw new Exception("Steam listed no connection managers");
            using (var cm = new Steam.SteamCm())
            {
                await cm.ConnectAsync(endpoints[0]);
                try
                {
                    await cm.LogOnAsync(steamId, refreshToken);
                }
                catch (Steam.SteamLogOnException error) when (MeansSignedOut(error))
                {
                    throw new SteamSignInRequiredException();
                }
                var request = new Steam.ProtoWriter()
                    .String(1, refreshToken).Fixed64(2, steamId).Finish();
                var response = await cm.ServiceAsync(
                    "Authentication.GenerateAccessTokenForApp#1", request);
                return response.Str(1);
            }
        }
    }
}
