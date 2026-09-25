using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;

namespace Kiosk
{
    /// <summary>
    /// What storage the app can reach beyond its own folder: drive letters,
    /// the removable devices a USB stick shows up under, and whether a file
    /// can be written there. Runs when probe-drives.txt is in LocalState and
    /// writes drives.txt beside it. The console's own disk fills up; this is
    /// how a USB drive is measured before games are put on one.
    /// </summary>
    internal static class DriveProbe
    {
        [DllImport("api-ms-win-core-file-fromapp-l1-1-0.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileFromAppW(
            string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("api-ms-win-core-handle-l1-1-0.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("api-ms-win-core-file-fromapp-l1-1-0.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool DeleteFileFromAppW(string name);

        private const uint GenericWrite = 0x40000000;
        private const uint CreateAlways = 2;
        private static readonly IntPtr Invalid = new IntPtr(-1);

        public static async Task RunAsync()
        {
            var local = ApplicationData.Current.LocalFolder;
            if (await local.TryGetItemAsync("probe-drives.txt") == null) return;

            var report = new StringBuilder();
            report.AppendLine(DateTime.UtcNow.ToString("o"));

            try
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    string detail;
                    try
                    {
                        detail = $"{drive.DriveType} {drive.DriveFormat} free {drive.AvailableFreeSpace} of {drive.TotalSize}";
                    }
                    catch (Exception error)
                    {
                        detail = error.GetType().Name;
                    }
                    report.AppendLine($"driveinfo {drive.Name} {detail}");
                }
            }
            catch (Exception error)
            {
                report.AppendLine("driveinfo failed " + error.GetType().Name);
            }

            for (var letter = 'C'; letter <= 'Z'; letter++)
            {
                var root = letter + @":\";
                var line = new StringBuilder("letter " + root);
                try
                {
                    line.Append(" dirs " + Directory.GetDirectories(root).Length);
                }
                catch (Exception error)
                {
                    line.Append(" dirs " + error.GetType().Name);
                }
                try
                {
                    var folder = await StorageFolder.GetFolderFromPathAsync(root);
                    var free = await Steam.SteamDownload.FreeBytesAsync(folder);
                    line.Append(" storage ok free " + (free?.ToString() ?? "?"));
                }
                catch (Exception error)
                {
                    line.Append(" storage " + error.GetType().Name);
                }
                var test = root + "nativra-probe.bin";
                var handle = CreateFileFromAppW(test, GenericWrite, 0, IntPtr.Zero, CreateAlways, 0, IntPtr.Zero);
                if (handle != Invalid && handle != IntPtr.Zero)
                {
                    CloseHandle(handle);
                    DeleteFileFromAppW(test);
                    line.Append(" fromapp write ok");
                }
                else
                {
                    line.Append(" fromapp error " + Marshal.GetLastWin32Error());
                }
                report.AppendLine(line.ToString());
            }

            try
            {
                var removable = KnownFolders.RemovableDevices;
                foreach (var device in await removable.GetFoldersAsync())
                {
                    var line = new StringBuilder($"removable {device.Name} path {device.Path}");
                    var free = await Steam.SteamDownload.FreeBytesAsync(device);
                    line.Append(" free " + (free?.ToString() ?? "?"));
                    try
                    {
                        var file = await device.CreateFileAsync("nativra-probe.bin", CreationCollisionOption.ReplaceExisting);
                        await FileIO.WriteBytesAsync(file, new byte[1024 * 1024]);
                        await file.DeleteAsync();
                        line.Append(" write ok");
                    }
                    catch (Exception error)
                    {
                        line.Append(" write " + error.GetType().Name + " " + error.Message);
                    }
                    try
                    {
                        var folder = await device.CreateFolderAsync("nativra-probe", CreationCollisionOption.OpenIfExists);
                        var odd = await folder.CreateFileAsync("probe.pak", CreationCollisionOption.ReplaceExisting);
                        await FileIO.WriteBytesAsync(odd, new byte[16]);
                        await folder.DeleteAsync();
                        line.Append(" undeclared type ok");
                    }
                    catch (Exception error)
                    {
                        line.Append(" undeclared type " + error.GetType().Name);
                    }
                    report.AppendLine(line.ToString());
                }
            }
            catch (Exception error)
            {
                report.AppendLine("removable failed " + error.GetType().Name + " " + error.Message);
            }

            try
            {
                var output = await local.CreateFileAsync("drives.txt", CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(output, report.ToString());
            }
            catch
            {
            }
        }
    }
}
