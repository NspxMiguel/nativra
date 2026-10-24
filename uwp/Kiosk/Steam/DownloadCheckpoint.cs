using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Kiosk.Steam
{
    public class DownloadFailure : IOException
    {
        public string Category { get; }
        public bool Recoverable { get; }
        public DownloadFailure(string category, bool recoverable, string detail)
            : base($"category={category} recoverable={recoverable.ToString().ToLowerInvariant()} {detail}")
        {
            Category = category;
            Recoverable = recoverable;
        }
    }

    public sealed class DiskFullException : DownloadFailure
    {
        public DiskFullException(string message) : base("storage-full", true, message) { }
    }

    public sealed class DownloadChunk
    {
        public long Offset;
        public int Length;
        public byte[] Hash;
    }

    /// <summary>The persisted file is the checkpoint; its current manifest hashes are authoritative.</summary>
    public static class DownloadCheckpoint
    {
        public const int MaxChunkBytes = 4 * 1024 * 1024;
        public const int MaxPayloadBytes = 8 * 1024 * 1024;
        public const int Attempts = 3;
        public static ulong ReserveBytes { get; set; } = 64UL * 1024 * 1024;

        public static string Identity(uint app, uint depot, string manifest, string file)
        {
            return $"{app}/{depot}/{manifest}/{SafePath(file)}";
        }

        public static string SafePath(string path)
        {
            path = (path ?? "").Replace('\\', '/');
            if (path.Length == 0 || path.StartsWith("/", StringComparison.Ordinal))
                throw new DownloadFailure("manifest", false, "invalid relative file path");
            var top = path.Split('/')[0];
            if (top.StartsWith(".", StringComparison.Ordinal))
                throw new DownloadFailure("manifest", false, "reserved install metadata path");
            foreach (var part in path.Split('/'))
                if (part == ".." || part == "." || part.Length == 0 || part.IndexOfAny(new[] { ':', '\r', '\n', '\0' }) >= 0)
                    throw new DownloadFailure("manifest", false, "invalid relative file path");
            return path;
        }

        public static bool IsDiskFull(Exception error) =>
            error is DiskFullException || error.HResult == unchecked((int)0x80070070) ||
            error.HResult == unchecked((int)0x80070027);

        public static string Category(Exception error)
        {
            if (error is DownloadFailure failure) return failure.Category;
            if (IsDiskFull(error)) return "storage-full";
            if (error is OperationCanceledException) return "cancelled";
            if (error is TimeoutException || error is HttpRequestException) return "network";
            if (error is CryptographicException) return "decryption";
            return "installation";
        }

        public static void RequireCapacity(ulong? available, ulong remaining, ulong staging = 0)
        {
            var required = checked(remaining + staging + ReserveBytes);
            if (!available.HasValue)
                throw new DownloadFailure("storage-unknown", true, $"required={required} available=unknown");
            if (required > available.Value)
                throw new DiskFullException($"required={required} available={available.Value}");
        }

        public static void ValidateLayout(long size, IList<DownloadChunk> chunks)
        {
            long end = 0;
            foreach (var chunk in chunks)
            {
                if (chunk.Offset != end || chunk.Length <= 0 || chunk.Length > MaxChunkBytes || chunk.Hash == null || chunk.Hash.Length != 20)
                    throw new DownloadFailure("manifest", false, "invalid chunk layout");
                end = checked(end + chunk.Length);
            }
            if (end != size) throw new DownloadFailure("manifest", false, "file size differs from chunks");
        }

        public static bool Matches(byte[] bytes, DownloadChunk chunk)
        {
            if (bytes.Length != chunk.Length) return false;
            using (var sha = SHA1.Create())
            {
                var hash = sha.ComputeHash(bytes);
                for (var i = 0; i < hash.Length; i++) if (hash[i] != chunk.Hash[i]) return false;
                return true;
            }
        }

        public static async Task<bool> VerifiedAsync(Stream stream, DownloadChunk chunk, CancellationToken cancellation)
        {
            if (stream.Length < chunk.Offset + chunk.Length) return false;
            var bytes = new byte[chunk.Length];
            stream.Position = chunk.Offset;
            var read = 0;
            while (read < bytes.Length)
            {
                var count = await stream.ReadAsync(bytes, read, bytes.Length - read, cancellation);
                if (count == 0) return false;
                read += count;
            }
            return Matches(bytes, chunk);
        }

        public static async Task<byte[]> FetchAsync(Func<Task<byte[]>> fetch, DownloadChunk chunk, CancellationToken cancellation)
        {
            string category = "network";
            for (var attempt = 0; attempt < Attempts; attempt++)
            {
                cancellation.ThrowIfCancellationRequested();
                try
                {
                    var bytes = await fetch();
                    if (bytes.Length != chunk.Length) throw new DownloadFailure("short-chunk", true, "decoded length mismatch");
                    if (!Matches(bytes, chunk)) throw new DownloadFailure("chunk-hash", true, "decoded SHA-1 mismatch");
                    return bytes;
                }
                catch (Exception error) when (error is TimeoutException || error is HttpRequestException ||
                    error is CryptographicException || error is InvalidDataException || error is DownloadFailure)
                {
                    if (error is DownloadFailure failure && !failure.Recoverable) throw;
                    category = error is InvalidDataException ? "decompression" : Category(error);
                }
            }
            throw new DownloadFailure(category, true, $"attempts={Attempts} exhausted");
        }

        /// <summary>Resume only bytes verified against this file's current manifest, never a size/marker.</summary>
        public static async Task RestoreAsync(Stream stream, long size, IList<DownloadChunk> chunks,
            Func<DownloadChunk, Task<byte[]>> fetch, Func<ulong, Task> capacity,
            Action<long> onVerified, CancellationToken cancellation)
        {
            ValidateLayout(size, chunks);
            foreach (var chunk in chunks)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!await VerifiedAsync(stream, chunk, cancellation))
                {
                    await capacity((ulong)Math.Max(0, chunk.Offset + chunk.Length - stream.Length));
                    var bytes = await FetchAsync(() => fetch(chunk), chunk, cancellation);
                    stream.Position = chunk.Offset;
                    await stream.WriteAsync(bytes, 0, bytes.Length, cancellation);
                    await stream.FlushAsync(cancellation);
                    if (!await VerifiedAsync(stream, chunk, cancellation))
                        throw new DownloadFailure("storage-verification", true, "written chunk failed verification");
                }
                onVerified?.Invoke(chunk.Length);
            }
            stream.SetLength(size);
            await stream.FlushAsync(cancellation);
        }

        /// <summary>Bounded copy/readback used by storage moves before publishing or deleting the source.</summary>
        public static async Task CopyVerifiedAsync(Stream source, Stream target)
        {
            var buffer = new byte[64 * 1024];
            byte[] expected;
            source.Position = 0;
            using (var hash = SHA256.Create())
            {
                int count;
                while ((count = await source.ReadAsync(buffer, 0, buffer.Length)) != 0)
                {
                    hash.TransformBlock(buffer, 0, count, buffer, 0);
                    await target.WriteAsync(buffer, 0, count);
                }
                hash.TransformFinalBlock(buffer, 0, 0);
                expected = hash.Hash;
            }
            target.SetLength(source.Length);
            await target.FlushAsync();
            target.Position = 0;
            using (var hash = SHA256.Create())
            {
                var actual = hash.ComputeHash(target);
                if (!Convert.ToBase64String(expected).Equals(Convert.ToBase64String(actual), StringComparison.Ordinal))
                    throw new DownloadFailure("move-verification", true, "copy differs from source");
            }
        }
    }
}
