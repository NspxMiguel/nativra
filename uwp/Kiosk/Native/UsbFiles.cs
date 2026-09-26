using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Storage;

namespace Kiosk.Native
{
    /// <summary>
    /// Fast opens on a USB drive.
    ///
    /// A file opened by path on a removable drive goes through the file
    /// broker in another process: measured at 205-315 ms per open, and the
    /// broker barely answers in parallel, so Hades needed about twenty
    /// minutes to load. A file opened from a StorageFolder that came from the
    /// removable-devices capability costs 9 ms. IStorageFolderHandleAccess
    /// turns that into an ordinary Win32 HANDLE, which ReadFile, the C
    /// runtime and memory mapping all accept. Folders are resolved once and
    /// kept.
    /// </summary>
    internal static class UsbFiles
    {
        private static readonly Guid FolderHandleAccess = new Guid("DF19938F-5462-48A0-BE65-D2A3271A08D6");
        private static readonly IntPtr Invalid = new IntPtr(-1);
        private const int NotFound = unchecked((int)0x80070002);
        private const int PathNotFound = unchecked((int)0x80070003);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CreateDelegate(IntPtr self, IntPtr name, uint creation, uint access, uint sharing,
            uint options, IntPtr oplock, out IntPtr handle);

        /// <summary>Drive root ("E:\") to the folder the capability gave for it.</summary>
        private static readonly Dictionary<string, StorageFolder> roots =
            new Dictionary<string, StorageFolder>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Directory path to its folder's IStorageFolderHandleAccess, or zero when it does not exist.</summary>
        private static readonly Dictionary<string, IntPtr> folders =
            new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);

        public static long Opened, Missed, Declined;

        /// <summary>Finds the removable drives; called once before the game starts.</summary>
        public static async Task InstallAsync()
        {
            try
            {
                foreach (var device in await KnownFolders.RemovableDevices.GetFoldersAsync())
                {
                    if (string.IsNullOrEmpty(device.Path)) continue;
                    lock (roots) roots[device.Path.TrimEnd('\\') + "\\"] = device;
                }
            }
            catch
            {
                // No capability or no drive: every open keeps its old route.
            }
        }

        private static StorageFolder RootFor(string path, out string relative)
        {
            relative = null;
            lock (roots)
            {
                foreach (var pair in roots)
                {
                    if (!path.StartsWith(pair.Key, StringComparison.OrdinalIgnoreCase)) continue;
                    relative = path.Substring(pair.Key.Length);
                    return pair.Value;
                }
            }
            return null;
        }

        /// <summary>The handle-access interface of a directory, resolved once.</summary>
        private static IntPtr Folder(string directory)
        {
            lock (folders)
                if (folders.TryGetValue(directory, out var known)) return known;

            var access = IntPtr.Zero;
            var root = RootFor(directory.TrimEnd('\\') + "\\", out var relative);
            if (root != null)
            {
                try
                {
                    relative = relative.TrimEnd('\\');
                    var folder = relative.Length == 0
                        ? root
                        : root.GetFolderAsync(relative).AsTask().GetAwaiter().GetResult();
                    var unknown = Marshal.GetIUnknownForObject(folder);
                    try
                    {
                        var iid = FolderHandleAccess;
                        if (Marshal.QueryInterface(unknown, ref iid, out var found) == 0) access = found;
                    }
                    finally
                    {
                        Marshal.Release(unknown);
                    }
                }
                catch
                {
                    // Not there: remembered as zero, so a miss costs nothing twice.
                }
            }
            lock (folders)
            {
                if (folders.TryGetValue(directory, out var raced))
                {
                    if (access != IntPtr.Zero && access != raced) Marshal.Release(access);
                    return raced;
                }
                folders[directory] = access;
            }
            return access;
        }

        /// <summary>Forgets resolved folders; called when the game creates or removes directories.</summary>
        public static void Forget()
        {
            lock (folders)
            {
                // Existing folders stay valid; only the remembered misses go.
                var missing = new List<string>();
                foreach (var pair in folders) if (pair.Value == IntPtr.Zero) missing.Add(pair.Key);
                foreach (var key in missing) folders.Remove(key);
            }
        }

        private static uint AccessOptions(uint access)
        {
            const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, GenericAll = 0x10000000;
            const uint Read = 0x120089, Write = 0x120116, ReadAttributes = 0x80, Delete = 0x10000;
            uint options = 0;
            if ((access & (GenericRead | GenericAll | 0x1)) != 0) options |= Read;
            if ((access & (GenericWrite | GenericAll | 0x2 | 0x4)) != 0) options |= Write;
            if ((access & 0x80) != 0) options |= ReadAttributes;
            if ((access & 0x10000) != 0) options |= Delete;
            return options == 0 ? ReadAttributes : options;
        }

        private static uint HandleOptions(uint flags)
        {
            // The FILE_FLAG_* bits HANDLE_OPTIONS shares with CreateFile.
            return flags & (0x04000000u | 0x08000000u | 0x10000000u | 0x20000000u | 0x40000000u | 0x80000000u);
        }

        /// <summary>
        /// Opens a file on a removable drive through its folder. Answers true
        /// when the call was handled here: handle is the file, or Invalid with
        /// the error set when it does not exist. False leaves the open to the
        /// broker (not a removable path, a directory, or anything unexpected).
        /// </summary>
        public static bool TryOpen(string path, uint access, uint share, uint disposition, uint flags,
            out IntPtr handle, out int error)
        {
            handle = Invalid;
            error = 0;
            if (string.IsNullOrEmpty(path) || (flags & 0x02000000) != 0) return false;   // directories
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path.Substring(4);
            path = path.Replace('/', '\\');
            if (path.IndexOf(@"\..", StringComparison.Ordinal) >= 0 || path.IndexOf(@"\.\", StringComparison.Ordinal) >= 0) return false;
            if (RootFor(path, out _) == null) return false;

            var cut = path.LastIndexOf('\\');
            if (cut < 0 || cut == path.Length - 1) return false;
            var folder = Folder(path.Substring(0, cut + 1));
            if (folder == IntPtr.Zero)
            {
                // The directory is not there: that is the answer, at no cost.
                System.Threading.Interlocked.Increment(ref Missed);
                error = 3;
                return true;
            }

            var name = Marshal.StringToHGlobalUni(path.Substring(cut + 1));
            try
            {
                var create = Marshal.GetDelegateForFunctionPointer<CreateDelegate>(
                    Marshal.ReadIntPtr(Marshal.ReadIntPtr(folder), 3 * IntPtr.Size));
                var code = create(folder, name, disposition == 0 ? 3u : disposition, AccessOptions(access),
                    share & 0x7, HandleOptions(flags), IntPtr.Zero, out handle);
                if (code == 0 && handle != IntPtr.Zero && handle != Invalid)
                {
                    System.Threading.Interlocked.Increment(ref Opened);
                    return true;
                }
                handle = Invalid;
                if (code == NotFound || code == PathNotFound)
                {
                    System.Threading.Interlocked.Increment(ref Missed);
                    error = code == NotFound ? 2 : 3;
                    return true;
                }
                System.Threading.Interlocked.Increment(ref Declined);
                return false;
            }
            catch
            {
                handle = Invalid;
                System.Threading.Interlocked.Increment(ref Declined);
                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(name);
            }
        }

        public static string Report() => "usb opened=" + Opened + " missed=" + Missed + " declined=" + Declined;
    }
}
