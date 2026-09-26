using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nativra.X86.Cpu;
using Nativra.X86.Loader;
using Xunit;

namespace Nativra.X86.Tests
{
    /// <summary>
    /// Real MSVC programs (x86/harness/guests, built by build-guests.cmd on the
    /// Windows runner) run end to end in the guest: C runtime start-up, C++
    /// exceptions, SEH, stdio files, and exit. The dynamic build maps the
    /// runner's own 32-bit ucrtbase, vcruntime140 and msvcp140 from SysWOW64
    /// into the guest. Without NATIVRA_GUEST_PROGRAMS (a machine that cannot
    /// build them) there is nothing to run.
    /// </summary>
    public sealed class GuestProgramTests
    {
        private static readonly string[] RuntimeDlls = { "ucrtbase.dll", "vcruntime140.dll", "msvcp140.dll" };

        [Theory]
        [InlineData("crt_static.exe")]
        [InlineData("crt_dynamic.exe")]
        public void MsvcProgramRunsToItsExitCode(string exe)
        {
            var folder = Environment.GetEnvironmentVariable("NATIVRA_GUEST_PROGRAMS");
            if (string.IsNullOrEmpty(folder)) return;

            var work = Path.Combine(Path.GetTempPath(), "nativra-guest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            var system32 = Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);

            using (var p = new GuestProcess(new GuestMemory(), useJit: true))
            {
                var kernel = new GuestKernel(p)
                {
                    ExePath = "C:\\guest\\" + exe,
                    Files = new HostFolderFiles("C:\\guest", work),
                };
                var log = new List<string>();
                kernel.Log = log.Add;
                kernel.SetCommandLine(exe);
                kernel.Install();
                p.ModuleSource = name =>
                {
                    if (!RuntimeDlls.Contains(name)) return null;
                    var path = Path.Combine(system32, name);
                    return File.Exists(path) ? File.ReadAllBytes(path) : null;
                };

                var image = p.LoadExecutable(exe, File.ReadAllBytes(Path.Combine(folder, exe)));
                var result = p.InitializeModules(20_000_000);
                if (result.Ok) result = p.Call(image.EntryPoint, out _, 20_000_000);

                var detail = $"{exe}: {result}; eip=0x{p.Cpu.Eip:X8} {p.Cpu}; modules={string.Join(",", p.Images.Select(i => i.Name))}" +
                             $"; raised={string.Join(",", kernel.ExceptionsRaised.Select(c => c.ToString("X8")))}" +
                             $"; probed-absent={string.Join(",", kernel.ProbedAbsent.Distinct())}" +
                             $"; log=[{string.Join(" | ", log)}]" +
                             $"; recent=[{string.Join(" ", p.RecentImports)}]" +
                             $"; code@eip={(p.Memory.IsMapped(p.Cpu.Eip) ? BitConverter.ToString(p.Memory.ReadBytes(p.Cpu.Eip, 16)) : "unmapped")}" +
                             $"; stack={(p.Memory.IsMapped(p.Cpu.Esp) ? string.Join(" ", Enumerable.Range(0, 8).Select(n => p.Memory.Read32(p.Cpu.Esp + (uint)n * 4).ToString("X8"))) : "unmapped")}" +
                             $"; unserved={string.Join(",", p.Images.SelectMany(i => i.Imports).Where(i => GuestImports.InRegion(i.Bound) && !(p.Imports.TryResolve(i.Bound, out var g) && g.Handler != null)).Select(i => i.ToString()).Distinct())}";
                Assert.True(result.Stop == GuestStop.Exited, detail);
                Assert.True(result.ExitCode == 42, detail);
                Assert.Contains("guest passed=31", log);
            }
            try { Directory.Delete(work, true); } catch (IOException) { }
        }
    }
}
