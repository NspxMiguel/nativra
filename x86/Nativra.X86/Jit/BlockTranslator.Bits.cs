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

        /// <summary>
        /// SHLD/SHRD by an immediate or by CL. The host leaves OF and AF undefined
        /// and shifts a 16-bit operand by more than 16 its own way, so the flags
        /// are rebuilt as the interpreter defines them (OF is whether the sign
        /// changed, AF is clear), and a 16-bit count above 16 stays with the
        /// interpreter. A count that masks to zero changes nothing.
        /// </summary>
        private bool EmitDoubleShift(in Instruction ins, int low, int size)
        {
            var prefix = size == 16 ? 0x66 : 0;
            var immediate = (low & 1) == 0;
            if (immediate)
            {
                var count = ins.Imm & 0x1F;
                if (count == 0 || (size == 16 && count > 16)) return false;
            }
            var bad = NewLabel("dshift_bad");
            var done = NewLabel("dshift_done");
            if (!immediate && size == 16)
            {
                e.Pushfq();
                e.MovRegReg(S2, G[Reg.Ecx]);
                e.AluRegImm(4, S2, 0x1F);
                e.AluRegImm(7, S2, 16);
                e.Jcc(7, bad);   // above
                e.Popfq();
            }

            if (IsMem(ins))
            {
                EmitAddress(ins);
                e.LoadMem(S3, Mem, S1, 1, 0, size);
            }
            else e.MovRegReg(S3, G[ins.Rm]);
            e.Pushfq();
            if (IsMem(ins)) e.Rm(prefix, false, 0x0F, low, G[ins.RegField], Mem, S1, 1, 0);
            else e.Rr(prefix, false, 0x0F, low, G[ins.RegField], G[ins.Rm]);
            if (immediate) e.U8((byte)ins.Imm);
            e.Pushfq();
            if (IsMem(ins)) e.LoadMem(S2, Mem, S1, 1, 0, size);
            else e.MovRegReg(S2, G[ins.Rm]);
            e.AluRegReg(6, S3, S2);                 // old ^ new
            e.ShiftImm(5, S3, (byte)(size - 1));
            e.AluRegImm(4, S3, 1);
            e.ShiftImm(4, S3, 11);                  // OF: the sign changed
            e.PopReg(S2);                           // the host's flags after the shift
            e.AluRegImm(4, S2, ~(Flag.OF | Flag.AF));
            e.AluRegReg(1, S2, S3);
            e.PopReg(S1);                           // the flags from before
            if (!immediate)
            {
                e.TestRegImm(G[Reg.Ecx], 0x1F, 8);
                e.Cmovcc(4, S2, S1);                // a count of zero leaves them alone
            }
            e.PushReg(S2);
            e.Popfq();
            if (!immediate && size == 16)
            {
                e.Jmp(done);
                e.Label(bad);
                e.Popfq();
                EmitExit(ins.Address, Ctx.ReasonFallback);
                e.Label(done);
            }
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
