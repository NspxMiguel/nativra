using System;
using System.Collections.Generic;
using Nativra.X86.Cpu;
using Xunit;

namespace Nativra.X86.Tests
{
    public sealed class JitRandomFormsTests
    {
        // Set NATIVRA_FUZZ_ITERATIONS=2000000 for an extended deterministic run.
        [SkippableFact]
        public void RandomSmallFormsMatchInterpreter()
        {
            Skip.IfNot(JitDiff.CanJit, "JIT needs an x64 host");
            var count = int.TryParse(Environment.GetEnvironmentVariable("NATIVRA_FUZZ_ITERATIONS"), out var requested)
                ? requested : 2000;
            var random = new Random(0x452438);
            var forms = new List<string>
            {
                "0F A3 0E", "0F AB 0E", "0F B3 0E", "0F BB 0E", // memory bit string
                "0F BA 6E 7F 03", "F0 0F BA 6E 7F 07", // immediate bit test
                "0F A4 4E 7F 05", "0F AD 4E 7F", // double shifts
                "0F BC 4E 7F", "0F BD 4E 7F", "0F BC 44 4E 7F", "0F BD 44 8E 7F", // scans and SIB
                "B4 81", "F6 D4", "F6 DC", "F6 C4 40", "9F 9E", // high byte and flags
                "93", "87 4E 7F", "F0 FF 46 7F", "F0 0F C1 4E 7F", // exchange and lock
                "0F 1F 44 8E 7F", "0F 18 46 7F", "F3 90", // no-ops
                "F3 C3", "F3 C2 08 00", // return forms
            };
            for (var i = 0; i < 128; i++) forms.Add(RandomMemoryForm(random));
            for (var formIndex = 0; formIndex < forms.Count; formIndex++)
            {
                var form = forms[formIndex];
                var isReturn = form == "F3 C3" || form == "F3 C2 08 00";
                var hex = isReturn ? form : form + " EB 00";
                using (var diff = new JitDiff(hex))
                {
                    var end = JitDiff.Code + (uint)Convert.FromHexString(hex.Replace(" ", "")).Length;
                    for (var iteration = formIndex; iteration < count; iteration += forms.Count)
                    {
                        var state = new uint[8];
                        for (var r = 0; r < state.Length; r++) state[r] = (uint)random.NextInt64();
                        state[Reg.Esi] = JitDiff.Data + 0xF80;
                        state[Reg.Ecx] = formIndex < 4 ? (uint)random.Next(-32, 32) : (uint)random.Next(0, 16);
                        state[Reg.Edx] = (uint)random.Next(0, 16);
                        state[Reg.Ebx] = (uint)random.Next(0, 16);
                        state[Reg.Esp] = JitDiff.Stack - 0x40;
                        var flags = Flag.Fixed | ((uint)random.NextInt64() & Flag.Arith);
                        var bytes = new byte[0x100];
                        random.NextBytes(bytes);
                        foreach (var cpu in new[] { diff.ExpectedCpu, diff.ActualCpu })
                        {
                            Array.Copy(state, cpu.R, 8);
                            cpu.EFlags = flags;
                            cpu.Eip = JitDiff.Code;
                        }
                        foreach (var memory in new[] { diff.ExpectedMemory, diff.ActualMemory })
                        {
                            memory.WriteBytes(JitDiff.Data + 0xF40, bytes);
                            memory.Write32(JitDiff.Stack - 0x40, end);
                        }
                        var label = $"seed=0x452438 iteration={iteration} form={form} flags={flags:X8}";
                        try
                        {
                            for (var steps = 0; diff.ExpectedCpu.Eip != end && steps < 8; steps++) diff.Interpreter.Step();
                        }
                        catch (Exception error) { throw new Exception(label + " regs=" + string.Join(",", state) + " exception=" + error + (error is GuestException fault ? " address=" + string.Join(",", fault.Information) : ""), error); }
                        for (var blocks = 0; diff.ActualCpu.Eip != end && blocks < 8; blocks++) diff.Jit.RunBlock(1, end);
                        Assert.True(diff.ExpectedCpu.R.AsSpan().SequenceEqual(diff.ActualCpu.R), label + " registers");
                        Assert.True((diff.ExpectedCpu.EFlags & Flag.Arith) == (diff.ActualCpu.EFlags & Flag.Arith), label + " flags");
                        Assert.True(diff.ExpectedCpu.Eip == diff.ActualCpu.Eip, label + " eip");
                        var expected = diff.ExpectedMemory.ReadBytes(JitDiff.Data + 0xF40, 0x100);
                        var actual = diff.ActualMemory.ReadBytes(JitDiff.Data + 0xF40, 0x100);
                        Assert.True(expected.AsSpan().SequenceEqual(actual), label + " memory");
                    }
                }
            }
        }
        private static string RandomMemoryForm(Random random)
        {
            var family = random.Next(6);
            var prefix16 = random.Next(2) == 0;
            var sib = random.Next(2) == 0;
            var source = family == 0 ? random.Next(1, 4) : random.Next(0, 8);
            var bytes = new List<byte>();
            if (prefix16) bytes.Add(0x66);
            if (family == 4 || family == 5) bytes.Add(0xF0);
            bytes.Add(0x0F);
            bytes.Add(family == 0 ? new byte[] { 0xA3, 0xAB, 0xB3, 0xBB }[random.Next(4)] :
                family == 1 ? new byte[] { 0xA4, 0xAC }[random.Next(2)] :
                family == 2 ? new byte[] { 0xBC, 0xBD }[random.Next(2)] :
                family == 3 ? (byte)0xBA : family == 4 ? (byte)0xC1 : (byte)0xB1);
            if (family == 3) source = random.Next(4, 8);
            bytes.Add((byte)(0x40 | (source << 3) | (sib ? 4 : 6)));
            if (sib)
            {
                var index = new[] { 1, 2, 3 }[random.Next(3)];
                bytes.Add((byte)((random.Next(4) << 6) | (index << 3) | 6));
            }
            bytes.Add(unchecked((byte)random.Next(-64, 64)));
            if (family == 1 || family == 3) bytes.Add((byte)random.Next(1, 32));
            var form = Convert.ToHexString(bytes.ToArray());
            return random.Next(2) == 0 ? form : form + " 9F 9E";
        }
    }
}
