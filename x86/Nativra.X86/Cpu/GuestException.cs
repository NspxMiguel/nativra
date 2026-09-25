using System;

namespace Nativra.X86.Cpu
{
    /// <summary>
    /// A fault raised by guest code, carrying the Windows exception code the
    /// guest's own handlers expect to see. The runtime turns it into a 32-bit
    /// EXCEPTION_RECORD and walks the guest's FS:[0] chain with it.
    /// </summary>
    public sealed class GuestException : Exception
    {
        public const uint AccessViolation = 0xC0000005;
        public const uint IllegalInstruction = 0xC000001D;
        public const uint PrivilegedInstruction = 0xC0000096;
        public const uint IntegerDivideByZero = 0xC0000094;
        public const uint IntegerOverflow = 0xC0000095;
        public const uint Breakpoint = 0x80000003;
        public const uint SingleStep = 0x80000004;
        public const uint ArrayBoundsExceeded = 0xC000008C;
        public const uint FloatInvalidOperation = 0xC0000090;
        public const uint StackOverflow = 0xC00000FD;

        public uint Code { get; }

        /// <summary>Guest EIP of the instruction that raised it.</summary>
        public uint Eip { get; }

        /// <summary>For access violations: 0 read, 1 write, 8 execute; then the address.</summary>
        public uint[] Information { get; }

        public GuestException(uint code, uint eip, params uint[] information)
            : base($"guest exception 0x{code:X8} at 0x{eip:X8}")
        {
            Code = code;
            Eip = eip;
            Information = information ?? Array.Empty<uint>();
        }
    }
}
