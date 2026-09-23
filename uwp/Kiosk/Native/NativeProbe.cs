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
        private const string PulseName = "native-pulse.txt";

        /// <summary>
        /// Whether a game has the screen. The console's own interface is still
        /// behind it, still focused and still listening to the same controller
        /// the game is reading — so it has to be told to sit still, or the
        /// player navigates a menu they cannot see while they play.
        /// </summary>
        public static bool GameRunning;

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
                lines.AddRange(ShareNotes);
                lines.Add("local=" + local.Path);
                if (folder == null)
                {
                    lines.Add("state=no win32 folder");
                    await WriteAsync(lines);
                    return;
                }

                // The dangerous half of the loader only runs when asked.
                // The marker's contents say how far to go, so one build can
                // answer several questions.
                // Full setup is the default now. The markers were a way to ask
                // one build several questions while each step was still able to
                // take the process down; every one of those steps is measured,
                // so asking has become guessing at a race with the download
                // that writes them.
                PeImage.TlsLevel = 6;
                if (await local.TryGetItemAsync("tls.txt") is StorageFile marker)
                {
                    int.TryParse((await FileIO.ReadTextAsync(marker)).Trim(), out var level);
                    if (level > 0) PeImage.TlsLevel = level;
                }
                lines.Add("tls.level=" + PeImage.TlsLevel);

                // Each step of the setup lands on disk as it happens.
                var trail = new List<string>(lines);
                PeImage.Step = note =>
                {
                    var snapshot = new List<string>(trail) { "tls.step=" + note };
                    WriteAsync(snapshot).GetAwaiter().GetResult();
                };

                // Tracing costs a managed call on every function the engine
                // uses, which is millions a second — worth it while the answer
                // is still "where did it stop", and turned off by dropping a
                // file once the answer is "how fast does it run".
                var imports = new SystemImports
                {
                    // Read from the app's own folder, not the game's: the game
                    // folder is created by a download that races this, and a
                    // switch that only sometimes exists is worse than none.
                    Trace = await local.TryGetItemAsync("notrace.txt") == null,
                };
                AudioBridge.Enabled = await local.TryGetItemAsync("noaudio.txt") == null;
                GraphicsBridge.Smaller = await local.TryGetItemAsync("small.txt") != null;
                GraphicsBridge.NoChain = await local.TryGetItemAsync("nochain.txt") != null;
                GraphicsBridge.NoDeviceStandIn =
                    await local.TryGetItemAsync("nodevice.txt") != null;
                GraphicsBridge.NameTheCard = await local.TryGetItemAsync("gpu.txt") != null;
                GraphicsBridge.NoMirror = await local.TryGetItemAsync("nomirror.txt") != null;
                // Sixty by default. The frame is shown by this application
                // rather than handed to the display, so nothing paces the game
                // any more — and a game with nothing pacing it runs as fast as
                // the hardware allows, which here means copying a screen a
                // thousand times a second and drowning everything else.
                GraphicsBridge.Ceiling =
                    await local.TryGetItemAsync("slow.txt") != null ? 30 : 60;
                lines.Add("audio.bridge=" + AudioBridge.Enabled);
                lines.Add("smaller=" + GraphicsBridge.Smaller);
                lines.Add("threads=" + ThreadRank.Note);

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

                // The switches only reach the game if the game can read them,
                // and it reads them from here rather than from its entry point.
                var started = "\"" + folder.Path + "\\" + exeName + "\""
                    + " -logFile " + local.Path + "\\unity.log"
                    + " -force-d3d11"
                    // One thread does the drawing instead of two.
                    //
                    // The engine's own log says threaded=1: the main thread
                    // hands work to a render thread and waits at every sync
                    // point. That render thread is exactly the one this bridge
                    // cannot see into — it goes into the console's own
                    // graphics library, which nothing here traces — and the
                    // picture that keeps coming back is a main thread waiting
                    // for it and thirty-odd workers waiting for them both.
                    //
                    // Without a render thread there is no handshake to
                    // deadlock and nowhere for the work to hide: whatever the
                    // engine does to the device, it does on a thread this
                    // bridge watches, and every call shows up in the trace.
                    + " -force-gfx-direct"
                    + " -screen-fullscreen 1 -screen-width 1920 -screen-height 1080"
                    // The engine sizes its worker pool to the machine and this
                    // machine has sixteen threads, so it takes thirty-four and
                    // leaves the thread that draws the screen with nothing.
                    // On a console the application is not competing with a
                    // desktop; it only has to leave room for itself.
                    + " -job-worker-count=4";
                ModuleFileName.SetCommandLine(imports, started);
                ImageLookup.Install(
                    imports, imports.SystemAddress("kernel32.dll", "RtlPcToFileHeader"));
                FileWatch.Install(imports);
                SuspendWatch.Install(imports);
                ProcessStubs.Install(imports);
                WindowStubs.Install(imports);
                GraphicsBridge.Install(imports);
                LoaderStubs.Install(imports);
                // Wrap the loader's priority hook rather than letting it
                // replace TLS initialization on every game-created thread.
                ThreadTls.Install(imports);
                PadBridge.Install(imports);
                FaultWatch.Install();
                TimerStubs.Install(imports);
                ComStubs.Install(imports);
                PlainAnswers.Install(imports);
                DisplayStubs.Install(imports);
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
                {
                    // Each step is written down before it is taken: if the
                    // process dies inside one, the file still says which.
                    foreach (var name in new[] { "baselib.dll", "UnityPlayer.dll", "GameAssembly.dll" })
                    {
                        var image = imports.Find(name);
                        if (image == null || image.EntryPoint == IntPtr.Zero) continue;

                        lines.Add("entry." + name + "=attempting");
                        await WriteAsync(lines);

                        ThreadTls.Adopt();
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
                    lines.Add("window=0x" + GraphicsBridge.ConsoleWindow.ToInt64().ToString("X"));
                    lines.Add("graphics=" + GraphicsBridge.SelfTest());
                    await WriteAsync(lines);
                    lock (LoaderStubs.Asked)
                    {
                        foreach (var name in LoaderStubs.Asked) lines.Add("  asked " + name);
                    }
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

                    // A marker that stops just short of playing: everything is
                    // mapped and every module's own startup has run, but the
                    // engine is never started. If the screen survives this and
                    // not the next step, the cause is inside the game.
                    if (await local.TryGetItemAsync("noplay.txt") != null)
                    {
                        lines.Add("exe=not started, by request");
                        entry = IntPtr.Zero;
                    }

                    if (entry != IntPtr.Zero)
                    {
                        lines.Add("exe=UnityMain starting");
                        await WriteAsync(lines);

                        // The program has to believe it is the process, or its
                        // startup reads the host application's headers instead
                        // of its own and dies before it asks for anything.
                        // The engine keeps its own diary, and it names what
                        // failed far better than any trace from outside can.
                        // It only writes one when told where to put it.
                        // The same line the game will read back from the
                        // system, so the two never disagree.
                        var commandLine = Marshal.StringToHGlobalAnsi(started);
                        // Rewriting the process-wide image base was needed
                        // back when the game had no other way to learn what it
                        // was. It now gets told its own path and its own
                        // command line, so the change may be pure cost — and
                        // the cost looks like the console taking the screen
                        // back a few seconds in. A marker turns it off so the
                        // two can be told apart.
                        var previousBase = IntPtr.Zero;
                        if (await local.TryGetItemAsync("nopeb.txt") == null)
                        {
                            previousBase = PeImage.SetProcessImageBase(
                                exe?.BaseAddress ?? engine.BaseAddress);
                        }
                        lines[lines.Count - 1] += $" base 0x{previousBase.ToInt64():X}"
                            + $" -> 0x{exe.BaseAddress.ToInt64():X}";
                        await WriteAsync(lines);

                        // A heartbeat, started before the game is. The engine
                        // can take the whole process down between two lines of
                        // the loop below, and when it does the only thing left
                        // is what reached the disk — so the last call each
                        // thread made is written continuously, small and fast,
                        // rather than waited for.
                        // The pointer has to keep moving whether or not the
                        // game is asking, or a stick held still between two
                        // reads looks like a stick let go.
                        var beating = true;
                        var pointer = new System.Threading.Thread(() =>
                        {
                            var last = Environment.TickCount;
                            while (beating)
                            {
                                var now = Environment.TickCount;
                                PointerBridge.Step(Math.Max(0, now - last) / 1000.0);
                                last = now;
                                System.Threading.Thread.Sleep(8);
                            }
                        });
                        pointer.IsBackground = true;
                        pointer.Start();
                        SuspendWatch.ThisThreadIsOurs();
                        PointerBridge.Announce();
                        GameRunning = true;

                        var pulse = new System.Threading.Thread(() =>
                        {
                            var wasFrames = 0L;
                            var wasAt = Environment.TickCount;
                            var rate = 0.0;
                            while (beating)
                            {
                                // Frames per second, measured over the gap
                                // between two beats rather than claimed.
                                var now = Environment.TickCount;
                                if (now - wasAt >= 500)
                                {
                                    rate = (GraphicsBridge.Frames - wasFrames)
                                        * 1000.0 / (now - wasAt);
                                    wasFrames = GraphicsBridge.Frames;
                                    wasAt = now;
                                }
                                try
                                {
                                    var beat = new List<string>
                                    {
                                        "at=" + DateTime.Now.ToString("HH:mm:ss.fff"),
                                        "calls=" + imports.Shim.Total,
                                        "pumped=" + WindowStubs.Pumped,
                                        "pad=" + PadBridge.Reads,
                                        "calmed=" + LoaderStubs.Calmed,
                                        "exit=" + ProcessStubs.Attempted,
                                        "buffers=" + GraphicsBridge.Buffers,
                                        "mirrored=" + FrameMirror.Copied + " shown=" + FrameMirror.Shown,
                                        "frames=" + GraphicsBridge.Frames
                                            + " at " + rate.ToString("0.0") + " a second",
                                        "keys=" + PointerBridge.Keys,
                                        "pointer=" + PointerBridge.Moves
                                            + " at " + PointerBridge.X + "," + PointerBridge.Y,
                                        "stubs=" + imports.Shim.Called.Count,
                                    };
                                    beat.AddRange(FaultWatch.Faults());
                                    lock (ComStubs.Wanted)
                                    {
                                        foreach (var id in ComStubs.Wanted) beat.Add("com " + id);
                                    }
                                    lock (AudioBridge.Notes)
                                    {
                                        foreach (var n in AudioBridge.Notes) beat.Add("audio " + n);
                                    }
                                    lock (LoaderStubs.Said)
                                    {
                                        var from = Math.Max(0, LoaderStubs.Said.Count - 200);
                                        for (var i = from; i < LoaderStubs.Said.Count; i++)
                                        {
                                            beat.Add("said " + LoaderStubs.Said[i]);
                                        }
                                    }
                                    lock (imports.Shim.Callers)
                                    {
                                        foreach (var pair in imports.Shim.Callers)
                                        {
                                            beat.Add("waiting " + pair.Value + "x " + pair.Key);
                                        }
                                    }
                                    beat.AddRange(imports.Shim.Threads());
                                    lock (FileWatch.Seen)
                                    {
                                        foreach (var f in FileWatch.Seen) beat.Add("file " + f);
                                    }
                                    lock (GraphicsBridge.Notes)
                                    {
                                        foreach (var note in GraphicsBridge.Notes)
                                        {
                                            beat.Add("dxgi " + note);
                                        }
                                    }
                                    foreach (var name in imports.Shim.Recent())
                                    {
                                        beat.Add("recent " + name);
                                    }
                                    SwapAsync(PulseName, beat).GetAwaiter().GetResult();
                                }
                                catch
                                {
                                    // A missed beat is a missed beat.
                                }
                                System.Threading.Thread.Sleep(25);
                            }
                        });
                        pulse.IsBackground = true;
                        pulse.Start();

                        var runner = new System.Threading.Thread(() =>
                        {
                            try
                            {
                                ThreadTls.Adopt();
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
                        // The engine's own thread too, for the same reason.
                        runner.Priority = System.Threading.ThreadPriority.BelowNormal;
                        runner.Start();

                        // Written over and over while it runs: the program can
                        // take the process down at any point, and the last
                        // function it reached is the whole answer.
                        // It dies in under a frame, so the first look is
                        // immediate and the rest are close behind.
                        // Given back as soon as the engine has read it. The
                        // process-wide image base is how everything else in
                        // this application answers "who am I" — the framework
                        // that draws the screen included — and leaving it
                        // pointing at the game is what took the screen away
                        // about ten seconds in, measured.
                        var handedBack = false;

                        var seen = 0L;
                        // Long enough for a game to load a scene and show a
                        // splash, not just to start. A first frame that arrives
                        // after the watch ended looks exactly like no frame.
                        for (var tick = 0; tick < 500; tick++)
                        {
                            await Task.Delay(tick == 0 ? 2 : (tick < 40 ? 50 : 500));
                            if (!handedBack && tick > 12 && previousBase != IntPtr.Zero)
                            {
                                PeImage.SetProcessImageBase(previousBase);
                                handedBack = true;
                                lines.Add("base.returned");
                            }

                            var now = imports.Shim.Total;
                            var snapshot = new List<string>(lines)
                            {
                                "exe.alive=" + runner.IsAlive,
                                "exe.stubs=" + imports.Shim.Called.Count,
                                "exe.thunks.full=" + imports.Shim.Overflowed,
                                "exe.files.failed=" + FileWatch.Failures,
                                "exe.tls.threads=" + ThreadTls.Adopted,
                                "exe.tls.blocks=" + ThreadTls.CopiedBlocks,
                                "exe.tls.failures=" + ThreadTls.Failures + " " + ThreadTls.LastError,
                                "exe.suspended=" + string.Join(
                                    "; ", SuspendWatch.Held()),
                                // Two numbers decide everything: a total that
                                // climbs means the engine is running, and a
                                // total that stands still means it is blocked.
                                "exe.calls=" + now + " (+" + (now - seen) + ")",
                                "exe.pumped=" + WindowStubs.Pumped,
                            };
                            lock (GraphicsBridge.Notes)
                            {
                                foreach (var note in GraphicsBridge.Notes)
                                {
                                    snapshot.Add("  dxgi " + note);
                                }
                            }
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
                        beating = false;
                        GameRunning = false;
                        GraphicsBridge.RestoreInterface();
                        lines.Add("exe.finished");

                        // The engine's whole account of its startup, not just
                        // the tail the heartbeat had room for.
                        lock (LoaderStubs.Said)
                        {
                            lines.Add("said.lines=" + LoaderStubs.Said.Count);
                            foreach (var line in LoaderStubs.Said) lines.Add("  said " + line);
                        }
                        lock (GraphicsBridge.Notes)
                        {
                            foreach (var note in GraphicsBridge.Notes) lines.Add("  dxgi " + note);
                        }
                        lock (AudioBridge.Notes)
                        {
                            foreach (var note in AudioBridge.Notes) lines.Add("  audio " + note);
                        }
                        lines.Add("pad.reads=" + PadBridge.Reads);
                        lines.Add("pumped=" + WindowStubs.Pumped);
                        if (!handedBack && previousBase != IntPtr.Zero)
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


        /// <summary>What each candidate developer path answered, for the report.</summary>
        public static readonly List<string> ShareNotes = new List<string>();

        /// <summary>
        /// The console's developer share. It is the one place this app can
        /// write that an uninstall does not take with it — and an uninstall is
        /// every build, so finding it is worth more than one download.
        ///
        /// Which letter it lives behind is not documented anywhere that agrees
        /// with this console, so every plausible one is tried and the answer
        /// each gave is written down.
        /// </summary>
        private static async Task<StorageFolder> DevelopmentFiles()
        {
            var roots = new[]
            {
                @"D:\DevelopmentFiles",
                @"D:\DevelopmentFiles\LooseApps",
                @"U:\DevelopmentFiles",
                @"T:\DevelopmentFiles",
                @"E:\DevelopmentFiles",
                @"S:\DevelopmentFiles",
            };

            foreach (var path in roots)
            {
                try
                {
                    var root = await StorageFolder.GetFolderFromPathAsync(path);
                    var games = await root.CreateFolderAsync(
                        "games", CreationCollisionOption.OpenIfExists);
                    var count = (await games.GetFoldersAsync()).Count;
                    ShareNotes.Add("share " + path + " = ok, " + count + " folder(s)");
                    return games;
                }
                catch (Exception error)
                {
                    ShareNotes.Add("share " + path + " = " + error.GetType().Name);
                }
            }
            return null;
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

        /// <summary>
        /// Writes the report somewhere else and swaps it in.
        ///
        /// Replacing the file in place empties it first, and the process this
        /// measures can die inside that window — which costs the whole run,
        /// because what is left on disk is nothing at all. A swap is never
        /// caught halfway: either the old report is there or the new one is.
        /// </summary>
        private static Task WriteAsync(List<string> lines) => SwapAsync(ReportName, lines);

        private static async Task SwapAsync(string name, List<string> lines)
        {
            try
            {
                var local = ApplicationData.Current.LocalFolder;
                var draft = await local.CreateFileAsync(
                    name + ".new", CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteLinesAsync(draft, lines);

                var existing = await local.TryGetItemAsync(name) as StorageFile;
                if (existing == null)
                {
                    await draft.RenameAsync(name, NameCollisionOption.ReplaceExisting);
                }
                else
                {
                    await draft.MoveAndReplaceAsync(existing);
                }
            }
            catch
            {
                // Losing the report loses the measurement, not the app.
            }
        }
    }
}
