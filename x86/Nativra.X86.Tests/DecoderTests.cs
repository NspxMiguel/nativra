using Nativra.X86.Cpu;
using Xunit;

namespace Nativra.X86.Tests
{
    /// <summary>
    /// Instruction-length and operand decoding, checked directly rather than
    /// through execution. Getting the length right is what lets the JIT step
    /// over an instruction it does not translate and the interpreter skip
    /// nothing, so it is worth pinning on its own.
    /// </summary>
    public sealed class DecoderTests
    {
        private sealed class Bytes : ICodeReader
        {
            private readonly byte[] data;
            public Bytes(params byte[] data) => this.data = data;
            public byte Read8(uint address) => data[address];
        }

        private static Instruction Decode(params byte[] code) => Decoder.Decode(new Bytes(code), 0);

        [Theory]
        // one-byte and ALU forms
        [InlineData(1, 0x90)]                                   // nop
        [InlineData(2, 0x01, 0xC8)]                             // add eax, ecx
        [InlineData(2, 0x00, 0xC8)]                             // add al, cl
        [InlineData(5, 0x05, 0x78, 0x56, 0x34, 0x12)]          // add eax, imm32
        [InlineData(2, 0x04, 0x42)]                             // add al, imm8
        [InlineData(6, 0x81, 0xC1, 0xEF, 0xBE, 0xAD, 0xDE)]    // add ecx, imm32
        [InlineData(3, 0x83, 0xE9, 0xFB)]                       // sub ecx, imm8
        [InlineData(3, 0x66, 0x01, 0xC8)]                       // add ax, cx (opsize)
        // moves and immediates
        [InlineData(5, 0xB8, 0xEF, 0xBE, 0xAD, 0xDE)]          // mov eax, imm32
        [InlineData(2, 0xB0, 0x42)]                             // mov al, imm8
        [InlineData(3, 0xC6, 0x00, 0x7F)]                       // mov byte [eax], imm8
        [InlineData(6, 0xC7, 0x00, 0x78, 0x56, 0x34, 0x12)]    // mov dword [eax], imm32
        // ModRM addressing shapes
        [InlineData(2, 0x8B, 0x00)]                             // mov eax, [eax]
        [InlineData(3, 0x8B, 0x40, 0x10)]                       // mov eax, [eax+0x10]
        [InlineData(6, 0x8B, 0x80, 0x00, 0x10, 0x00, 0x00)]    // mov eax, [eax+0x1000]
        [InlineData(3, 0x8B, 0x04, 0x91)]                       // mov eax, [ecx+edx*4]
        [InlineData(7, 0x8B, 0x04, 0x85, 0x00, 0x10, 0x00, 0x00)] // mov eax, [edx*4+0x1000]
        [InlineData(6, 0x8B, 0x05, 0x00, 0x10, 0x40, 0x00)]    // mov eax, [disp32]
        [InlineData(4, 0x8D, 0x44, 0x0A, 0x10)]                // lea eax, [edx+ecx+0x10]
        // branches
        [InlineData(2, 0x74, 0x05)]                             // jz rel8
        [InlineData(5, 0xE8, 0x00, 0x01, 0x00, 0x00)]          // call rel32
        [InlineData(5, 0xE9, 0x00, 0x01, 0x00, 0x00)]          // jmp rel32
        [InlineData(6, 0x0F, 0x84, 0x00, 0x01, 0x00, 0x00)]    // jz rel32
        [InlineData(3, 0xC2, 0x08, 0x00)]                       // ret imm16
        [InlineData(1, 0xC3)]                                   // ret
        // two-byte map
        [InlineData(3, 0x0F, 0xB6, 0xC3)]                       // movzx eax, bl
        [InlineData(3, 0x0F, 0xAF, 0xC1)]                       // imul eax, ecx
        [InlineData(4, 0x0F, 0xBA, 0xE0, 0x0D)]                // bt eax, 13
        [InlineData(4, 0x0F, 0xA4, 0xC8, 0x07)]                // shld eax, ecx, 7
        [InlineData(3, 0x0F, 0x94, 0xC0)]                       // sete al
        // SSE with a mandatory prefix
        [InlineData(4, 0xF3, 0x0F, 0x58, 0xC1)]                // addss
        [InlineData(4, 0x66, 0x0F, 0x6E, 0xC1)]                // movd xmm0, ecx
        [InlineData(5, 0x66, 0x0F, 0x70, 0xC1, 0x1B)]          // pshufd xmm0, xmm1, 0x1B
        [InlineData(5, 0x66, 0x0F, 0x72, 0xF1, 0x05)]          // pslld xmm1, 5
        // three-byte and misc
        [InlineData(2, 0xF7, 0xD8)]                             // neg eax
        [InlineData(6, 0xF7, 0xC1, 0xFF, 0x00, 0x00, 0x00)]    // test ecx, imm32
        [InlineData(2, 0xF6, 0xD8)]                             // neg al
        [InlineData(2, 0xFF, 0xD0)]                             // call eax
        [InlineData(1, 0x99)]                                   // cdq
        [InlineData(2, 0xF3, 0xA4)]                             // rep movsb
        public void Length(int expected, int b0, params int[] rest)
        {
            var code = new byte[1 + rest.Length];
            code[0] = (byte)b0;
            for (var i = 0; i < rest.Length; i++) code[i + 1] = (byte)rest[i];
            var ins = Decode(code);
            Assert.True(ins.Valid, $"decode failed for op 0x{ins.Op:X}");
            Assert.Equal(expected, ins.Length);
        }

        [Fact]
        public void SibWithoutBase()
        {
            var ins = Decode(0x8B, 0x04, 0x85, 0x00, 0x10, 0x00, 0x00);
            Assert.True(ins.IsMemory);
            Assert.Equal(-1, ins.Base);
            Assert.Equal(Reg.Eax, ins.Index);
            Assert.Equal(4, ins.Scale);
            Assert.Equal(0x1000u, ins.Disp);
        }

        [Fact]
        public void RipStyleDisp32IsAbsoluteIn32Bit()
        {
            var ins = Decode(0x8B, 0x05, 0x44, 0x33, 0x22, 0x11);
            Assert.True(ins.IsMemory);
            Assert.Equal(-1, ins.Base);
            Assert.Equal(-1, ins.Index);
            Assert.Equal(0x11223344u, ins.Disp);
        }

        [Fact]
        public void SegmentOverrideRecorded()
        {
            var ins = Decode(0x64, 0x8B, 0x05, 0x18, 0x00, 0x00, 0x00); // mov eax, fs:[0x18]
            Assert.Equal(Seg.Fs, ins.Segment);
            Assert.Equal(7, ins.Length);
        }

        [Fact]
        public void PrefixesAndImmediateTogether()
        {
            var ins = Decode(0xF0, 0x81, 0x00, 0x01, 0x00, 0x00, 0x00); // lock add dword [eax], 1
            Assert.True(ins.Lock);
            Assert.Equal(7, ins.Length);
            Assert.Equal(1u, ins.Imm);
        }
    }
}
