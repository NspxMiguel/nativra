using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Kiosk;
using Kiosk.Steam;
using Windows.Storage;
using Xunit;

namespace Nativra.X86.Tests
{
    [CollectionDefinition("Download storage", DisableParallelization = true)]
    public sealed class DownloadStorageCollection { }

    [Collection("Download storage")]
    public sealed class DownloadIntegrationTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "nativra-download-" + Guid.NewGuid());
        private readonly HttpClient originalHttp = SteamDepot.Http;
        private readonly FixtureCdn cdn = new FixtureCdn();
        private readonly byte[] payload = new byte[100 * 1024];
        private readonly byte[] key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        private int completed;
        private string Game => Path.Combine(root, "local", "games", "312530");

        public DownloadIntegrationTests()
        {
            Directory.CreateDirectory(Path.Combine(root, "local"));
            Directory.CreateDirectory(Path.Combine(root, "removable", "usb"));
            ApplicationData.Current.LocalFolder = new StorageFolder(Path.Combine(root, "local"));
            KnownFolders.RemovableDevices = new StorageFolder(Path.Combine(root, "removable"));
            FolderProperties.Free = 1024UL * 1024 * 1024;
            StorageFile.WriteBudget = null;
            StorageFolder.FailRename = false;
            SteamCm.DepotKey = key;
            SteamDepot.Http = new HttpClient(cdn);
            for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i * 17 + i / 1024);
            ConfigureManifest("101");
        }

        private void ConfigureManifest(string manifest)
        {
            SteamCm.AppInfo = Encoding.UTF8.GetBytes($"\"appinfo\" {{ \"depots\" {{ \"1\" {{ \"config\" {{ \"oslist\" \"windows\" }} \"manifests\" {{ \"public\" \"{manifest}\" }} }} }} }}");
            var mapping = new ProtoWriter().String(1, "game.exe").Uint(2, (ulong)payload.Length);
            for (var i = 0; i < 100; i++)
            {
                var plain = payload.Skip(i * 1024).Take(1024).ToArray();
                var hash = SHA1.HashData(plain);
                cdn.Chunks[SteamDepot.Hex(hash)] = Encrypt(Zip(plain), key);
                mapping.Bytes(6, new ProtoWriter().Bytes(1, hash).Uint(3, (ulong)(i * 1024)).Uint(4, 1024).Finish());
            }
            using (var body = new MemoryStream())
            using (var writer = new BinaryWriter(body))
            {
                var files = new ProtoWriter().Bytes(1, mapping.Finish()).Finish();
                var metadata = new ProtoWriter().Uint(1, 1).Uint(4, 0).Finish();
                writer.Write(0x71F617D0U); writer.Write(files.Length); writer.Write(files);
                writer.Write(0x1F4812BEU); writer.Write(metadata.Length); writer.Write(metadata);
                writer.Write(0x32C415ABU); writer.Write(0);
                cdn.Manifest = Zip(body.ToArray());
            }
        }

        private static byte[] Zip(byte[] bytes)
        {
            using (var output = new MemoryStream())
            {
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
                using (var entry = zip.CreateEntry("data").Open()) entry.Write(bytes, 0, bytes.Length);
                return output.ToArray();
            }
        }

        private static byte[] Encrypt(byte[] bytes, byte[] key)
        {
            using (var aes = Aes.Create())
            {
                aes.Key = key;
                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.None;
                var iv = new byte[16];
                var encryptedIv = aes.CreateEncryptor().TransformFinalBlock(iv, 0, iv.Length);
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.IV = iv;
                return encryptedIv.Concat(aes.CreateEncryptor().TransformFinalBlock(bytes, 0, bytes.Length)).ToArray();
            }
        }

        private async Task RunAsync(Action<DownloadProgress> progress = null, CancellationToken cancellation = default(CancellationToken))
        {
            await SteamDownload.RunAsync(new SteamSession(), 312530, "local", progress, cancellation: cancellation);
            completed++;
        }

        [Theory]
        [InlineData(49)]
        [InlineData(50)]
        [InlineData(51)]
        public async Task ActualDownloaderRestartsAtVerifiedChunkAndPublishesOnce(int percent)
        {
            using (var cancellation = new CancellationTokenSource())
            {
                var error = await Assert.ThrowsAsync<DownloadFailure>(() => RunAsync(p =>
                {
                    if (p.Percent == percent) cancellation.Cancel();
                }, cancellation.Token));
                Assert.Equal("cancelled", error.Category);
            }
            Assert.False(await GameStorage.IsReadyAsync(new StorageFolder(Game)));
            Assert.False(File.Exists(Path.Combine(Game, ".downloaded")));
            Assert.Equal(percent, cdn.ChunkRequests);
            await RunAsync();
            Assert.Equal(100, cdn.ChunkRequests);
            Assert.Equal(1, completed);
            Assert.Equal(payload, File.ReadAllBytes(Path.Combine(Game, "game.exe")));
            Assert.True(await GameStorage.IsReadyAsync(new StorageFolder(Game)));
        }

        [Fact]
        public async Task ChangedManifestAndCorruptCompleteFileRepairOnlyChangedChunks()
        {
            await RunAsync();
            payload[20 * 1024] ^= 128;
            ConfigureManifest("102");
            using (var stream = File.OpenWrite(Path.Combine(Game, "game.exe")))
            {
                stream.Position = 30 * 1024;
                stream.WriteByte(0xff);
            }
            await RunAsync();
            Assert.Equal(102, cdn.ChunkRequests);
            Assert.Equal(payload, File.ReadAllBytes(Path.Combine(Game, "game.exe")));
        }

        [Theory]
        [InlineData("short-encrypted-chunk")]
        [InlineData("decryption")]
        [InlineData("network")]
        public async Task ActualEncryptedCdnFailuresAreBoundedCategorizedAndResumable(string failure)
        {
            cdn.Failure = failure;
            var error = await Assert.ThrowsAsync<DownloadFailure>(() => RunAsync());
            Assert.Equal(failure, error.Category);
            Assert.Contains("identity=312530/1/101/game.exe", error.Message);
            Assert.Contains("chunk=0", error.Message);
            Assert.DoesNotContain("synthetic-token", error.ToString());
            Assert.Equal(3, cdn.ChunkRequests);
            Assert.False(await GameStorage.IsReadyAsync(new StorageFolder(Game)));
            cdn.Failure = null;
            await RunAsync();
            Assert.Equal(103, cdn.ChunkRequests);
            Assert.Equal(1, completed);
        }

        [Fact]
        public async Task CapacityFailurePrecedesChunkFetchAndRejectsStaleMarkerAndPartialExe()
        {
            Directory.CreateDirectory(Game);
            File.WriteAllText(Path.Combine(Game, "game.exe"), "MZ");
            Assert.False(await GameStorage.IsReadyAsync(new StorageFolder(Game)));
            File.WriteAllText(Path.Combine(Game, ".downloaded"), "stale");
            Assert.False(await GameStorage.IsReadyAsync(new StorageFolder(Game)));
            FolderProperties.Free = DownloadCheckpoint.ReserveBytes;
            var error = await Assert.ThrowsAsync<DiskFullException>(() => RunAsync());
            Assert.Contains("required=", error.Message);
            Assert.Contains("available=", error.Message);
            Assert.Equal(0, cdn.ChunkRequests);
            Assert.False(await GameStorage.IsReadyAsync(new StorageFolder(Game)));
            FolderProperties.Free = null;
            Assert.Equal("storage-unknown", (await Assert.ThrowsAsync<DownloadFailure>(() => RunAsync())).Category);
        }

        [Fact]
        public async Task ActualDiskFullWritePreservesVerifiedChunksForRestart()
        {
            StorageFile.WriteBudget = 50 * 1024;
            var error = await Assert.ThrowsAsync<DiskFullException>(() => RunAsync());
            Assert.Equal("storage-full", error.Category);
            Assert.Equal(50 * 1024, new FileInfo(Path.Combine(Game, "game.exe")).Length);
            StorageFile.WriteBudget = null;
            await RunAsync();
            Assert.Equal(101, cdn.ChunkRequests);
            Assert.Equal(payload, File.ReadAllBytes(Path.Combine(Game, "game.exe")));
        }

        [Theory]
        [InlineData("before")]
        [InlineData("during")]
        [InlineData("commit")]
        public async Task ActualMoveRollsBackAndPreservesSourceAndSaves(string phase)
        {
            await RunAsync();
            File.WriteAllText(Path.Combine(Game, "save.dat"), "owned save");
            var destination = "usb:" + Path.Combine(root, "removable", "usb");
            if (phase == "before") FolderProperties.Free = DownloadCheckpoint.ReserveBytes;
            if (phase == "during") StorageFile.WriteBudget = 4096;
            if (phase == "commit") StorageFolder.FailRename = true;
            await Assert.ThrowsAnyAsync<IOException>(() => GameStorage.MoveAsync(new StorageFolder(Game), destination, null));
            Assert.Equal(payload, File.ReadAllBytes(Path.Combine(Game, "game.exe")));
            Assert.Equal("owned save", File.ReadAllText(Path.Combine(Game, "save.dat")));
            var games = await GameStorage.GamesFolderAsync(destination, false);
            Assert.Null(await games.TryGetItemAsync("312530"));
            Assert.Null(await games.TryGetItemAsync(".move-312530"));
            FolderProperties.Free = 1024UL * 1024 * 1024;
            StorageFile.WriteBudget = null;
            StorageFolder.FailRename = false;
            var moved = await GameStorage.MoveAsync(new StorageFolder(Game), destination, null);
            Assert.False(Directory.Exists(Game));
            Assert.Equal(payload, File.ReadAllBytes(Path.Combine(moved.Path, "game.exe")));
            Assert.Equal("owned save", File.ReadAllText(Path.Combine(moved.Path, "save.dat")));
        }

        [Fact]
        public async Task MoveNeverOverwritesExistingInstallation()
        {
            await RunAsync();
            var destination = "usb:" + Path.Combine(root, "removable", "usb");
            var games = await GameStorage.GamesFolderAsync(destination, true);
            var baseline = await games.CreateFolderAsync("312530", CreationCollisionOption.OpenIfExists);
            File.WriteAllText(Path.Combine(baseline.Path, "save.dat"), "baseline save");
            var error = await Assert.ThrowsAsync<DownloadFailure>(() => GameStorage.MoveAsync(new StorageFolder(Game), destination, null));
            Assert.Equal("move-conflict", error.Category);
            Assert.Equal("baseline save", File.ReadAllText(Path.Combine(baseline.Path, "save.dat")));
            Assert.True(Directory.Exists(Game));
        }

        private sealed class FixtureCdn : HttpMessageHandler
        {
            public readonly Dictionary<string, byte[]> Chunks = new Dictionary<string, byte[]>();
            public byte[] Manifest;
            public int ChunkRequests;
            public string Failure;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var chunk = request.RequestUri.AbsolutePath.Contains("/chunk/");
                byte[] bytes;
                if (chunk)
                {
                    ChunkRequests++;
                    if (Failure == "network") throw new TimeoutException("synthetic-token must not escape");
                    bytes = Chunks[request.RequestUri.Segments.Last()];
                    if (Failure == "short-encrypted-chunk") bytes = bytes.Take(17).ToArray();
                    if (Failure == "decryption") bytes = new byte[32];
                }
                else bytes = Manifest;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
            }
        }
        public void Dispose()
        {
            SteamDepot.Http.Dispose();
            SteamDepot.Http = originalHttp;
            FolderProperties.Free = 1024UL * 1024 * 1024;
            StorageFile.WriteBudget = null;
            StorageFolder.FailRename = false;
            Directory.Delete(root, true);
        }
    }
}
