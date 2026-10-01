using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nativra.X86.Cpu;
using Nativra.X86.Loader;

namespace Nativra.X86.Run
{
    /// <summary>
    /// nativra-run [options] &lt;folder&gt; &lt;exe&gt; [arguments...]
    ///
    /// The folder is the game's own (it holds the exe and the DLLs it carries);
    /// the guest sees it as C:\game. Options:
    ///   --dlls DIR      more 32-bit DLLs to link when the game does not carry them
    ///                   (the redistributable C runtime the package ships)
    ///   --interp        reference interpreter only, even where the JIT could run
    ///   --budget N      blocks to run before giving up (default 400 million)
    ///   --imports FILE  write every import with its state (linked/served/MISSING)
    ///   --log N         guest log lines to keep (default 200)
    ///   --trace FILE    every served import call, with arguments and result
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            string dlls = null, importsFile = null, traceFile = null;
            var interp = false;
            long budget = 400_000_000, dllBudget = 0;
            var logLines = 200;
            var rest = new List<string>();
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--dlls": dlls = args[++i]; break;
                    case "--interp": interp = true; break;
                    case "--budget": budget = long.Parse(args[++i]); break;
                    case "--imports": importsFile = args[++i]; break;
                    case "--log": logLines = int.Parse(args[++i]); break;
                    case "--trace": traceFile = args[++i]; break;
                    case "--dll-budget": dllBudget = long.Parse(args[++i]); break;
                    default: rest.Add(args[i]); break;
                }
            }
            if (rest.Count < 2)
            {
                Console.Error.WriteLine("usage: nativra-run [--dlls DIR] [--interp] [--budget N] [--imports FILE] <folder> <exe> [arguments...]");
                return 2;
            }
            var folder = Path.GetFullPath(rest[0]);
            var exe = rest[1];
            var commandLine = "\"C:\\game\\" + exe + "\"" + string.Concat(rest.Skip(2).Select(a => " " + a));

            var lines = new List<string>();
            var started = System.Diagnostics.Stopwatch.StartNew();
            GuestRunResult result = null;
            using (var memory = new GuestMemory(native: true))
            using (var process = new GuestProcess(memory, useJit: !interp))
            {
                StreamWriter trace = null;
                if (traceFile != null)
                {
                    trace = new StreamWriter(traceFile);
                    process.CallTrace = trace.WriteLine;
                }
                var kernel = new GuestKernel(process);
                if (dllBudget > 0) kernel.LoadLibraryBudget = dllBudget;
                kernel.ExePath = "C:\\game\\" + exe;
                kernel.SetCommandLine(commandLine);
                var guestLog = new List<string>();
                kernel.Log = text =>
                {
                    if (guestLog.Count < logLines) guestLog.Add(text);
                };
                var profile = Path.Combine(Path.GetTempPath(), "nativra-run-profile");
                Directory.CreateDirectory(profile);
                kernel.ProfileRoot = profile;
                kernel.Files = new HostFolderFiles("C:\\game", folder);
                kernel.Install();
                var com = new GuestCom(process, kernel);

                process.ModuleSource = name =>
                    Read(Path.Combine(folder, name)) ?? Read(Path.Combine(folder, "bin", name)) ??
                    (dlls != null ? Read(Path.Combine(dlls, name)) : null);

                try
                {
                    var image = process.LoadExecutable(exe, File.ReadAllBytes(Path.Combine(folder, exe)));
                    lines.Add("x86.image=" + exe + " base=0x" + image.BaseAddress.ToString("X8") +
                              " entry=0x" + image.EntryPoint.ToString("X8"));
                    lines.Add("x86.jit=" + (process.UsesJit ? "on" : "off (interpreter)"));
                    lines.Add("x86.modules=" + string.Join(",", process.Images.Select(i => i.Name)));
                    foreach (var group in process.Images.SelectMany(i => i.Imports)
                                 .Where(i => GuestImports.InRegion(i.Bound)).GroupBy(i => i.Module).OrderBy(g => g.Key))
                    {
                        var served = group.Count(i => process.Imports.TryResolve(i.Bound, out var g) && g.Handler != null);
                        lines.Add("x86.imports." + group.Key + "=" + group.Count() + " (" + served + " served)");
                    }
                    result = process.InitializeModules(budget);
                    lines.Add("x86.init=" + result);
                    if (result.Ok)
                    {
                        result = process.Call(image.EntryPoint, out var exitCode, budget);
                        lines.Add("x86.run=" + result + (result.Ok ? " (entry returned " + exitCode + ")" : ""));
                    }
                }
                catch (Exception error)
                {
                    lines.Add("x86.failed=" + error.ToString().Replace("\r", "").Replace("\n", " | "));
                }

                lines.Add("x86.eip=0x" + process.Cpu.Eip.ToString("X8") + " " + process.Cpu);
                lines.Add("x86.modules.end=" + string.Join(",", process.Images.Select(i => i.Name + "@" + i.BaseAddress.ToString("X8"))));
                lines.Add("x86.recent=" + string.Join(" ", process.RecentImports));
                if (kernel.ProbedAbsent.Count > 0)
                    lines.Add("x86.probed-absent=" + string.Join(",", kernel.ProbedAbsent.Distinct()));
                if (kernel.FilesNotFound.Count > 0)
                    lines.Add("x86.files-not-found=" + string.Join(",", kernel.FilesNotFound.Distinct().Take(80)));
                if (com.MissingClasses.Count > 0)
                    lines.Add("x86.com.missing-classes=" + string.Join(",", com.MissingClasses));
                lines.Add("x86.raised=" + string.Join(",", kernel.ExceptionsRaised.Select(c => c.ToString("X8"))));
                lines.Add("x86.threads=" + string.Join(",", process.Threads.Select(t => t.ToString())));
                foreach (var text in guestLog) lines.Add("x86.log=" + text);
                lines.Add("x86.memory=committed " + (memory.MappedPages * GuestMemory.PageSize / (1024 * 1024)) + " MB (" + memory.MappedPages +
                          " pages), reserved " + (memory.ReservedPages * GuestMemory.PageSize / (1024 * 1024)) + " MB more");
                lines.Add("x86.seconds=" + started.Elapsed.TotalSeconds.ToString("0.0"));
                trace?.Dispose();

                var missing = process.Images.SelectMany(i => i.Imports)
                    .Where(i => GuestImports.InRegion(i.Bound) && !(process.Imports.TryResolve(i.Bound, out var g) && g.Handler != null))
                    .Select(i => i.ToString()).Distinct().OrderBy(s => s).ToList();
                lines.Add("x86.unserved(" + missing.Count + ")=" + string.Join(",", missing));

                if (importsFile != null)
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
                    File.WriteAllLines(importsFile, report);
                }
            }
            foreach (var line in lines) Console.WriteLine(line);
            return result != null && (result.Stop == GuestStop.Exited || result.Stop == GuestStop.Returned) ? 0 : 1;
        }

        private static byte[] Read(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;
    }
}
