using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Kiosk.Steam
{
    public sealed class ContentServer
    {
        public string Host { get; set; }
        public string VHost { get; set; }
    }

    public sealed class Chunk
    {
        public byte[] Sha { get; set; }
        public long Offset { get; set; }
        public int Original { get; set; }
    }

    public sealed class ManifestFile
    {
        public string Name { get; set; }
        public long Size { get; set; }
        public uint Flags { get; set; }
        public List<Chunk> Chunks { get; } = new List<Chunk>();
        public bool IsDirectory => (Flags & 64) != 0;
    }

    public sealed class Manifest
    {
        public uint DepotId { get; set; }
        public long TotalBytes { get; set; }
        public bool NamesEncrypted { get; set; }
        public List<ManifestFile> Files { get; } = new List<ManifestFile>();
    }

    /// <summary>
    /// Manifests and chunks: the part of a Steam install that travels over
    /// plain HTTPS, once the client connection has handed over the depot key
    /// and the manifest request code.
    /// </summary>
    public static class SteamDepot
    {
        private static readonly HttpClient Http = new HttpClient();
        private static int rotation;

        public static async Task<List<ContentServer>> ServersAsync(SteamCm cm)
        {
            var request = new ProtoWriter().Uint(1, 0).Uint(2, 20).Finish();
            var response = await cm.ServiceAsync(
                "ContentServerDirectory.GetServersForSteamPipe#1", request);

            var servers = new List<ContentServer>();
            foreach (var server in response.List(1))
            {
                var type = server.Str(1) ?? string.Empty;
                if (type != "SteamCache" && type != "CDN") continue;
                var host = server.Str(8);
                if (string.IsNullOrEmpty(host)) continue;
                servers.Add(new ContentServer { Host = host, VHost = server.Str(9) ?? host });
            }
            return servers;
        }

        public static async Task<ulong> ManifestCodeAsync(
            SteamCm cm, uint appId, uint depotId, string manifestId, string branch = "public")
        {
            var request = new ProtoWriter()
                .Uint(1, appId)
                .Uint(2, depotId)
                .Uint(3, ulong.Parse(manifestId))
                .String(4, branch)
                .Finish();
            var response = await cm.ServiceAsync(
                "ContentServerDirectory.GetManifestRequestCode#1", request);
            var code = response.Num(1);
            if (code == 0) throw new Exception($"Steam gave no manifest code for depot {depotId}");
            return code;
        }

        /// <summary>
        /// Caches fail one at a time: one answers 404 for a chunk another
        /// holds, and some present a certificate for a different name. So every
        /// fetch walks the list instead of trusting one server.
        /// </summary>
        private static async Task<byte[]> FetchAsync(
            List<ContentServer> servers, string path, int attempts = 6)
        {
            Exception last = null;
            var tries = Math.Min(attempts, Math.Max(1, servers.Count));
            for (var i = 0; i < tries; i++)
            {
                var server = servers[(rotation++ + i) % servers.Count];
                try
                {
                    var response = await Http.GetAsync($"https://{server.Host}{path}");
                    if (!response.IsSuccessStatusCode)
                    {
                        last = new Exception($"HTTP {(int)response.StatusCode} from {server.Host}");
                        continue;
                    }
                    return await response.Content.ReadAsByteArrayAsync();
                }
                catch (Exception error)
                {
                    last = error;
                }
            }
            throw last ?? new Exception("no content server answered " + path);
        }

        public static Task<byte[]> FetchManifestAsync(
            List<ContentServer> servers, uint depotId, string manifestId, ulong code) =>
            FetchAsync(servers, $"/depot/{depotId}/manifest/{manifestId}/5/{code}");

        public static Task<byte[]> FetchChunkAsync(
            List<ContentServer> servers, uint depotId, byte[] sha) =>
            FetchAsync(servers, $"/depot/{depotId}/chunk/{Hex(sha)}");

        public static string Hex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) builder.Append(b.ToString("x2"));
            return builder.ToString();
        }

        // ------------------------------------------------------------ parsing

        private const uint MagicPayload = 0x71F617D0;
        private const uint MagicMetadata = 0x1F4812BE;
        private const uint MagicEnd = 0x32C415AB;

        /// <summary>A zip holding one entry; reading the header beats a library.</summary>
        private static byte[] UnzipSingle(byte[] buffer)
        {
            if (BitConverter.ToUInt32(buffer, 0) != 0x04034B50)
            {
                throw new Exception("not a zip");
            }
            var method = BitConverter.ToUInt16(buffer, 8);
            var compressed = (int)BitConverter.ToUInt32(buffer, 18);
            var nameLength = BitConverter.ToUInt16(buffer, 26);
            var extraLength = BitConverter.ToUInt16(buffer, 28);
            var start = 30 + nameLength + extraLength;

            if (method == 0)
            {
                var stored = new byte[compressed];
                Array.Copy(buffer, start, stored, 0, compressed);
                return stored;
            }

            using (var input = new MemoryStream(buffer, start, compressed))
            using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                deflate.CopyTo(output);
                return output.ToArray();
            }
        }

        public static Manifest ParseManifest(byte[] zipped)
        {
            var buffer = UnzipSingle(zipped);
            var at = 0;
            ProtoMessage payload = null;
            ProtoMessage metadata = null;

            while (at + 8 <= buffer.Length)
            {
                var magic = BitConverter.ToUInt32(buffer, at);
                if (magic == MagicEnd) break;
                var length = (int)BitConverter.ToUInt32(buffer, at + 4);
                if (magic == MagicPayload) payload = ProtoMessage.Read(buffer, at + 8, length);
                else if (magic == MagicMetadata) metadata = ProtoMessage.Read(buffer, at + 8, length);
                at += 8 + length;
            }
            if (payload == null || metadata == null) throw new Exception("manifest is missing a block");

            var manifest = new Manifest
            {
                DepotId = (uint)metadata.Num(1),
                NamesEncrypted = metadata.Num(4) == 1,
            };

            foreach (var mapping in payload.List(1))
            {
                var file = new ManifestFile
                {
                    Name = mapping.Str(1) ?? string.Empty,
                    Size = (long)mapping.Num(2),
                    Flags = (uint)mapping.Num(3),
                };
                foreach (var chunk in mapping.List(6))
                {
                    var sha = chunk.Raw(1);
                    if (sha == null) continue;
                    file.Chunks.Add(new Chunk
                    {
                        Sha = sha,
                        Offset = (long)chunk.Num(3),
                        Original = (int)chunk.Num(4),
                    });
                }
                manifest.TotalBytes += file.Size;
                manifest.Files.Add(file);
            }
            return manifest;
        }

        // ------------------------------------------------------------- crypto

        /// <summary>
        /// AES-256-ECB over the first block yields the IV; the rest is
        /// AES-256-CBC with it. File names travel the same way.
        /// </summary>
        public static byte[] Decrypt(byte[] data, byte[] key)
        {
            using (var aes = Aes.Create())
            {
                aes.Key = key;
                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.None;

                var iv = new byte[16];
                using (var ecb = aes.CreateDecryptor())
                {
                    ecb.TransformBlock(data, 0, 16, iv, 0);
                }

                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.IV = iv;
                using (var cbc = aes.CreateDecryptor())
                {
                    return cbc.TransformFinalBlock(data, 16, data.Length - 16);
                }
            }
        }

        public static string DecryptName(string encoded, byte[] key)
        {
            var plain = Decrypt(Convert.FromBase64String(encoded), key);
            var end = Array.IndexOf(plain, (byte)0);
            return Encoding.UTF8.GetString(plain, 0, end < 0 ? plain.Length : end);
        }

        /// <summary>
        /// A decrypted chunk is a zip (PK) or one of Valve's own containers.
        /// "VSZa" is zstd, measured on Resident Evil Requiem: eight bytes of
        /// header, the frame, then fifteen bytes of footer ending in "zsv".
        /// </summary>
        public static byte[] Decompress(byte[] data)
        {
            if (data.Length > 2 && data[0] == 0x50 && data[1] == 0x4B) return UnzipSingle(data);
            if (data.Length > 24 && data[0] == 0x56 && data[1] == 0x53)
            {
                var declared = (int)BitConverter.ToUInt32(data, data.Length - 11);
                var payload = new byte[data.Length - 8 - 15];
                Array.Copy(data, 8, payload, 0, payload.Length);
                var plain = new byte[declared];
                using (var decompressor = new ZstdSharp.Decompressor())
                {
                    var written = decompressor.Unwrap(payload, plain);
                    if (written != declared)
                    {
                        throw new Exception($"chunk came to {written}, not the stated {declared}");
                    }
                }
                return plain;
            }
            throw new Exception(
                "unknown chunk container " + (char)data[0] + (char)data[1]);
        }
    }
}
