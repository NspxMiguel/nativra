using System;

namespace Nativra.X86.Loader
{
    internal sealed class AdpcmDecoder
    {
        private static readonly int[] Adaptation = { 230, 230, 230, 230, 307, 409, 512, 614, 768, 614, 512, 409, 307, 230, 230, 230 };
        internal readonly ushort Tag;
        internal readonly int Channels;
        internal readonly int BlockAlign;
        internal readonly int SamplesPerBlock;
        private readonly short[] coefficients;

        internal AdpcmDecoder(byte[] format)
        {
            Tag = Read16(format, 0);
            Channels = Read16(format, 2);
            BlockAlign = Read16(format, 12);
            if (Channels < 1 || Channels > 2 || BlockAlign < 1) throw new ArgumentException("Invalid ADPCM format");
            if (Tag == 2)
            {
                if (format.Length < 22) throw new ArgumentException("Incomplete MS ADPCM format");
                SamplesPerBlock = Read16(format, 18);
                var count = Read16(format, 20);
                if (count < 1 || format.Length < 22 + count * 4 || BlockAlign < Channels * 7 ||
                    SamplesPerBlock < 2 || SamplesPerBlock > 2 + (BlockAlign - Channels * 7) * 2 / Channels)
                    throw new ArgumentException("Invalid MS ADPCM format");
                coefficients = new short[count * 2];
                for (var i = 0; i < coefficients.Length; i++) coefficients[i] = (short)Read16(format, 22 + i * 2);
            }
            else if (Tag == 0x11)
            {
                if (format.Length < 20 || BlockAlign < Channels * 4) throw new ArgumentException("Invalid IMA ADPCM format");
                SamplesPerBlock = Read16(format, 18);
                if (SamplesPerBlock < 1 || SamplesPerBlock > 1 + (BlockAlign - Channels * 4) * 2 / Channels)
                    throw new ArgumentException("Invalid IMA ADPCM block size");
            }
            else throw new ArgumentException("Unsupported ADPCM format");
        }

        private static ushort Read16(byte[] data, int offset) => (ushort)(data[offset] | data[offset + 1] << 8);
        private static short Clamp(int value) => (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, value));
        private static void Write(byte[] output, int sample, int channel, int channels, short value)
        {
            var offset = (sample * channels + channel) * 2;
            output[offset] = (byte)value;
            output[offset + 1] = (byte)(value >> 8);
        }

        internal byte[] Decode(byte[] data)
        {
            if (Tag == 0x11) return ImaAdpcmDecoder.Decode(data, Channels, BlockAlign, SamplesPerBlock);
            var blocks = (data.Length + BlockAlign - 1) / BlockAlign;
            var output = new byte[checked(blocks * SamplesPerBlock * Channels * 2)];
            var written = 0;
            for (var start = 0; start < data.Length; start += BlockAlign)
            {
                var end = Math.Min(start + BlockAlign, data.Length);
                var header = Channels * 7;
                if (end - start < header) throw new ArgumentException("Truncated MS ADPCM block");
                var delta = new int[Channels];
                var previous = new short[Channels];
                var older = new short[Channels];
                var pair = new int[Channels];
                for (var ch = 0; ch < Channels; ch++)
                {
                    pair[ch] = data[start + ch];
                    if (pair[ch] * 2 >= coefficients.Length) throw new ArgumentException("Invalid MS ADPCM predictor");
                    delta[ch] = Math.Max(16, (int)Read16(data, start + Channels + ch * 2));
                    previous[ch] = (short)Read16(data, start + Channels * 3 + ch * 2);
                    older[ch] = (short)Read16(data, start + Channels * 5 + ch * 2);
                    Write(output, written, ch, Channels, older[ch]);
                    Write(output, written + 1, ch, Channels, previous[ch]);
                }
                var available = Math.Min(SamplesPerBlock - 2, (end - start - header) * 2 / Channels);
                for (var sample = 0; sample < available; sample++)
                    for (var ch = 0; ch < Channels; ch++)
                    {
                        var nibbleIndex = sample * Channels + ch;
                        var packed = data[start + header + nibbleIndex / 2];
                        var nibble = nibbleIndex % 2 == 0 ? packed >> 4 : packed & 15;
                        var signed = nibble < 8 ? nibble : nibble - 16;
                        var estimate = (previous[ch] * coefficients[pair[ch] * 2] + older[ch] * coefficients[pair[ch] * 2 + 1]) >> 8;
                        var decoded = Clamp(estimate + signed * delta[ch]);
                        older[ch] = previous[ch];
                        previous[ch] = decoded;
                        delta[ch] = Math.Max(16, delta[ch] * Adaptation[nibble] / 256);
                        Write(output, written + 2 + sample, ch, Channels, decoded);
                    }
                written += available + 2;
            }
            if (written * Channels * 2 == output.Length) return output;
            Array.Resize(ref output, written * Channels * 2);
            return output;
        }
    }
}
