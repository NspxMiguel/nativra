using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Nativra.X86.Cpu;
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
        private const long BlockBudget = 200_000_000;
        private const int LogLines = 50;

        /// <summary>
        /// Loads and runs the program. True when the game ended by itself (it
        /// returned or called ExitProcess); false when it stopped at something
        /// the layer does not serve yet, with the reason in <paramref name="lines"/>.
        /// </summary>
        public static async Task<bool> RunAsync(string folderPath, string exeName, byte[] exeBytes, List<string> lines)
        {
            try
            {
                return await RunCoreAsync(folderPath, exeName, exeBytes, lines);
            }
            catch (Exception error)
            {
                // A layer failure (no room for the guest space, an image it cannot
                // map) is still "this game cannot start yet", not a crash.
                lines.Add("x86.failed=" + Flat(error));
                return false;
            }
        }

        private static async Task<bool> RunCoreAsync(string folderPath, string exeName, byte[] exeBytes, List<string> lines)
        {
            // Before anything reserves guest or code memory: the app container
            // refuses the plain VirtualAlloc family, and both the guest space and
            // the JIT's code cache capture the backend when they are created.
            HostPages.Override = new AppContainerPages();

            // x86interp.txt in the app's folder runs the reference interpreter
            // only: the A/B check when a game behaves differently under the JIT.
            var local = ApplicationData.Current.LocalFolder;
            var interpreterOnly = await local.TryGetItemAsync("x86interp.txt") != null;

            var started = System.Diagnostics.Stopwatch.StartNew();
            GuestRunResult result;
            using (var memory = new GuestMemory(native: true))
            using (var process = new GuestProcess(memory, useJit: !interpreterOnly))
            {
                var kernel = new GuestKernel(process);
                kernel.ExePath = folderPath.TrimEnd('\\') + "\\" + exeName;
                kernel.SetCommandLine("\"" + kernel.ExePath + "\"");
                var guestLog = new List<string>();
                kernel.Log = text => { if (guestLog.Count < LogLines) guestLog.Add(text); };
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
                // Direct3D 9 through the packaged 64-bit layer.
                var com = new GuestCom(process, kernel);
                X86Direct3D9.Install(process, kernel, com);
                // XAudio 2.7 through the packaged 64-bit shim, as for a 64-bit game.
                var xaudio = new XAudio27Com(process, kernel, com);
                xaudio.Install((clsid, iid, made) => XAudio27Route.Create(clsid, iid, made));

                var packaged = System.IO.Path.Combine(Windows.ApplicationModel.Package.Current.InstalledLocation.Path, "x86");
                var fromPackage = new List<string>();
                process.ModuleSource = name =>
                {
                    var bytes = ReadGameModule(folderPath, name);
                    if (bytes != null) return bytes;
                    bytes = ReadPackagedModule(packaged, name);
                    if (bytes != null) fromPackage.Add(name);
                    return bytes;
                };

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
                    if (result.Ok)
                    {
                        result = process.Call(image.EntryPoint, out var exitCode, BlockBudget);
                        lines.Add("x86.run=" + result + (result.Ok ? " (entry returned " + exitCode + ")" : ""));
                    }
                }
                catch (Exception error)
                {
                    // Whatever broke, the report below still says where the game was.
                    lines.Add("x86.failed=" + Flat(error));
                    result = new GuestRunResult(GuestStop.HostError, detail: error.ToString());
                }

                lines.Add("x86.eip=0x" + process.Cpu.Eip.ToString("X8") + " " + process.Cpu);
                lines.Add("x86.blocks=" + (process.Jit != null
                    ? process.Jit.BlocksCompiled + " compiled, " + process.Jit.BlocksExecuted + " run, " +
                      process.Jit.InterpreterFallbacks + " interpreted"
                    : "interpreter"));
                lines.Add("x86.recent=" + string.Join(" ", process.RecentImports));
                if (process.JitRefusal != null) lines.Add("x86.jit.refused=" + process.JitRefusal);
                if (kernel.ProbedAbsent.Count > 0)
                    lines.Add("x86.probed-absent=" + string.Join(",", kernel.ProbedAbsent.Distinct()));
                if (kernel.FilesNotFound.Count > 0)
                    lines.Add("x86.files-not-found=" + string.Join(",", kernel.FilesNotFound.Distinct().Take(LogLines)));
                foreach (var text in guestLog) lines.Add("x86.log=" + text);
                lines.Add("x86.threads=" + string.Join(",", process.Threads.Select(t => t.ToString())));
                lines.Add("x86.window=0x" + kernel.InputWindow.ToString("X") + " dispatched=" + kernel.MessagesDispatched);
                lines.Add("x86.d3d9=" + X86Direct3D9.Note + " lockheap=" + (X86Direct3D9.LockBytes >> 20) + "MB proxies=" + com.ProxyCount);
                lines.Add("x86.xaudio=" + XAudio27Route.Note + " callbacks=" + xaudio.CallbacksDelivered +
                          " dropped=" + xaudio.CallbacksDropped + " effect-chains-dropped=" + xaudio.EffectChainsDropped);
                if (com.MissingClasses.Count > 0)
                    lines.Add("x86.com.missing-classes=" + string.Join(",", com.MissingClasses));
                foreach (var call in com.Calls.OrderByDescending(pair => pair.Value).Take(40))
                    lines.Add("x86.com " + call.Value + "x " + call.Key);
                lines.Add("x86.seconds=" + started.Elapsed.TotalSeconds.ToString("0.0"));

                await WriteImportsAsync(process, kernel);
            }
            return result.Stop == GuestStop.Exited || result.Stop == GuestStop.Returned;
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
