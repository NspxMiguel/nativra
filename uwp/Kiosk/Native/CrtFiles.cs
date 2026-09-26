using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Kiosk.Native
{
    /// <summary>
    /// The C runtime's own file opening, for games on a USB drive.
    ///
    /// fopen and friends call CreateFileW from inside the system's C runtime,
    /// where the game's imports are not in the way, so they never reach the
    /// FromApp forms that open a file on a removable drive: Hades read its
    /// checksums with fopen and was told "Permission denied". Each open is
    /// tried as the game asked first; only when that fails is the file opened
    /// through CreateFileFromAppW and handed to the runtime as a descriptor,
    /// so from then on it is an ordinary FILE* or descriptor.
    /// </summary>
    internal static class CrtFiles
    {
        private const string Stdio = "api-ms-win-crt-stdio-l1-1-0.dll";
        private const string FileSystem = "api-ms-win-crt-filesystem-l1-1-0.dll";
        private const string Runtime = "api-ms-win-crt-runtime-l1-1-0.dll";

        private const int O_WRONLY = 0x0001, O_RDWR = 0x0002, O_APPEND = 0x0008;
        private const int O_CREAT = 0x0100, O_TRUNC = 0x0200, O_EXCL = 0x0400;
        private const int O_TEXT = 0x4000, O_BINARY = 0x8000;
        private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000;
        private const uint ShareAll = 0x1 | 0x2 | 0x4;
        private const uint CreateNew = 1, CreateAlways = 2, OpenExisting = 3, OpenAlways = 4, TruncateExisting = 5;
        private static readonly IntPtr Invalid = new IntPtr(-1);

        [DllImport("api-ms-win-core-file-fromapp-l1-1-0.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileFromAppW(
            string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("api-ms-win-core-handle-l1-1-0.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("api-ms-win-core-file-fromapp-l1-1-0.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateDirectoryFromAppW(string name, IntPtr security);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr OpenNarrow(IntPtr name, IntPtr mode);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr ShareOpen(IntPtr name, IntPtr mode, int share);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int OpenSecure(IntPtr stream, IntPtr name, IntPtr mode);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DescriptorOpen(IntPtr name, int flags, int permission);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DescriptorOpenSecure(IntPtr descriptor, IntPtr name, int flags, int share, int permission);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int MakeDirectory(IntPtr name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Stat(IntPtr name, IntPtr buffer);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FromHandle(IntPtr handle, int flags);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr FromDescriptor(int descriptor, IntPtr mode);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Close(int descriptor);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr ErrnoAddress();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Seek(IntPtr stream, int offset, int origin);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr StreamOpen(IntPtr name, int mode, int protection);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DescriptorStat(int descriptor, IntPtr buffer);

        private static readonly List<Delegate> roots = new List<Delegate>();
        private static FromHandle openHandle;
        private static FromDescriptor fdopen, wfdopen;
        private static Close close;
        private static ErrnoAddress errno;
        private static Seek seek;

        public static long Rescued;

        /// <summary>The last opens that failed both ways: function, path, answer.</summary>
        public static readonly List<string> Failed = new List<string>();

        private static void Record(string function, string path, int answer)
        {
            lock (Failed)
            {
                if (Failed.Count >= 24) Failed.RemoveAt(0);
                Failed.Add(function + " " + path + " -> " + answer + (lastMissing ? " (missing)" : ""));
            }
        }

        private static IntPtr Keep(Delegate function)
        {
            roots.Add(function);
            return Marshal.GetFunctionPointerForDelegate(function);
        }

        private static T Real<T>(SystemImports imports, string module, string name) where T : class
        {
            var address = imports.SystemAddress(module, name);
            return address == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(address);
        }

        private static string Narrow(IntPtr text) => text == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(text);
        private static string Wide(IntPtr text) => text == IntPtr.Zero ? null : Marshal.PtrToStringUni(text);

        /// <summary>fopen's mode string as open flags and a CreateFile request.</summary>
        private static bool Parse(string mode, out int flags, out uint access, out uint disposition)
        {
            flags = 0;
            access = 0;
            disposition = 0;
            if (string.IsNullOrEmpty(mode)) return false;
            var plus = mode.IndexOf('+') >= 0;
            switch (mode[0])
            {
                case 'r':
                    flags = plus ? O_RDWR : 0;
                    access = GenericRead | (plus ? GenericWrite : 0);
                    disposition = OpenExisting;
                    break;
                case 'w':
                    flags = plus ? O_RDWR : O_WRONLY;
                    access = GenericWrite | (plus ? GenericRead : 0);
                    disposition = CreateAlways;
                    break;
                case 'a':
                    flags = (plus ? O_RDWR : O_WRONLY) | O_APPEND;
                    access = GenericWrite | (plus ? GenericRead : 0);
                    disposition = OpenAlways;
                    break;
                default:
                    return false;
            }
            flags |= mode.IndexOf('b') >= 0 ? O_BINARY : O_TEXT;
            return true;
        }

        /// <summary>Opens through the broker and returns a C runtime descriptor, or -1.</summary>
        private static int Descriptor(string path, uint access, uint disposition, int flags)
        {
            if (string.IsNullOrEmpty(path) || openHandle == null) return -1;
            if ((access & GenericWrite) != 0 || disposition != OpenExisting) FileWatch.Invalidate();
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var handle = CreateFileFromAppW(path, access, ShareAll, IntPtr.Zero, disposition, 0x80, IntPtr.Zero);
            FileWatch.Count(ref FileWatch.OpenCalls, ref FileWatch.OpenTicks, started);
            if (handle == Invalid || handle == IntPtr.Zero)
            {
                lastMissing = !FileWatch.PathExists(path);
                return -1;
            }
            var descriptor = openHandle(handle, flags & (O_WRONLY | O_RDWR | O_APPEND | O_TEXT | O_BINARY));
            if (descriptor < 0) CloseHandle(handle);
            else System.Threading.Interlocked.Increment(ref Rescued);
            return descriptor;
        }

        /// <summary>fopen through the broker; null when that fails too.</summary>
        private static IntPtr Stream(string path, string mode, bool wide)
        {
            if (!Parse(mode, out var flags, out var access, out var disposition)) return IntPtr.Zero;
            var descriptor = Descriptor(path, access, disposition, flags);
            if (descriptor < 0) return IntPtr.Zero;
            var text = wide ? Marshal.StringToHGlobalUni(mode) : Marshal.StringToHGlobalAnsi(mode);
            try
            {
                var stream = wide ? wfdopen(descriptor, text) : fdopen(descriptor, text);
                if (stream == IntPtr.Zero) close(descriptor);
                return stream;
            }
            finally
            {
                Marshal.FreeHGlobal(text);
            }
        }

        /// <summary>The last brokered open failed on a path that does not exist.</summary>
        [ThreadStatic] private static bool lastMissing;

        private static void SetErrno(int value)
        {
            var at = errno == null ? IntPtr.Zero : errno();
            if (at != IntPtr.Zero) Marshal.WriteInt32(at, value);
        }

        private static int ErrnoNow()
        {
            var at = errno == null ? IntPtr.Zero : errno();
            return at == IntPtr.Zero ? -1 : Marshal.ReadInt32(at);
        }
        private const int ENOENT = 2;

        /// <summary>
        /// Runs the rescue. When it fails too, errno is what the first attempt
        /// left, except that a path that does not exist is ENOENT: outside the
        /// app's folders the runtime, and the broker, say EACCES for
        /// everything, and games treat the two differently. Hades looks for
        /// InGameUI.sjson in one folder, and on ENOENT tries the next; on
        /// EACCES it stopped.
        /// </summary>
        private static T KeepingErrno<T>(Func<T> attempt, Func<T, bool> worked, T failed)
        {
            var at = errno == null ? IntPtr.Zero : errno();
            var saved = at == IntPtr.Zero ? 0 : Marshal.ReadInt32(at);
            lastMissing = false;
            var result = attempt();
            if (worked(result)) return result;
            if (at != IntPtr.Zero) Marshal.WriteInt32(at, lastMissing ? ENOENT : saved);
            return failed;
        }

        private static int OpenFlagsToDescriptor(string path, int flags)
        {
            var access = (flags & O_RDWR) != 0 ? GenericRead | GenericWrite
                : (flags & O_WRONLY) != 0 ? GenericWrite : GenericRead;
            uint disposition;
            if ((flags & O_CREAT) != 0)
                disposition = (flags & O_EXCL) != 0 ? CreateNew : (flags & O_TRUNC) != 0 ? CreateAlways : OpenAlways;
            else
                disposition = (flags & O_TRUNC) != 0 ? TruncateExisting : OpenExisting;
            return Descriptor(path, access, disposition, flags);
        }

        /// <summary>std::filebuf's open modes as the fopen mode MSVC maps them to.</summary>
        private static string FilebufMode(int mode)
        {
            const int In = 0x01, Out = 0x02, App = 0x08, Trunc = 0x10, Binary = 0x20;
            string text;
            switch (mode & (In | Out | App | Trunc))
            {
                case In: text = "r"; break;
                case Out: case Out | Trunc: text = "w"; break;
                case Out | App: case App: text = "a"; break;
                case In | Out: text = "r+"; break;
                case In | Out | Trunc: text = "w+"; break;
                case In | Out | App: case In | App: text = "a+"; break;
                default: return null;
            }
            return (mode & Binary) != 0 ? text + "b" : text;
        }

        public static void Install(SystemImports imports)
        {
            openHandle = Real<FromHandle>(imports, Stdio, "_open_osfhandle");
            fdopen = Real<FromDescriptor>(imports, Stdio, "_fdopen");
            wfdopen = Real<FromDescriptor>(imports, Stdio, "_wfdopen");
            close = Real<Close>(imports, Stdio, "_close");
            errno = Real<ErrnoAddress>(imports, Runtime, "_errno");
            seek = Real<Seek>(imports, Stdio, "fseek");
            if (openHandle == null || fdopen == null || wfdopen == null || close == null) return;

            var ours = new Dictionary<string, IntPtr>();
            var fopen = Real<OpenNarrow>(imports, Stdio, "fopen");
            if (fopen != null)
                ours["fopen"] = Keep(new OpenNarrow((name, mode) =>
                {
                    var stream = fopen(name, mode);
                    if (stream != IntPtr.Zero) return stream;
                    stream = KeepingErrno(() => Stream(Narrow(name), Narrow(mode), false), s => s != IntPtr.Zero, IntPtr.Zero);
                    if (stream == IntPtr.Zero) Record("fopen", Narrow(name), ErrnoNow());
                    return stream;
                }));
            var wfopen = Real<OpenNarrow>(imports, Stdio, "_wfopen");
            if (wfopen != null)
                ours["_wfopen"] = Keep(new OpenNarrow((name, mode) =>
                {
                    var stream = wfopen(name, mode);
                    if (stream != IntPtr.Zero) return stream;
                    stream = KeepingErrno(() => Stream(Wide(name), Wide(mode), true), s => s != IntPtr.Zero, IntPtr.Zero);
                    if (stream == IntPtr.Zero) Record("_wfopen", Wide(name), ErrnoNow());
                    return stream;
                }));
            var fsopen = Real<ShareOpen>(imports, Stdio, "_fsopen");
            if (fsopen != null)
                ours["_fsopen"] = Keep(new ShareOpen((name, mode, share) =>
                {
                    var stream = fsopen(name, mode, share);
                    if (stream != IntPtr.Zero) return stream;
                    stream = KeepingErrno(() => Stream(Narrow(name), Narrow(mode), false), s => s != IntPtr.Zero, IntPtr.Zero);
                    if (stream == IntPtr.Zero) Record("_fsopen", Narrow(name), ErrnoNow());
                    return stream;
                }));
            var wfsopen = Real<ShareOpen>(imports, Stdio, "_wfsopen");
            if (wfsopen != null)
                ours["_wfsopen"] = Keep(new ShareOpen((name, mode, share) =>
                {
                    var stream = wfsopen(name, mode, share);
                    if (stream != IntPtr.Zero) return stream;
                    stream = KeepingErrno(() => Stream(Wide(name), Wide(mode), true), s => s != IntPtr.Zero, IntPtr.Zero);
                    if (stream == IntPtr.Zero) Record("_wfsopen", Wide(name), ErrnoNow());
                    return stream;
                }));
            foreach (var pair in new[] { new { Name = "fopen_s", Wide = false }, new { Name = "_wfopen_s", Wide = true } })
            {
                var real = Real<OpenSecure>(imports, Stdio, pair.Name);
                if (real == null) continue;
                var wide = pair.Wide;
                ours[pair.Name] = Keep(new OpenSecure((result, name, mode) =>
                {
                    var error = real(result, name, mode);
                    if (error == 0 || result == IntPtr.Zero) return error;
                    lastMissing = false;
                    var stream = wide ? Stream(Wide(name), Wide(mode), true) : Stream(Narrow(name), Narrow(mode), false);
                    // The _s forms return the error rather than set errno.
                    if (stream == IntPtr.Zero)
                    {
                        // Returned and in errno both: games print strerror(errno).
                        var answer = lastMissing ? ENOENT : error;
                        SetErrno(answer);
                        Record(pair.Name, wide ? Wide(name) : Narrow(name), answer);
                        return answer;
                    }
                    Marshal.WriteIntPtr(result, stream);
                    return 0;
                }));
            }
            foreach (var pair in new[] { new { Name = "_open", Wide = false }, new { Name = "_wopen", Wide = true } })
            {
                var real = Real<DescriptorOpen>(imports, Stdio, pair.Name);
                if (real == null) continue;
                var wide = pair.Wide;
                ours[pair.Name] = Keep(new DescriptorOpen((name, flags, permission) =>
                {
                    var descriptor = real(name, flags, permission);
                    if (descriptor >= 0) return descriptor;
                    descriptor = KeepingErrno(() => OpenFlagsToDescriptor(wide ? Wide(name) : Narrow(name), flags), d => d >= 0, -1);
                    if (descriptor < 0) Record(pair.Name, wide ? Wide(name) : Narrow(name), ErrnoNow());
                    return descriptor;
                }));
            }
            foreach (var pair in new[] { new { Name = "_sopen_s", Wide = false }, new { Name = "_wsopen_s", Wide = true } })
            {
                var real = Real<DescriptorOpenSecure>(imports, Stdio, pair.Name);
                if (real == null) continue;
                var wide = pair.Wide;
                ours[pair.Name] = Keep(new DescriptorOpenSecure((result, name, flags, share, permission) =>
                {
                    var error = real(result, name, flags, share, permission);
                    if (error == 0 || result == IntPtr.Zero) return error;
                    lastMissing = false;
                    var descriptor = OpenFlagsToDescriptor(wide ? Wide(name) : Narrow(name), flags);
                    if (descriptor < 0)
                    {
                        var answer = lastMissing ? ENOENT : error;
                        SetErrno(answer);
                        Record(pair.Name, wide ? Wide(name) : Narrow(name), answer);
                        return answer;
                    }
                    Marshal.WriteInt32(result, descriptor);
                    return 0;
                }));
            }

            var mkdir = Real<MakeDirectory>(imports, FileSystem, "_mkdir");
            var wmkdir = Real<MakeDirectory>(imports, FileSystem, "_wmkdir");
            var directories = new Dictionary<string, IntPtr>();
            if (mkdir != null)
                directories["_mkdir"] = Keep(new MakeDirectory(name =>
                {
                    FileWatch.Invalidate();
                    return mkdir(name) == 0 || CreateDirectoryFromAppW(Narrow(name), IntPtr.Zero) ? 0 : -1;
                }));
            if (wmkdir != null)
                directories["_wmkdir"] = Keep(new MakeDirectory(name =>
                {
                    FileWatch.Invalidate();
                    return wmkdir(name) == 0 || CreateDirectoryFromAppW(Wide(name), IntPtr.Zero) ? 0 : -1;
                }));

            // stat on a path: opened through the broker for its attributes,
            // then answered by the runtime's own fstat from the descriptor.
            foreach (var pair in new[]
                     {
                         new { Name = "_stat64", Fstat = "_fstat64", Wide = false },
                         new { Name = "_wstat64", Fstat = "_fstat64", Wide = true },
                         new { Name = "_stat64i32", Fstat = "_fstat64i32", Wide = false },
                         new { Name = "_wstat64i32", Fstat = "_fstat64i32", Wide = true },
                     })
            {
                var real = Real<Stat>(imports, FileSystem, pair.Name);
                var fstat = imports.SystemAddress(FileSystem, pair.Fstat);
                if (real == null || fstat == IntPtr.Zero) continue;
                var byDescriptor = Marshal.GetDelegateForFunctionPointer<DescriptorStat>(fstat);
                var wide = pair.Wide;
                directories[pair.Name] = Keep(new Stat((name, buffer) =>
                {
                    var result = real(name, buffer);
                    if (result == 0) return 0;
                    var path = wide ? Wide(name) : Narrow(name);
                    // FILE_FLAG_BACKUP_SEMANTICS opens a directory as well.
                    var handle = CreateFileFromAppW(path, 0x80, ShareAll, IntPtr.Zero, OpenExisting, 0x02000000, IntPtr.Zero);
                    if (handle == Invalid || handle == IntPtr.Zero) return result;
                    var descriptor = openHandle(handle, 0);
                    if (descriptor < 0)
                    {
                        CloseHandle(handle);
                        return result;
                    }
                    try
                    {
                        return byDescriptor(descriptor, buffer) == 0 ? 0 : result;
                    }
                    finally
                    {
                        close(descriptor);
                    }
                }));
            }

            foreach (var module in new[] { Stdio, "ucrtbase.dll", "UCRTBASE.dll" })
                foreach (var pair in ours)
                    imports.Overrides[module + "!" + pair.Key] = pair.Value;
            foreach (var module in new[] { FileSystem, "ucrtbase.dll", "UCRTBASE.dll" })
                foreach (var pair in directories)
                    imports.Overrides[module + "!" + pair.Key] = pair.Value;

            InstallFilebuf(imports);
        }

        /// <summary>
        /// std::basic_filebuf::open goes through _Fiopen in the C++ runtime,
        /// which calls the C runtime's own fopen where no import sees it.
        /// </summary>
        private static void InstallFilebuf(SystemImports imports)
        {
            const string FiopenNarrow = "?_Fiopen@std@@YAPEAU_iobuf@@PEBDHH@Z";
            const string FiopenWide = "?_Fiopen@std@@YAPEAU_iobuf@@PEB_WHH@Z";
            foreach (var entry in new[] { new { Name = FiopenNarrow, Wide = false }, new { Name = FiopenWide, Wide = true } })
            {
                var address = imports.SystemAddress("msvcp140_app.dll", entry.Name);
                if (address == IntPtr.Zero) continue;
                var real = Marshal.GetDelegateForFunctionPointer<StreamOpen>(address);
                var wide = entry.Wide;
                var ours = Keep(new StreamOpen((name, mode, protection) =>
                {
                    var stream = real(name, mode, protection);
                    if (stream != IntPtr.Zero) return stream;
                    var text = FilebufMode(mode);
                    if (text == null) return IntPtr.Zero;
                    stream = KeepingErrno(() => Stream(wide ? Wide(name) : Narrow(name), text, false),
                        s => s != IntPtr.Zero, IntPtr.Zero);
                    if (stream == IntPtr.Zero) Record("_Fiopen", wide ? Wide(name) : Narrow(name), ErrnoNow());
                    // ios_base::ate: opened, then placed at the end.
                    if (stream != IntPtr.Zero && (mode & 0x04) != 0 && seek != null) seek(stream, 0, 2);
                    return stream;
                }));
                foreach (var module in new[] { "MSVCP140.dll", "msvcp140.dll", "msvcp140_app.dll" })
                    imports.Overrides[module + "!" + entry.Name] = ours;
            }
        }
    }
}
