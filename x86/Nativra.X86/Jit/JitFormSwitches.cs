using System;

namespace Nativra.X86.Jit
{
    [Flags]
    public enum JitFormSwitches
    {
        None = 0, Bits = 1, Shld = 2, Bsf = 4, Lock = 8, Xchg = 16,
        Lahf = 32, HighByte = 64, Nops = 128, RetForm = 256,
        Strings = 512, X87 = 1024, Mmx = 2048,
        All = Bits | Shld | Bsf | Lock | Xchg | Lahf | HighByte | Nops | RetForm | Strings | X87 | Mmx,
    }
}
