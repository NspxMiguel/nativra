using System;

namespace Nativra.X86.Cpu
{
    /// <summary>
    /// Bit and floating-point helpers netstandard2.0 does not provide
    /// (System.Numerics.BitOperations, Math.ScaleB and friends are newer).
    /// </summary>
    public static unsafe class Bits
    {
        public static int PopCount(uint v)
        {
            v -= (v >> 1) & 0x55555555;
            v = (v & 0x33333333) + ((v >> 2) & 0x33333333);
            return (int)((((v + (v >> 4)) & 0x0F0F0F0F) * 0x01010101) >> 24);
        }

        public static int TrailingZeros(uint v)
        {
            if (v == 0) return 32;
            var n = 0;
            while ((v & 1) == 0) { v >>= 1; n++; }
            return n;
        }

        public static int LeadingZeros(uint v)
        {
            if (v == 0) return 32;
            var n = 0;
            while ((v & 0x80000000) == 0) { v <<= 1; n++; }
            return n;
        }

        public static int LeadingZeros(ulong v)
        {
            if (v == 0) return 64;
            var n = 0;
            while ((v & 0x8000000000000000UL) == 0) { v <<= 1; n++; }
            return n;
        }

        public static float Int32BitsToSingle(int bits) => *(float*)&bits;

        public static int SingleToInt32Bits(float value) => *(int*)&value;

        /// <summary>x * 2^n, exact whenever the result is representable.</summary>
        public static double ScaleB(double x, int n)
        {
            // Apply in steps so the intermediate power of two never overflows.
            while (n > 1000) { x *= Pow2(1000); n -= 1000; }
            while (n < -1000) { x *= Pow2(-1000); n += 1000; }
            return x * Pow2(n);
        }

        private static double Pow2(int n)
        {
            if (n >= -1022) return BitConverter.Int64BitsToDouble((long)(n + 1023) << 52);
            // Subnormal powers of two.
            return BitConverter.Int64BitsToDouble(1L << (n + 1074));
        }

        /// <summary>The unbiased binary exponent of a finite, nonzero value.</summary>
        public static int ILogB(double x)
        {
            var bits = BitConverter.DoubleToInt64Bits(x);
            var exponent = (int)((bits >> 52) & 0x7FF);
            if (exponent == 0)
            {
                var fraction = (ulong)bits & 0xFFFFFFFFFFFFFUL;
                return -1022 - (LeadingZeros(fraction) - 12) - 1;
            }
            return exponent - 1023;
        }

        public static double Log2(double x) => Math.Log(x) / Math.Log(2.0);
    }
}
