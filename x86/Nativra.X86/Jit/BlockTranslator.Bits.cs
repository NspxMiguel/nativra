using Nativra.X86.Cpu;

namespace Nativra.X86.Jit
{
    public sealed partial class BlockTranslator
    {
        /// <summary>
        /// BT/BTS/BTR/BTC with a register or an immediate bit offset. The host
        /// instruction has the same bit-string addressing for a memory operand;
        /// only CF is defined, and the interpreter leaves every other flag alone
        /// (what an Intel core does too, but not every host does).
        /// </summary>
        private bool EmitBitTest(in Instruction ins, int low, int size)
        {
            var immediate = low == 0xBA;
            if (immediate && ins.RegField < 4) return false;
            var prefix = size == 16 ? 0x66 : 0;
            var reg = immediate ? ins.RegField : G[ins.RegField];
            e.Pushfq();
            if (IsMem(ins))
            {
                EmitAddress(ins);
                if (ins.Lock) e.U8(0xF0);
                e.Rm(prefix, false, 0x0F, low, reg, Mem, S1, 1, 0);
            }
            else e.Rr(prefix, false, 0x0F, low, reg, G[ins.Rm]);
            if (immediate) e.U8((byte)ins.Imm);
            KeepFlagsEnd(Flag.CF);
            return true;
        }

        /// <summary>SHLD/SHRD by an immediate or by CL.</summary>
        private bool EmitDoubleShift(in Instruction ins, int low, int size)
        {
            var prefix = size == 16 ? 0x66 : 0;
            var immediate = (low & 1) == 0;
            if (IsMem(ins))
            {
                EmitAddress(ins);
                e.Rm(prefix, false, 0x0F, low, G[ins.RegField], Mem, S1, 1, 0);
            }
            else e.Rr(prefix, false, 0x0F, low, G[ins.RegField], G[ins.Rm]);
            if (immediate) e.U8((byte)ins.Imm);
            return true;
        }

        /// <summary>BSF/BSR: ZF is the only flag written, and a zero source leaves the destination alone.</summary>
        private bool EmitBitScan(in Instruction ins, bool forward, int size)
        {
            var prefix = size == 16 ? 0x66 : 0;
            var opcode = forward ? 0xBC : 0xBD;
            e.Pushfq();
            if (IsMem(ins))
            {
                EmitAddress(ins);
                e.Rm(prefix, false, 0x0F, opcode, G[ins.RegField], Mem, S1, 1, 0);
            }
            else e.Rr(prefix, false, 0x0F, opcode, G[ins.RegField], G[ins.Rm]);
            KeepFlagsEnd(Flag.ZF);
            return true;
        }

        /// <summary>
        /// Closes a Pushfq that preceded a host instruction: every arithmetic flag
        /// outside <paramref name="defined"/> goes back to its value before it, so
        /// the guest sees the flags the interpreter leaves, not whatever the host
        /// leaves undefined.
        /// </summary>
        private void KeepFlagsEnd(uint defined)
        {
            e.Pushfq();
            e.PopReg(S2);
            e.AluRegImm(4, S2, defined);
            e.PopReg(S3);
            e.AluRegImm(4, S3, ~defined);
            e.AluRegReg(1, S3, S2);
            e.PushReg(S3);
            e.Popfq();
        }

        /// <summary>LAHF: AH = SF:ZF:0:AF:0:PF:1:CF.</summary>
        private void EmitLahf()
        {
            e.Pushfq();
            e.PopReg(S2);
            e.Pushfq();
            e.AluRegImm(4, S2, 0xD7);
            e.AluRegImm(1, S2, 2);
            WriteByteRegister(4, S2);
            e.Popfq();
        }

        /// <summary>SAHF: SF, ZF, AF, PF and CF come from AH.</summary>
        private void EmitSahf()
        {
            const uint low = Flag.CF | Flag.PF | Flag.AF | Flag.ZF | Flag.SF;
            e.Pushfq();
            e.PopReg(S2);
            e.AluRegImm(4, S2, ~low);
            var ah = ReadByteRegister(4, S3);
            e.AluRegImm(4, ah, low);
            e.AluRegReg(1, S2, ah);
            e.PushReg(S2);
            e.Popfq();
        }
    }
}
