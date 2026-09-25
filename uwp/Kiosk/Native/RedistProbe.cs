using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Storage;

namespace Kiosk.Native
{
    /// <summary>
    /// Reports whether the console can load the DirectX libraries that
    /// SDK-era games reach for, so the redistributable shims
    /// (native/directx-redist) are only leaned on where they are actually
    /// needed.
    ///
    /// The first question is the shader compiler: Hades imports
    /// D3DCOMPILER_47.dll and compiles HLSL at runtime. That DLL is a Windows 10
    /// system module, so the console most likely already has it — but "most
    /// likely" is not "measured", and this writes the measurement to
    /// redist-probe.txt next to the other probe reports. The 2.9 audio engine
    /// and the older redistributable names are checked the same way.
    ///
    /// Reads only; it never replaces anything. Wiring the shims themselves (the
    /// XAudio 2.7 CoCreateInstance route and the packaged DLL names) is a
    /// separate, single-line step documented in docs/progress.
    /// </summary>
    public static class RedistProbe
    {
        [DllImport("api-ms-win-core-libraryloader-l2-1-0.dll", SetLastError = true,
            CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadPackagedLibrary(string name, uint reserved);

        [DllImport("api-ms-win-core-libraryloader-l1-2-0.dll", SetLastError = true,
            CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string name);

        private const string ReportName = "redist-probe.txt";

        /// <summary>The libraries a redist-dependent game asks for, most-wanted first.</summary>
        private static readonly string[] Libraries =
        {
            "d3dcompiler_47.dll", "d3dcompiler_43.dll",
            "xaudio2_9.dll", "xaudio2_8.dll",
            "d3dx9_43.dll", "d3dx11_43.dll", "d3dx10_43.dll",
            "X3DAudio1_7.dll", "XAPOFX1_5.dll",
        };

        /// <summary>Runs the probe once, off the caller's thread; failures cost the report, not the game.</summary>
        public static void Run()
        {
            var __ = Task.Run(async () =>
            {
                try { await ProbeAsync(); }
                catch { /* a missing report is a missing report */ }
            });
        }

        private static async Task ProbeAsync()
        {
            var lines = new List<string> { "at=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") };
            foreach (var name in Libraries)
            {
                var already = GetModuleHandleW(name);
                var loaded = already != IntPtr.Zero ? already : TryLoad(name);
                lines.Add(name + "=" +
                    (already != IntPtr.Zero ? "already-loaded"
                     : loaded != IntPtr.Zero ? "loadable 0x" + loaded.ToInt64().ToString("X")
                     : "missing (err " + Marshal.GetLastWin32Error() + ")"));
            }

            try
            {
                var folder = ApplicationData.Current.LocalFolder;
                var draft = await folder.CreateFileAsync(ReportName + ".new",
                    CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteLinesAsync(draft, lines);
                var existing = await folder.TryGetItemAsync(ReportName) as StorageFile;
                if (existing == null) await draft.RenameAsync(ReportName, NameCollisionOption.ReplaceExisting);
                else await draft.MoveAndReplaceAsync(existing);
            }
            catch
            {
                // Losing the report loses the measurement, not the app.
            }
        }

        private static IntPtr TryLoad(string name)
        {
            try { return LoadPackagedLibrary(name, 0); }
            catch { return IntPtr.Zero; }
        }
    }
}
