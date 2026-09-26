using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Nativra.X86.Tests
{
    /// <summary>One before/after pair from the hardware oracle.</summary>
    public sealed class OracleCase
    {
        public uint[] RegIn = new uint[8];
        public uint FlagsIn;
        public byte[] FxIn;
        public byte[] DataIn;
        public byte[] StackIn;

        public uint[] RegOut = new uint[8];
        public uint FlagsOut;
        public byte[] FxOut;
        public byte[] DataOut;
        public byte[] StackOut;
    }

    /// <summary>All the oracle cases for one snippet.</summary>
    public sealed class OracleSnippet
    {
        public string Name;
        public byte[] Code;
        public uint IgnoreFlags;
        public readonly List<OracleCase> Cases = new List<OracleCase>();
    }

    /// <summary>
    /// Reads the file <c>oracle</c> produces: each snippet's machine code, then
    /// a run of <c>I</c>/<c>O</c> lines giving the guest state a real 32-bit CPU
    /// saw going in and coming out. The interpreter and JIT tests replay the
    /// <c>I</c> states and check they reach the <c>O</c> states.
    /// </summary>
    public static class OracleVectors
    {
        public const uint DataBase = 0x10000000;
        public const uint StackTop = 0x20001000;
        public const uint StackWindow = 64;   // bytes captured either side of ESP
        public const uint CodeBase = 0x30000000;
        public const int FxBytes = 288;

        public static string FindFile()
        {
            var names = new[] { "oracle-vectors.txt", "oracle-out.txt" };
            var dir = AppContext.BaseDirectory;
            for (var i = 0; i < 8 && dir != null; i++)
            {
                foreach (var name in names)
                {
                    var candidate = Path.Combine(dir, name);
                    if (File.Exists(candidate)) return candidate;
                    var inHarness = Path.Combine(dir, "harness", name);
                    if (File.Exists(inHarness)) return inHarness;
                }
                dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
            }
            var env = Environment.GetEnvironmentVariable("ORACLE_VECTORS");
            return env != null && File.Exists(env) ? env : null;
        }

        public static List<OracleSnippet> Load(string path)
        {
            var snippets = new List<OracleSnippet>();
            OracleSnippet current = null;
            OracleCase pending = null;
            foreach (var raw in File.ReadLines(path))
            {
                if (raw.Length == 0) continue;
                var parts = raw.Split(' ');
                if (raw[0] == 'T')
                {
                    current = new OracleSnippet
                    {
                        Name = parts[1],
                        Code = ParseHex(parts[2]),
                        IgnoreFlags = uint.Parse(parts[3], NumberStyles.HexNumber),
                    };
                    snippets.Add(current);
                    pending = null;
                }
                else if (raw[0] == 'I' && current != null)
                {
                    pending = new OracleCase();
                    ReadState(parts, pending.RegIn, out pending.FlagsIn, out pending.FxIn, out pending.DataIn, out pending.StackIn);
                    current.Cases.Add(pending);
                }
                else if (raw[0] == 'O' && pending != null)
                {
                    ReadState(parts, pending.RegOut, out pending.FlagsOut, out pending.FxOut, out pending.DataOut, out pending.StackOut);
                    pending = null;
                }
            }
            return snippets;
        }

        private static void ReadState(string[] parts, uint[] regs, out uint flags, out byte[] fx, out byte[] data, out byte[] stack)
        {
            for (var i = 0; i < 8; i++) regs[i] = uint.Parse(parts[1 + i], NumberStyles.HexNumber);
            flags = uint.Parse(parts[9], NumberStyles.HexNumber);
            fx = ParseHex(parts[10]);
            data = ParseHex(parts[11]);
            stack = ParseHex(parts[12]);
        }

        public static byte[] ParseHex(string s)
        {
            var bytes = new byte[s.Length / 2];
            for (var i = 0; i < bytes.Length; i++)
                bytes[i] = byte.Parse(s.Substring(i * 2, 2), NumberStyles.HexNumber);
            return bytes;
        }
    }
}
