using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage;

namespace Kiosk.Native
{
    /// <summary>
    /// Measures how far a real Windows binary gets inside this app. Whatever
    /// answer it gives decides the shape of the whole translation layer, so it
    /// is written to a file rather than left on a screen nobody is watching.
    /// </summary>
    public static class NativeProbe
    {
        private const string Folder = "win32";
        private const string ReportName = "native-probe.txt";

        /// <summary>
        /// Everything here runs on a thread of its own. Setting up thread-local
        /// storage rewrites state that belongs to the thread doing it, and the
        /// interface thread is the last one that should be experimented on.
        /// </summary>
        public static Task RunAsync()
        {
            var done = new TaskCompletionSource<bool>();
            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    // Waited on here rather than awaited: an async lambda would
                    // return at the first await and leave this thread empty,
                    // which is the opposite of the point.
                    WorkAsync().GetAwaiter().GetResult();
                }
                catch
                {
                    // The report on disk is the record; nothing to raise to.
                }
                finally
                {
                    done.TrySetResult(true);
                }
            }, 8 * 1024 * 1024);
            thread.IsBackground = true;
            thread.Start();
            return done.Task;
        }

        private static async Task WorkAsync()
        {
            var lines = new List<string> { "at=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") };
            try
            {
                var local = ApplicationData.Current.LocalFolder;

                // A game downloaded on the console is the real target; the
                // scratch folder is only for binaries pushed by hand.
                StorageFolder folder = null;

                // The developer share survives reinstalling the app; the app's
                // own storage does not, and a reinstall is every single build.
                // So the game is looked for there first, and a copy that is
                // still only in local storage is moved there once.
                folder = await GameIn(await DevelopmentFiles());

                var games = await local.TryGetItemAsync("games") as StorageFolder;
                if (folder == null && games != null)
                {
                    var here = await GameIn(games);
                    if (here != null)
                    {
                        lines.Add("mirror=" + here.Name);
                        await WriteAsync(lines);
                        folder = await MirrorAsync(here) ?? here;
                        lines[lines.Count - 1] = "mirror=" + here.Name + " -> " + folder.Path;
                    }
                }
                folder = folder ?? await local.TryGetItemAsync(Folder) as StorageFolder;
                if (folder == null)
                {
                    lines.Add("state=no win32 folder");
                    await WriteAsync(lines);
                    return;
                }

                // The dangerous half of the loader only runs when asked.
                // The marker's contents say how far to go, so one build can
                // answer several questions.
                PeImage.TlsLevel = 0;
                if (await folder.TryGetItemAsync("tls.txt") is StorageFile marker)
                {
                    int.TryParse((await FileIO.ReadTextAsync(marker)).Trim(), out var level);
                    PeImage.TlsLevel = level;
                }
                lines.Add("tls.level=" + PeImage.TlsLevel);

                // Each step of the setup lands on disk as it happens.
                var trail = new List<string>(lines);
                PeImage.Step = note =>
                {
                    var snapshot = new List<string>(trail) { "tls.step=" + note };
                    WriteAsync(snapshot).GetAwaiter().GetResult();
                };

                var imports = new SystemImports
                {
                    Trace = await folder.TryGetItemAsync("trace.txt") != null,
                };

                // Where the game thinks it lives, which is how it finds its data.
                var exeName = "game.exe";
                foreach (var file in await folder.GetFilesAsync())
                {
                    if (file.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                        !file.Name.StartsWith("Unity", StringComparison.OrdinalIgnoreCase))
                    {
                        exeName = file.Name;
                        break;
                    }
                }
                ModuleFileName.Install(imports, folder.Path + "\\" + exeName);
                ImageLookup.Install(
                    imports, imports.SystemAddress("kernel32.dll", "RtlPcToFileHeader"));
                ProcessStubs.Install(imports);
                WindowStubs.Install(imports);
                lines.Add("as=" + folder.Path + "\\" + exeName);

                // A module that imports another has to be loaded after it, or
                // its imports resolve against nothing. Unity's order is fixed.
                var order = new List<string> { "baselib.dll", "unityplayer.dll", "gameassembly.dll" };
                var files = new List<StorageFile>(await folder.GetFilesAsync());
                files.Sort((a, b) =>
                {
                    int Rank(StorageFile f)
                    {
                        var index = order.IndexOf(f.Name.ToLowerInvariant());
                        if (index >= 0) return index;
                        return f.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                            ? order.Count
                            : order.Count + 1;
                    }
                    var byRank = Rank(a).CompareTo(Rank(b));
                    return byRank != 0 ? byRank : string.CompareOrdinal(a.Name, b.Name);
                });

                foreach (var file in files)
                {
                    if (!file.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                        !file.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var bytes = (await FileIO.ReadBufferAsync(file)).ToArray();
                    // Written before the attempt: mapping an image runs the
                    // module's own code, and that can take the process with it.
                    lines.Add(file.Name + ": loading");
                    await WriteAsync(lines);
                    lines.RemoveAt(lines.Count - 1);
                    try
                    {
                        var image = PeImage.Load(file.Name, bytes, imports.Resolve);
                        imports.Add(image);
                        lines.Add(
                            $"{file.Name}: mapped at 0x{image.BaseAddress.ToInt64():X} " +
                            $"exports={image.ExportCount} unresolved={image.Unresolved.Count} " +
                            $"unwind={image.ExceptionsRegistered} tls={image.TlsCallbacksRun} " +
                            $"slot={image.TlsSlot} [{PeImage.TlsNote}]");
                    }
                    catch (Exception error)
                    {
                        lines.Add($"{file.Name}: FAILED {error.GetType().Name}: {error.Message}");
                    }
                    await WriteAsync(lines);
                }

                lines.Add($"resolved.system={imports.FromSystem} resolved.images={imports.FromImages}");
                lines.Add("modules.missing=" + string.Join(",", imports.MissingModules));
                lines.Add($"functions.missing={imports.MissingFunctions.Count} stubbed={imports.FromStubs}");
                imports.Shim.Seal();
                // The list itself is the work queue for the shim.
                var take = Math.Min(imports.MissingFunctions.Count, 400);
                for (var i = 0; i < take; i++) lines.Add("  " + imports.MissingFunctions[i]);

                // Calling into a module nobody initialised takes the whole
                // process down, and an access violation is not something a
                // managed catch can hold. So the report is on disk first, and
                // the attempt only happens when a marker file asks for it.
                if (await folder.TryGetItemAsync("call.txt") != null)
                {
                    // Each step is written down before it is taken: if the
                    // process dies inside one, the file still says which.
                    foreach (var name in new[] { "baselib.dll", "UnityPlayer.dll", "GameAssembly.dll" })
                    {
                        var image = imports.Find(name);
                        if (image == null || image.EntryPoint == IntPtr.Zero) continue;

                        lines.Add("entry." + name + "=attempting");
                        await WriteAsync(lines);

                        var result = StartModule(image);
                        lines[lines.Count - 1] = "entry." + name + "=" + result;
                        lines.Add("stubs.called=" + imports.Shim.Called.Count);
                        foreach (var called in imports.Shim.Called) lines.Add("  called " + called);
                        await WriteAsync(lines);
                        lines.RemoveRange(
                            lines.Count - 1 - imports.Shim.Called.Count,
                            imports.Shim.Called.Count + 1);
                    }
                    lines.Add("stubs.called=" + imports.Shim.Called.Count);
                    foreach (var called in imports.Shim.Called) lines.Add("  called " + called);
                    lines.Add("ticks=" + Ticks(imports));
                    await WriteAsync(lines);

                    // The game's own entry point. It does not return — it opens
                    // a window and loops — so it runs on a thread of its own and
                    // what it asked for is read a few seconds later.
                    // The game's own executable is a few lines that call into
                    // the engine. Calling the engine straight avoids the part
                    // of a program's startup that assumes it owns the process.
                    var engine = imports.Find("UnityPlayer.dll");
                    var entry = engine?.Export("UnityMain") ?? IntPtr.Zero;
                    var exe = imports.FindExecutable();

                    if (entry != IntPtr.Zero)
                    {
                        lines.Add("exe=UnityMain starting");
                        await WriteAsync(lines);

                        // The program has to believe it is the process, or its
                        // startup reads the host application's headers instead
                        // of its own and dies before it asks for anything.
                        var commandLine = Marshal.StringToHGlobalAnsi(
                            "\"" + folder.Path + "\\game.exe\"");
                        var previousBase = PeImage.SetProcessImageBase(
                            exe?.BaseAddress ?? engine.BaseAddress);
                        lines[lines.Count - 1] += $" base 0x{previousBase.ToInt64():X}"
                            + $" -> 0x{exe.BaseAddress.ToInt64():X}";
                        await WriteAsync(lines);

                        var runner = new System.Threading.Thread(() =>
                        {
                            try
                            {
                                var main = Marshal.GetDelegateForFunctionPointer<UnityMainDelegate>(
                                    entry);
                                // An empty string, not nothing: a program that
                                // reads its command line reads through this
                                // pointer, and nothing is a fault.
                                main(engine.BaseAddress, IntPtr.Zero, commandLine, 1);
                            }
                            catch
                            {
                                // Whatever it did is in the list of stubs it reached.
                            }
                        }, 16 * 1024 * 1024);
                        runner.IsBackground = true;
                        runner.Start();

                        // Written over and over while it runs: the program can
                        // take the process down at any point, and the last
                        // function it reached is the whole answer.
                        // It dies in under a frame, so the first look is
                        // immediate and the rest are close behind.
                        var seen = 0L;
                        for (var tick = 0; tick < 200; tick++)
                        {
                            await Task.Delay(tick == 0 ? 2 : (tick < 40 ? 50 : 500));
                            var now = imports.Shim.Total;
                            var snapshot = new List<string>(lines)
                            {
                                "exe.alive=" + runner.IsAlive,
                                "exe.stubs=" + imports.Shim.Called.Count,
                                // Two numbers decide everything: a total that
                                // climbs means the engine is running, and a
                                // total that stands still means it is blocked.
                                "exe.calls=" + now + " (+" + (now - seen) + ")",
                                "exe.pumped=" + WindowStubs.Pumped,
                            };
                            seen = now;
                            foreach (var line in imports.Shim.Threads())
                            {
                                snapshot.Add("  " + line);
                            }
                            foreach (var line in imports.Shim.Busiest(20))
                            {
                                snapshot.Add("  busiest " + line);
                            }
                            foreach (var name in imports.Shim.Recent())
                            {
                                snapshot.Add("  recent " + name);
                            }
                            lock (imports.Shim.Called)
                            {
                                foreach (var called in imports.Shim.Called)
                                {
                                    snapshot.Add("  called " + called);
                                }
                            }
                            await WriteAsync(snapshot);
                            if (!runner.IsAlive) break;
                        }
                        lines.Add("exe.finished");
                        if (previousBase != IntPtr.Zero)
                        {
                            PeImage.SetProcessImageBase(previousBase);
                        }
                    }
                }
            }
            catch (Exception error)
            {
                lines.Add("probe failed: " + error.GetType().Name + ": " + error.Message);
            }
            await WriteAsync(lines);
        }


        /// <summary>
        /// The console's developer share. It is the one place this app can
        /// write that an uninstall does not take with it.
        /// </summary>
        private static async Task<StorageFolder> DevelopmentFiles()
        {
            try
            {
                var root = await StorageFolder.GetFolderFromPathAsync(@"D:\DevelopmentFiles");
                return await root.CreateFolderAsync(
                    "games", CreationCollisionOption.OpenIfExists);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>A folder under this one that holds a Unity game.</summary>
        private static async Task<StorageFolder> GameIn(StorageFolder parent)
        {
            if (parent == null) return null;
            try
            {
                foreach (var candidate in await parent.GetFoldersAsync())
                {
                    if (await candidate.TryGetItemAsync("UnityPlayer.dll") != null)
                    {
                        return candidate;
                    }
                }
            }
            catch
            {
                // An unreadable share is the same as an empty one here.
            }
            return null;
        }

        /// <summary>
        /// Copies a game tree into the developer share, once. Downloading it
        /// again on every build costs more than the whole rest of the cycle.
        /// </summary>
        private static async Task<StorageFolder> MirrorAsync(StorageFolder source)
        {
            var games = await DevelopmentFiles();
            if (games == null) return null;
            try
            {
                var target = await games.CreateFolderAsync(
                    source.Name, CreationCollisionOption.OpenIfExists);
                await CopyInto(source, target);
                return target;
            }
            catch
            {
                return null;
            }
        }

        private static async Task CopyInto(StorageFolder source, StorageFolder target)
        {
            foreach (var file in await source.GetFilesAsync())
            {
                if (await target.TryGetItemAsync(file.Name) != null) continue;
                await file.CopyAsync(target, file.Name, NameCollisionOption.ReplaceExisting);
            }
            foreach (var child in await source.GetFoldersAsync())
            {
                var into = await target.CreateFolderAsync(
                    child.Name, CreationCollisionOption.OpenIfExists);
                await CopyInto(child, into);
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate ulong TicksDelegate();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int DllMainDelegate(IntPtr instance, uint reason, IntPtr reserved);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int MainDelegate();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int UnityMainDelegate(
            IntPtr instance, IntPtr previous, IntPtr commandLine, int show);

        /// <summary>Runs a module's entry point, which is where it sets itself up.</summary>
        private static string StartModule(PeImage image)
        {
            try
            {
                var start = Marshal.GetDelegateForFunctionPointer<DllMainDelegate>(
                    image.EntryPoint);
                return start(image.BaseAddress, 1, IntPtr.Zero).ToString();
            }
            catch (Exception error)
            {
                return error.GetType().Name + ": " + error.Message;
            }
        }

        /// <summary>Something out of the game's own code, to prove it runs at all.</summary>
        private static string Ticks(SystemImports imports)
        {
            try
            {
                const string Symbol =
                    "?Baselib_Timer_GetHighPrecisionTimerTicks@il2cpp_baselib@@YA_KXZ";
                var address = imports.Find("baselib.dll")?.Export(Symbol) ?? IntPtr.Zero;
                if (address == IntPtr.Zero) return "symbol not exported";
                var ticks = Marshal.GetDelegateForFunctionPointer<TicksDelegate>(address);
                var first = ticks();
                var second = ticks();
                return second > first ? $"OK {first}->{second}" : "flat";
            }
            catch (Exception error)
            {
                return error.GetType().Name;
            }
        }

        private static async Task WriteAsync(List<string> lines)
        {
            try
            {
                var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    ReportName, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteLinesAsync(file, lines);
            }
            catch
            {
                // Losing the report loses the measurement, not the app.
            }
        }
    }
}
