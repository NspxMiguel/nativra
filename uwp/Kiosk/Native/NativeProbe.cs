using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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
                foreach (var file in await folder.GetFilesAsync())
                {
                    if (!file.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                        !file.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var bytes = (await FileIO.ReadBufferAsync(file)).ToArray();
                    try
                    {
                        var image = PeImage.Load(file.Name, bytes, imports.Resolve);
                        imports.Add(image);
                        lines.Add(
                            $"{file.Name}: mapped at 0x{image.BaseAddress.ToInt64():X} " +
                            $"exports={image.ExportCount} unresolved={image.Unresolved.Count}");
                    }
                    catch (Exception error)
                    {
                        lines.Add($"{file.Name}: FAILED {error.GetType().Name}: {error.Message}");
                    }
                }

                lines.Add($"resolved.system={imports.FromSystem} resolved.images={imports.FromImages}");
                lines.Add("modules.missing=" + string.Join(",", imports.MissingModules));
                lines.Add($"functions.missing={imports.MissingFunctions.Count}");
                // The list itself is the work queue for the shim.
                var take = Math.Min(imports.MissingFunctions.Count, 400);
                for (var i = 0; i < take; i++) lines.Add("  " + imports.MissingFunctions[i]);
            }
            catch (Exception error)
            {
                lines.Add("probe failed: " + error.GetType().Name + ": " + error.Message);
            }
            await WriteAsync(lines);
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
