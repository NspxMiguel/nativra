using System;
using System.IO;
using System.Text;
using Nativra.X86.Cpu;
using Nativra.X86.Loader;
using Xunit;

namespace Nativra.X86.Tests
{
    /// <summary>
    /// Runs one real program twice, under the JIT and under the reference
    /// interpreter, block by block, and names the first block after which
    /// their registers differ: the JIT bug a whole-program failure only
    /// hints at. Opt-in: NATIVRA_LOCKSTEP_EXE names a statically linked
    /// program (the harness's crt_static.exe).
    /// </summary>
    public sealed class JitLockstepTests
    {
        private static GuestProcess Start(string path, bool jit, out uint halt)
        {
            // Threads switch only where the program waits or yields, never on a
            // block count the two engines would reach at different places.
            var p = new GuestProcess(new GuestMemory(native: true), useJit: jit) { SliceBlocks = int.MaxValue };
            // The same surroundings as GuestProgramTests, so the program's own
            // checks pass and it ends with its success code.
            var work = Path.Combine(Path.GetTempPath(), "nativra-lockstep-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            var kernel = new GuestKernel(p)
            {
                DeterministicTime = true,
                ExePath = "C:\\guest\\" + Path.GetFileName(path),
                Files = new HostFolderFiles("C:\\guest", work),
            };
            kernel.SetCommandLine(Path.GetFileName(path));
            kernel.Install();
            var image = p.LoadExecutable(Path.GetFileName(path), File.ReadAllBytes(path));
            Assert.True(p.InitializeModules(20_000_000).Ok);
            // The entry point called as the loader would: returning lands on the halt address.
            p.Cpu.Esp -= 4;
            p.Memory.Write32(p.Cpu.Esp, GuestProcess.HaltAddress);
            p.Cpu.Eip = image.EntryPoint;
            halt = GuestProcess.HaltAddress;
            return p;
        }

        [Fact]
        public void JitAndInterpreterAgreeBlockByBlock()
        {
            var path = Environment.GetEnvironmentVariable("NATIVRA_LOCKSTEP_EXE");
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

            using (var jit = Start(path, true, out var halt))
            using (var reference = Start(path, false, out _))
            {
                Assert.True(jit.UsesJit);
                for (long block = 0; block < 5_000_000; block++)
                {
                    var from = jit.Cpu.Eip;
                    if (from == halt) return;
                    var a = jit.Run(halt, 1);
                    if (a.Stop == GuestStop.Exited)
                    {
                        // ExitProcess ends the comparison: the reference must get there too, with the same code.
                        var end = reference.Run(halt, 100_000_000);
                        Assert.True(end.Stop == GuestStop.Exited && end.ExitCode == a.ExitCode,
                            $"block {block}: the JIT run exited with {a.ExitCode}, the interpreter's ended {end}");
                        Assert.Equal(42u, a.ExitCode);   // crt_test's "every check passed"
                        return;
                    }
                    if (a.Stop != GuestStop.Budget && a.Stop != GuestStop.Returned)
                        Assert.Fail($"block {block} at 0x{from:X8}: the JIT run stopped: {a} ({jit.Cpu})");

                    // The interpreter catches up to wherever the block ended. A
                    // block can branch back into its own middle, so the end
                    // address may come up more than once before the JIT's
                    // state does: only a visit that matches settles it.
                    var target = jit.Cpu.Eip;
                    string diff = null, seen = null;
                    for (var steps = 0; ; steps++)
                    {
                        if (reference.Cpu.Eip == target)
                        {
                            diff = Compare(jit.Cpu, reference.Cpu);
                            if (diff == null) break;
                            seen = reference.Cpu.ToString();
                        }
                        var b = reference.Run(halt, 1);
                        if (steps > 100_000 || (b.Stop != GuestStop.Budget && b.Stop != GuestStop.Returned))
                        {
                            var code = Describe(jit, from);
                            Assert.Fail(diff != null
                                ? $"block {block} starting at 0x{from:X8} diverged: {diff}\n  jit: {jit.Cpu}\n  ref: {seen}\n  code: {code}"
                                : $"block {block} at 0x{from:X8}: the interpreter never reached 0x{target:X8} ({b}; {reference.Cpu})\n  code: {code}");
                        }
                    }
                }
            }
        }

        /// <summary>The block's first bytes, or the import it is the sentinel of.</summary>
        private static string Describe(GuestProcess p, uint address)
        {
            if (p.Imports.TryResolve(address, out var import))
                return $"import {import} (recent: {string.Join(" ", p.RecentImports)})";
            try { return BitConverter.ToString(p.Memory.ReadBytes(address, 32)); }
            catch (GuestFaultException) { return "unmapped"; }
        }

        private static string Compare(CpuState a, CpuState b)
        {
            var diff = new StringBuilder();
            string[] names = { "eax", "ecx", "edx", "ebx", "esp", "ebp", "esi", "edi" };
            for (var r = 0; r < 8; r++)
                if (a.R[r] != b.R[r]) diff.Append($"{names[r]} {a.R[r]:X8}/{b.R[r]:X8} ");
            // Flags are not compared on their own: several instructions leave
            // some undefined, and a flag that matters shows up in a register
            // (or a branch, and so an address) soon after.
            return diff.Length == 0 ? null : diff.ToString();
        }
    }
}
