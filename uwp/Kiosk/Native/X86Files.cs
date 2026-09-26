using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Nativra.X86.Loader;

namespace Kiosk.Native
{
    /// <summary>
    /// The 32-bit guest's files as the console lets the app reach them. The
    /// portable System.IO layer is refused outside the app's own folders, so a
    /// game on a USB drive found none of its files. This opens the way the
    /// 64-bit side does: through the removable drive's folder handle
    /// (<see cref="UsbFiles"/>, fast), else the FromApp calls that go through
    /// the file broker. The broker answers ACCESS_DENIED for a file that is
    /// simply not there, so a refusal is checked against the file's
    /// attributes before it is reported as one.
    /// </summary>
    internal sealed class X86Files : IGuestFiles
    {
        private const string FromApp = "api-ms-win-core-file-fromapp-l1-1-0.dll";
        private const string FileApi = "api-ms-win-core-file-l1-1-0.dll";
        private static readonly IntPtr Invalid = new IntPtr(-1);

        [DllImport(FromApp, CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileFromAppW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport(FromApp, CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetFileAttributesExFromAppW(string name, int level, byte[] data);

        [DllImport(FromApp, CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindFirstFileExFromAppW(string name, int level, byte[] data, int search, IntPtr filter, uint flags);

        [DllImport(FromApp, CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateDirectoryFromAppW(string name, IntPtr security);

        [DllImport(FromApp, CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool DeleteFileFromAppW(string name);

        [DllImport(FromApp, CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool RemoveDirectoryFromAppW(string name);

        [DllImport(FileApi, CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool FindNextFileW(IntPtr find, byte[] data);

        [DllImport(FileApi, SetLastError = true)]
        private static extern bool FindClose(IntPtr find);

        public Stream Open(string path, FileMode mode, FileAccess access)
        {
            const uint GenericRead = 0x80000000, GenericWrite = 0x40000000;
            const uint ShareAll = 0x7;   // read, write, delete: the guest kernel shares everything too
            uint disposition;
            switch (mode)
            {
                case FileMode.CreateNew: disposition = 1; break;
                case FileMode.Create: disposition = 2; break;
                case FileMode.Open: disposition = 3; break;
                case FileMode.Truncate: disposition = 5; break;
                default: disposition = 4; break;   // OpenOrCreate, Append
            }
            var rights = (access & FileAccess.Read) != 0 ? GenericRead : 0;
            if ((access & FileAccess.Write) != 0) rights |= GenericWrite;

            IntPtr handle;
            int error;
            if (!UsbFiles.TryOpen(path, rights, ShareAll, disposition, 0, out handle, out error))
            {
                handle = CreateFileFromAppW(path, rights, ShareAll, IntPtr.Zero, disposition, 0, IntPtr.Zero);
                error = handle == Invalid || handle == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
            }
            if (handle == Invalid || handle == IntPtr.Zero)
            {
                // The broker says "access denied" for a missing file: look before believing it.
                if (error == 5 && mode != FileMode.CreateNew && mode != FileMode.Create && Stat(path) == null) error = 2;
                switch (error)
                {
                    case 2: throw new FileNotFoundException(path);
                    case 3: throw new DirectoryNotFoundException(path);
                    case 80: case 183: throw new IOException("exists: " + path);
                    case 5: throw new UnauthorizedAccessException(path);
                    default: throw new IOException("cannot open " + path + " (" + error + ")");
                }
            }
            var stream = new FileStream(new SafeFileHandle(handle, true), access);
            if (mode == FileMode.Append) stream.Seek(0, SeekOrigin.End);
            return stream;
        }

        public GuestFileEntry Stat(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var trimmed = path.Length > 3 ? path.TrimEnd('\\') : path;
            var data = new byte[36];   // WIN32_FILE_ATTRIBUTE_DATA
            if (!GetFileAttributesExFromAppW(trimmed, 0, data)) return null;
            var slash = trimmed.LastIndexOf('\\');
            return Entry(slash >= 0 ? trimmed.Substring(slash + 1) : trimmed,
                BitConverter.ToUInt32(data, 0), BitConverter.ToInt64(data, 20),
                ((long)BitConverter.ToUInt32(data, 28) << 32) | BitConverter.ToUInt32(data, 32));
        }

        public IEnumerable<GuestFileEntry> List(string folder)
        {
            var data = new byte[592];   // WIN32_FIND_DATAW
            var find = FindFirstFileExFromAppW(folder.TrimEnd('\\') + "\\*", 1 /* FindExInfoBasic */, data, 0, IntPtr.Zero, 0);
            if (find == Invalid || find == IntPtr.Zero) return Stat(folder) != null ? new List<GuestFileEntry>() : null;
            var list = new List<GuestFileEntry>();
            try
            {
                do
                {
                    var name = System.Text.Encoding.Unicode.GetString(data, 44, 520);
                    var end = name.IndexOf('\0');
                    if (end >= 0) name = name.Substring(0, end);
                    if (name == "." || name == "..") continue;
                    list.Add(Entry(name, BitConverter.ToUInt32(data, 0), BitConverter.ToInt64(data, 20),
                        ((long)BitConverter.ToUInt32(data, 28) << 32) | BitConverter.ToUInt32(data, 32)));
                }
                while (FindNextFileW(find, data));
            }
            finally
            {
                FindClose(find);
            }
            return list;
        }

        public bool CreateDirectory(string path)
        {
            var made = CreateDirectoryFromAppW(path, IntPtr.Zero);
            if (made) UsbFiles.Forget();   // a folder remembered as missing is there now
            return made;
        }

        public bool Delete(string path) => DeleteFileFromAppW(path) || RemoveDirectoryFromAppW(path);

        private static GuestFileEntry Entry(string name, uint attributes, long writeTime, long size) => new GuestFileEntry
        {
            Name = name,
            Attributes = (attributes & 0x10) != 0 ? FileAttributes.Directory
                : (FileAttributes)(attributes & 0x27) | FileAttributes.Archive,   // read-only, hidden, system
            Size = (attributes & 0x10) != 0 ? 0 : size,
            WriteTimeUtc = writeTime > 0 ? DateTime.FromFileTimeUtc(writeTime) : DateTime.UtcNow,
        };
    }
}
