using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Kiosk.Native;

namespace Kiosk.Native
{
    // The loader's only outside dependency, reduced to what mapping needs.
    internal static class ThreadTls
    {
        public static string LastError = "";
        private static readonly List<byte[]> templates = new List<byte[]>();
        public static int Remember(byte[] template, int size) { templates.Add(template); return templates.Count - 1; }
        public static void Restore() { }
        public static bool Adopt() => true;
    }
}

internal static class Program
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Counters
    {
        public uint cb, PageFaultCount;
        public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
            QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetProcessMemoryInfo(IntPtr process, out Counters counters, uint size);

    private static Counters Measure()
    {
        GetProcessMemoryInfo(System.Diagnostics.Process.GetCurrentProcess().Handle, out var c, (uint)Marshal.SizeOf<Counters>());
        return c;
    }

    private static long Mb(UIntPtr v) => (long)v.ToUInt64() >> 20;

    // What LoaderStubs/NativeProbe did before: the whole file in a native
    // buffer, then a managed copy of it, handed to PeImage.
    private static byte[] ReadWholeFile(string path)
    {
        var size = new FileInfo(path).Length;
        var buffer = Marshal.AllocHGlobal(new IntPtr(size));
        try
        {
            using (var stream = File.OpenRead(path))
            {
                var chunk = new byte[1 << 20];
                long done = 0;
                int got;
                while ((got = stream.Read(chunk, 0, chunk.Length)) > 0)
                {
                    Marshal.Copy(chunk, 0, buffer + (int)done, got);
                    done += got;
                }
            }
            var bytes = new byte[size];
            Marshal.Copy(buffer, bytes, 0, (int)size);
            return bytes;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0] : "new";
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        // The biggest 64-bit DLLs on the machine stand in for a game's
        // UnityPlayer/GameAssembly: large images with real relocations.
        var files = Directory.EnumerateFiles(system, "*.dll")
            .Select(f => new FileInfo(f))
            .Where(f => f.Length > 8 << 20)
            .OrderByDescending(f => f.Length)
            .Take(8)
            .ToList();

        var before = Measure();
        long fileBytes = 0, mapped = 0;
        var images = new List<PeImage>();
        var kept = new List<byte[]>();
        foreach (var file in files)
        {
            try
            {
                PeImage image;
                if (mode == "old")
                {
                    var bytes = ReadWholeFile(file.FullName);
                    kept.Add(bytes);   // the old callers held it for the whole load
                    image = PeImage.Load(file.Name, bytes, (m, f) => IntPtr.Zero);
                }
                else
                {
                    using (var source = ImageFile.TryOpen(file.FullName))
                        image = PeImage.Load(file.Name, source, (m, f) => IntPtr.Zero);
                }
                images.Add(image);
                fileBytes += file.Length;
            }
            catch (Exception e)
            {
                Console.WriteLine($"  skip {file.Name}: {e.GetType().Name} {e.Message}");
            }
        }
        mapped = PeImage.MappedBytes;
        var loaded = Measure();
        kept.Clear();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var settled = Measure();

        Console.WriteLine($"mode={mode} images={images.Count} file={fileBytes >> 20} MB mapped={mapped >> 20} MB");
        Console.WriteLine($"  private: before={Mb(before.PrivateUsage)} MB loaded={Mb(loaded.PrivateUsage)} MB after-gc={Mb(settled.PrivateUsage)} MB");
        Console.WriteLine($"  peak commit={Mb(settled.PeakPagefileUsage)} MB (over baseline: {Mb(settled.PeakPagefileUsage) - Mb(before.PrivateUsage)} MB)");
        Console.WriteLine($"RESULT {mode} peak_over_baseline_mb={Mb(settled.PeakPagefileUsage) - Mb(before.PrivateUsage)} mapped_mb={mapped >> 20}");
        foreach (var image in images) image.Dispose();
        return images.Count > 0 ? 0 : 1;
    }
}
