using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Kiosk.Steam;
using Xunit;

namespace Nativra.X86.Tests
{
    public sealed class DownloadCheckpointTests
    {
        private static List<DownloadChunk> Layout(byte[] bytes, int length = 100)
        {
            return Enumerable.Range(0, bytes.Length / length).Select(i => new DownloadChunk
            {
                Offset = i * length,
                Length = length,
                Hash = SHA1.HashData(bytes.Skip(i * length).Take(length).ToArray()),
            }).ToList();
        }

        private static byte[] Payload(int length = 10000) => Enumerable.Range(0, length).Select(i => (byte)(i * 13)).ToArray();
        private static Task Capacity(ulong bytes) => Task.CompletedTask;

        [Theory]
        [InlineData(49)]
        [InlineData(50)]
        [InlineData(51)]
        public async Task ProcessRestartDownloadsOnlyUnverifiedChunks(int percent)
        {
            var bytes = Payload();
            var path = Path.GetTempFileName();
            var chunks = Layout(bytes);
            try
            {
                var cancellation = new CancellationTokenSource();
                long verified = 0;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DownloadCheckpoint.RestoreAsync(
                        stream, bytes.Length, chunks,
                        chunk => Task.FromResult(bytes.Skip((int)chunk.Offset).Take(chunk.Length).ToArray()), Capacity,
                        count => { verified += count; if (verified == percent * 100) cancellation.Cancel(); }, cancellation.Token));
                var fetched = new List<long>();
                var completed = 0;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
                {
                    await DownloadCheckpoint.RestoreAsync(stream, bytes.Length, chunks, chunk =>
                    {
                        fetched.Add(chunk.Offset);
                        return Task.FromResult(bytes.Skip((int)chunk.Offset).Take(chunk.Length).ToArray());
                    }, Capacity, null, CancellationToken.None);
                    completed++;
                }
                Assert.Equal(100 - percent, fetched.Count);
                Assert.All(fetched, offset => Assert.True(offset >= percent * 100));
                Assert.Equal(bytes, File.ReadAllBytes(path));
                Assert.Equal(1, completed);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public async Task CurrentManifestRepairsOnlyChangedCorruptOrMissingRanges()
        {
            var bytes = Payload(500);
            using (var stream = new MemoryStream())
            {
                stream.Write(bytes, 0, 350);
                stream.Position = 100;
                stream.WriteByte(0xff);
                var updated = (byte[])bytes.Clone();
                updated[200] ^= 0x80;
                var fetched = new List<long>();
                await DownloadCheckpoint.RestoreAsync(stream, updated.Length, Layout(updated), chunk =>
                {
                    fetched.Add(chunk.Offset);
                    return Task.FromResult(updated.Skip((int)chunk.Offset).Take(chunk.Length).ToArray());
                }, Capacity, null, CancellationToken.None);
                Assert.Equal(new long[] { 100, 200, 300, 400 }, fetched);
                Assert.Equal(updated, stream.ToArray());
            }
            Assert.NotEqual(DownloadCheckpoint.Identity(1, 2, "3", "a.exe"), DownloadCheckpoint.Identity(1, 2, "4", "a.exe"));
            Assert.NotEqual(DownloadCheckpoint.Identity(1, 2, "3", "a.exe"), DownloadCheckpoint.Identity(2, 2, "3", "a.exe"));
        }

        [Theory]
        [InlineData("short-chunk")]
        [InlineData("chunk-hash")]
        [InlineData("decryption")]
        [InlineData("network")]
        public async Task BoundedFailuresDoNotWriteUnverifiedData(string category)
        {
            var bytes = Payload(100);
            var chunk = Layout(bytes)[0];
            using (var stream = new MemoryStream())
            {
                var attempts = 0;
                var error = await Assert.ThrowsAsync<DownloadFailure>(() => DownloadCheckpoint.RestoreAsync(
                    stream, 100, new[] { chunk }, _ =>
                    {
                        attempts++;
                        if (category == "network") throw new TimeoutException("secret request payload");
                        if (category == "decryption") throw new CryptographicException("secret key");
                        return Task.FromResult(new byte[category == "short-chunk" ? 50 : 100]);
                    }, Capacity, null, CancellationToken.None));
                Assert.Equal(category, error.Category);
                Assert.Equal(DownloadCheckpoint.Attempts, attempts);
                Assert.Equal(0, stream.Length);
                Assert.DoesNotContain("secret", error.ToString());
            }
        }

        [Fact]
        public async Task TransientTimeoutRetriesWithinChunkWithoutRepeatingGoodChunks()
        {
            var bytes = Payload(100);
            var attempts = 0;
            using (var stream = new MemoryStream())
            {
                await DownloadCheckpoint.RestoreAsync(stream, 100, Layout(bytes), _ =>
                {
                    if (++attempts < 3) throw new TimeoutException();
                    return Task.FromResult(bytes);
                }, Capacity, null, CancellationToken.None);
                Assert.Equal(3, attempts);
                Assert.Equal(bytes, stream.ToArray());
            }
        }

        [Fact]
        public void CapacityIncludesStagingReserveAndReportsUnknown()
        {
            var reserve = DownloadCheckpoint.ReserveBytes;
            var error = Assert.Throws<DiskFullException>(() => DownloadCheckpoint.RequireCapacity(11200000000, 15300000000));
            Assert.Contains($"required={15300000000UL + reserve} available=11200000000", error.Message);
            Assert.Throws<DiskFullException>(() => DownloadCheckpoint.RequireCapacity(reserve + 999, 500, 500));
            DownloadCheckpoint.RequireCapacity(reserve + 1000, 500, 500);
            Assert.Contains("available=unknown", Assert.Throws<DownloadFailure>(() => DownloadCheckpoint.RequireCapacity(null, 1)).Message);
        }

        private sealed class FullStream : MemoryStream
        {
            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                throw new IOException("disk full", unchecked((int)0x80070027));
            }
        }

        [Fact]
        public async Task FailedMoveKeepsOriginalBytesAndNoCommit()
        {
            var bytes = Payload();
            using (var source = new MemoryStream(bytes))
            using (var target = new FullStream())
            {
                var committed = false;
                var error = await Assert.ThrowsAsync<IOException>(async () =>
                {
                    await DownloadCheckpoint.CopyVerifiedAsync(source, target);
                    committed = true;
                });
                Assert.True(DownloadCheckpoint.IsDiskFull(error));
                Assert.False(committed);
                Assert.Equal(bytes, source.ToArray());
            }
        }

        [Fact]
        public async Task VerifiedCopyTruncatesOldTailAndLeavesSourceIntact()
        {
            var bytes = Payload();
            using (var source = new MemoryStream(bytes))
            using (var target = new MemoryStream())
            {
                target.SetLength(20000);
                target.Position = 0;
                await DownloadCheckpoint.CopyVerifiedAsync(source, target);
                Assert.Equal(bytes, target.ToArray());
                Assert.Equal(bytes, source.ToArray());
            }
        }

        [Fact]
        public void InvalidLayoutsAndTraversalAreRejectedBeforeAllocation()
        {
            Assert.Throws<DownloadFailure>(() => DownloadCheckpoint.ValidateLayout(100, new[] { new DownloadChunk { Offset = 1, Length = 100, Hash = new byte[20] } }));
            Assert.Throws<DownloadFailure>(() => DownloadCheckpoint.ValidateLayout(0, new[] { new DownloadChunk { Length = int.MaxValue, Hash = new byte[20] } }));
            foreach (var path in new[] { "../save", "C:/save", "/save", "a\nb", "." })
                Assert.Throws<DownloadFailure>(() => DownloadCheckpoint.SafePath(path));
        }

        [Fact]
        public void WindowsDepotSelectionExcludesMacLinuxAndSharedDepots()
        {
            var app = KeyValue.Parse(System.Text.Encoding.UTF8.GetBytes("\"appinfo\" { \"depots\" { \"1\" { \"config\" { \"oslist\" \"windows\" } \"manifests\" { \"public\" \"101\" } } \"2\" { \"config\" { \"oslist\" \"macos\" } \"manifests\" { \"public\" \"102\" } } \"3\" { \"config\" { \"oslist\" \"linux\" } \"manifests\" { \"public\" \"103\" } } \"4\" { \"depotfromapp\" \"99\" \"manifests\" { \"public\" \"104\" } } } }"));
            Assert.Equal(1U, Assert.Single(Depots.ForWindows(app)).Id);
        }
    }
}
