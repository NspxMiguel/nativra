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

        public static async Task RunAsync()
        {
            var lines = new List<string> { "at=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") };
            try
            {
                var local = ApplicationData.Current.LocalFolder;
                var folder = await local.TryGetItemAsync(Folder) as StorageFolder;
                if (folder == null)
                {
                    lines.Add("state=no win32 folder");
                    await WriteAsync(lines);
                    return;
                }

                var imports = new SystemImports();

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
                            $"unwind={image.ExceptionsRegistered} tls={image.TlsCallbacksRun}");
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
                }
            }
            catch (Exception error)
            {
                lines.Add("probe failed: " + error.GetType().Name + ": " + error.Message);
            }
            await WriteAsync(lines);
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate ulong TicksDelegate();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int DllMainDelegate(IntPtr instance, uint reason, IntPtr reserved);

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
