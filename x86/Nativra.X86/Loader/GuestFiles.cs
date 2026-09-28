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

    /// <summary>
    /// What the guest kernel really talks to: another <see cref="IGuestFiles"/>
    /// behind two rules, as Wine keeps a Windows program inside its prefix.
    ///
    /// Mapped folders: a guest prefix (C:\users\Player) stands for another
    /// path of the inner file system (the app's profile folder), so the guest
    /// sees Windows paths and never the host's.
    ///
    /// Nothing escapes: a host refusal becomes a plain "no" (null, false, or an
    /// IOException/UnauthorizedAccessException from Open, which the callers
    /// turn into Win32 errors and errno), and every folder on the way to one
    /// the guest can reach (the drive root, C:\users, the host folders above
    /// the game) answers as an existing directory, so code that walks a path
    /// from its root creating each part never asks the host for a folder the
    /// app may not see.
    /// </summary>
    public sealed class GuardedFiles : IGuestFiles
    {
        private readonly IGuestFiles inner;
        private readonly List<KeyValuePair<string, string>> maps = new List<KeyValuePair<string, string>>();
        private readonly List<string> reachable = new List<string>();

        public GuardedFiles(IGuestFiles inner) { this.inner = inner; }

        /// <summary>Makes <paramref name="guestPrefix"/> and everything under it stand for <paramref name="target"/>.</summary>
        public void Map(string guestPrefix, string target)
        {
            guestPrefix = Clean(guestPrefix);
            maps.Add(new KeyValuePair<string, string>(guestPrefix, Clean(target)));
            maps.Sort((a, b) => b.Key.Length.CompareTo(a.Key.Length));   // longest prefix first
            Reach(guestPrefix);
        }

        /// <summary>A folder the guest can reach: its ancestors exist, as far as the guest can tell.</summary>
        public void Reach(string guestPath)
        {
            if (!string.IsNullOrEmpty(guestPath)) reachable.Add(Clean(guestPath));
        }

        private static string Clean(string path) => (path ?? "").Replace('/', '\\').TrimEnd('\\');

        /// <summary>The path the inner file system sees.</summary>
        public string Resolve(string path)
        {
            var clean = Clean(path);
            foreach (var map in maps)
            {
                if (clean.Equals(map.Key, StringComparison.OrdinalIgnoreCase)) return map.Value;
                if (clean.StartsWith(map.Key + "\\", StringComparison.OrdinalIgnoreCase)) return map.Value + clean.Substring(map.Key.Length);
            }
            return path;
        }

        /// <summary>True for a strict ancestor of a reachable folder (C:, C:\users...).</summary>
        private bool IsAncestor(string path)
        {
            var clean = Clean(path);
            if (clean.Length == 0) return false;
            foreach (var r in reachable)
                if (r.Length > clean.Length && r.StartsWith(clean + "\\", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool Critical(Exception e) => e is OutOfMemoryException || e is StackOverflowException;

        public Stream Open(string path, FileMode mode, FileAccess access)
        {
            try { return inner.Open(Resolve(path), mode, access); }
            catch (IOException) { throw; }
            catch (UnauthorizedAccessException) { throw; }
            catch (Exception e) when (!Critical(e)) { throw new IOException(e.Message, e); }
        }

        public GuestFileEntry Stat(string path)
        {
            GuestFileEntry entry = null;
            try { entry = inner.Stat(Resolve(path)); }
            catch (Exception e) when (!Critical(e)) { }
            if (entry == null && IsAncestor(path))
            {
                var clean = Clean(path);
                var slash = clean.LastIndexOf('\\');
                entry = new GuestFileEntry { Name = slash >= 0 ? clean.Substring(slash + 1) : clean, Attributes = FileAttributes.Directory, WriteTimeUtc = DateTime.UtcNow };
            }
            return entry;
        }

        public IEnumerable<GuestFileEntry> List(string folder)
        {
            IEnumerable<GuestFileEntry> entries = null;
            try { entries = inner.List(Resolve(folder)); }
            catch (Exception e) when (!Critical(e)) { }
            if (entries != null || !IsAncestor(folder)) return entries;

            // A folder the host will not list: only the way down to what the guest can reach.
            var clean = Clean(folder);
            var names = new List<GuestFileEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in reachable)
            {
                if (!r.StartsWith(clean + "\\", StringComparison.OrdinalIgnoreCase)) continue;
                var rest = r.Substring(clean.Length + 1);
                var child = rest.Split('\\')[0];
                if (seen.Add(child)) names.Add(new GuestFileEntry { Name = child, Attributes = FileAttributes.Directory, WriteTimeUtc = DateTime.UtcNow });
            }
            return names;
        }

        public bool CreateDirectory(string path)
        {
            if (IsAncestor(path)) return false;   // there already, as far as the guest can tell
            try { return inner.CreateDirectory(Resolve(path)); }
            catch (Exception e) when (!Critical(e)) { return false; }
        }

        public bool Delete(string path)
        {
            if (IsAncestor(path)) return false;
            try { return inner.Delete(Resolve(path)); }
            catch (Exception e) when (!Critical(e)) { return false; }
        }
    }
}
