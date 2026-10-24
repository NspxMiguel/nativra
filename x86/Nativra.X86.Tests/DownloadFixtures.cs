// Portable adapters for integration tests of the actual UWP downloader/storage
// sources. Only WinRT I/O and Steam CM transport are substituted; no account or
// network is used. The Windows package is built separately in CI.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Kiosk.Steam;

namespace Windows.Storage
{
    public enum FileAccessMode { ReadWrite }
    public sealed class FixtureRandomAccessStream : IDisposable
    {
        private readonly Stream stream;
        public FixtureRandomAccessStream(Stream stream) { this.stream = stream; }
        public Stream AsStream() => stream;
        public void Dispose() => stream.Dispose();
    }
    public enum CreationCollisionOption { OpenIfExists, ReplaceExisting }
    public enum NameCollisionOption { ReplaceExisting, FailIfExists }
    public enum StorageDeleteOption { PermanentDelete }
    public interface IStorageItem
    {
        string Name { get; }
        Task DeleteAsync();
    }
    public sealed class BasicProperties { public ulong Size; }
    public sealed class FolderProperties
    {
        public static ulong? Free = 1024UL * 1024 * 1024;
        public Task<IDictionary<string, object>> RetrievePropertiesAsync(string[] names) =>
            Task.FromResult<IDictionary<string, object>>(new Dictionary<string, object> { ["System.FreeSpace"] = Free });
    }
    public sealed class StorageFolder : IStorageItem
    {
        public string Path { get; private set; }
        public string Name => System.IO.Path.GetFileName(Path);
        public FolderProperties Properties { get; } = new FolderProperties();
        public StorageFolder(string path) { Path = path; }
        public static Task<StorageFolder> GetFolderFromPathAsync(string path) => Directory.Exists(path)
            ? Task.FromResult(new StorageFolder(path)) : throw new DirectoryNotFoundException();
        public Task<StorageFolder> CreateFolderAsync(string name, CreationCollisionOption option)
        {
            var path = System.IO.Path.Combine(Path, name);
            Directory.CreateDirectory(path);
            return Task.FromResult(new StorageFolder(path));
        }
        public Task<StorageFile> CreateFileAsync(string name, CreationCollisionOption option)
        {
            var path = System.IO.Path.Combine(Path, name);
            if (option == CreationCollisionOption.ReplaceExisting || !File.Exists(path)) File.WriteAllBytes(path, Array.Empty<byte>());
            return Task.FromResult(new StorageFile(path));
        }
        public Task<IStorageItem> TryGetItemAsync(string name)
        {
            var path = System.IO.Path.Combine(Path, name);
            return Task.FromResult<IStorageItem>(Directory.Exists(path) ? (IStorageItem)new StorageFolder(path) : File.Exists(path) ? new StorageFile(path) : null);
        }
        public Task<StorageFolder> GetFolderAsync(string name) => GetFolderFromPathAsync(System.IO.Path.Combine(Path, name));
        public Task<IReadOnlyList<StorageFile>> GetFilesAsync() => Task.FromResult<IReadOnlyList<StorageFile>>(Directory.GetFiles(Path).Select(p => new StorageFile(p)).ToArray());
        public Task<IReadOnlyList<StorageFolder>> GetFoldersAsync() => Task.FromResult<IReadOnlyList<StorageFolder>>(Directory.GetDirectories(Path).Select(p => new StorageFolder(p)).ToArray());
        public Task DeleteAsync() { Directory.Delete(Path, true); return Task.CompletedTask; }
        public Task DeleteAsync(StorageDeleteOption option) => DeleteAsync();
        public static bool FailRename;
        public Task RenameAsync(string name, NameCollisionOption option)
        {
            if (FailRename) throw new IOException("fixture commit failed");
            var target = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path), name);
            Directory.Move(Path, target);
            Path = target;
            return Task.CompletedTask;
        }
    }
    public sealed class StorageFile : IStorageItem
    {
        public string Path { get; }
        public string Name => System.IO.Path.GetFileName(Path);
        public StorageFile(string path) { Path = path; }
        public Task<BasicProperties> GetBasicPropertiesAsync() => Task.FromResult(new BasicProperties { Size = (ulong)new FileInfo(Path).Length });
        public Task<Stream> OpenStreamForReadAsync() => Task.FromResult<Stream>(File.OpenRead(Path));
        public static long? WriteBudget;
        private sealed class BudgetStream : FileStream
        {
            public BudgetStream(string path) : base(path, FileMode.Open, FileAccess.ReadWrite) { }
            public override Task WriteAsync(byte[] buffer, int offset, int count, System.Threading.CancellationToken token)
            {
                if (WriteBudget.HasValue)
                {
                    if (WriteBudget.Value < count) throw new IOException("fixture disk full", unchecked((int)0x80070027));
                    WriteBudget -= count;
                }
                return base.WriteAsync(buffer, offset, count, token);
            }
        }
        public Task<FixtureRandomAccessStream> OpenAsync(FileAccessMode mode) =>
            Task.FromResult(new FixtureRandomAccessStream(new BudgetStream(Path)));
        public Task DeleteAsync() { File.Delete(Path); return Task.CompletedTask; }
        public Task DeleteAsync(StorageDeleteOption option) => DeleteAsync();
        public Task CopyAsync(StorageFolder folder, string name, NameCollisionOption option)
        {
            File.Copy(Path, System.IO.Path.Combine(folder.Path, name), option == NameCollisionOption.ReplaceExisting);
            return Task.CompletedTask;
        }
    }
    public static class FileIO
    {
        public static Task<string> ReadTextAsync(StorageFile file) => File.ReadAllTextAsync(file.Path);
        public static Task WriteTextAsync(StorageFile file, string text) => File.WriteAllTextAsync(file.Path, text);
    }
    public sealed class ApplicationData
    {
        public static ApplicationData Current { get; } = new ApplicationData();
        public StorageFolder LocalFolder { get; set; }
    }
    public static class KnownFolders { public static StorageFolder RemovableDevices { get; set; } }
}
namespace Kiosk
{
    public sealed class SteamSession
    {
        public ulong SteamId => 1;
        public Task<string> RefreshTokenForLogOnAsync() => Task.FromResult("synthetic-token");
        public Task<bool> ForgetIfRefusedAsync() => Task.FromResult(false);
    }
    public static class Texts { public static string Get(string key, params object[] args) => key; }
}
namespace Kiosk.Steam
{
    public sealed class SteamLogOnException : Exception { public int Result { get; } }
    public sealed class DepotLicenseException : Exception { }
    public sealed class SteamCm : IDisposable
    {
        public static byte[] AppInfo;
        public static byte[] DepotKey;
        public static Task<List<string>> EndpointsAsync() => Task.FromResult(new List<string> { "fixture" });
        public Task ConnectAsync(string endpoint) => Task.CompletedTask;
        public Task LogOnAsync(ulong id, string token) => Task.CompletedTask;
        public Task<ulong> AppTokenAsync(uint app) => Task.FromResult(0UL);
        public Task<byte[]> AppInfoAsync(uint app, ulong token) => Task.FromResult(AppInfo);
        public Task<byte[]> DepotKeyAsync(uint app, uint depot) => Task.FromResult(DepotKey);
        public Task<ProtoMessage> ServiceAsync(string name, byte[] request)
        {
            var bytes = name.Contains("GetServersForSteamPipe")
                ? new ProtoWriter().Bytes(1, new ProtoWriter().String(1, "CDN").String(8, "fixture.invalid").Finish()).Finish()
                : new ProtoWriter().Uint(1, 1).Finish();
            return Task.FromResult(ProtoMessage.Read(bytes, 0, bytes.Length));
        }
        public void Dispose() { }
    }
}
