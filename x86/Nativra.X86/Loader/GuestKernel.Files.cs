using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Nativra.X86.Loader
{
    // kernel32's file API over IGuestFiles: handles, reads and writes, seeking,
    // attributes, directory listings and path resolution. The current directory
    // starts as the program's folder, as it does when a game is launched from
    // its own shortcut.
    public sealed partial class GuestKernel
    {
        private const uint InvalidHandleValue = 0xFFFFFFFF;
        private const uint InvalidFileAttributes = 0xFFFFFFFF;
        private const uint ErrorFileNotFound = 2, ErrorPathNotFound = 3, ErrorAccessDenied = 5;
        private const uint ErrorInvalidHandle = 6, ErrorNoMoreFiles = 18, ErrorFileExists = 80;
        private const uint ErrorInvalidParameter = 87, ErrorAlreadyExists = 183, ErrorNegativeSeek = 131;

        private readonly Dictionary<uint, OpenFile> files = new Dictionary<uint, OpenFile>();
        private readonly Dictionary<uint, Queue<GuestFileEntry>> searches = new Dictionary<uint, Queue<GuestFileEntry>>();
        private string currentDirectory;

        private sealed class OpenFile
        {
            public string Path;
            public Stream Stream;   // null for a folder opened with FILE_FLAG_BACKUP_SEMANTICS
        }

        private IGuestFiles filesInner = new HostFolderFiles();
        private GuardedFiles filesGuard;

        /// <summary>
        /// The guest's file system: whatever the host sets (the portable
        /// System.IO one by default), always seen through <see cref="GuardedFiles"/>,
        /// which maps the guest's profile and keeps host refusals from escaping.
        /// </summary>
        public IGuestFiles Files
        {
            get => filesGuard ?? (filesGuard = BuildGuard());
            set { filesInner = value; filesGuard = null; }
        }

        /// <summary>The guest user's profile folder, as Wine names it.</summary>
        public const string GuestProfile = "C:\\users\\Player";

        private string profileRoot;

        /// <summary>
        /// Where the guest's C:\users\Player (and C:\users\Public, C:\ProgramData)
        /// really are, as a path the inner file system understands: the app's
        /// profile folder. By default a folder beside the game.
        /// </summary>
        public string ProfileRoot
        {
            get => profileRoot ?? Folder(ExePath) + "nativra-user";
            set { profileRoot = value?.TrimEnd('\\'); filesGuard = null; }
        }

        private GuardedFiles BuildGuard()
        {
            var guard = new GuardedFiles(filesInner);
            var root = ProfileRoot;
            guard.Map(GuestProfile, root);
            guard.Map("C:\\users\\Public", root + "\\Public");
            guard.Map("C:\\ProgramData", root + "\\ProgramData");
            guard.Reach(Folder(ExePath));
            guard.Reach(root);
            return guard;
        }

        /// <summary>Files the guest looked for and did not find, in order — what a missing asset looks like.</summary>
        public List<string> FilesNotFound { get; } = new List<string>();

        private string CurrentDirectory
        {
            get => currentDirectory ?? Folder(ExePath).TrimEnd('\\');
            set => currentDirectory = value;
        }

        private void InstallFiles(GuestImports i)
        {
            const string k = "kernel32.dll";

            i.Register(k, "CreateFileA", CallConv.Stdcall, 7, c => CreateFile(c, false));
            i.Register(k, "CreateFileW", CallConv.Stdcall, 7, c => CreateFile(c, true));
            i.Register(k, "ReadFile", CallConv.Stdcall, 5, c => ReadFile(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3)));
            i.Register(k, "WriteFile", CallConv.Stdcall, 5, c => WriteFile(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3)));
            i.Register(k, "SetFilePointer", CallConv.Stdcall, 4, c => SetFilePointer(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3)));
            i.Register(k, "SetFilePointerEx", CallConv.Stdcall, 5, c =>
                SetFilePointerEx(c.Arg(0), (long)c.Arg64(1), c.Arg(3), c.Arg(4)));
            i.Register(k, "GetFileSize", CallConv.Stdcall, 2, c => GetFileSize(c.Arg(0), c.Arg(1)));
            i.Register(k, "GetFileSizeEx", CallConv.Stdcall, 2, c =>
            {
                if (!TryFile(c.Arg(0), out var f)) return 0;
                memory.Write64(c.Arg(1), (ulong)f.Stream.Length);
                return 1;
            });
            i.Register(k, "GetFileType", CallConv.Stdcall, 1, c =>
            {
                var h = c.Arg(0);
                if (h == StdInput || h == StdOutput || h == StdError) return 2;   // FILE_TYPE_CHAR
                return files.ContainsKey(h) ? 1u : 0u;                             // FILE_TYPE_DISK / UNKNOWN
            });
            i.Register(k, "SetEndOfFile", CallConv.Stdcall, 1, c =>
            {
                if (!TryFile(c.Arg(0), out var f)) return 0;
                f.Stream.SetLength(f.Stream.Position);
                return 1;
            });
            i.Register(k, "FlushFileBuffers", CallConv.Stdcall, 1, c =>
            {
                if (files.TryGetValue(c.Arg(0), out var f) && f.Stream != null) f.Stream.Flush();
                return 1;
            });
            i.Register(k, "CloseHandle", CallConv.Stdcall, 1, c => CloseHandle(c.Arg(0)));

            i.Register(k, "GetFileAttributesA", CallConv.Stdcall, 1, c => FileAttributesOf(c.Arg(0), false));
            i.Register(k, "GetFileAttributesW", CallConv.Stdcall, 1, c => FileAttributesOf(c.Arg(0), true));
            i.Register(k, "GetFileAttributesExA", CallConv.Stdcall, 3, c => FileAttributesEx(c.Arg(0), c.Arg(2), false));
            i.Register(k, "GetFileAttributesExW", CallConv.Stdcall, 3, c => FileAttributesEx(c.Arg(0), c.Arg(2), true));
            i.Register(k, "SetFileAttributesA", CallConv.Stdcall, 2, c => 1);
            i.Register(k, "SetFileAttributesW", CallConv.Stdcall, 2, c => 1);

            i.Register(k, "FindFirstFileA", CallConv.Stdcall, 2, c => FindFirst(c.Arg(0), c.Arg(1), false));
            i.Register(k, "FindFirstFileW", CallConv.Stdcall, 2, c => FindFirst(c.Arg(0), c.Arg(1), true));
            i.Register(k, "FindFirstFileExA", CallConv.Stdcall, 6, c => FindFirst(c.Arg(0), c.Arg(2), false));
            i.Register(k, "FindFirstFileExW", CallConv.Stdcall, 6, c => FindFirst(c.Arg(0), c.Arg(2), true));
            i.Register(k, "FindNextFileA", CallConv.Stdcall, 2, c => FindNext(c.Arg(0), c.Arg(1), false));
            i.Register(k, "FindNextFileW", CallConv.Stdcall, 2, c => FindNext(c.Arg(0), c.Arg(1), true));
            i.Register(k, "FindClose", CallConv.Stdcall, 1, c => searches.Remove(c.Arg(0)) ? 1u : 0u);

            i.Register(k, "GetCurrentDirectoryA", CallConv.Stdcall, 2, c => CopyPath(CurrentDirectory, c.Arg(1), c.Arg(0), false));
            i.Register(k, "GetCurrentDirectoryW", CallConv.Stdcall, 2, c => CopyPath(CurrentDirectory, c.Arg(1), c.Arg(0), true));
            i.Register(k, "SetCurrentDirectoryA", CallConv.Stdcall, 1, c => SetCurrentDirectory(c.Arg(0), false));
            i.Register(k, "SetCurrentDirectoryW", CallConv.Stdcall, 1, c => SetCurrentDirectory(c.Arg(0), true));
            i.Register(k, "GetFullPathNameA", CallConv.Stdcall, 4, c => FullPathName(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3), false));
            i.Register(k, "GetFullPathNameW", CallConv.Stdcall, 4, c => FullPathName(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3), true));
            i.Register(k, "CreateDirectoryA", CallConv.Stdcall, 2, c => CreateDirectory(c.Arg(0), false));
            i.Register(k, "CreateDirectoryW", CallConv.Stdcall, 2, c => CreateDirectory(c.Arg(0), true));
            i.Register(k, "DeleteFileA", CallConv.Stdcall, 1, c => DeleteFile(c.Arg(0), false));
            i.Register(k, "DeleteFileW", CallConv.Stdcall, 1, c => DeleteFile(c.Arg(0), true));
            i.Register(k, "AreFileApisANSI", CallConv.Stdcall, 0, c => 1);
            i.Register(k, "SetFileApisToANSI", CallConv.Stdcall, 0, c => 0);
            // Temporary files go to the game's own folder, which the file
            // system serves; the console has no C:\Temp for it.
            i.Register(k, "GetTempPathA", CallConv.Stdcall, 2, c => CopyPath(Folder(ExePath), c.Arg(1), c.Arg(0), false));
            i.Register(k, "GetTempPathW", CallConv.Stdcall, 2, c => CopyPath(Folder(ExePath), c.Arg(1), c.Arg(0), true));
            i.Register(k, "GetFileInformationByHandle", CallConv.Stdcall, 2, c => FileInformation(c.Arg(0), c.Arg(1)));
            i.Register(k, "DuplicateHandle", CallConv.Stdcall, 7, c =>
            {
                // Same process, one thread: the duplicate is the handle itself.
                if (c.Arg(3) != 0) memory.Write32(c.Arg(3), c.Arg(1));
                return 1;
            });
            i.Register(k, "GetDriveTypeA", CallConv.Stdcall, 1, c => 3);   // DRIVE_FIXED
            i.Register(k, "GetDriveTypeW", CallConv.Stdcall, 1, c => 3);
        }

        // --- paths --------------------------------------------------------

        /// <summary>
        /// A guest path made absolute against the current directory and
        /// normalised: forward slashes turned back, "." and ".." folded.
        /// </summary>
        internal string FullPath(string path)
        {
            path = (path ?? "").Replace('/', '\\');
            if (path.StartsWith("\\\\?\\", StringComparison.Ordinal)) path = path.Substring(4);

            string root, rest;
            if (path.Length >= 2 && path[1] == ':')
            {
                root = char.ToUpperInvariant(path[0]) + ":";
                rest = path.Substring(2);
                if (!rest.StartsWith("\\", StringComparison.Ordinal))
                    rest = CurrentDirectory.Substring(2) + "\\" + rest;   // "C:file": relative on that drive
            }
            else if (path.StartsWith("\\", StringComparison.Ordinal))
            {
                root = CurrentDirectory.Substring(0, 2);
                rest = path;
            }
            else
            {
                root = CurrentDirectory.Substring(0, 2);
                rest = CurrentDirectory.Substring(2) + "\\" + path;
            }

            var parts = new List<string>();
            foreach (var part in rest.Split('\\'))
            {
                if (part.Length == 0 || part == ".") continue;
                if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
                parts.Add(part);
            }
            var full = new StringBuilder(root);
            foreach (var part in parts) full.Append('\\').Append(part);
            if (parts.Count == 0) full.Append('\\');
            if (path.EndsWith("\\", StringComparison.Ordinal) && parts.Count > 0) full.Append('\\');
            return full.ToString();
        }

        private uint CopyPath(string text, uint buffer, uint size, bool wide)
        {
            var needed = CopyOut(text, buffer, size, wide);
            return needed <= size && buffer != 0 ? needed - 1 : needed;
        }

        private uint FullPathName(uint name, uint size, uint buffer, uint filePart, bool wide)
        {
            var full = FullPath(ReadText(name, wide));
            var result = CopyPath(full, buffer, size, wide);
            if (filePart != 0 && result < size)
            {
                var slash = full.LastIndexOf('\\');
                var offset = (uint)(wide ? (slash + 1) * 2 : Ansi.Encode(full.Substring(0, slash + 1)).Length);
                memory.Write32(filePart, slash + 1 < full.Length ? buffer + offset : 0);
            }
            return result;
        }

        private uint SetCurrentDirectory(uint name, bool wide)
        {
            var full = FullPath(ReadText(name, wide)).TrimEnd('\\');
            if (full.Length == 2) full += "\\";
            var entry = Files.Stat(full);
            if (entry == null || (entry.Attributes & FileAttributes.Directory) == 0)
            {
                process.LastError = ErrorPathNotFound;
                return 0;
            }
            CurrentDirectory = full;
            return 1;
        }

        // --- handles ------------------------------------------------------

        private uint CreateFile(GuestCall c, bool wide)
        {
            const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, GenericAll = 0x10000000;
            const uint FileReadData = 0x1, FileWriteData = 0x2, FileAppendData = 0x4;
            const uint BackupSemantics = 0x02000000;

            var path = FullPath(ReadText(c.Arg(0), wide));
            uint access = c.Arg(1), disposition = c.Arg(4), flags = c.Arg(5);

            var read = (access & (GenericRead | GenericAll | FileReadData)) != 0;
            var write = (access & (GenericWrite | GenericAll | FileWriteData | FileAppendData)) != 0;
            var entry = Files.Stat(path);

            if (entry != null && (entry.Attributes & FileAttributes.Directory) != 0)
            {
                if ((flags & BackupSemantics) == 0) { process.LastError = ErrorAccessDenied; return InvalidHandleValue; }
                var folder = NewHandle();
                files[folder] = new OpenFile { Path = path };
                return folder;
            }

            FileMode mode;
            switch (disposition)
            {
                case 1: mode = FileMode.CreateNew; break;
                case 2: mode = FileMode.Create; break;
                case 3: mode = FileMode.Open; break;
                case 4: mode = FileMode.OpenOrCreate; break;
                case 5: mode = FileMode.Truncate; break;
                default: process.LastError = ErrorInvalidParameter; return InvalidHandleValue;
            }
            if (mode == FileMode.CreateNew && entry != null) { process.LastError = ErrorFileExists; return InvalidHandleValue; }
            if ((mode == FileMode.Open || mode == FileMode.Truncate) && entry == null)
            {
                FilesNotFound.Add(path);
                process.LastError = ErrorFileNotFound;
                return InvalidHandleValue;
            }

            var fileAccess = write ? (read ? FileAccess.ReadWrite : FileAccess.Write) : FileAccess.Read;
            if (!write && mode != FileMode.Open) fileAccess = FileAccess.ReadWrite;   // creating needs write access

            Stream stream;
            try
            {
                stream = Files.Open(path, mode, fileAccess);
            }
            catch (FileNotFoundException) { FilesNotFound.Add(path); process.LastError = ErrorFileNotFound; return InvalidHandleValue; }
            catch (DirectoryNotFoundException) { FilesNotFound.Add(path); process.LastError = ErrorPathNotFound; return InvalidHandleValue; }
            catch (UnauthorizedAccessException) { process.LastError = ErrorAccessDenied; return InvalidHandleValue; }
            catch (IOException) { process.LastError = ErrorAccessDenied; return InvalidHandleValue; }

            var handle = NewHandle();
            files[handle] = new OpenFile { Path = path, Stream = stream };
            // CREATE_ALWAYS / OPEN_ALWAYS over an existing file succeed and say so.
            process.LastError = (mode == FileMode.Create || mode == FileMode.OpenOrCreate) && entry != null ? ErrorAlreadyExists : 0;
            return handle;
        }

        private bool TryFile(uint handle, out OpenFile file)
        {
            if (files.TryGetValue(handle, out file) && file.Stream != null) return true;
            process.LastError = ErrorInvalidHandle;
            return false;
        }

        private uint ReadFile(uint handle, uint buffer, uint count, uint readOut)
        {
            if (readOut != 0) memory.Write32(readOut, 0);
            if (handle == StdInput) return 1;   // nothing to read: end of input
            if (!TryFile(handle, out var f)) return 0;

            var chunk = new byte[Math.Min(count, 1u << 20)];
            uint total = 0;
            while (total < count)
            {
                var n = f.Stream.Read(chunk, 0, (int)Math.Min((uint)chunk.Length, count - total));
                if (n <= 0) break;
                memory.WriteBytes(buffer + total, chunk, 0, n);
                total += (uint)n;
            }
            if (readOut != 0) memory.Write32(readOut, total);
            return 1;
        }

        private uint WriteFile(uint handle, uint buffer, uint count, uint writtenOut)
        {
            if (handle == StdOutput || handle == StdError)
            {
                Say(Ansi.Decode(memory.ReadBytes(buffer, (int)count)));
                if (writtenOut != 0) memory.Write32(writtenOut, count);
                return 1;
            }
            if (writtenOut != 0) memory.Write32(writtenOut, 0);
            if (!TryFile(handle, out var f)) return 0;

            uint total = 0;
            while (total < count)
            {
                var n = (int)Math.Min(1u << 20, count - total);
                f.Stream.Write(memory.ReadBytes(buffer + total, n), 0, n);
                total += (uint)n;
            }
            if (writtenOut != 0) memory.Write32(writtenOut, total);
            return 1;
        }

        private long Seek(OpenFile f, long distance, uint method)
        {
            long origin;
            switch (method)
            {
                case 0: origin = 0; break;
                case 1: origin = f.Stream.Position; break;
                case 2: origin = f.Stream.Length; break;
                default: process.LastError = ErrorInvalidParameter; return -1;
            }
            var target = origin + distance;
            if (target < 0) { process.LastError = ErrorNegativeSeek; return -1; }
            f.Stream.Position = target;
            return target;
        }

        private uint SetFilePointer(uint handle, uint low, uint highPtr, uint method)
        {
            if (!TryFile(handle, out var f)) return InvalidHandleValue;
            var distance = highPtr != 0
                ? (long)(((ulong)memory.Read32(highPtr) << 32) | low)
                : (int)low;   // without a high part the distance is a signed 32-bit value
            var position = Seek(f, distance, method);
            if (position < 0) return 0xFFFFFFFF;   // INVALID_SET_FILE_POINTER
            if (highPtr != 0) memory.Write32(highPtr, (uint)(position >> 32));
            process.LastError = 0;
            return (uint)position;
        }

        private uint SetFilePointerEx(uint handle, long distance, uint newPosition, uint method)
        {
            if (!TryFile(handle, out var f)) return 0;
            var position = Seek(f, distance, method);
            if (position < 0) return 0;
            if (newPosition != 0) memory.Write64(newPosition, (ulong)position);
            return 1;
        }

        private uint GetFileSize(uint handle, uint highOut)
        {
            if (!TryFile(handle, out var f)) return 0xFFFFFFFF;   // INVALID_FILE_SIZE
            var length = f.Stream.Length;
            if (highOut != 0) memory.Write32(highOut, (uint)(length >> 32));
            process.LastError = 0;
            return (uint)length;
        }

        private uint CloseHandle(uint handle)
        {
            if (files.TryGetValue(handle, out var f))
            {
                f.Stream?.Dispose();
                files.Remove(handle);
                return 1;
            }
            if (CloseRuntimeHandle(handle) || CloseMapping(handle)) return 1;
            // Standard handles, pseudo handles and anything else the guest holds.
            return 1;
        }

        private uint FileInformation(uint handle, uint info)
        {
            if (!files.TryGetValue(handle, out var f)) { process.LastError = ErrorInvalidHandle; return 0; }
            var entry = Files.Stat(f.Path);
            memory.WriteBytes(info, new byte[52]);
            var size = f.Stream != null ? f.Stream.Length : entry?.Size ?? 0;
            var time = (ulong)(entry?.WriteTimeUtc ?? DateTime.UtcNow).ToFileTimeUtc();
            memory.Write32(info + 0, entry != null ? Win32Attributes(entry) : 0x80);
            memory.Write64(info + 4, time);
            memory.Write64(info + 12, time);
            memory.Write64(info + 20, time);
            memory.Write32(info + 28, 0x4E415456);   // volume serial
            memory.Write32(info + 32, (uint)(size >> 32));
            memory.Write32(info + 36, (uint)size);
            memory.Write32(info + 40, 1);            // links
            var id = (uint)f.Path.ToLowerInvariant().GetHashCode();
            memory.Write32(info + 48, id);           // file index: stable per path
            return 1;
        }

        // --- attributes and listings ----------------------------------------

        private static uint Win32Attributes(GuestFileEntry e)
        {
            var a = (uint)e.Attributes & 0xFFFF;
            return a == 0 ? 0x80 /* FILE_ATTRIBUTE_NORMAL */ : a;
        }

        private GuestFileEntry StatOrFail(string path)
        {
            var entry = Files.Stat(path);
            if (entry == null)
            {
                FilesNotFound.Add(path);
                process.LastError = Files.Stat(Folder(path).TrimEnd('\\')) == null ? ErrorPathNotFound : ErrorFileNotFound;
            }
            return entry;
        }

        private uint FileAttributesOf(uint name, bool wide)
        {
            var entry = StatOrFail(FullPath(ReadText(name, wide)));
            return entry == null ? InvalidFileAttributes : Win32Attributes(entry);
        }

        private uint FileAttributesEx(uint name, uint data, bool wide)
        {
            var entry = StatOrFail(FullPath(ReadText(name, wide)));
            if (entry == null) return 0;
            var time = (ulong)entry.WriteTimeUtc.ToFileTimeUtc();
            memory.Write32(data + 0, Win32Attributes(entry));
            memory.Write64(data + 4, time);    // creation
            memory.Write64(data + 12, time);   // last access
            memory.Write64(data + 20, time);   // last write
            memory.Write32(data + 28, (uint)(entry.Size >> 32));
            memory.Write32(data + 32, (uint)entry.Size);
            return 1;
        }

        private uint FindFirst(uint patternPtr, uint data, bool wide)
        {
            var full = FullPath(ReadText(patternPtr, wide));
            var slash = full.LastIndexOf('\\');
            var folder = full.Substring(0, slash);
            if (folder.Length == 2) folder += "\\";
            var pattern = full.Substring(slash + 1);

            var listing = Files.List(folder);
            if (listing == null)
            {
                process.LastError = ErrorPathNotFound;
                return InvalidHandleValue;
            }
            var matches = new Queue<GuestFileEntry>();
            foreach (var entry in listing)
                if (Wildcard(pattern, entry.Name)) matches.Enqueue(entry);
            if (matches.Count == 0)
            {
                FilesNotFound.Add(full);
                process.LastError = ErrorFileNotFound;
                return InvalidHandleValue;
            }

            var handle = NewHandle();
            searches[handle] = matches;
            WriteFindData(data, matches.Dequeue(), wide);
            return handle;
        }

        private uint FindNext(uint handle, uint data, bool wide)
        {
            if (!searches.TryGetValue(handle, out var matches)) { process.LastError = ErrorInvalidHandle; return 0; }
            if (matches.Count == 0) { process.LastError = ErrorNoMoreFiles; return 0; }
            WriteFindData(data, matches.Dequeue(), wide);
            return 1;
        }

        private void WriteFindData(uint p, GuestFileEntry e, bool wide)
        {
            // WIN32_FIND_DATA: 44 bytes of header, then cFileName[260] and
            // cAlternateFileName[14] in the call's character width.
            var nameBytes = wide ? 2u : 1u;
            memory.WriteBytes(p, new byte[44 + 274 * nameBytes]);
            var time = (ulong)e.WriteTimeUtc.ToFileTimeUtc();
            memory.Write32(p + 0, Win32Attributes(e));
            memory.Write64(p + 4, time);
            memory.Write64(p + 12, time);
            memory.Write64(p + 20, time);
            memory.Write32(p + 28, (uint)(e.Size >> 32));
            memory.Write32(p + 32, (uint)e.Size);
            var name = e.Name.Length > 259 ? e.Name.Substring(0, 259) : e.Name;
            WriteText(p + 44, name, wide);
        }

        /// <summary>DOS wildcard match, case-insensitive; "*.*" matches names with no dot too.</summary>
        public static bool Wildcard(string pattern, string name)
        {
            if (pattern == "*" || pattern == "*.*") return true;
            return Match(pattern.ToLowerInvariant(), 0, name.ToLowerInvariant(), 0);
        }

        private static bool Match(string p, int pi, string s, int si)
        {
            while (pi < p.Length)
            {
                var ch = p[pi];
                if (ch == '*')
                {
                    for (var k = si; k <= s.Length; k++)
                        if (Match(p, pi + 1, s, k)) return true;
                    return false;
                }
                if (si >= s.Length) return ch == '.' && pi == p.Length - 1;   // "name." matches "name"
                if (ch != '?' && ch != s[si]) return false;
                pi++;
                si++;
            }
            return si == s.Length;
        }

        private uint CreateDirectory(uint name, bool wide)
        {
            var path = FullPath(ReadText(name, wide)).TrimEnd('\\');
            if (Files.Stat(path) != null) { process.LastError = ErrorAlreadyExists; return 0; }
            try
            {
                if (Files.CreateDirectory(path)) return 1;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            process.LastError = ErrorPathNotFound;
            return 0;
        }

        private uint DeleteFile(uint name, bool wide)
        {
            var path = FullPath(ReadText(name, wide));
            try
            {
                if (Files.Delete(path)) return 1;
                process.LastError = ErrorFileNotFound;
            }
            catch (IOException) { process.LastError = ErrorAccessDenied; }
            catch (UnauthorizedAccessException) { process.LastError = ErrorAccessDenied; }
            return 0;
        }
    }
}
