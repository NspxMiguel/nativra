using System;
using System.Collections.Generic;
using System.IO;

namespace Nativra.X86.Loader
{
    /// <summary>One directory entry, as FindFirstFile reports it.</summary>
    public sealed class GuestFileEntry
    {
        public string Name { get; set; }
        public FileAttributes Attributes { get; set; }
        public long Size { get; set; }
        public DateTime WriteTimeUtc { get; set; }
    }

    /// <summary>
    /// Where the guest's file calls land. Paths are the guest's, absolute and
    /// normalised (drive letter, backslashes). The app plugs in its own broker
    /// for the console's storage; <see cref="HostFolderFiles"/> is the portable
    /// default, used on the runner.
    /// </summary>
    public interface IGuestFiles
    {
        /// <summary>Opens a file, or throws FileNotFound/DirectoryNotFound/UnauthorizedAccess/IOException.</summary>
        Stream Open(string path, FileMode mode, FileAccess access);

        /// <summary>The entry for a file or folder, or null when there is none.</summary>
        GuestFileEntry Stat(string path);

        /// <summary>The entries of a folder, or null when it does not exist.</summary>
        IEnumerable<GuestFileEntry> List(string folder);

        bool CreateDirectory(string path);
        bool Delete(string path);
    }

    /// <summary>
    /// The guest's files through System.IO, with a guest folder standing for a
    /// host folder: "C:\game\data\x.pak" under guest root "C:\game" is
    /// hostRoot/data/x.pak. With no guest root the paths are used as they are
    /// (the app, where guest and host share a file system). Paths outside the
    /// guest root do not exist.
    /// </summary>
    public sealed class HostFolderFiles : IGuestFiles
    {
        private readonly string guestRoot;
        private readonly string hostRoot;

        public HostFolderFiles(string guestRoot = null, string hostRoot = null)
        {
            this.guestRoot = guestRoot?.TrimEnd('\\');
            this.hostRoot = hostRoot;
        }

        private string Host(string path)
        {
            if (guestRoot == null) return path;
            if (path.Equals(guestRoot, StringComparison.OrdinalIgnoreCase)) return hostRoot;
            if (!path.StartsWith(guestRoot + "\\", StringComparison.OrdinalIgnoreCase)) return null;
            var rest = path.Substring(guestRoot.Length + 1).Replace('\\', Path.DirectorySeparatorChar);
            return Path.Combine(hostRoot, rest);
        }

        public Stream Open(string path, FileMode mode, FileAccess access)
        {
            var host = Host(path) ?? throw new DirectoryNotFoundException(path);
            return new FileStream(host, mode, access, FileShare.ReadWrite | FileShare.Delete);
        }

        public GuestFileEntry Stat(string path)
        {
            var host = Host(path);
            if (host == null) return null;
            if (Directory.Exists(host)) return Entry(new DirectoryInfo(host));
            if (File.Exists(host)) return Entry(new FileInfo(host));
            return null;
        }

        public IEnumerable<GuestFileEntry> List(string folder)
        {
            var host = Host(folder);
            if (host == null || !Directory.Exists(host)) return null;
            var list = new List<GuestFileEntry>();
            foreach (var info in new DirectoryInfo(host).EnumerateFileSystemInfos()) list.Add(Entry(info));
            return list;
        }

        public bool CreateDirectory(string path)
        {
            var host = Host(path);
            if (host == null || Directory.Exists(host) || File.Exists(host)) return false;
            Directory.CreateDirectory(host);
            return true;
        }

        public bool Delete(string path)
        {
            var host = Host(path);
            if (host == null || !File.Exists(host)) return false;
            File.Delete(host);
            return true;
        }

        private static GuestFileEntry Entry(FileSystemInfo info) => new GuestFileEntry
        {
            Name = info.Name,
            Attributes = info is DirectoryInfo ? FileAttributes.Directory : FileAttributes.Archive,
            Size = info is FileInfo f ? f.Length : 0,
            WriteTimeUtc = info.LastWriteTimeUtc,
        };
    }
}
