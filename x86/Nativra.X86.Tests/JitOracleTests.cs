using System;
using System.Collections.Generic;
using System.Text;
using Nativra.X86.Cpu;
using Nativra.X86.Jit;
using Xunit;

namespace Nativra.X86.Tests
{
    /// <summary>
    /// Runs every hardware-oracle case through the JIT (with interpreter
    /// fallback) and checks it reaches the state a real 32-bit CPU reached.
    /// Because the JIT pins guest registers to host registers, the flags it
    /// produces are the host processor's own — so here they are compared with
    /// no ignore mask at all, undefined bits included.
    /// </summary>
    public sealed class JitOracleTests
    {
        // Only runnable where a real 4 GB reservation and executable pages are
        // available: the CI windows runner and normal 64-bit desktops.
        private static bool CanJit => IntPtr.Size == 8;

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
            Skip.IfNot(CanJit, "JIT needs a 64-bit host");
            var failures = new List<string>();
            for (var i = 0; i < snippet.Cases.Count; i++)
            {
                var problem = Replay(snippet, snippet.Cases[i]);
                if (problem != null) failures.Add($"case {i}: {problem}");
            }
            Assert.True(failures.Count == 0, snippet.Name + "\n" + string.Join("\n", failures));
        }

        /// <summary>
        /// Snippets the block translator is expected to cover end to end. If the
        /// JIT ever silently regresses to all-interpreter for one of these, this
        /// catches it — the differential test alone would still pass on the
        /// fallback path.
        /// </summary>
        private static readonly HashSet<string> ExpectedFullyJit = new HashSet<string>
        {
            "add_r32", "add_r8", "add_rm_r32", "or_r32", "adc_r32", "sbb_r32", "and_r32",
            "sub_r32", "xor_r32", "cmp_r32", "add_r16", "sub_r16",
            "add_eax_imm", "add_al_imm", "grp1_imm32", "grp1_imm8_sx", "grp1_or", "grp1_and8",
            "test_imm32", "test_r32", "mov_r32", "mov_r8", "mov_imm32",
            "movzx_bl", "movsx_bl", "movzx_wx", "movsx_wx", "lea_sib", "lea_disp",
            "inc_r32", "dec_r32", "neg_r32", "not_r32", "inc_mem8",
            "shl_imm", "shr_imm", "sar_imm", "shl_cl", "shr_cl", "sar_cl", "shl_1",
            "rol_imm", "ror_imm", "rcr_imm",
            "mul_r32", "imul_r32", "imul2", "imul3_imm8", "imul3_imm32", "div_r32", "idiv_r32", "mul_r8",
            "sete", "setl", "cmovz", "cmovs", "bswap",
            "push_pop",
        };

        [SkippableFact]
        public void CoversTheExpectedSubsetWithoutFallback()
        {
            Skip.IfNot(CanJit, "JIT needs a 64-bit host");
            var path = OracleVectors.FindFile();
            Skip.If(path == null, "oracle vectors not generated");

            var notFull = new List<string>();
            foreach (var snippet in OracleVectors.Load(path))
            {
                if (!ExpectedFullyJit.Contains(snippet.Name)) continue;
                using (var memory = new GuestMemory(native: true))
                {
                    memory.Map(OracleVectors.CodeBase, 0x1000);
                    memory.WriteBytes(OracleVectors.CodeBase, snippet.Code);
                    var translator = new BlockTranslator();
                    translator.Translate(memory, OracleVectors.CodeBase,
                        OracleVectors.CodeBase + (uint)snippet.Code.Length);
                    if (!translator.FullyTranslated) notFull.Add(snippet.Name);
                }
            }
            Assert.True(notFull.Count == 0, "fell back to the interpreter for: " + string.Join(", ", notFull));
        }

        private static string Replay(OracleSnippet snippet, OracleCase test)
        {
            using (var memory = new GuestMemory(native: true))
            {
                memory.Map(OracleVectors.DataBase, 256);
                memory.Map(OracleVectors.StackTop - 0x1000, 0x2000);
                memory.Map(OracleVectors.CodeBase, 0x1000);

                var cpu = new CpuState();
                Array.Copy(test.RegIn, cpu.R, 8);
                cpu.EFlags = test.FlagsIn | Flag.Fixed;
                cpu.Eip = OracleVectors.CodeBase;

                using (var jit = new JitEngine(cpu, memory))
                {
                    jit.Interpreter.Fpu.ReadFxsave(Pad(test.FxIn, 512));
                    memory.WriteBytes(OracleVectors.DataBase, test.DataIn);
                    memory.WriteBytes(OracleVectors.StackTop - OracleVectors.StackWindow, test.StackIn);
                    memory.WriteBytes(OracleVectors.CodeBase, snippet.Code);

                    var end = OracleVectors.CodeBase + (uint)snippet.Code.Length;
                    try
                    {
                        if (!jit.RunToStop(end, 100000)) return "did not terminate";
                    }
                    catch (GuestException ex)
                    {
                        return "guest exception 0x" + ex.Code.ToString("X8");
                    }

                    // When the whole snippet was JIT-translated, the flags are
                    // the host CPU's own, so they must match the oracle bit for
                    // bit — undefined bits included (no mask). If any instruction
                    // fell back to the interpreter, that instruction's officially
                    // undefined flags need the snippet's declared mask, exactly
                    // as the interpreter test uses it.
                    var ignore = jit.InterpreterFallbacks == 0 ? 0u : snippet.IgnoreFlags;
                    var compared = new OracleSnippet { Name = snippet.Name, Code = snippet.Code, IgnoreFlags = ignore };
                    return InterpreterOracleTests.Compare(compared, test, cpu, jit.Interpreter, memory);
                }
            }
        }

        private static byte[] Pad(byte[] src, int length)
        {
            if (src.Length >= length) return src;
            var padded = new byte[length];
            Array.Copy(src, padded, src.Length);
            return padded;
        }
    }
}
