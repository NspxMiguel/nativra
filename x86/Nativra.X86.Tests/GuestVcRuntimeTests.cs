using System;
using System.IO;
using System.Linq;
using Nativra.X86.Cpu;
using Nativra.X86.Loader;
using Xunit;
using Xunit.Abstractions;

namespace Nativra.X86.Tests
{
    /// <summary>
    /// The Visual C++ runtimes (msvcr100/110/120, msvcp*, vcruntime140...) are
    /// carried by the app package and mapped as real images: the game's own
    /// copy first, then the package's, never the host's msvcrt handlers.
    /// </summary>
    public sealed class GuestVcRuntimeTests
    {
        private readonly ITestOutputHelper output;

        public GuestVcRuntimeTests(ITestOutputHelper output) => this.output = output;

        private static GuestProcess NewProcess()
        {
            var p = new GuestProcess(new GuestMemory(), useJit: false);
            new GuestKernel(p).Install();
            return p;
        }

        [Fact]
        public void APackagedRuntimeIsMappedAsARealImageNotAHostSentinel()
        {
            var p = NewProcess();
            var runtime = TestPe32.Minimal(dll: true, dllMain: true);
            p.ModuleSource = name => name == "msvcr120.dll" ? runtime : null;

            var game = p.LoadExecutable("game.exe", TestPe32.Minimal(importModule: "msvcr120.dll", importName: "Start"));

            var image = p.FindModule("msvcr120.dll");
            Assert.NotNull(image);
            var bound = game.Imports.Single(i => i.Module == "msvcr120.dll" && i.Function == "Start").Bound;
            Assert.False(GuestImports.InRegion(bound));
            Assert.Equal(image.Export("Start"), bound);
        }

        [Fact]
        public void TheGamesOwnCopyIsUsedBeforeThePackagedOne()
        {
            var p = NewProcess();
            var carried = TestPe32.Minimal(dll: true, dllMain: true);
            var packagedAsked = false;
            p.ModuleSearch = name => name == "msvcr120.dll" ? carried : null;
            p.ModuleSource = name => { packagedAsked |= name == "msvcr120.dll"; return null; };

            p.LoadExecutable("game.exe", TestPe32.Minimal(importModule: "msvcr120.dll", importName: "Start"));

            Assert.NotNull(p.FindModule("msvcr120.dll"));
            Assert.False(packagedAsked);
        }

        [Fact]
        public void ThePackagedRuntimeIsUsedWhenTheGameCarriesNone()
        {
            var p = NewProcess();
            var packaged = TestPe32.Minimal(dll: true, dllMain: true);
            p.ModuleSearch = name => null;
            p.ModuleSource = name => name == "msvcr110.dll" ? packaged : null;

            p.LoadExecutable("game.exe", TestPe32.Minimal(importModule: "msvcr110.dll", importName: "Start"));

            Assert.NotNull(p.FindModule("msvcr110.dll"));
        }

        [Fact]
        public void SetHandleCountEchoesTheCountAsMsvcr100ExpectsAtStartUp()
        {
            var p = NewProcess();
            var result = p.Call(p.Imports.Bind("kernel32.dll", "SetHandleCount", -1), out var eax, 1_000_000, 20);
            Assert.True(result.Ok);
            Assert.Equal(20u, eax);
        }

        [Fact]
        public void TheUniversalCrtApiSetsFoldOntoUcrtbase()
        {
            Assert.Equal("ucrtbase.dll", GuestImports.Canonical("api-ms-win-crt-stdio-l1-1-0.dll"));
            Assert.Equal("msvcr120.dll", GuestImports.Canonical("MSVCR120.dll"));
        }

        // The real redistributable DLLs, extracted by native/vc-redist/fetch.ps1 (or
        // by hand from Microsoft's installers); never part of the repository.
        // NATIVRA_VC_REDIST names the folder.
        [SkippableTheory]
        [InlineData("msvcr100.dll")]
        [InlineData("msvcp100.dll")]
        [InlineData("msvcr110.dll")]
        [InlineData("msvcp110.dll")]
        [InlineData("msvcr120.dll")]
        [InlineData("msvcp120.dll")]
        [InlineData("vcomp120.dll")]
        public void ARealRuntimeMapsAndItsDllMainSucceeds(string name)
        {
            var folder = Environment.GetEnvironmentVariable("NATIVRA_VC_REDIST");
            Skip.If(string.IsNullOrEmpty(folder) || !File.Exists(Path.Combine(folder ?? "", name)),
                "set NATIVRA_VC_REDIST to a folder holding the extracted x86 VC++ redistributable DLLs");

            var p = NewProcess();
            p.ModuleSource = n => File.Exists(Path.Combine(folder, n)) ? File.ReadAllBytes(Path.Combine(folder, n)) : null;
            p.LoadExecutable("game.exe", TestPe32.Minimal());
            var first = p.Images.Count;
            Assert.NotNull(p.LoadModule(name));
            var result = p.AttachModulesFrom(first);

            var unserved = p.Images.SelectMany(i => i.Imports)
                .Where(i => GuestImports.InRegion(i.Bound) &&
                            !(p.Imports.TryResolve(i.Bound, out var g) && g.Handler != null))
                .Select(i => i.ToString()).Distinct().OrderBy(s => s).ToList();
            output.WriteLine(name + " unserved(" + unserved.Count + ")=" + string.Join(",", unserved));
            Assert.True(result.Ok, name + " stopped as " + result.Stop + " at 0x" + result.FaultAddress.ToString("X8") + " after " + string.Join(" ", p.RecentImports));
        }

        [SkippableFact]
        public void RealMsvcr120InitializersAndCommonEntryPointsRun()
        {
            var folder = Environment.GetEnvironmentVariable("NATIVRA_VC_REDIST");
            Skip.If(string.IsNullOrEmpty(folder) || !File.Exists(Path.Combine(folder ?? "", "msvcr120.dll")),
                "set NATIVRA_VC_REDIST to the extracted x86 VC++ redistributable DLLs");

            var p = NewProcess();
            p.ModuleSource = n => File.Exists(Path.Combine(folder, n)) ? File.ReadAllBytes(Path.Combine(folder, n)) : null;
            p.LoadExecutable("game.exe", TestPe32.Minimal());
            var first = p.Images.Count;
            var runtime = p.LoadModule("msvcr120.dll");
            Assert.NotNull(runtime);
            Assert.True(p.AttachModulesFrom(first).Ok);

            var empty = new GuestHeap(p.Memory, 0x30000000, 0x10000000).Alloc(128, zero: true);
            void Run(string name, params uint[] args)
            {
                var address = runtime.Export(name);
                Assert.NotEqual(0u, address);
                var result = p.Call(address, out _, 50_000_000, args);
                Assert.True(result.Ok, name + " stopped as " + result + " after " + string.Join(" ", p.RecentImports));
            }
            Run("_initterm_e", empty, empty);
            Run("__crtGetShowWindowMode");
            Run("_controlfp_s", empty, 0, 0);
            Run("setlocale", 0, 0);
            p.Memory.WriteAnsi(empty, "vc runtime smoke test\n");
            Run("printf", empty);
            Run("_cexit");
        }
    }
}
