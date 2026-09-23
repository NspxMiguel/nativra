using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace Kiosk.Steam
{
    public sealed class SteamLogOnException : Exception
    {
        public int Result { get; }

        public SteamLogOnException(int result)
            : base($"Steam refused the logon (eresult {result})")
        {
            Result = result;
        }
    }

    /// <summary>
    /// The Steam client connection, spoken from the console itself.
    ///
    /// A depot's decryption key and an app's list of depots exist only here —
    /// the Web API hands out neither — so downloading a game means speaking the
    /// client protocol. The modern connection managers accept a websocket over
    /// TLS, which removes the old encryption handshake entirely.
    /// </summary>
    public sealed class SteamCm : IDisposable
    {
        private const int EMsgMulti = 1;
        private const int EMsgServiceMethodCallFromClient = 151;
        private const int EMsgHeartBeat = 703;
        private const int EMsgLogOnResponse = 751;
        private const int EMsgGetDepotDecryptionKey = 5438;
        private const int EMsgLogon = 5514;
        private const int EMsgPicsProductInfoRequest = 8903;
        private const int EMsgPicsAccessTokenRequest = 8905;

        private const uint ProtoMask = 0x80000000;

        private MessageWebSocket socket;
        private DataWriter writer;
        private int sessionId;
        private ulong steamId;
        private ulong nextJob = 1;
        private Timer heartbeat;

        private readonly Dictionary<string, TaskCompletionSource<ProtoMessage>> pending =
            new Dictionary<string, TaskCompletionSource<ProtoMessage>>();

        private static readonly HttpClient Http = new HttpClient();

        public static async Task<List<string>> EndpointsAsync()
        {
            var list = new List<string>();
            var text = await Http.GetStringAsync(
                "https://api.steampowered.com/ISteamDirectory/GetCMListForConnect/v1/"
                + "?cellid=0&cmtype=websockets");
            if (!JsonObject.TryParse(text, out var root)) return list;

            var response = root.GetNamedObject("response", new JsonObject());
            if (!response.ContainsKey("serverlist")) return list;
            foreach (var value in response.GetNamedArray("serverlist"))
            {
                var endpoint = value.GetObject().GetNamedString("endpoint", string.Empty);
                if (!string.IsNullOrEmpty(endpoint)) list.Add(endpoint);
            }
            return list;
        }

        public async Task ConnectAsync(string endpoint)
        {
            socket = new MessageWebSocket();
            socket.Control.MessageType = SocketMessageType.Binary;
            socket.MessageReceived += OnMessage;
            await socket.ConnectAsync(new Uri($"wss://{endpoint}/cmsocket/"));
            writer = new DataWriter(socket.OutputStream);
        }

        // ------------------------------------------------------------- framing

        private async Task SendAsync(int emsg, byte[] body, ProtoWriter header)
        {
            var headerBytes = header.Finish();
            var packet = new byte[8 + headerBytes.Length + body.Length];
            Array.Copy(BitConverter.GetBytes((uint)emsg | ProtoMask), 0, packet, 0, 4);
            Array.Copy(BitConverter.GetBytes((uint)headerBytes.Length), 0, packet, 4, 4);
            Array.Copy(headerBytes, 0, packet, 8, headerBytes.Length);
            Array.Copy(body, 0, packet, 8 + headerBytes.Length, body.Length);

            writer.WriteBytes(packet);
            await writer.StoreAsync();
        }

        private ProtoWriter BaseHeader()
        {
            var header = new ProtoWriter();
            if (steamId != 0) header.Fixed64(1, steamId);
            if (sessionId != 0) header.Uint(2, (ulong)sessionId);
            return header;
        }

        private void OnMessage(MessageWebSocket sender, MessageWebSocketMessageReceivedEventArgs args)
        {
            try
            {
                using (var reader = args.GetDataReader())
                {
                    var bytes = new byte[reader.UnconsumedBufferLength];
                    reader.ReadBytes(bytes);
                    HandlePacket(bytes);
                }
            }
            catch
            {
                // A malformed frame is one lost reply, not a dead connection.
            }
        }

        private void HandlePacket(byte[] packet)
        {
            if (packet.Length < 8) return;
            var rawEmsg = BitConverter.ToUInt32(packet, 0);
            if ((rawEmsg & ProtoMask) == 0) return;
            var emsg = (int)(rawEmsg & ~ProtoMask);

            var headerLength = (int)BitConverter.ToUInt32(packet, 4);
            var header = ProtoMessage.Read(packet, 8, headerLength);
            var bodyStart = 8 + headerLength;
            var bodyLength = packet.Length - bodyStart;

            if (emsg == EMsgMulti)
            {
                HandleMulti(ProtoMessage.Read(packet, bodyStart, bodyLength));
                return;
            }

            if (emsg == EMsgLogOnResponse)
            {
                sessionId = (int)header.Num(2);
                steamId = header.Num(1);
            }

            var jobTarget = header.Num(11);
            var key = jobTarget != 0 && jobTarget != ulong.MaxValue
                ? "job:" + jobTarget
                : "emsg:" + emsg;

            TaskCompletionSource<ProtoMessage> waiting = null;
            lock (pending)
            {
                if (!pending.TryGetValue(key, out waiting))
                {
                    pending.TryGetValue("emsg:" + emsg, out waiting);
                    pending.Remove("emsg:" + emsg);
                }
                else
                {
                    pending.Remove(key);
                }
            }
            if (waiting == null) return;

            var eresult = (int)header.Num(13, 0);
            if (eresult != 0 && eresult != 1)
            {
                waiting.TrySetException(new Exception(
                    header.Str(14) ?? $"Steam eresult {eresult}"));
                return;
            }
            waiting.TrySetResult(ProtoMessage.Read(packet, bodyStart, bodyLength));
        }

        /// <summary>Steam batches messages, and the batch may be gzipped.</summary>
        private void HandleMulti(ProtoMessage multi)
        {
            var payload = multi.Raw(2);
            if (payload == null) return;

            if (multi.Num(1) > 0)
            {
                using (var input = new MemoryStream(payload))
                using (var gzip = new GZipStream(input, CompressionMode.Decompress))
                using (var output = new MemoryStream())
                {
                    gzip.CopyTo(output);
                    payload = output.ToArray();
                }
            }

            var at = 0;
            while (at + 4 <= payload.Length)
            {
                var size = (int)BitConverter.ToUInt32(payload, at);
                at += 4;
                if (at + size > payload.Length) break;
                var inner = new byte[size];
                Array.Copy(payload, at, inner, 0, size);
                HandlePacket(inner);
                at += size;
            }
        }

        private Task<ProtoMessage> Await(string key, int timeoutMs = 30000)
        {
            var source = new TaskCompletionSource<ProtoMessage>();
            lock (pending) pending[key] = source;

            var timer = new Timer(_ =>
            {
                lock (pending) pending.Remove(key);
                source.TrySetException(new TimeoutException($"Steam did not answer {key}"));
            }, null, timeoutMs, Timeout.Infinite);

            return source.Task.ContinueWith(task =>
            {
                timer.Dispose();
                return task.GetAwaiter().GetResult();
            });
        }

        // --------------------------------------------------------------- logon

        /// <summary>
        /// The schema calls field 108 access_token, but what the client puts
        /// there is the refresh token from the QR sign-in.
        /// </summary>
        public async Task LogOnAsync(ulong accountId, string refreshToken)
        {
            var body = new ProtoWriter()
                .Uint(1, 65580)
                .Uint(3, 0)
                .Uint(5, 1771)
                .String(6, "english")
                .Uint(7, 20)
                .Bool(8, true) // Persistent QR refresh-token session.
                .Bytes(30, new byte[0])
                .String(96, "Xbox Series X")
                .Uint(100, 0)
                .Bool(102, true)
                .String(108, refreshToken)
                .Finish();

            var header = new ProtoWriter().Fixed64(1, accountId).Uint(2, 0);
            var waiting = Await("emsg:" + EMsgLogOnResponse, 45000);
            await SendAsync(EMsgLogon, body, header);

            var response = await waiting;
            var eresult = (int)response.Num(1, 2);
            if (eresult != 1) throw new SteamLogOnException(eresult);

            steamId = response.Num(20) != 0 ? response.Num(20) : accountId;
            var seconds = Math.Max(5, (int)response.Num(3, 9));
            heartbeat = new Timer(async _ =>
            {
                try
                {
                    await SendAsync(EMsgHeartBeat, new ProtoWriter().Finish(), BaseHeader());
                }
                catch
                {
                    // A dead socket shows up on the next real call.
                }
            }, null, seconds * 1000, seconds * 1000);
        }

        // --------------------------------------------------------------- calls

        private ProtoWriter JobHeader(out string key)
        {
            var job = nextJob++;
            key = "job:" + job;
            return BaseHeader().Fixed64(10, job);
        }

        public async Task<ProtoMessage> ServiceAsync(string name, byte[] request)
        {
            var header = JobHeader(out var key).String(12, name);
            var waiting = Await(key);
            await SendAsync(EMsgServiceMethodCallFromClient, request, header);
            return await waiting;
        }

        public async Task<byte[]> DepotKeyAsync(uint appId, uint depotId)
        {
            var header = JobHeader(out var key);
            var body = new ProtoWriter().Uint(1, depotId).Uint(2, appId).Finish();
            var waiting = Await(key);
            await SendAsync(EMsgGetDepotDecryptionKey, body, header);

            var response = await waiting;
            if ((int)response.Num(1, 2) != 1)
            {
                throw new Exception($"no depot key for {depotId}");
            }
            var depotKey = response.Raw(3);
            if (depotKey == null) throw new Exception($"empty depot key for {depotId}");
            return depotKey;
        }

        public async Task<ulong> AppTokenAsync(uint appId)
        {
            var header = JobHeader(out var key);
            var waiting = Await(key);
            await SendAsync(EMsgPicsAccessTokenRequest,
                new ProtoWriter().Uint(2, appId).Finish(), header);

            var response = await waiting;
            foreach (var token in response.List(3))
            {
                if ((uint)token.Num(1) == appId) return token.Num(2);
            }
            return 0;
        }

        /// <summary>The app's KeyValues buffer, which lists its depots.</summary>
        public async Task<byte[]> AppInfoAsync(uint appId, ulong token)
        {
            var header = JobHeader(out var key);
            var app = new ProtoWriter().Uint(1, appId);
            if (token != 0) app.Uint(2, token);
            var body = new ProtoWriter().Message(2, app).Bool(7, true).Finish();

            var waiting = Await(key, 45000);
            await SendAsync(EMsgPicsProductInfoRequest, body, header);

            var response = await waiting;
            foreach (var info in response.List(1))
            {
                if ((uint)info.Num(1) != appId) continue;
                var buffer = info.Raw(5);
                if (buffer != null) return buffer;
                if (info.Num(3) == 1) throw new Exception($"PICS refused {appId}: missing token");
            }
            throw new Exception($"PICS said nothing about {appId}");
        }

        public void Dispose()
        {
            heartbeat?.Dispose();
            try
            {
                writer?.Dispose();
                socket?.Close(1000, "done");
            }
            catch
            {
                // Closing a socket Steam already dropped is not a problem here.
            }
        }
    }
}
