using System;

namespace Nativra.X86.Loader
{
    internal static class ImaAdpcmDecoder
    {
        private static readonly int[] IndexChange = { -1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8 };
        private static readonly int[] Step = { 7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767 };

        internal static byte[] Decode(byte[] data, int channels, int blockAlign, int samplesPerBlock)
        {
            var output = new byte[checked(((data.Length + blockAlign - 1) / blockAlign) * samplesPerBlock * channels * 2)];
            var written = 0;
            for (var start = 0; start < data.Length; start += blockAlign)
            {
                var end = Math.Min(data.Length, start + blockAlign);
                if (end - start < channels * 4) throw new ArgumentException("Truncated IMA ADPCM block");
                var predictor = new int[channels];
                var index = new int[channels];
                var count = new int[channels];
                for (var ch = 0; ch < channels; ch++)
                {
                    var at = start + ch * 4;
                    predictor[ch] = (short)(data[at] | data[at + 1] << 8);
                    index[ch] = data[at + 2];
                    if (index[ch] > 88) throw new ArgumentException("Invalid IMA ADPCM step index");
                    Write(output, written, ch, channels, predictor[ch]);
                    count[ch] = 1;
                }
                var offset = start + channels * 4;
                while (offset < end)
                    for (var ch = 0; ch < channels; ch++)
                        for (var byteIndex = 0; byteIndex < 4 && offset < end; byteIndex++)
                        {
                            var packed = data[offset++];
                            for (var half = 0; half < 2 && count[ch] < samplesPerBlock; half++)
                            {
                                var nibble = half == 0 ? packed & 15 : packed >> 4;
                                var step = Step[index[ch]];
                                var difference = step >> 3;
                                if ((nibble & 1) != 0) difference += step >> 2;
                                if ((nibble & 2) != 0) difference += step >> 1;
                                if ((nibble & 4) != 0) difference += step;
                                predictor[ch] += (nibble & 8) != 0 ? -difference : difference;
                                predictor[ch] = Math.Max(short.MinValue, Math.Min(short.MaxValue, predictor[ch]));
                                index[ch] = Math.Max(0, Math.Min(88, index[ch] + IndexChange[nibble]));
                                Write(output, written + count[ch]++, ch, channels, predictor[ch]);
                            }
                        }
                written += count[0];
            }
            Array.Resize(ref output, written * channels * 2);
            return output;
        }

        private static void Write(byte[] output, int sample, int channel, int channels, int value)
        {
            var offset = (sample * channels + channel) * 2;
            output[offset] = (byte)value;
            output[offset + 1] = (byte)(value >> 8);
        }
    }
}
