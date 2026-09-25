using System;
using System.Collections.Generic;
using System.Text;
using Nativra.X86.Cpu;
using Xunit;

namespace Nativra.X86.Tests
{
    /// <summary>
    /// Replays every hardware-oracle case through the interpreter and checks it
    /// reaches the state a real 32-bit CPU reached. This is the correctness net
    /// under the whole layer: if the interpreter matches silicon here, it can be
    /// trusted as the reference the JIT is diffed against.
    /// </summary>
    public sealed class InterpreterOracleTests
    {
        public static IEnumerable<object[]> Snippets()
        {
            var path = OracleVectors.FindFile();
            if (path == null) yield break;
            foreach (var snippet in OracleVectors.Load(path))
                yield return new object[] { snippet };
        }

        [SkippableTheory]
        [MemberData(nameof(Snippets))]
        public void MatchesHardware(OracleSnippet snippet)
        {
            var failures = new List<string>();
            for (var i = 0; i < snippet.Cases.Count; i++)
            {
                var problem = Replay(snippet, snippet.Cases[i]);
                if (problem != null) failures.Add($"case {i}: {problem}");
            }
            Assert.True(failures.Count == 0, snippet.Name + "\n" + string.Join("\n", failures));
        }

        [Fact]
        public void OracleVectorsArePresent()
        {
            Skip.If(OracleVectors.FindFile() == null,
                "oracle vectors not generated (run the harness); the CI job always has them");
        }

        internal static string Replay(OracleSnippet snippet, OracleCase test)
        {
            using (var memory = new GuestMemory())
            {
                memory.Map(OracleVectors.DataBase, 256);
                memory.Map(OracleVectors.StackTop - 0x1000, 0x2000);
                memory.Map(OracleVectors.CodeBase, 0x1000);

                var cpu = new CpuState();
                var cpuMem = memory;
                Array.Copy(test.RegIn, cpu.R, 8);
                cpu.EFlags = test.FlagsIn | Flag.Fixed;
                cpu.Eip = OracleVectors.CodeBase;

                var interp = new Interpreter(cpu, memory);
                interp.Fpu.ReadFxsave(Pad(test.FxIn, 512));
                cpuMem.WriteBytes(OracleVectors.DataBase, test.DataIn);
                cpuMem.WriteBytes(OracleVectors.StackTop - OracleVectors.StackWindow, test.StackIn);
                cpuMem.WriteBytes(OracleVectors.CodeBase, snippet.Code);

                var end = OracleVectors.CodeBase + (uint)snippet.Code.Length;
                try
                {
                    for (var steps = 0; cpu.Eip < end; steps++)
                    {
                        if (steps > 100000) return "did not terminate";
                        interp.Step();
                    }
                }
                catch (GuestException e)
                {
                    return "guest exception 0x" + e.Code.ToString("X8");
                }

                return Compare(snippet, test, cpu, interp, memory);
            }
        }

        internal static string Compare(OracleSnippet snippet, OracleCase test, CpuState cpu, Interpreter interp, GuestMemory memory)
        {
            var problems = new StringBuilder();
            for (var r = 0; r < 8; r++)
                if (cpu.R[r] != test.RegOut[r])
                    problems.Append($" {RegName(r)}={cpu.R[r]:X8} want {test.RegOut[r]:X8};");

            var got = cpu.EFlags & 0x8D5 & ~snippet.IgnoreFlags;
            var want = test.FlagsOut & 0x8D5 & ~snippet.IgnoreFlags;
            if (got != want) problems.Append($" flags={cpu.EFlags & 0x8D5:X3} want {test.FlagsOut & 0x8D5:X3} (ignore {snippet.IgnoreFlags:X3});");

            var fx = new byte[512];
            interp.Fpu.WriteFxsave(fx);
            var fxProblem = CompareFx(fx, test.FxOut);
            if (fxProblem != null) problems.Append(" fx:" + fxProblem);

            var data = memory.ReadBytes(OracleVectors.DataBase, test.DataOut.Length);
            for (var i = 0; i < data.Length; i++)
                if (data[i] != test.DataOut[i]) { problems.Append($" data[{i}]={data[i]:X2} want {test.DataOut[i]:X2};"); break; }

            var stack = memory.ReadBytes(OracleVectors.StackTop - OracleVectors.StackWindow, test.StackOut.Length);
            for (var i = 0; i < stack.Length; i++)
                if (stack[i] != test.StackOut[i]) { problems.Append($" stack[{i}]={stack[i]:X2} want {test.StackOut[i]:X2};"); break; }

            return problems.Length == 0 ? null : problems.ToString();
        }

        /// <summary>
        /// Compares the guest-visible FXSAVE fields, skipping the ones no
        /// software interpreter reproduces bit-for-bit and no correct guest
        /// depends on:
        ///  * the x87 instruction/data pointers and opcode (offsets 6-23) and
        ///    the hardware MXCSR mask (28-31), which are environment, not data;
        ///  * the sticky FP exception flags — FSW bits 0-7 (offset 2) and the
        ///    low six MXCSR bits (offset 24) — which depend on the exact
        ///    rounding of every prior operation;
        ///  * FSW bit 9 (C1), the "rounded up" / stack-fault-direction bit.
        /// The JIT runs these instructions on real silicon, so it reproduces
        /// all of them exactly; the interpreter is only the value reference.
        /// </summary>
        private static string CompareFx(byte[] got, byte[] want)
        {
            for (var i = 0; i < OracleVectors.FxBytes; i++)
            {
                if (i >= 6 && i < 24) continue;
                if (i == 2) continue;                 // FSW exception + summary bits
                if (i >= 28 && i < 32) continue;      // MXCSR mask (hardware-filled)
                byte mask = 0xFF;
                if (i == 3) mask = 0xFD;              // FSW high byte, minus C1
                else if (i == 24) mask = 0xC0;        // MXCSR low byte, minus the six exception flags
                if (((got[i] ^ want[i]) & mask) != 0)
                {
                    var region = i < 32 ? "hdr" : i < 160 ? "st" + ((i - 32) / 16) : "xmm" + ((i - 160) / 16);
                    return $"{region} byte {i}={got[i]:X2} want {want[i]:X2}";
                }
            }
            return null;
        }

        private static byte[] Pad(byte[] src, int length)
        {
            if (src.Length >= length) return src;
            var padded = new byte[length];
            Array.Copy(src, padded, src.Length);
            return padded;
        }

        private static string RegName(int r) =>
            new[] { "eax", "ecx", "edx", "ebx", "esp", "ebp", "esi", "edi" }[r];
    }
}
