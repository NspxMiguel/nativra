using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Kiosk.Native
{
    /// <summary>
    /// Writes down which files the game opens, and which it fails to open.
    ///
    /// A game that stops without saying why is usually waiting on something it
    /// asked the file system for. Its own log would say so, but the log is the
    /// first thing that goes missing when the log cannot be opened — which is
    /// exactly the case this was written to settle.
    ///
    /// Opening files is one of the hottest calls a game makes, so almost none
    /// of them are recorded: only the ones that fail, and the handful whose
    /// name says they matter. Recording all of them would change the timing of
    /// the thing being measured, which is how a measurement stops being one.
    /// </summary>
    internal static class FileWatch
    {
        private const int Keep = 120;
        private static readonly IntPtr InvalidHandle = new IntPtr(-1);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr CreateFileDelegate(
            IntPtr name, uint access, uint share, IntPtr security,
            uint disposition, uint flags, IntPtr template);

        private static CreateFileDelegate wide;
        private static CreateFileDelegate real;

        /// <summary>What was opened, and how it went. Read under its own lock.</summary>
        public static readonly List<string> Seen = new List<string>();

        /// <summary>How many opens failed, whether or not each one was kept.</summary>
        public static long Failures;

        private static void Say(string line)
        {
            lock (Seen)
            {
                if (Seen.Count < Keep) Seen.Add(line);
            }
        }

        /// <summary>
        /// A name worth keeping even when the open succeeds: the log the engine
        /// was told to write, and the files it loads its own code from. The
        /// rest — textures, bundles, the thousands of small reads a scene makes
        /// — are only interesting when they fail.
        /// </summary>
        private static bool WorthKeeping(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var lower = path.ToLowerInvariant();
            return lower.EndsWith(".log")
                || lower.EndsWith("global-metadata.dat")
                || lower.EndsWith(".dll")
                || lower.EndsWith("boot.config")
                || lower.EndsWith(".xml");
        }

        // The FromApp forms reach what the app may open directly and, through
        // the broker, what it may only reach by capability: a game on a USB
        // drive. Where direct access works they behave like the plain calls.
        private const string FromApp = "api-ms-win-core-file-fromapp-l1-1-0.dll";

        [DllImport(FromApp, EntryPoint = "FindFirstFileExFromAppW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindFirstFileExW(string name, int level, IntPtr data, int search, IntPtr filter, uint flags);

        [DllImport(FromApp, CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetFileAttributesExFromAppW(string name, int level, IntPtr data);

        [DllImport(FromApp, CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileFromAppW(string from, string to);

        [DllImport(FromApp, CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool DeleteFileFromAppW(string name);

        [DllImport(FromApp, CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CopyFileFromAppW(string from, string to, bool failIfExists);

        private const uint InvalidAttributes = uint.MaxValue;
        private const uint MoveReplaceExisting = 0x1;
        private const uint MoveCopyAllowed = 0x2;

        // ----------------------------------------------------------- broker

        /// <summary>Calls that went to the file broker, and the time they took.</summary>
        public static long BrokerCalls, BrokerTicks;

        /// <summary>The same, by kind: opens, directory listings, attribute questions.</summary>
        public static long OpenCalls, OpenTicks, FindCalls, FindTicks, AttributeCalls, AttributeTicks;

        public static string BrokerReport()
        {
            var perMs = System.Diagnostics.Stopwatch.Frequency / 1000.0;
            return "broker calls=" + BrokerCalls + " ms=" + (long)(BrokerTicks / perMs)
                + " opens=" + OpenCalls + "/" + (long)(OpenTicks / perMs) + "ms"
                + " finds=" + FindCalls + "/" + (long)(FindTicks / perMs) + "ms"
                + " attributes=" + AttributeCalls + "/" + (long)(AttributeTicks / perMs) + "ms | "
                + UsbFiles.Report();
        }

        internal static void Count(ref long calls, ref long ticks, long started)
        {
            var spent = System.Diagnostics.Stopwatch.GetTimestamp() - started;
            System.Threading.Interlocked.Increment(ref calls);
            System.Threading.Interlocked.Add(ref ticks, spent);
            System.Threading.Interlocked.Increment(ref BrokerCalls);
            System.Threading.Interlocked.Add(ref BrokerTicks, spent);
        }

        /// <summary>
        /// The game's own download folder. Every file call for a game on a USB
        /// drive is a round trip to the broker in another process, and Hades
        /// spent its frames there, asking about the same files again and again.
        /// What is in the game's folder does not change unless the game
        /// writes, so the answers there are kept.
        /// </summary>
        public static string CacheRoot;

        private const int AttributeData = 36;
        private static readonly Dictionary<string, byte[]> attributeCache =
            new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        private static readonly byte[] Missing = new byte[0];

        /// <summary>Forgets every cached answer; called on anything that writes.</summary>
        public static void Invalidate()
        {
            lock (attributeCache) attributeCache.Clear();
            UsbFiles.Forget();
        }

        private static bool Cacheable(string path) =>
            CacheRoot != null && path != null && path.StartsWith(CacheRoot, StringComparison.OrdinalIgnoreCase);

        /// <summary>GetFileAttributesEx through the broker, answered from the cache when it can be.</summary>
        private static bool Attributes(string path, IntPtr data)
        {
            var cacheable = Cacheable(path);
            if (cacheable)
            {
                byte[] known;
                lock (attributeCache) attributeCache.TryGetValue(path, out known);
                if (known != null)
                {
                    if (known.Length == 0)
                    {
                        SetLastError(2);
                        return false;
                    }
                    if (data != IntPtr.Zero) Marshal.Copy(known, 0, data, AttributeData);
                    return true;
                }
            }
            var buffer = Marshal.AllocHGlobal(40);
            try
            {
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                var ok = GetFileAttributesExFromAppW(path, 0, buffer);
                var error = Marshal.GetLastWin32Error();
                Count(ref AttributeCalls, ref AttributeTicks, started);
                byte[] answer = Missing;
                if (ok)
                {
                    answer = new byte[AttributeData];
                    Marshal.Copy(buffer, answer, 0, AttributeData);
                    if (data != IntPtr.Zero) Marshal.Copy(answer, 0, data, AttributeData);
                }
                // Only "not there" is worth remembering among failures.
                if (cacheable && (ok || error == 2 || error == 3))
                    lock (attributeCache) attributeCache[path] = answer;
                if (!ok) SetLastError((uint)error);
                return ok;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        [DllImport("api-ms-win-core-errorhandling-l1-1-0.dll")]
        private static extern void SetLastError(uint code);

        private static uint GetFileAttributesW(string name)
        {
            var data = Marshal.AllocHGlobal(40);
            try
            {
                // WIN32_FILE_ATTRIBUTE_DATA: the attributes lead it.
                return Attributes(name, data) ? (uint)Marshal.ReadInt32(data) : InvalidAttributes;
            }
            finally
            {
                Marshal.FreeHGlobal(data);
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int AttributesExDelegate(IntPtr name, int level, IntPtr data);
        private static AttributesExDelegate attributesEx;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int WritePathDelegate(IntPtr name, IntPtr extra);
        private static readonly List<Delegate> writers = new List<Delegate>();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int MoveExDelegate(IntPtr from, IntPtr to, uint flags);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int Func1Delegate(IntPtr name);
        private static MoveExDelegate moveEx;

        [UnmanagedFunctionPointer(CallingConvention.StdCall, SetLastError = true)]
        private delegate IntPtr FindFirstDelegate(IntPtr name, IntPtr data);
        [UnmanagedFunctionPointer(CallingConvention.StdCall, SetLastError = true)]
        private delegate IntPtr FindFirstExDelegate(IntPtr name, int level, IntPtr data, int search, IntPtr filter, uint flags);
        [UnmanagedFunctionPointer(CallingConvention.StdCall, SetLastError = true)]
        private delegate uint AttributesDelegate(IntPtr name);
        private static FindFirstDelegate findFirst;
        private static FindFirstExDelegate findFirstEx;
        private static AttributesDelegate attributes;

        [DllImport("api-ms-win-core-memory-l1-1-1.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileMappingFromApp(IntPtr file, IntPtr attributes, uint protect, ulong maximumSize, string name);

        [DllImport("api-ms-win-core-memory-l1-1-1.dll", SetLastError = true)]
        private static extern IntPtr MapViewOfFileFromApp(IntPtr mapping, uint access, ulong offset, IntPtr size);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, SetLastError = true)]
        private delegate IntPtr MappingWDelegate(IntPtr file, IntPtr attributes, uint protect, uint sizeHigh, uint sizeLow, IntPtr name);
        [UnmanagedFunctionPointer(CallingConvention.StdCall, SetLastError = true)]
        private delegate IntPtr MapViewDelegate(IntPtr mapping, uint access, uint offsetHigh, uint offsetLow, IntPtr size);
        private static MappingWDelegate mappingW, mappingA;
        private static MapViewDelegate mapView;

        [DllImport("api-ms-win-core-file-l1-1-0.dll", SetLastError = true)]
        private static extern bool ReadFile(IntPtr file, IntPtr buffer, uint count, IntPtr read, IntPtr overlapped);
        [DllImport("api-ms-win-core-file-l1-1-0.dll", SetLastError = true)]
        private static extern uint GetFileSize(IntPtr file, IntPtr high);
        [DllImport("api-ms-win-core-file-l1-1-0.dll", SetLastError = true)]
        private static extern bool GetFileSizeEx(IntPtr file, out long size);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, SetLastError = true)]
        private delegate int ReadDelegate(IntPtr file, IntPtr buffer, uint count, IntPtr read, IntPtr overlapped);
        [UnmanagedFunctionPointer(CallingConvention.StdCall, SetLastError = true)]
        private delegate uint SizeDelegate(IntPtr file, IntPtr high);
        [UnmanagedFunctionPointer(CallingConvention.StdCall, SetLastError = true)]
        private delegate int SizeExDelegate(IntPtr file, IntPtr size);
        private static ReadDelegate readFile;
        private static SizeDelegate fileSize;
        private static SizeExDelegate fileSizeEx;

        /// <summary>
        /// Reads and sizes that fail or come back empty, which a program
        /// takes as a missing file. Opt-in (filewatch.txt): ReadFile is hot.
        /// </summary>
        public static void WatchReads(SystemImports imports)
        {
            readFile = (file, buffer, count, read, overlapped) =>
            {
                var ok = ReadFile(file, buffer, count, read, overlapped);
                var got = read == IntPtr.Zero ? -1 : Marshal.ReadInt32(read);
                if (!ok || (count > 0 && got == 0 && overlapped == IntPtr.Zero))
                    Say("read " + (ok ? "empty" : "failed (" + Marshal.GetLastWin32Error() + ")") + " asked " + count + (overlapped != IntPtr.Zero ? " overlapped" : ""));
                return ok ? 1 : 0;
            };
            fileSize = (file, high) =>
            {
                var size = GetFileSize(file, high);
                if (size == 0 || size == uint.MaxValue) Say("size " + size + " (" + Marshal.GetLastWin32Error() + ")");
                return size;
            };
            fileSizeEx = (file, size) =>
            {
                var ok = GetFileSizeEx(file, out var value);
                if (size != IntPtr.Zero) Marshal.WriteInt64(size, value);
                if (!ok || value == 0) Say("sizeex " + value + " ok=" + ok);
                return ok ? 1 : 0;
            };
            foreach (var module in new[] { "KERNEL32.dll", "kernel32.dll", "KERNELBASE.dll", "api-ms-win-core-file-l1-1-0.dll" })
            {
                imports.Overrides[module + "!ReadFile"] = Marshal.GetFunctionPointerForDelegate(readFile);
                imports.Overrides[module + "!GetFileSize"] = Marshal.GetFunctionPointerForDelegate(fileSize);
                imports.Overrides[module + "!GetFileSizeEx"] = Marshal.GetFunctionPointerForDelegate(fileSizeEx);
            }
        }

        /// <summary>
        /// File mappings, through the calls an app container is allowed to
        /// make. CreateFileMappingW and MapViewOfFile are desktop calls; the
        /// FromApp pair does the same for a packaged app. Adobe AIR maps its
        /// application descriptor to read it.
        /// </summary>
        private static void BridgeMappings(SystemImports imports)
        {
            mappingW = (file, attributes, protect, high, low, name) =>
            {
                var made = CreateFileMappingFromApp(file, attributes, protect,
                    ((ulong)high << 32) | low, name == IntPtr.Zero ? null : Marshal.PtrToStringUni(name));
                if (made == IntPtr.Zero) Say("mapping failed (" + Marshal.GetLastWin32Error() + ")");
                return made;
            };
            mappingA = (file, attributes, protect, high, low, name) =>
            {
                var made = CreateFileMappingFromApp(file, attributes, protect,
                    ((ulong)high << 32) | low, name == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(name));
                if (made == IntPtr.Zero) Say("mapping failed (" + Marshal.GetLastWin32Error() + ")");
                return made;
            };
            mapView = (mapping, access, high, low, size) =>
            {
                var view = MapViewOfFileFromApp(mapping, access, ((ulong)high << 32) | low, size);
                if (view == IntPtr.Zero) Say("map view failed (" + Marshal.GetLastWin32Error() + ")");
                return view;
            };
            foreach (var module in new[] { "KERNEL32.dll", "kernel32.dll", "KERNELBASE.dll", "api-ms-win-core-memory-l1-1-0.dll" })
            {
                imports.Overrides[module + "!CreateFileMappingW"] = Marshal.GetFunctionPointerForDelegate(mappingW);
                imports.Overrides[module + "!CreateFileMappingA"] = Marshal.GetFunctionPointerForDelegate(mappingA);
                imports.Overrides[module + "!MapViewOfFile"] = Marshal.GetFunctionPointerForDelegate(mapView);
            }
        }

        /// <summary>
        /// The lookups that decide whether a program believes a file exists.
        /// Watched for the same reason as opens: a failed one, with its path,
        /// says what the program looked for and where.
        /// </summary>
        private static void WatchLookups(SystemImports imports)
        {
            findFirstEx = (name, level, data, search, filter, flags) =>
            {
                var path = name == IntPtr.Zero ? null : Marshal.PtrToStringUni(name);
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                var found = FindFirstFileExW(path, level, data, search, filter, flags);
                Count(ref FindCalls, ref FindTicks, started);
                if (found == InvalidHandle) Say("find failed " + (path ?? "?") + " (" + Marshal.GetLastWin32Error() + ")");
                else Say("find " + (path ?? "?"));
                return found;
            };
            findFirst = (name, data) => findFirstEx(name, 0, data, 0, IntPtr.Zero, 0);
            attributes = name =>
            {
                var path = name == IntPtr.Zero ? null : Marshal.PtrToStringUni(name);
                var result = GetFileAttributesW(path);
                if (result == uint.MaxValue) Say("attributes failed " + (path ?? "?") + " (" + Marshal.GetLastWin32Error() + ")");
                return result;
            };
            foreach (var module in new[] { "KERNEL32.dll", "kernel32.dll", "KERNELBASE.dll", "api-ms-win-core-file-l1-1-0.dll" })
            {
                imports.Overrides[module + "!FindFirstFileW"] = Marshal.GetFunctionPointerForDelegate(findFirst);
                imports.Overrides[module + "!FindFirstFileExW"] = Marshal.GetFunctionPointerForDelegate(findFirstEx);
                imports.Overrides[module + "!GetFileAttributesW"] = Marshal.GetFunctionPointerForDelegate(attributes);
            }
        }

        /// <summary>Answers an open by name, or zero to let it through.</summary>
        public static Func<IntPtr, IntPtr> Intercept;

        public static void Install(SystemImports imports)
        {
            WatchLookups(imports);
            BridgeMappings(imports);
            real = null;
            var address = imports.SystemAddress(FromApp, "CreateFileFromAppW");
            if (address == IntPtr.Zero) address = imports.SystemAddress("kernel32.dll", "CreateFileW");
            if (address != IntPtr.Zero)
            {
                real = Marshal.GetDelegateForFunctionPointer<CreateFileDelegate>(address);
            }

            wide = (name, access, share, security, disposition, flags, template) =>
            {
                // Paths that name something the bridge provides, such as the
                // listed controller, are answered before the file system.
                var ours = Intercept == null ? IntPtr.Zero : Intercept(name);
                if (ours != IntPtr.Zero) return ours;
                if (real == null) return InvalidHandle;

                // On a USB drive, through the drive's folder: 9 ms instead
                // of the broker's 220, and a missing file costs nothing.
                if (name != IntPtr.Zero && UsbFiles.TryOpen(Marshal.PtrToStringUni(name), access, share,
                        disposition, flags, out var fast, out var missing))
                {
                    if ((access & 0x40000000) != 0 || (disposition != 3 && disposition != 0)) Invalidate();
                    if (fast == InvalidHandle) SetLastError((uint)missing);
                    return fast;
                }

                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                var handle = real(
                    name, access, share, security, disposition, flags, template);
                Count(ref OpenCalls, ref OpenTicks, started);
                // Anything that can create or change a file changes the answers.
                if ((access & 0x40000000) != 0 || (disposition != 3 && disposition != 0)) Invalidate();

                // Reading the name costs a copy, so it is only read when there
                // is a reason to: the open failed, or there is still room and
                // the name might be one of the few that matter.
                var failed = handle == InvalidHandle;
                if (!failed && Seen.Count >= Keep) return handle;

                string path = null;
                try
                {
                    path = name == IntPtr.Zero ? null : Marshal.PtrToStringUni(name);
                }
                catch
                {
                    // An unreadable name is not worth failing the open over.
                }

                if (failed)
                {
                    Failures++;
                    Say("failed " + (path ?? "?"));
                }
                else if (WorthKeeping(path))
                {
                    Say("opened " + path);
                }
                return handle;
            };

            foreach (var module in new[]
                     {
                         "KERNEL32.dll", "kernel32.dll", "KERNELBASE.dll",
                         "api-ms-win-core-file-l1-1-0.dll",
                         "api-ms-win-core-file-l1-2-0.dll",
                         "api-ms-win-core-file-l1-2-1.dll",
                     })
            {
                imports.Overrides[module + "!CreateFileW"] =
                    Marshal.GetFunctionPointerForDelegate(wide);
            }
            BrokerFileCalls(imports);
        }

        /// <summary>
        /// The rest of what a game does to files, sent to the FromApp forms,
        /// which take the same arguments. MoveFileExW has no such form, so its
        /// two common flags are done by hand.
        /// </summary>
        private static void BrokerFileCalls(SystemImports imports)
        {
            var same = new[]
            {
                new[] { "SetFileAttributesW", "SetFileAttributesFromAppW" },
                new[] { "MoveFileW", "MoveFileFromAppW" },
                new[] { "CopyFileW", "CopyFileFromAppW" },
                new[] { "ReplaceFileW", "ReplaceFileFromAppW" },
                new[] { "CreateFile2", "CreateFile2FromAppW" },
            };
            moveEx = (from, to, flags) =>
            {
                Invalidate();
                var source = from == IntPtr.Zero ? null : Marshal.PtrToStringUni(from);
                var target = to == IntPtr.Zero ? null : Marshal.PtrToStringUni(to);
                if (source == null) return 0;
                // A null target means "delete at reboot", which a game only
                // asks for to clean up; deleting now is the nearest thing.
                if (target == null) return DeleteFileFromAppW(source) ? 1 : 0;
                if ((flags & MoveReplaceExisting) != 0 && PathExists(target)) DeleteFileFromAppW(target);
                if (MoveFileFromAppW(source, target)) return 1;
                if ((flags & MoveCopyAllowed) == 0 || !CopyFileFromAppW(source, target, false)) return 0;
                DeleteFileFromAppW(source);
                return 1;
            };
            var moveExAddress = Marshal.GetFunctionPointerForDelegate(moveEx);
            attributesEx = (name, level, data) =>
                Attributes(name == IntPtr.Zero ? null : Marshal.PtrToStringUni(name), data) ? 1 : 0;
            var attributesExAddress = Marshal.GetFunctionPointerForDelegate(attributesEx);
            // Calls that change the folder: made through the broker, and the
            // cache is forgotten first.
            var changing = new Dictionary<string, IntPtr>();
            foreach (var pair in new[]
                     {
                         new[] { "CreateDirectoryW", "CreateDirectoryFromAppW" },
                         new[] { "RemoveDirectoryW", "RemoveDirectoryFromAppW" },
                         new[] { "DeleteFileW", "DeleteFileFromAppW" },
                     })
            {
                var target = imports.SystemAddress(FromApp, pair[1]);
                if (target == IntPtr.Zero) continue;
                var takesTwo = pair[0] == "CreateDirectoryW";
                var twoArgs = takesTwo ? Marshal.GetDelegateForFunctionPointer<WritePathDelegate>(target) : null;
                var oneArg = takesTwo ? null : Marshal.GetDelegateForFunctionPointer<Func1Delegate>(target);
                WritePathDelegate wrapper = (name, extra) =>
                {
                    Invalidate();
                    return takesTwo ? twoArgs(name, extra) : oneArg(name);
                };
                writers.Add(wrapper);
                changing[pair[0]] = Marshal.GetFunctionPointerForDelegate(wrapper);
            }
            foreach (var module in new[]
                     {
                         "KERNEL32.dll", "kernel32.dll", "KERNELBASE.dll",
                         "api-ms-win-core-file-l1-1-0.dll",
                         "api-ms-win-core-file-l1-2-0.dll",
                         "api-ms-win-core-file-l1-2-1.dll",
                         "api-ms-win-core-file-l2-1-0.dll",
                         "api-ms-win-core-file-l2-1-1.dll",
                     })
            {
                foreach (var pair in same)
                {
                    var target = imports.SystemAddress(FromApp, pair[1]);
                    if (target != IntPtr.Zero) imports.Overrides[module + "!" + pair[0]] = target;
                }
                imports.Overrides[module + "!MoveFileExW"] = moveExAddress;
                imports.Overrides[module + "!GetFileAttributesExW"] = attributesExAddress;
                foreach (var pair in changing) imports.Overrides[module + "!" + pair.Key] = pair.Value;
            }
        }

        [DllImport("api-ms-win-core-file-l2-1-0.dll", SetLastError = true)]
        private static extern IntPtr ReOpenFile(IntPtr original, uint access, uint share, uint flags);

        /// <summary>
        /// Measures what the broker costs on this drive, to choose how to make
        /// loading from it faster: one open, a ReOpenFile on a brokered handle
        /// (if that works without the broker, handles can be reused), and eight
        /// opens at once against eight in a row (if the broker answers in
        /// parallel, files can be opened ahead in bulk).
        /// </summary>
        public static List<string> MeasureBroker(IList<string> files)
        {
            var lines = new List<string>();
            if (files.Count < 17) return lines;
            var tick = System.Diagnostics.Stopwatch.Frequency / 1000.0;
            Func<string, IntPtr> open = path => CreateFileFromAppW(path, 0x80000000, 1, IntPtr.Zero, 3, 0x80, IntPtr.Zero);

            var t = System.Diagnostics.Stopwatch.GetTimestamp();
            var first = open(files[0]);
            lines.Add("brokerprobe one open " + ((System.Diagnostics.Stopwatch.GetTimestamp() - t) / tick).ToString("0.0") + "ms ok=" + (first != InvalidHandle));
            if (first != InvalidHandle)
            {
                t = System.Diagnostics.Stopwatch.GetTimestamp();
                var again = ReOpenFile(first, 0x80000000, 1, 0);
                var error = Marshal.GetLastWin32Error();
                lines.Add("brokerprobe reopen " + ((System.Diagnostics.Stopwatch.GetTimestamp() - t) / tick).ToString("0.0") + "ms ok=" + (again != InvalidHandle) + " error=" + error);
                if (again != InvalidHandle) CloseHandle(again);
                CloseHandle(first);
            }

            t = System.Diagnostics.Stopwatch.GetTimestamp();
            for (var i = 1; i <= 8; i++)
            {
                var handle = open(files[i]);
                if (handle != InvalidHandle) CloseHandle(handle);
            }
            lines.Add("brokerprobe 8 in a row " + ((System.Diagnostics.Stopwatch.GetTimestamp() - t) / tick).ToString("0") + "ms");

            t = System.Diagnostics.Stopwatch.GetTimestamp();
            var tasks = new System.Threading.Tasks.Task[8];
            for (var i = 0; i < 8; i++)
            {
                var path = files[9 + i];
                tasks[i] = System.Threading.Tasks.Task.Run(() =>
                {
                    var handle = open(path);
                    if (handle != InvalidHandle) CloseHandle(handle);
                });
            }
            System.Threading.Tasks.Task.WaitAll(tasks);
            lines.Add("brokerprobe 8 at once " + ((System.Diagnostics.Stopwatch.GetTimestamp() - t) / tick).ToString("0") + "ms");
            return lines;
        }

        /// <summary>File or folder, found through the broker as well.</summary>
        public static bool PathExists(string path) =>
            !string.IsNullOrEmpty(path) && GetFileAttributesW(path) != InvalidAttributes;

        [DllImport(FromApp, CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileFromAppW(
            string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("api-ms-win-core-handle-l1-1-0.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        /// <summary>
        /// A whole file by path, wherever it is: System.IO is refused outside
        /// the app's folders, and a game on a USB drive is outside them.
        /// </summary>
        public static byte[] ReadAll(string path)
        {
            try
            {
                return System.IO.File.ReadAllBytes(path);
            }
            catch (UnauthorizedAccessException)
            {
            }
            const uint GenericRead = 0x80000000, ShareRead = 1, OpenExisting = 3;
            var handle = CreateFileFromAppW(path, GenericRead, ShareRead, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle == InvalidHandle || handle == IntPtr.Zero)
                throw new System.IO.FileNotFoundException("cannot open " + path + " (" + Marshal.GetLastWin32Error() + ")");
            try
            {
                if (!GetFileSizeEx(handle, out var size)) throw new System.IO.IOException("cannot size " + path);
                var bytes = new byte[size];
                var buffer = Marshal.AllocHGlobal(new IntPtr(Math.Max(1, size)));
                var read = Marshal.AllocHGlobal(4);
                try
                {
                    long done = 0;
                    while (done < size)
                    {
                        var chunk = (uint)Math.Min(size - done, 16 * 1024 * 1024);
                        if (!ReadFile(handle, buffer + (int)done, chunk, read, IntPtr.Zero))
                            throw new System.IO.IOException("cannot read " + path);
                        var got = Marshal.ReadInt32(read);
                        if (got == 0) break;
                        done += got;
                    }
                    Marshal.Copy(buffer, bytes, 0, (int)done);
                }
                finally
                {
                    Marshal.FreeHGlobal(read);
                    Marshal.FreeHGlobal(buffer);
                }
                return bytes;
            }
            finally
            {
                CloseHandle(handle);
            }
        }
    }
}
