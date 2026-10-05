using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Nativra.X86.Cpu;
using Nativra.X86.Jit;
using Nativra.X86.Loader;
using Windows.Storage;

namespace Kiosk.Native
{
    /// <summary>
    /// Starts a 32-bit (PE32, i386) game under the x86 layer.
    ///
    /// The 64-bit path maps a game into this process and lets the CPU run it; a
    /// 32-bit program cannot run on the 64-bit host that way, so it goes into
    /// an emulated 4 GB guest space instead (Nativra.X86): the PE32 is mapped
    /// there, the DLLs the game carries are linked in beside it, and the block
    /// JIT translates its code to x64 as it runs. Each call into Windows lands on
    /// a sentinel and is served by a host handler.
    ///
    /// The handler set is still growing, so a game stops at the first import
    /// nothing serves yet. That stop is the measurement: this writes how far the
    /// game got — the import it wanted, every import it declares, the optional
    /// APIs it probed for — to the probe report and to x86-imports.txt, which is
    /// what decides the next handlers to write.
    /// </summary>
    internal static class X86Launch
    {
        private const string ImportsReport = "x86-imports.txt";
        // DLL initialisation is bounded: a DllMain that never returns is a bug to report.
        private const long BlockBudget = 200_000_000;
        // The game itself runs until it exits: a budget here ended games mid-play.
        private const long GameBudget = long.MaxValue;
        private static readonly TimeSpan SnapshotEvery = TimeSpan.FromSeconds(30);
        private const int LogLines = 150;

        /// <summary>
        /// Loads and runs the program. True when the game ended by itself (it
        /// returned or called ExitProcess); false when it stopped at something
        /// the layer does not serve yet, with the reason in <paramref name="lines"/>.
        /// </summary>
        public static async Task<bool> RunAsync(string folderPath, string exeName, byte[] exeBytes, List<string> lines,
            Func<List<string>, Task> snapshot = null)
        {
            try
            {
                return await RunCoreAsync(folderPath, exeName, exeBytes, lines, snapshot);
            }
            catch (Exception error)
            {
                // A layer failure (no room for the guest space, an image it cannot
                // map) is still "this game cannot start yet", not a crash.
                lines.Add("x86.failed=" + Flat(error));
                return false;
            }
        }

        private static async Task<bool> RunCoreAsync(string folderPath, string exeName, byte[] exeBytes, List<string> lines,
            Func<List<string>, Task> snapshot)
        {
            // Before anything reserves guest or code memory: the app container
            // refuses the plain VirtualAlloc family, and both the guest space and
            // the JIT's code cache capture the backend when they are created.
            HostPages.Override = new AppContainerPages();

            // x86interp.txt in the app's folder runs the reference interpreter
            // only: the A/B check when a game behaves differently under the JIT.
            var local = ApplicationData.Current.LocalFolder;
            var interpreterOnly = await local.TryGetItemAsync("x86interp.txt") != null;
            var noX87Environment = await local.TryGetItemAsync("nojit-x87env.txt") != null;
            var noJecxz = await local.TryGetItemAsync("nojit-jecxz.txt") != null;
            var noBlockCache = await local.TryGetItemAsync("nojit-blockcache.txt") != null;
            var noComFast = await local.TryGetItemAsync("nocom-fast.txt") != null;
            var formNames = new[] { "bits", "shld", "bsf", "lock", "xchg", "lahf", "highbyte", "nops", "retform", "strings", "x87", "mmx" };
            var formValues = new[] { JitFormSwitches.Bits, JitFormSwitches.Shld, JitFormSwitches.Bsf,
                JitFormSwitches.Lock, JitFormSwitches.Xchg, JitFormSwitches.Lahf,
                JitFormSwitches.HighByte, JitFormSwitches.Nops, JitFormSwitches.RetForm,
                JitFormSwitches.Strings, JitFormSwitches.X87, JitFormSwitches.Mmx };
            var noNewForms = await local.TryGetItemAsync("nojit-newforms.txt") != null;
            var disabledForms = noNewForms ? JitFormSwitches.All : JitFormSwitches.None;
            var switches = "x86interp=" + interpreterOnly + " nojit-x87env=" + noX87Environment +
                " nojit-jecxz=" + noJecxz + " nojit-blockcache=" + noBlockCache + " nocom-fast=" + noComFast +
                " nojit-newforms=" + noNewForms;
            for (var i = 0; i < formNames.Length; i++)
            {
                var selected = await local.TryGetItemAsync("nojit-" + formNames[i] + ".txt") != null;
                if (selected) disabledForms |= formValues[i];
                switches += " nojit-" + formNames[i] + "=" + (selected || noNewForms);
            }
            var faultPath = System.IO.Path.Combine(local.Path, "native-fault.txt");
            System.IO.File.WriteAllText(faultPath, "x86.switches=" + switches + Environment.NewLine);
            JitEngine.ConfigureFaultReport(faultPath, System.IO.Path.Combine(local.Path, "native-probe.txt"));
            lines.Add("x86.switches=" + switches);

            var started = System.Diagnostics.Stopwatch.StartNew();
            GuestRunResult result;
            using (var memory = new GuestMemory(native: true))
            using (var process = new GuestProcess(memory, useJit: !interpreterOnly))
            {
                if (process.Jit != null)
                {
                    process.Jit.DisableX87Environment = noX87Environment;
                    process.Jit.DisableJecxz = noJecxz;
                    process.Jit.DisableBlockCache = noBlockCache;
                    process.Jit.DisabledForms = disabledForms;
                }
                using (var heartbeat = new X86Heartbeat(System.IO.Path.Combine(local.Path, "x86-heartbeat.txt"), process, memory, switches))
                {
                    var kernel = new GuestKernel(process);
                    kernel.ShaderCompiler = X86ShaderCompiler.Compile;
                    kernel.ExePath = folderPath.TrimEnd('\\') + "\\" + exeName;
                    kernel.SetCommandLine("\"" + kernel.ExePath + "\"");
                    var guestLog = new List<string>();
                    // GetProcAddress lookups fill the head of every log and say little; the
                    // lines after them (assertions, exceptions, file opens) are the news.
                    kernel.Log = text =>
                    {
                        X86Heartbeat.Remember(text);
                        if (!text.StartsWith("GetProcAddress ", StringComparison.Ordinal) && guestLog.Count < LogLines)
                            guestLog.Add(text);
                    };
                    // The same input the 64-bit path reads (pad as mouse and keys),
                    // delivered as messages on the game's window.
                    kernel.Input = new ConsoleInput();
                    // Documents, Saved Games, AppData...: the same profile folders a
                    // 64-bit game gets, so saves live in one place whatever the game's bitness.
                    // The guest sees C:\users\Player (Wine's layout); it lands in LocalState\profile.
                    if (UserFolders.Root == null) UserFolders.Root = System.IO.Path.Combine(local.Path, "profile");
                    kernel.ProfileRoot = UserFolders.Root;
                    // Files through the removable drive's folder handle or the broker:
                    // System.IO cannot reach a game on a USB drive.
                    await UsbFiles.InstallAsync();
                    kernel.Files = new X86Files();
                    kernel.Install();
                    kernel.Log("imports.diagnose kernel32!InterlockedCompareExchange: " +
                        process.Imports.Diagnose("kernel32.dll", "InterlockedCompareExchange"));
                    // Direct3D 9 through the packaged 64-bit layer.
                    if (process.Jit != null) process.Jit.CollectFallbacks = true;   // cheap: one dictionary bump per fallback
                    var com = new GuestCom(process, kernel);
                    com.Log = text => kernel.Log?.Invoke(text);
                    com.UseHeapArguments = noComFast;
                    X86Direct3D9.Install(process, kernel, com);
                    using (var directSound = new GuestDirectSound(process, kernel, new X86DirectSoundOutput()))
                    {
                        directSound.Install();
                        PadBridge.InstallX86(process);   // xinput1_3/1_4/9_1_0: the same pad the 64-bit games read
                                                         // Steamworks for the signed-in account, as the 64-bit bridge answers it:
                                                         // the game's own steam_api.dll is not mapped, its exports are served.
                        GuestSteam steam = null;
                        if (SteamBridge.Active)
                        {
                            SteamBridge.Resolve("SteamAPI_Init");   // loads the account's saved achievements and stats
                            steam = new GuestSteam(process, kernel, new X86SteamAccount());
                            steam.Install();
                        }
                        // XAudio 2.7 through the packaged 64-bit shim, as for a 64-bit game.
                        var xaudio = new XAudio27Com(process, kernel, com);
                        xaudio.Install((clsid, iid, made) => XAudio27Route.Create(clsid, iid, made));

                        var packaged = System.IO.Path.Combine(Windows.ApplicationModel.Package.Current.InstalledLocation.Path, "x86");
                        var fromPackage = new List<string>();
                        process.ModuleSource = name =>
                        {
                            if (steam != null && name.Equals("steam_api.dll", StringComparison.OrdinalIgnoreCase)) return null;
                            var bytes = ReadGameModule(folderPath, name);
                            if (bytes != null) return bytes;
                            bytes = ReadPackagedModule(packaged, name);
                            if (bytes != null) fromPackage.Add(name);
                            return bytes;
                        };

                        // Where the game is: registers, recent calls, its log, the bridges' counters.
                        void Describe(List<string> into)
                        {
                            into.Add("x86.switches=" + switches);
                            into.Add("x86.eip=0x" + process.Cpu.Eip.ToString("X8") + " " + process.Cpu);
                            into.Add("x86.blocks=" + (process.Jit != null
                                ? process.Jit.BlocksCompiled + " compiled, " + process.Jit.BlocksExecuted + " run, " +
                                  process.Jit.InterpreterFallbacks + " interpreted"
                                : "interpreter"));
                            if (process.Jit != null) into.Add("x86.code-cache=" + process.Jit.CodeCacheBytes + " bytes");
                            into.Add("x86.recent=" + string.Join(" ", process.RecentImports));
                            // Return-address candidates above ESP: who called the code that stopped.
                            try
                            {
                                var callers = new List<string>();
                                var top = process.Cpu.Esp;
                                for (uint n = 0; n < 96 && callers.Count < 14; n++)
                                {
                                    if (!memory.IsMapped(top + n * 4)) break;
                                    var word = memory.Read32(top + n * 4);
                                    foreach (var image in process.Images)
                                        if (word >= image.BaseAddress && word - image.BaseAddress < image.ImageSize)
                                        {
                                            callers.Add(image.Name + "+0x" + (word - image.BaseAddress).ToString("X"));
                                            break;
                                        }
                                }
                                into.Add("x86.stack=" + string.Join(" < ", callers));
                            }
                            catch (Exception) { }
                            // What still falls back to the interpreter, most frequent first: the next
                            // instructions worth translating for this game.
                            if (process.Jit != null && process.Jit.CollectFallbacks)
                                foreach (var pair in process.Jit.FallbackCounts.ToArray().OrderByDescending(f => f.Value).Take(15))
                                    into.Add("x86.fallback=" + pair.Key + " count=" + pair.Value);
                            if (process.JitRefusal != null) into.Add("x86.jit.refused=" + process.JitRefusal);
                            if (kernel.ProbedAbsent.Count > 0)
                                into.Add("x86.probed-absent=" + string.Join(",", kernel.ProbedAbsent.Distinct()));
                            if (kernel.FilesNotFound.Count > 0)
                                into.Add("x86.files-not-found=" + string.Join(",", kernel.FilesNotFound.Distinct().Take(LogLines)));
                            foreach (var text in guestLog.ToArray()) into.Add("x86.log=" + text);
                            into.Add("x86.threads=" + string.Join(",", process.Threads.Select(t => t.ToString())));
                            into.Add("x86.window=0x" + kernel.InputWindow.ToString("X") + " dispatched=" + kernel.MessagesDispatched);
                            // Whether the pad reaches the game: its XInput polls, and the keys the host window holds.
                            into.Add("x86.pad=reads " + PadBridge.Reads + " probes " + PadBridge.Probes +
                                     " held " + PointerBridge.HostKeys.Count(down => down));
                            into.Add("x86.d3d9=" + X86Direct3D9.Note + " lockheap=" + (X86Direct3D9.LockBytes >> 20) + "MB proxies=" + com.ProxyCount);
                            into.Add("x86.dsound=buffers " + directSound.BuffersCreated + " frames " + directSound.FramesMixed +
                                     " underruns " + directSound.Underruns +
                                     (directSound.Failure != null ? " (" + directSound.Failure + ")" : ""));
                            into.Add("x86.xaudio=" + XAudio27Route.Note + " callbacks=" + xaudio.CallbacksDelivered +
                                      " dropped=" + xaudio.CallbacksDropped + " effect-chains-dropped=" + xaudio.EffectChainsDropped);
                            if (com.MissingClasses.Count > 0)
                                into.Add("x86.com.missing-classes=" + string.Join(",", com.MissingClasses));
                            foreach (var call in com.Calls.OrderByDescending(pair => pair.Value).Take(40))
                                into.Add("x86.com " + call.Value + "x " + call.Key);
                            if (steam != null)
                            {
                                into.Add("x86.steam=" + string.Join(",", steam.Versions));
                                foreach (var call in steam.Calls.OrderByDescending(pair => pair.Value).Take(30))
                                    into.Add("x86.steam " + call.Value + "x " + call.Key);
                            }
                            into.Add("x86.seconds=" + started.Elapsed.TotalSeconds.ToString("0.0"));
                        }

                        result = null;
                        try
                        {
                            var image = process.LoadExecutable(exeName, exeBytes);
                            lines.Add("x86.image=" + exeName +
                                      " base=0x" + image.BaseAddress.ToString("X8") +
                                      " preferred=0x" + image.PreferredBase.ToString("X8") +
                                      " entry=0x" + image.EntryPoint.ToString("X8") +
                                      " size=0x" + image.ImageSize.ToString("X"));
                            lines.Add("x86.jit=" + (process.UsesJit ? "on" : interpreterOnly ? "off (x86interp.txt)" : "unavailable"));
                            lines.Add("x86.modules=" + string.Join(",", process.Images.Select(i => i.Name)));
                            if (fromPackage.Count > 0) lines.Add("x86.packaged=" + string.Join(",", fromPackage));
                            ReportImports(process, lines);

                            result = process.InitializeModules(BlockBudget);
                            lines.Add("x86.init=" + result);
                            lines.Add("x86.init.diagnose=" +
                                process.Imports.Diagnose("kernel32.dll", "InterlockedCompareExchange"));
                            if (result.Ok)
                            {
                                // The report is otherwise written only when the game ends; while it
                                // plays, a copy goes out every half minute so a sweep sees where it is.
                                var playing = new System.Threading.CancellationTokenSource();
                                var reporter = snapshot == null ? Task.CompletedTask : Task.Run(async () =>
                                {
                                    while (!playing.IsCancellationRequested)
                                    {
                                        try { await Task.Delay(SnapshotEvery, playing.Token); } catch (TaskCanceledException) { break; }
                                        try
                                        {
                                            var now = new List<string>(lines) { "x86.run=running" };
                                            Describe(now);
                                            await snapshot(now);
                                        }
                                        catch (Exception) { }   // lists in use by the game thread; the next tick tries again
                                    }
                                });
                                uint exitCode;
                                try { result = process.Call(image.EntryPoint, out exitCode, GameBudget); }
                                finally { playing.Cancel(); }
                                await reporter;
                                lines.Add("x86.run=" + result + (result.Ok ? " (entry returned " + exitCode + ")" : ""));
                                // The run line keeps only the exception's first line; the stack says where.
                                if (result.Stop == GuestStop.HostError && result.Detail != null)
                                    lines.Add("x86.host-error=" + result.Detail.Replace("\r", "").Replace("\n", " | "));
                            }
                        }
                        catch (Exception error)
                        {
                            // Whatever broke, the report below still says where the game was.
                            lines.Add("x86.failed=" + Flat(error));
                            result = new GuestRunResult(GuestStop.HostError, detail: error.ToString());
                        }

                        // The guest's sockets end with its run: a server's bound port must not outlive it
                        // (the app keeps running, and the next launch binds the same port).
                        kernel.CloseNetwork();

                        Describe(lines);

                        await WriteImportsAsync(process, kernel);
                    }
                }
            }
            return result.Stop == GuestStop.Exited || result.Stop == GuestStop.Returned;
        }

        private sealed class X86Heartbeat : IDisposable
        {
            private static readonly List<string> Tail = new List<string>();

            /// <summary>Keeps the guest's latest log lines so a game that never ends still shows what it said last.</summary>
            public static void Remember(string text)
            {
                lock (Tail)
                {
                    if (text.StartsWith("GetProcAddress ", StringComparison.Ordinal)) return;
                    Tail.Add(text.Length > 300 ? text.Substring(0, 300) : text);
                    if (Tail.Count > 40) Tail.RemoveAt(0);
                }
            }

            private static string TailText()
            {
                lock (Tail)
                {
                    var text = "";
                    foreach (var line in Tail) text += "x86.log.tail=" + line.Replace('\r', ' ').Replace('\n', ' ') + Environment.NewLine;
                    return text;
                }
            }

            private readonly string path;
            private readonly GuestProcess process;
            private readonly GuestMemory memory;
            private readonly string switches;
            private readonly System.Threading.ManualResetEvent stopped = new System.Threading.ManualResetEvent(false);
            private readonly System.Threading.Thread thread;

            public X86Heartbeat(string path, GuestProcess process, GuestMemory memory, string switches)
            {
                this.path = path;
                this.process = process;
                this.memory = memory;
                this.switches = switches;
                Write();
                thread = new System.Threading.Thread(Run) { IsBackground = true, Name = "x86 heartbeat" };
                thread.Start();
            }

            private void Run()
            {
                while (!stopped.WaitOne(2000)) Write();
            }

            private void Write()
            {
                try
                {
                    var jit = process.Jit;
                    var cache = jit == null ? 0 : jit.CodeCacheBytes;
                    var guest = memory.MappedPages * GuestMemory.PageSize;
                    var managed = GC.GetTotalMemory(false);
                    ulong appMemory = 0;
                    try { appMemory = Windows.System.MemoryManager.AppMemoryUsage; } catch (Exception) { }
                    System.IO.File.WriteAllText(path,
                        "time=" + DateTime.UtcNow.ToString("o") + Environment.NewLine +
                        "x86.switches=" + switches + Environment.NewLine +
                        "x86.eip=0x" + process.Cpu.Eip.ToString("X8") + Environment.NewLine +
                        "x86.running-block=0x" + (jit == null ? 0 : jit.RunningEip).ToString("X8") + Environment.NewLine +
                        "x86.running-thread=" + (jit == null ? 0 : jit.RunningThreadId) + Environment.NewLine +
                        "x86.blocks=" + (jit == null ? 0 : jit.BlocksCompiled) + " compiled, " +
                            (jit == null ? 0 : jit.BlocksExecuted) + " run" + Environment.NewLine +
                        "x86.code-cache=" + cache + " bytes" + Environment.NewLine +
                        "x86.guest-committed=" + guest + " bytes" + Environment.NewLine +
                        "x86.managed=" + managed + " bytes" + Environment.NewLine +
                        "x86.private-estimate=" + (guest + cache + managed) + " bytes" + Environment.NewLine +
                        "x86.app-memory=" + appMemory + " bytes" + Environment.NewLine +
                        "x86.threads=" + string.Join(",", process.Threads.Select(t => t.ToString())) + Environment.NewLine +
                        "x86.recent=" + string.Join(" ", process.RecentImports) + Environment.NewLine +
                        "x86.com-calls=" + GuestCom.CallSummary() + Environment.NewLine +
                        TailText());
                }
                catch (Exception) { }
            }

            public void Dispose()
            {
                stopped.Set();
                thread.Join();
                Write();
                stopped.Dispose();
            }
        }

        /// <summary>A DLL from the game's own folder, or null when the game does not carry it.</summary>
        private static byte[] ReadGameModule(string folderPath, string name)
        {
            try
            {
                var path = System.IO.Path.Combine(folderPath, name);
                return FileWatch.PathExists(path) ? FileWatch.ReadAll(path) : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>An exception with its stack and inner exceptions, on one report line.</summary>
        private static string Flat(Exception error) =>
            error.ToString().Replace("\r", "").Replace("\n", " | ");

        /// <summary>
        /// A 32-bit DLL the package carries for games that do not bring their
        /// own: the redist shims and the redistributable C runtime.
        /// </summary>
        private static byte[] ReadPackagedModule(string packaged, string name)
        {
            try
            {
                var path = System.IO.Path.Combine(packaged, name);
                return System.IO.File.Exists(path) ? System.IO.File.ReadAllBytes(path) : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Per-module import counts, and how many of each the host already serves.</summary>
        private static void ReportImports(GuestProcess process, List<string> lines)
        {
            var all = process.Images.SelectMany(i => i.Imports)
                .Where(i => GuestImports.InRegion(i.Bound));   // those served by the host, not a game DLL
            foreach (var group in all.GroupBy(i => i.Module).OrderBy(g => g.Key))
            {
                var served = group.Count(i => process.Imports.TryResolve(i.Bound, out var g) && g.Handler != null);
                lines.Add("x86.imports." + group.Key + "=" + group.Count() + " (" + served + " served)");
            }
        }

        private static async Task WriteImportsAsync(GuestProcess process, GuestKernel kernel)
        {
            var report = new List<string>();
            foreach (var image in process.Images)
            {
                report.Add("[" + image.Name + "] base=0x" + image.BaseAddress.ToString("X8"));
                foreach (var import in image.Imports)
                {
                    string state;
                    if (!GuestImports.InRegion(import.Bound)) state = "linked";
                    else if (process.Imports.TryResolve(import.Bound, out var g) && g.Handler != null) state = "served";
                    else state = "MISSING";
                    report.Add("  " + import + " " + state);
                }
            }
            if (kernel.ProbedAbsent.Count > 0)
            {
                report.Add("[GetProcAddress, not served]");
                foreach (var name in kernel.ProbedAbsent.Distinct()) report.Add("  " + name);
            }

            try
            {
                var folder = ApplicationData.Current.LocalFolder;
                var file = await folder.CreateFileAsync(ImportsReport, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteLinesAsync(file, report);
            }
            catch
            {
                // Losing the list loses a measurement, not the app.
            }
        }

        /// <summary>
        /// PointerBridge's queue and state for a 32-bit guest: it writes 64-bit
        /// MSGs, so each is read back from one reused native block and handed
        /// over as the three values the guest's 32-bit MSG needs.
        /// </summary>
        private sealed class ConsoleInput : IGuestInput
        {
            private readonly IntPtr message = Marshal.AllocHGlobal(48);

            public bool TakeMessage(bool remove, out uint id, out uint wParam, out uint lParam)
            {
                id = wParam = lParam = 0;
                if (!PointerBridge.Take(message, remove)) return false;
                id = (uint)Marshal.ReadInt32(message, 8);
                wParam = (uint)Marshal.ReadInt64(message, 16);
                lParam = (uint)Marshal.ReadInt64(message, 24);
                return true;
            }

            public void CursorPosition(out int x, out int y) => PointerBridge.ReadPosition(out x, out y);
            public void SetCursorPosition(int x, int y) => PointerBridge.Warp(x, y);

            public bool KeyDown(int virtualKey)
            {
                if (virtualKey == 1) return PointerBridge.Left;    // VK_LBUTTON
                if (virtualKey == 2) return PointerBridge.Right;   // VK_RBUTTON
                return PointerBridge.Down(virtualKey);
            }
        }

        /// <summary>
        /// Guest and code-cache memory through the *FromApp allocators, the ones
        /// an app container may call. Executable pages additionally need the
        /// codeGeneration capability, which the package declares.
        /// </summary>
        private sealed class AppContainerPages : HostPages.IBackend
        {
            private const uint MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000, MEM_DECOMMIT = 0x4000, MEM_RELEASE = 0x8000;
            private const uint PAGE_NOACCESS = 0x01, PAGE_READONLY = 0x02, PAGE_READWRITE = 0x04;
            private const uint PAGE_EXECUTE_READ = 0x20, PAGE_EXECUTE_READWRITE = 0x40;

            [DllImport("api-ms-win-core-memory-l1-1-3.dll", SetLastError = true)]
            private static extern IntPtr VirtualAllocFromApp(IntPtr address, UIntPtr size, uint type, uint protect);

            [DllImport("api-ms-win-core-memory-l1-1-3.dll", SetLastError = true)]
            private static extern bool VirtualProtectFromApp(IntPtr address, UIntPtr size, uint protect, out uint old);

            [DllImport("api-ms-win-core-memory-l1-1-0.dll", SetLastError = true)]
            private static extern bool VirtualFree(IntPtr address, UIntPtr size, uint type);

            public IntPtr Reserve(ulong size) =>
                VirtualAllocFromApp(IntPtr.Zero, (UIntPtr)size, MEM_RESERVE, PAGE_NOACCESS);

            public bool Commit(IntPtr address, ulong size) =>
                VirtualAllocFromApp(address, (UIntPtr)size, MEM_COMMIT, PAGE_READWRITE) != IntPtr.Zero;

            public bool Protect(IntPtr address, ulong size, bool write, bool execute)
            {
                var protect = execute
                    ? (write ? PAGE_EXECUTE_READWRITE : PAGE_EXECUTE_READ)
                    : (write ? PAGE_READWRITE : PAGE_READONLY);
                return VirtualProtectFromApp(address, (UIntPtr)size, protect, out _);
            }

            public void Decommit(IntPtr address, ulong size) => VirtualFree(address, (UIntPtr)size, MEM_DECOMMIT);

            public void Release(IntPtr address, ulong size) => VirtualFree(address, UIntPtr.Zero, MEM_RELEASE);
        }
    }
}
