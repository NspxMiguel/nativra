using System;
using Nativra.X86.Cpu;
using Nativra.X86.Jit;
using Xunit;

namespace Nativra.X86.Tests
{
    public sealed class JitFormSwitchTests
    {
        [SkippableFact]
        public void EveryFormSwitchReturnsItsInstructionToTheInterpreter()
        {
            Skip.IfNot(JitDiff.CanJit, "JIT needs an x64 host");
            var cases = new (JitFormSwitches Form, string Code)[]
            {
                (JitFormSwitches.Bits, "0F BA E0 01"),
                (JitFormSwitches.Shld, "0F A4 C8 01"),
                (JitFormSwitches.Bsf, "0F BC C8"),
                (JitFormSwitches.Lock, "F0 FF 06"),
                (JitFormSwitches.Xchg, "93"),
                (JitFormSwitches.Lahf, "9F"),
                (JitFormSwitches.HighByte, "B4 81"),
                (JitFormSwitches.Nops, "0F 1F 00"),
                (JitFormSwitches.RetForm, "F3 C3"),
                (JitFormSwitches.Strings, "F3 A4"),
                (JitFormSwitches.X87, "D9 E8"),
                (JitFormSwitches.Mmx, "0F 77"),
            };
            foreach (var item in cases)
            {
                using (var memory = new GuestMemory())
                {
                    var bytes = Convert.FromHexString(item.Code.Replace(" ", ""));
                    memory.Map(JitDiff.Code, 0x1000);
                    memory.WriteBytes(JitDiff.Code, bytes);
                    var translator = new BlockTranslator { DisabledForms = item.Form };
                    translator.Translate(memory, JitDiff.Code, JitDiff.Code + (uint)bytes.Length);
                    Assert.False(translator.FullyTranslated, item.Form + " " + item.Code);
                }
            }
        }

    }
}
