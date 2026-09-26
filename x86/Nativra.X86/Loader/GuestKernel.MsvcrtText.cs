using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Nativra.X86.Cpu;

namespace Nativra.X86.Loader
{
    // msvcrt: strings (byte-exact for the narrow forms, as C compares
    // unsigned chars), ctype, number conversions, math including the x87
    // intrinsics MSVC code calls with operands on the FPU stack (_CIpow,
    // _ftol), the 64-bit arithmetic helpers, qsort and bsearch.
    public sealed partial class GuestKernel
    {
        private void InstallCrtText(GuestImports i)
        {
            InstallCrtStrings(i);
            InstallCrtWideStrings(i);
            InstallCrtCtype(i);
            InstallCrtConversions(i);
            InstallCrtMath(i);
        }

        // --- raw string access -------------------------------------------------

        internal uint StrLen(uint s)
        {
            uint n = 0;
            while (memory.Read8(s + n) != 0) n++;
            return n;
        }

        internal uint WcsLen(uint s)
        {
            uint n = 0;
            while (memory.Read16(s + n * 2) != 0) n++;
            return n;
        }

        private byte[] CBytes(uint s) => memory.ReadBytes(s, (int)StrLen(s));

        private string CString(uint s) => s == 0 ? null : Ansi.Decode(CBytes(s));

        private string WString(uint s) => s == 0 ? null : memory.ReadUnicode(s, int.MaxValue);

        private void WriteCBytes(uint at, byte[] bytes)
        {
            memory.WriteBytes(at, bytes);
            memory.Write8(at + (uint)bytes.Length, 0);
        }

        private static byte LowerByte(byte b) => b >= 'A' && b <= 'Z' ? (byte)(b + 32) : b;
        private static byte UpperByte(byte b) => b >= 'a' && b <= 'z' ? (byte)(b - 32) : b;

        private int CompareBytes(uint a, uint b, uint max, bool ignoreCase)
        {
            for (uint n = 0; n < max; n++)
            {
                var x = memory.Read8(a + n);
                var y = memory.Read8(b + n);
                if (ignoreCase) { x = LowerByte(x); y = LowerByte(y); }
                if (x != y) return x < y ? -1 : 1;
                if (x == 0) return 0;
            }
            return 0;
        }

        private int CompareWide(uint a, uint b, uint max, bool ignoreCase)
        {
            for (uint n = 0; n < max; n++)
            {
                var x = (char)memory.Read16(a + n * 2);
                var y = (char)memory.Read16(b + n * 2);
                if (ignoreCase) { x = char.ToLowerInvariant(x); y = char.ToLowerInvariant(y); }
                if (x != y) return x < y ? -1 : 1;
                if (x == 0) return 0;
            }
            return 0;
        }

        private static uint Result(int comparison) => (uint)Math.Sign(comparison);

        // --- narrow strings and memory ----------------------------------------------

        private void InstallCrtStrings(GuestImports i)
        {
            C(i, "strlen", 1, c => StrLen(c.Arg(0)));
            C(i, "strnlen", 2, c => { uint n = 0; while (n < c.Arg(1) && memory.Read8(c.Arg(0) + n) != 0) n++; return n; });
            C(i, "strcpy", 2, c => { MoveMemory(c.Arg(0), c.Arg(1), StrLen(c.Arg(1)) + 1); return c.Arg(0); });
            C(i, "strcat", 2, c => { MoveMemory(c.Arg(0) + StrLen(c.Arg(0)), c.Arg(1), StrLen(c.Arg(1)) + 1); return c.Arg(0); });
            C(i, "strncpy", 3, c =>
            {
                uint n = 0, max = c.Arg(2);
                for (; n < max; n++) { var b = memory.Read8(c.Arg(1) + n); memory.Write8(c.Arg(0) + n, b); if (b == 0) break; }
                if (n < max) FillMemory(c.Arg(0) + n, max - n, 0);   // pads with NULs, as C requires
                return c.Arg(0);
            });
            C(i, "strncat", 3, c =>
            {
                var end = c.Arg(0) + StrLen(c.Arg(0));
                uint n = 0;
                for (; n < c.Arg(2); n++) { var b = memory.Read8(c.Arg(1) + n); if (b == 0) break; memory.Write8(end + n, b); }
                memory.Write8(end + n, 0);
                return c.Arg(0);
            });
            C(i, "strcmp", 2, c => Result(CompareBytes(c.Arg(0), c.Arg(1), uint.MaxValue, false)));
            C(i, "strncmp", 3, c => Result(CompareBytes(c.Arg(0), c.Arg(1), c.Arg(2), false)));
            foreach (var name in new[] { "_stricmp", "_strcmpi", "stricmp", "strcmpi", "_stricoll", "_stricmp_l" })
                C(i, name, 2, c => Result(CompareBytes(c.Arg(0), c.Arg(1), uint.MaxValue, true)));
            foreach (var name in new[] { "_strnicmp", "strnicmp", "_strnicoll", "_strnicmp_l" })
                C(i, name, 3, c => Result(CompareBytes(c.Arg(0), c.Arg(1), c.Arg(2), true)));
            C(i, "strcoll", 2, c => Result(CompareBytes(c.Arg(0), c.Arg(1), uint.MaxValue, false)));
            C(i, "_strncoll", 3, c => Result(CompareBytes(c.Arg(0), c.Arg(1), c.Arg(2), false)));
            C(i, "strxfrm", 3, c =>
            {
                var length = StrLen(c.Arg(1));
                if (length < c.Arg(2)) MoveMemory(c.Arg(0), c.Arg(1), length + 1);
                return length;
            });
            C(i, "strchr", 2, c =>
            {
                var ch = (byte)c.Arg(1);
                for (var p = c.Arg(0); ; p++) { var b = memory.Read8(p); if (b == ch) return p; if (b == 0) return 0; }
            });
            C(i, "strrchr", 2, c =>
            {
                var ch = (byte)c.Arg(1);
                uint found = 0;
                for (var p = c.Arg(0); ; p++) { var b = memory.Read8(p); if (b == ch) found = p; if (b == 0) return found; }
            });
            C(i, "strstr", 2, c =>
            {
                var hay = CBytes(c.Arg(0));
                var needle = CBytes(c.Arg(1));
                var at = IndexOf(hay, needle);
                return at < 0 ? 0 : c.Arg(0) + (uint)at;
            });
            C(i, "strpbrk", 2, c =>
            {
                var set = new HashSet<byte>(CBytes(c.Arg(1)));
                for (var p = c.Arg(0); ; p++) { var b = memory.Read8(p); if (b == 0) return 0; if (set.Contains(b)) return p; }
            });
            C(i, "strspn", 2, c => Span(c.Arg(0), c.Arg(1), true));
            C(i, "strcspn", 2, c => Span(c.Arg(0), c.Arg(1), false));
            C(i, "strtok", 2, c => StrTok(c.Arg(0), c.Arg(1), 0));
            C(i, "strtok_s", 3, c => StrTok(c.Arg(0), c.Arg(1), c.Arg(2)));
            foreach (var name in new[] { "_strdup", "strdup" })
                C(i, name, 1, c =>
                {
                    if (c.Arg(0) == 0) return 0;
                    var n = StrLen(c.Arg(0)) + 1;
                    var p = CrtAlloc(n, false);
                    if (p != 0) MoveMemory(p, c.Arg(0), n);
                    return p;
                });
            C(i, "_strlwr", 1, c => MapBytes(c.Arg(0), LowerByte));
            C(i, "_strupr", 1, c => MapBytes(c.Arg(0), UpperByte));
            C(i, "_strrev", 1, c =>
            {
                var b = CBytes(c.Arg(0));
                Array.Reverse(b);
                memory.WriteBytes(c.Arg(0), b);
                return c.Arg(0);
            });
            C(i, "_strset", 2, c => { FillMemory(c.Arg(0), StrLen(c.Arg(0)), (byte)c.Arg(1)); return c.Arg(0); });
            C(i, "_strnset", 3, c => { FillMemory(c.Arg(0), Math.Min(StrLen(c.Arg(0)), c.Arg(2)), (byte)c.Arg(1)); return c.Arg(0); });
            C(i, "strerror", 1, c => ErrorText((int)c.Arg(0), null));
            C(i, "_strerror", 1, c => ErrorText((int)memory.Read32(ErrnoSlot()), c.Arg(0) != 0 ? CString(c.Arg(0)) : null));
            C(i, "strcpy_s", 3, c => CopyS(c.Arg(0), c.Arg(1), c.Arg(2), StrLen(c.Arg(2)), false, 1));
            C(i, "strcat_s", 3, c =>
            {
                var used = StrLen(c.Arg(0));
                return CopyS(c.Arg(0) + used, c.Arg(1) - used, c.Arg(2), StrLen(c.Arg(2)), false, 1);
            });
            C(i, "strncpy_s", 4, c => CopyS(c.Arg(0), c.Arg(1), c.Arg(2), Math.Min(StrLen(c.Arg(2)), c.Arg(3)), false, 1));

            C(i, "memcpy", 3, c => { MoveMemory(c.Arg(0), c.Arg(1), c.Arg(2)); return c.Arg(0); });
            C(i, "memmove", 3, c => { MoveMemory(c.Arg(0), c.Arg(1), c.Arg(2)); return c.Arg(0); });
            C(i, "memset", 3, c => { FillMemory(c.Arg(0), c.Arg(2), (byte)c.Arg(1)); return c.Arg(0); });
            C(i, "memcmp", 3, c =>
            {
                for (uint n = 0; n < c.Arg(2); n++)
                {
                    var x = memory.Read8(c.Arg(0) + n); var y = memory.Read8(c.Arg(1) + n);
                    if (x != y) return x < y ? 0xFFFFFFFF : 1;
                }
                return 0;
            });
            C(i, "_memicmp", 3, c =>
            {
                for (uint n = 0; n < c.Arg(2); n++)
                {
                    var x = LowerByte(memory.Read8(c.Arg(0) + n)); var y = LowerByte(memory.Read8(c.Arg(1) + n));
                    if (x != y) return x < y ? 0xFFFFFFFF : 1;
                }
                return 0;
            });
            C(i, "memchr", 3, c =>
            {
                for (uint n = 0; n < c.Arg(2); n++) if (memory.Read8(c.Arg(0) + n) == (byte)c.Arg(1)) return c.Arg(0) + n;
                return 0;
            });
            C(i, "_memccpy", 4, c =>
            {
                for (uint n = 0; n < c.Arg(3); n++)
                {
                    var b = memory.Read8(c.Arg(1) + n);
                    memory.Write8(c.Arg(0) + n, b);
                    if (b == (byte)c.Arg(2)) return c.Arg(0) + n + 1;
                }
                return 0;
            });
            C(i, "memcpy_s", 4, c =>
            {
                if (c.Arg(3) > c.Arg(1)) { FillMemory(c.Arg(0), c.Arg(1), 0); return Erange; }
                MoveMemory(c.Arg(0), c.Arg(2), c.Arg(3));
                return 0;
            });
            C(i, "memmove_s", 4, c =>
            {
                if (c.Arg(3) > c.Arg(1)) return Erange;
                MoveMemory(c.Arg(0), c.Arg(2), c.Arg(3));
                return 0;
            });
            C(i, "qsort", 4, c => { QSort(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3)); return 0; });
            C(i, "bsearch", 5, c =>
            {
                uint low = 0, high = c.Arg(2);
                while (low < high)
                {
                    var mid = (low + high) / 2;
                    var element = c.Arg(1) + mid * c.Arg(3);
                    var order = (int)CallGuest(c.Arg(4), c.Arg(0), element);
                    if (order == 0) return element;
                    if (order < 0) high = mid; else low = mid + 1;
                }
                return 0;
            });
        }

        private static int IndexOf(byte[] hay, byte[] needle)
        {
            if (needle.Length == 0) return 0;
            for (var n = 0; n + needle.Length <= hay.Length; n++)
            {
                var k = 0;
                while (k < needle.Length && hay[n + k] == needle[k]) k++;
                if (k == needle.Length) return n;
            }
            return -1;
        }

        private uint Span(uint s, uint setPtr, bool inSet)
        {
            var set = new HashSet<byte>(CBytes(setPtr));
            uint n = 0;
            for (; ; n++)
            {
                var b = memory.Read8(s + n);
                if (b == 0 || set.Contains(b) != inSet) return n;
            }
        }

        private uint StrTok(uint s, uint delimiters, uint context)
        {
            var id = process.CurrentThread.Id;
            if (s == 0) s = context != 0 ? memory.Read32(context) : strtokNext.TryGetValue(id, out var next) ? next : 0;
            if (s == 0) return 0;
            var set = new HashSet<byte>(CBytes(delimiters));
            while (memory.Read8(s) != 0 && set.Contains(memory.Read8(s))) s++;
            if (memory.Read8(s) == 0) { Save(0); return 0; }
            var start = s;
            while (memory.Read8(s) != 0 && !set.Contains(memory.Read8(s))) s++;
            if (memory.Read8(s) != 0) { memory.Write8(s, 0); Save(s + 1); } else Save(0);
            return start;

            void Save(uint value)
            {
                if (context != 0) memory.Write32(context, value); else strtokNext[id] = value;
            }
        }

        private uint MapBytes(uint s, Func<byte, byte> map)
        {
            for (var p = s; ; p++)
            {
                var b = memory.Read8(p);
                if (b == 0) return s;
                memory.Write8(p, map(b));
            }
        }

        /// <summary>The _s copy forms: fails with ERANGE (and an empty destination) when the text and NUL do not fit.</summary>
        private uint CopyS(uint destination, uint size, uint source, uint count, bool wide, int _)
        {
            if (destination == 0 || size == 0) return Einval;
            if (source == 0) { WriteText(destination, "", wide); return Einval; }
            if (count + 1 > size) { WriteText(destination, "", wide); return Erange; }
            var unit = wide ? 2u : 1u;
            MoveMemory(destination, source, count * unit);
            if (wide) memory.Write16(destination + count * 2, 0); else memory.Write8(destination + count, 0);
            return 0;
        }

        private static readonly string[] ErrorMessages =
        {
            "No error", "Operation not permitted", "No such file or directory", "No such process",
            "Interrupted function call", "Input/output error", "No such device or address", "Arg list too long",
            "Exec format error", "Bad file descriptor", "No child processes", "Resource temporarily unavailable",
            "Not enough space", "Permission denied", "Bad address", "Unknown error", "Resource device",
            "File exists", "Improper link", "No such device", "Not a directory", "Is a directory",
            "Invalid argument", "Too many open files in system", "Too many open files",
            "Inappropriate I/O control operation", "Unknown error", "File too large", "No space left on device",
            "Invalid seek", "Read-only file system", "Too many links", "Broken pipe", "Domain error",
            "Result too large", "Unknown error", "Resource deadlock avoided", "Unknown error",
            "Filename too long", "No locks available", "Function not implemented", "Directory not empty",
            "Illegal byte sequence",
        };

        private uint ErrorText(int errno, string prefix)
        {
            var text = errno >= 0 && errno < ErrorMessages.Length ? ErrorMessages[errno] : "Unknown error";
            if (prefix != null) text = prefix + ": " + text + "\n";
            var buffer = heap.Alloc((uint)text.Length + 1);
            WriteText(buffer, text, false);
            return buffer;
        }

        /// <summary>A stable merge sort: the guest comparator decides, and an inconsistent one cannot break it.</summary>
        private void QSort(uint baseAddress, uint count, uint size, uint compare)
        {
            if (count < 2 || size == 0) return;
            var scratch = heap.Alloc(count * size);
            MoveMemory(scratch, baseAddress, count * size);
            var order = new int[count];
            for (var n = 0; n < order.Length; n++) order[n] = n;
            var temp = new int[count];
            for (var width = 1; width < order.Length; width *= 2)
            {
                for (var left = 0; left < order.Length; left += 2 * width)
                {
                    int mid = Math.Min(left + width, order.Length), right = Math.Min(left + 2 * width, order.Length);
                    int a = left, b = mid, k = left;
                    while (a < mid && b < right)
                    {
                        var r = (int)CallGuest(compare, scratch + (uint)order[a] * size, scratch + (uint)order[b] * size);
                        temp[k++] = r <= 0 ? order[a++] : order[b++];
                    }
                    while (a < mid) temp[k++] = order[a++];
                    while (b < right) temp[k++] = order[b++];
                }
                var swap = order; order = temp; temp = swap;
            }
            for (var n = 0; n < order.Length; n++)
                MoveMemory(baseAddress + (uint)n * size, scratch + (uint)order[n] * size, size);
            heap.Free(scratch);
        }

        // --- wide strings ---------------------------------------------------------------

        private void InstallCrtWideStrings(GuestImports i)
        {
            C(i, "wcslen", 1, c => WcsLen(c.Arg(0)));
            C(i, "wcsnlen", 2, c => { uint n = 0; while (n < c.Arg(1) && memory.Read16(c.Arg(0) + n * 2) != 0) n++; return n; });
            C(i, "wcscpy", 2, c => { MoveMemory(c.Arg(0), c.Arg(1), (WcsLen(c.Arg(1)) + 1) * 2); return c.Arg(0); });
            C(i, "wcscat", 2, c => { MoveMemory(c.Arg(0) + WcsLen(c.Arg(0)) * 2, c.Arg(1), (WcsLen(c.Arg(1)) + 1) * 2); return c.Arg(0); });
            C(i, "wcsncpy", 3, c =>
            {
                uint n = 0, max = c.Arg(2);
                for (; n < max; n++) { var w = memory.Read16(c.Arg(1) + n * 2); memory.Write16(c.Arg(0) + n * 2, w); if (w == 0) break; }
                if (n < max) FillMemory(c.Arg(0) + n * 2, (max - n) * 2, 0);
                return c.Arg(0);
            });
            C(i, "wcsncat", 3, c =>
            {
                var end = c.Arg(0) + WcsLen(c.Arg(0)) * 2;
                uint n = 0;
                for (; n < c.Arg(2); n++) { var w = memory.Read16(c.Arg(1) + n * 2); if (w == 0) break; memory.Write16(end + n * 2, w); }
                memory.Write16(end + n * 2, 0);
                return c.Arg(0);
            });
            C(i, "wcscmp", 2, c => Result(CompareWide(c.Arg(0), c.Arg(1), uint.MaxValue, false)));
            C(i, "wcsncmp", 3, c => Result(CompareWide(c.Arg(0), c.Arg(1), c.Arg(2), false)));
            foreach (var name in new[] { "_wcsicmp", "wcsicmp", "_wcsicoll" })
                C(i, name, 2, c => Result(CompareWide(c.Arg(0), c.Arg(1), uint.MaxValue, true)));
            foreach (var name in new[] { "_wcsnicmp", "wcsnicmp", "_wcsnicoll" })
                C(i, name, 3, c => Result(CompareWide(c.Arg(0), c.Arg(1), c.Arg(2), true)));
            C(i, "wcscoll", 2, c => Result(string.CompareOrdinal(WString(c.Arg(0)), WString(c.Arg(1)))));
            C(i, "wcsxfrm", 3, c =>
            {
                var length = WcsLen(c.Arg(1));
                if (length < c.Arg(2)) MoveMemory(c.Arg(0), c.Arg(1), (length + 1) * 2);
                return length;
            });
            C(i, "wcschr", 2, c =>
            {
                var ch = (ushort)c.Arg(1);
                for (var p = c.Arg(0); ; p += 2) { var w = memory.Read16(p); if (w == ch) return p; if (w == 0) return 0; }
            });
            C(i, "wcsrchr", 2, c =>
            {
                var ch = (ushort)c.Arg(1);
                uint found = 0;
                for (var p = c.Arg(0); ; p += 2) { var w = memory.Read16(p); if (w == ch) found = p; if (w == 0) return found; }
            });
            C(i, "wcsstr", 2, c =>
            {
                var at = WString(c.Arg(0)).IndexOf(WString(c.Arg(1)), StringComparison.Ordinal);
                return at < 0 ? 0 : c.Arg(0) + (uint)at * 2;
            });
            C(i, "wcspbrk", 2, c =>
            {
                var at = WString(c.Arg(0)).IndexOfAny(WString(c.Arg(1)).ToCharArray());
                return at < 0 ? 0 : c.Arg(0) + (uint)at * 2;
            });
            C(i, "wcsspn", 2, c => { var s = WString(c.Arg(0)); var set = WString(c.Arg(1)); var n = 0; while (n < s.Length && set.IndexOf(s[n]) >= 0) n++; return (uint)n; });
            C(i, "wcscspn", 2, c => { var s = WString(c.Arg(0)); var set = WString(c.Arg(1)); var n = 0; while (n < s.Length && set.IndexOf(s[n]) < 0) n++; return (uint)n; });
            C(i, "wcstok", 2, c => WcsTok(c.Arg(0), c.Arg(1)));
            C(i, "_wcsdup", 1, c =>
            {
                if (c.Arg(0) == 0) return 0;
                var n = (WcsLen(c.Arg(0)) + 1) * 2;
                var p = CrtAlloc(n, false);
                if (p != 0) MoveMemory(p, c.Arg(0), n);
                return p;
            });
            C(i, "_wcslwr", 1, c => { WriteText(c.Arg(0), WString(c.Arg(0)).ToLowerInvariant(), true); return c.Arg(0); });
            C(i, "_wcsupr", 1, c => { WriteText(c.Arg(0), WString(c.Arg(0)).ToUpperInvariant(), true); return c.Arg(0); });
            C(i, "_wcsrev", 1, c => { var a = WString(c.Arg(0)).ToCharArray(); Array.Reverse(a); WriteText(c.Arg(0), new string(a), true); return c.Arg(0); });
            C(i, "wcscpy_s", 3, c => CopyS(c.Arg(0), c.Arg(1), c.Arg(2), WcsLen(c.Arg(2)), true, 0));
            C(i, "wcscat_s", 3, c =>
            {
                var used = WcsLen(c.Arg(0));
                return CopyS(c.Arg(0) + used * 2, c.Arg(1) - used, c.Arg(2), WcsLen(c.Arg(2)), true, 0);
            });
            C(i, "wcsncpy_s", 4, c => CopyS(c.Arg(0), c.Arg(1), c.Arg(2), Math.Min(WcsLen(c.Arg(2)), c.Arg(3)), true, 0));
        }

        private uint WcsTok(uint s, uint delimiters)
        {
            var id = process.CurrentThread.Id;
            var key = id | 0x80000000;
            if (s == 0) s = strtokNext.TryGetValue(key, out var next) ? next : 0;
            if (s == 0) return 0;
            var set = WString(delimiters);
            while (memory.Read16(s) != 0 && set.IndexOf((char)memory.Read16(s)) >= 0) s += 2;
            if (memory.Read16(s) == 0) { strtokNext[key] = 0; return 0; }
            var start = s;
            while (memory.Read16(s) != 0 && set.IndexOf((char)memory.Read16(s)) < 0) s += 2;
            if (memory.Read16(s) != 0) { memory.Write16(s, 0); strtokNext[key] = s + 2; } else strtokNext[key] = 0;
            return start;
        }

        // --- ctype -----------------------------------------------------------------------

        private ushort CtypeOf(uint ch) => ch == 0xFFFFFFFF ? (ushort)0 : ch < 256 ? memory.Read16(ctypeTable + 2 + ch * 2) : (ushort)0;

        private static ushort WctypeOf(uint ch) =>
            ch < 128 ? ClassicCtype((int)ch) : ch == 0xFFFF || ch > 0xFFFF ? (ushort)0 : (ushort)(CharType1((char)ch) & 0x1FF);

        private void InstallCrtCtype(GuestImports i)
        {
            void Class(string narrow, string wide, ushort mask)
            {
                C(i, narrow, 1, c => (uint)(CtypeOf(c.Arg(0)) & mask));
                if (wide != null) C(i, wide, 1, c => (uint)(WctypeOf(c.Arg(0) & 0xFFFF) & mask));
            }
            Class("isalpha", "iswalpha", 0x103);
            Class("isupper", "iswupper", 0x001);
            Class("islower", "iswlower", 0x002);
            Class("isdigit", "iswdigit", 0x004);
            Class("isxdigit", "iswxdigit", 0x080);
            Class("isspace", "iswspace", 0x008);
            Class("ispunct", "iswpunct", 0x010);
            Class("isalnum", "iswalnum", 0x107);
            Class("isprint", "iswprint", 0x157);
            Class("isgraph", "iswgraph", 0x117);
            Class("iscntrl", "iswcntrl", 0x020);
            Class("isblank", "iswblank", 0x040);
            C(i, "_isctype", 2, c => (uint)(CtypeOf(c.Arg(0)) & c.Arg(1)));
            C(i, "iswctype", 2, c => (uint)(WctypeOf(c.Arg(0) & 0xFFFF) & c.Arg(1)));
            C(i, "is_wctype", 2, c => (uint)(WctypeOf(c.Arg(0) & 0xFFFF) & c.Arg(1)));
            foreach (var name in new[] { "isascii", "__isascii", "iswascii" }) C(i, name, 1, c => c.Arg(0) < 128 ? 1u : 0u);
            foreach (var name in new[] { "toascii", "__toascii" }) C(i, name, 1, c => c.Arg(0) & 0x7F);
            foreach (var name in new[] { "iscsym", "__iscsym" }) C(i, name, 1, c => (CtypeOf(c.Arg(0)) & 0x107) != 0 || c.Arg(0) == '_' ? 1u : 0u);
            foreach (var name in new[] { "iscsymf", "__iscsymf" }) C(i, name, 1, c => (CtypeOf(c.Arg(0)) & 0x103) != 0 || c.Arg(0) == '_' ? 1u : 0u);
            C(i, "toupper", 1, c => c.Arg(0) >= 'a' && c.Arg(0) <= 'z' ? c.Arg(0) - 32 : c.Arg(0));
            C(i, "tolower", 1, c => c.Arg(0) >= 'A' && c.Arg(0) <= 'Z' ? c.Arg(0) + 32 : c.Arg(0));
            C(i, "_toupper", 1, c => c.Arg(0) - 32);   // unchecked forms: the caller knows the case
            C(i, "_tolower", 1, c => c.Arg(0) + 32);
            C(i, "towupper", 1, c => char.ToUpperInvariant((char)c.Arg(0)));
            C(i, "towlower", 1, c => char.ToLowerInvariant((char)c.Arg(0)));
        }

        // --- conversions ---------------------------------------------------------------------

        private void InstallCrtConversions(GuestImports i)
        {
            C(i, "atoi", 1, c => (uint)(int)ParseInteger(CString(c.Arg(0)), 10, true, 32, out _, false));
            C(i, "atol", 1, c => (uint)(int)ParseInteger(CString(c.Arg(0)), 10, true, 32, out _, false));
            C(i, "_atoi64", 1, c => (ulong)ParseInteger(CString(c.Arg(0)), 10, true, 64, out _, false));
            C(i, "_wtoi", 1, c => (uint)(int)ParseInteger(WString(c.Arg(0)), 10, true, 32, out _, false));
            C(i, "_wtol", 1, c => (uint)(int)ParseInteger(WString(c.Arg(0)), 10, true, 32, out _, false));
            C(i, "_wtoi64", 1, c => (ulong)ParseInteger(WString(c.Arg(0)), 10, true, 64, out _, false));
            C(i, "atof", 1, c => ReturnDouble(ParseDouble(CString(c.Arg(0)), out _)));
            C(i, "_wtof", 1, c => ReturnDouble(ParseDouble(WString(c.Arg(0)), out _)));
            C(i, "_atodbl", 2, c => { memory.Write64(c.Arg(0), (ulong)BitConverter.DoubleToInt64Bits(ParseDouble(CString(c.Arg(1)), out _))); return 0; });
            C(i, "strtol", 3, c => StrTo(c, false, true, 32));
            C(i, "strtoul", 3, c => StrTo(c, false, false, 32));
            C(i, "_strtoi64", 3, c => StrTo(c, false, true, 64));
            C(i, "_strtoui64", 3, c => StrTo(c, false, false, 64));
            C(i, "wcstol", 3, c => StrTo(c, true, true, 32));
            C(i, "wcstoul", 3, c => StrTo(c, true, false, 32));
            C(i, "_wcstoi64", 3, c => StrTo(c, true, true, 64));
            C(i, "_wcstoui64", 3, c => StrTo(c, true, false, 64));
            C(i, "strtod", 2, c => StrToD(c, false));
            C(i, "wcstod", 2, c => StrToD(c, true));
            C(i, "_itoa", 3, c => IntToText(c.Arg(0), c.Arg(1), c.Arg(2), false, true));
            C(i, "_ltoa", 3, c => IntToText(c.Arg(0), c.Arg(1), c.Arg(2), false, true));
            C(i, "_ultoa", 3, c => IntToText(c.Arg(0), c.Arg(1), c.Arg(2), false, false));
            C(i, "_i64toa", 4, c => IntToText((long)c.Arg64(0), c.Arg(2), c.Arg(3), false, true));
            C(i, "_ui64toa", 4, c => IntToText((long)c.Arg64(0), c.Arg(2), c.Arg(3), false, false));
            C(i, "_itow", 3, c => IntToText(c.Arg(0), c.Arg(1), c.Arg(2), true, true));
            C(i, "_ltow", 3, c => IntToText(c.Arg(0), c.Arg(1), c.Arg(2), true, true));
            C(i, "_ultow", 3, c => IntToText(c.Arg(0), c.Arg(1), c.Arg(2), true, false));
            C(i, "_i64tow", 4, c => IntToText((long)c.Arg64(0), c.Arg(2), c.Arg(3), true, true));
            C(i, "_ui64tow", 4, c => IntToText((long)c.Arg64(0), c.Arg(2), c.Arg(3), true, false));
            C(i, "_gcvt", 4, c =>
            {
                WriteText(c.Arg(3), FormatFloat(D(c, 0), 'g', (int)c.Arg(2), false, false), false);
                return c.Arg(3);
            });
            C(i, "_ecvt", 4, c => Cvt(D(c, 0), (int)c.Arg(2), c.Arg(3), c.Arg(4), false));
            C(i, "_fcvt", 4, c => Cvt(D(c, 0), (int)c.Arg(2), c.Arg(3), c.Arg(4), true));

            // Multibyte: the "C" locale's code page is single-byte (1252 here).
            C(i, "mbstowcs", 3, c => MbsToWcs(c.Arg(0), c.Arg(1), c.Arg(2)));
            C(i, "wcstombs", 3, c => WcsToMbs(c.Arg(0), c.Arg(1), c.Arg(2)));
            C(i, "mbtowc", 3, c =>
            {
                if (c.Arg(1) == 0 || c.Arg(2) == 0) return 0;
                var b = memory.Read8(c.Arg(1));
                if (c.Arg(0) != 0) memory.Write16(c.Arg(0), Ansi.Decode(new[] { b })[0]);
                return b == 0 ? 0u : 1u;
            });
            C(i, "wctomb", 2, c =>
            {
                if (c.Arg(0) == 0) return 0;
                memory.Write8(c.Arg(0), Ansi.Encode(((char)c.Arg(1)).ToString())[0]);
                return 1;
            });
            C(i, "mblen", 2, c => c.Arg(0) == 0 || c.Arg(1) == 0 ? 0u : memory.Read8(c.Arg(0)) == 0 ? 0u : 1u);
            C(i, "_mbstrlen", 1, c => StrLen(c.Arg(0)));
            C(i, "btowc", 1, c => c.Arg(0) == 0xFFFFFFFF ? 0xFFFFu : Ansi.Decode(new[] { (byte)c.Arg(0) })[0]);
            C(i, "wctob", 1, c => c.Arg(0) < 256 ? c.Arg(0) : 0xFFFFFFFF);
        }

        private uint MbsToWcs(uint destination, uint source, uint count)
        {
            var text = CString(source);
            if (destination == 0) return (uint)text.Length;
            var n = (uint)Math.Min(text.Length, (int)Math.Min(count, int.MaxValue));
            for (uint k = 0; k < n; k++) memory.Write16(destination + k * 2, text[(int)k]);
            if (n < count) memory.Write16(destination + n * 2, 0);
            return n;
        }

        private uint WcsToMbs(uint destination, uint source, uint count)
        {
            var bytes = Ansi.Encode(WString(source));
            if (destination == 0) return (uint)bytes.Length;
            var n = (uint)Math.Min(bytes.Length, (int)Math.Min(count, int.MaxValue));
            memory.WriteBytes(destination, bytes, 0, (int)n);
            if (n < count) memory.Write8(destination + n, 0);
            return n;
        }

        /// <summary>
        /// strtol's grammar: spaces, a sign, a base prefix (0x, 0 for base 0 /
        /// 16 / 8), digits. Out of range saturates (and sets ERANGE when the
        /// caller wants errno); an unsigned conversion of "-1" wraps.
        /// </summary>
        private long ParseInteger(string text, int radix, bool signed, int bits, out int consumed, bool setErrno)
        {
            consumed = 0;
            if (text == null) return 0;
            var n = 0;
            while (n < text.Length && (text[n] == ' ' || (text[n] >= '\t' && text[n] <= '\r'))) n++;
            var negative = false;
            if (n < text.Length && (text[n] == '+' || text[n] == '-')) { negative = text[n] == '-'; n++; }
            if ((radix == 0 || radix == 16) && n + 1 < text.Length && text[n] == '0' && (text[n + 1] == 'x' || text[n + 1] == 'X') &&
                n + 2 < text.Length && HexDigit(text[n + 2]) >= 0)
            {
                n += 2;
                radix = 16;
            }
            else if (radix == 0) radix = n < text.Length && text[n] == '0' ? 8 : 10;
            if (radix < 2 || radix > 36) return 0;

            ulong value = 0;
            var overflow = false;
            var start = n;
            var limit = bits == 64 ? ulong.MaxValue : uint.MaxValue;
            for (; n < text.Length; n++)
            {
                var ch = text[n];
                int digit = ch >= '0' && ch <= '9' ? ch - '0' : ch >= 'a' && ch <= 'z' ? ch - 'a' + 10 : ch >= 'A' && ch <= 'Z' ? ch - 'A' + 10 : 99;
                if (digit >= radix) break;
                if (value > (limit - (ulong)digit) / (ulong)radix) overflow = true;
                else value = value * (ulong)radix + (ulong)digit;
            }
            if (n == start) return 0;   // no digits: nothing consumed
            consumed = n;

            if (signed)
            {
                var max = bits == 64 ? (ulong)long.MaxValue : int.MaxValue;
                if (overflow || value > max + (negative ? 1UL : 0UL))
                {
                    if (setErrno) SetErrno(Erange);
                    return negative ? (bits == 64 ? long.MinValue : int.MinValue) : (long)max;
                }
                return negative ? -(long)value : (long)value;
            }
            if (overflow)
            {
                if (setErrno) SetErrno(Erange);
                return unchecked((long)limit);
            }
            return negative ? unchecked((long)((0 - value) & limit)) : (long)value;
        }

        private ulong StrTo(GuestCall c, bool wide, bool signed, int bits)
        {
            var text = wide ? WString(c.Arg(0)) : CString(c.Arg(0));
            var value = ParseInteger(text, (int)c.Arg(2), signed, bits, out var consumed, true);
            if (c.Arg(1) != 0) memory.Write32(c.Arg(1), c.Arg(0) + (uint)consumed * (wide ? 2u : 1u));
            return bits == 64 ? (ulong)value : (uint)value;
        }

        /// <summary>strtod's grammar: spaces, sign, digits with one point, an exponent; also inf/infinity/nan.</summary>
        private static double ParseDouble(string text, out int consumed)
        {
            consumed = 0;
            if (text == null) return 0;
            var n = 0;
            while (n < text.Length && (text[n] == ' ' || (text[n] >= '\t' && text[n] <= '\r'))) n++;
            var start = n;
            if (n < text.Length && (text[n] == '+' || text[n] == '-')) n++;
            var rest = text.Substring(n);
            if (rest.StartsWith("inf", StringComparison.OrdinalIgnoreCase))
            {
                consumed = n + (rest.StartsWith("infinity", StringComparison.OrdinalIgnoreCase) ? 8 : 3);
                return text[start] == '-' ? double.NegativeInfinity : double.PositiveInfinity;
            }
            if (rest.StartsWith("nan", StringComparison.OrdinalIgnoreCase)) { consumed = n + 3; return double.NaN; }

            var digits = 0;
            while (n < text.Length && char.IsDigit(text[n]) && text[n] < 128) { n++; digits++; }
            if (n < text.Length && text[n] == '.') { n++; while (n < text.Length && text[n] >= '0' && text[n] <= '9') { n++; digits++; } }
            if (digits == 0) return 0;
            if (n < text.Length && (text[n] == 'e' || text[n] == 'E'))
            {
                var e = n + 1;
                if (e < text.Length && (text[e] == '+' || text[e] == '-')) e++;
                if (e < text.Length && text[e] >= '0' && text[e] <= '9')
                {
                    while (e < text.Length && text[e] >= '0' && text[e] <= '9') e++;
                    n = e;
                }
            }
            consumed = n;
            double.TryParse(text.Substring(start, n - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var value);
            return value;
        }

        private ulong StrToD(GuestCall c, bool wide)
        {
            var text = wide ? WString(c.Arg(0)) : CString(c.Arg(0));
            var value = ParseDouble(text, out var consumed);
            if (double.IsInfinity(value) && consumed > 0 && text.IndexOf("inf", StringComparison.OrdinalIgnoreCase) < 0) SetErrno(Erange);
            if (c.Arg(1) != 0) memory.Write32(c.Arg(1), c.Arg(0) + (uint)consumed * (wide ? 2u : 1u));
            return ReturnDouble(value);
        }

        private uint IntToText(long value, uint buffer, uint radix, bool wide, bool signed)
        {
            if (radix < 2 || radix > 36) { SetErrno(Einval); return buffer; }
            var negative = signed && radix == 10 && value < 0;
            var magnitude = negative ? (ulong)-value : (ulong)value;
            if (!signed || radix != 10) magnitude = (ulong)value;
            var s = new StringBuilder();
            do { var d = (int)(magnitude % radix); s.Insert(0, (char)(d < 10 ? '0' + d : 'a' + d - 10)); magnitude /= radix; } while (magnitude != 0);
            if (negative) s.Insert(0, '-');
            WriteText(buffer, s.ToString(), wide);
            return buffer;
        }

        private uint IntToText(uint value, uint buffer, uint radix, bool wide, bool signed) =>
            IntToText(signed && radix == 10 ? (long)(int)value : value, buffer, radix, wide, signed);

        /// <summary>_ecvt / _fcvt: the digits alone, with the decimal point's position and the sign reported separately.</summary>
        private uint Cvt(double value, int count, uint decimalOut, uint signOut, bool fixedPoint)
        {
            memory.Write32(signOut, value < 0 ? 1u : 0u);
            value = Math.Abs(value);
            string digits;
            int point;
            if (value == 0) { digits = new string('0', Math.Max(count, 0)); point = fixedPoint ? 0 : 1; }
            else
            {
                var e = value.ToString("E" + Math.Max(0, (fixedPoint ? 17 : count) - 1), CultureInfo.InvariantCulture);
                var exponent = int.Parse(e.Substring(e.IndexOf('E') + 1), CultureInfo.InvariantCulture);
                point = exponent + 1;
                if (fixedPoint)
                {
                    var f = value.ToString("F" + Math.Max(count, 0), CultureInfo.InvariantCulture).Replace(".", "");
                    digits = f.TrimStart('0');
                    if (digits.Length == 0) { digits = ""; point = -count; }
                    else point = digits.Length - Math.Max(count, 0);
                }
                else digits = e.Substring(0, e.IndexOf('E')).Replace(".", "");
            }
            memory.Write32(decimalOut, (uint)point);
            var buffer = crtScratch + 16;   // a static buffer, as the CRT's own
            WriteText(buffer, digits.Length > 40 ? digits.Substring(0, 40) : digits, false);
            return buffer;
        }

        // --- math --------------------------------------------------------------------------

        private void InstallCrtMath(GuestImports i)
        {
            void Unary(string name, Func<double, double> f) => C(i, name, 2, c => ReturnDouble(f(D(c, 0))));
            void Binary(string name, Func<double, double, double> f) => C(i, name, 4, c => ReturnDouble(f(D(c, 0), D(c, 2))));
            Unary("sin", Math.Sin); Unary("cos", Math.Cos); Unary("tan", Math.Tan);
            Unary("asin", Math.Asin); Unary("acos", Math.Acos); Unary("atan", Math.Atan);
            Unary("sinh", Math.Sinh); Unary("cosh", Math.Cosh); Unary("tanh", Math.Tanh);
            Unary("exp", Math.Exp); Unary("log", Math.Log); Unary("log10", Math.Log10);
            Unary("sqrt", Math.Sqrt); Unary("fabs", Math.Abs); Unary("ceil", Math.Ceiling); Unary("floor", Math.Floor);
            Unary("_logb", x => x == 0 ? double.NegativeInfinity : Math.Floor(Math.Log(Math.Abs(x), 2)));
            Binary("atan2", Math.Atan2); Binary("pow", Math.Pow); Binary("fmod", (x, y) => x % y);
            Binary("_hypot", (x, y) => Math.Sqrt(x * x + y * y)); Binary("hypot", (x, y) => Math.Sqrt(x * x + y * y));
            Binary("_copysign", (x, y) => Math.Abs(x) * (y < 0 || (y == 0 && 1 / y < 0) ? -1 : 1));
            Binary("_nextafter", NextAfter);
            Unary("_chgsign", x => -x);
            C(i, "_scalb", 3, c => ReturnDouble(D(c, 0) * Math.Pow(2, (int)c.Arg(2))));
            C(i, "ldexp", 3, c => ReturnDouble(D(c, 0) * Math.Pow(2, (int)c.Arg(2))));
            C(i, "frexp", 3, c =>
            {
                var x = D(c, 0);
                var e = 0;
                if (x != 0 && !double.IsInfinity(x) && !double.IsNaN(x))
                {
                    e = (int)Math.Floor(Math.Log(Math.Abs(x), 2)) + 1;
                    x /= Math.Pow(2, e);
                    if (Math.Abs(x) >= 1) { x /= 2; e++; }
                    if (Math.Abs(x) < 0.5) { x *= 2; e--; }
                }
                memory.Write32(c.Arg(2), (uint)e);
                return ReturnDouble(x);
            });
            C(i, "modf", 3, c =>
            {
                var x = D(c, 0);
                var whole = Math.Truncate(x);
                memory.Write64(c.Arg(2), (ulong)BitConverter.DoubleToInt64Bits(whole));
                return ReturnDouble(x - whole);
            });
            C(i, "_cabs", 4, c => ReturnDouble(Math.Sqrt(D(c, 0) * D(c, 0) + D(c, 2) * D(c, 2))));
            C(i, "_isnan", 2, c => double.IsNaN(D(c, 0)) ? 1u : 0u);
            C(i, "_finite", 2, c => double.IsNaN(D(c, 0)) || double.IsInfinity(D(c, 0)) ? 0u : 1u);
            C(i, "_fpclass", 2, c => FpClass(D(c, 0)));
            C(i, "abs", 1, c => (uint)Math.Abs((long)(int)c.Arg(0)));
            C(i, "labs", 1, c => (uint)Math.Abs((long)(int)c.Arg(0)));
            C(i, "_abs64", 2, c => (ulong)((long)c.Arg64(0) < 0 ? -(long)c.Arg64(0) : (long)c.Arg64(0)));
            C(i, "div", 2, c => DivResult((int)c.Arg(0), (int)c.Arg(1)));
            C(i, "ldiv", 2, c => DivResult((int)c.Arg(0), (int)c.Arg(1)));
            C(i, "rand", 0, c =>
            {
                var id = process.CurrentThread.Id;
                if (!randSeeds.TryGetValue(id, out var seed)) seed = randSeedDefault;
                seed = unchecked(seed * 214013 + 2531011);
                randSeeds[id] = seed;
                return (uint)(seed >> 16) & 0x7FFF;
            });
            C(i, "srand", 1, c => { randSeeds[process.CurrentThread.Id] = (int)c.Arg(0); return 0; });
            C(i, "_rotl", 2, c => (c.Arg(0) << (int)(c.Arg(1) & 31)) | (c.Arg(0) >> (int)((32 - c.Arg(1)) & 31)));
            C(i, "_lrotl", 2, c => (c.Arg(0) << (int)(c.Arg(1) & 31)) | (c.Arg(0) >> (int)((32 - c.Arg(1)) & 31)));
            C(i, "_rotr", 2, c => (c.Arg(0) >> (int)(c.Arg(1) & 31)) | (c.Arg(0) << (int)((32 - c.Arg(1)) & 31)));
            C(i, "_lrotr", 2, c => (c.Arg(0) >> (int)(c.Arg(1) & 31)) | (c.Arg(0) << (int)((32 - c.Arg(1)) & 31)));

            // Floating-point control.
            C(i, "_controlfp", 2, c => ControlFp(c.Arg(0), c.Arg(1) & ~0x00040000u));   // _MCW_IC is ignored on x86 msvcrt too
            C(i, "_control87", 2, c => ControlFp(c.Arg(0), c.Arg(1)));
            C(i, "__control87_2", 4, c =>
            {
                if (c.Arg(2) != 0) memory.Write32(c.Arg(2), ControlFp(c.Arg(0), c.Arg(1)));
                if (c.Arg(3) != 0) memory.Write32(c.Arg(3), controlWord);
                return 1;
            });
            C(i, "_controlfp_s", 3, c =>
            {
                var value = ControlFp(c.Arg(1), c.Arg(2));
                if (c.Arg(0) != 0) memory.Write32(c.Arg(0), value);
                return 0;
            });
            C(i, "_clearfp", 0, c => 0);
            C(i, "_statusfp", 0, c => 0);
            C(i, "_fpreset", 0, c => { ControlFp(0x0009001F, 0xFFFFFFFF); return 0; });
            C(i, "__fpecode", 0, c => crtScratch + 60);

            // The x87 intrinsics: operands on the FPU stack, result in ST(0).
            var fpu = process.Interpreter.Fpu;
            void Fpu1(string name, Func<double, double> f) => C(i, name, 0, c => { var x = fpu.St(0); fpu.Pop(); return ReturnDouble(f(x)); });
            void Fpu2(string name, Func<double, double, double> f) => C(i, name, 0, c =>
            {
                var y = fpu.St(0); var x = fpu.St(1);
                fpu.Pop(); fpu.Pop();
                return ReturnDouble(f(x, y));
            });
            Fpu1("_CIsin", Math.Sin); Fpu1("_CIcos", Math.Cos); Fpu1("_CItan", Math.Tan);
            Fpu1("_CIasin", Math.Asin); Fpu1("_CIacos", Math.Acos); Fpu1("_CIatan", Math.Atan);
            Fpu1("_CIsinh", Math.Sinh); Fpu1("_CIcosh", Math.Cosh); Fpu1("_CItanh", Math.Tanh);
            Fpu1("_CIexp", Math.Exp); Fpu1("_CIlog", Math.Log); Fpu1("_CIlog10", Math.Log10); Fpu1("_CIsqrt", Math.Sqrt);
            Fpu2("_CIpow", Math.Pow); Fpu2("_CIatan2", Math.Atan2); Fpu2("_CIfmod", (x, y) => x % y);
            HostCall ftol = c =>
            {
                var x = fpu.St(0);
                fpu.Pop();
                if (double.IsNaN(x) || x >= 9.2233720368547758E18 || x < -9.2233720368547758E18) return 0x8000000000000000UL;
                return (ulong)(long)Math.Truncate(x);
            };
            C(i, "_ftol", 0, ftol);
            C(i, "_ftol2", 0, ftol);
            C(i, "_ftol2_sse", 0, ftol);
            C(i, "_ftoul", 0, ftol);

            // 64-bit arithmetic helpers: two 64-bit operands, callee pops 16 bytes.
            void Long(string name, Func<ulong, ulong, ulong> f) =>
                i.Register(Crt, name, CallConv.Stdcall, 4, c => f(c.Arg64(0), c.Arg64(2)));
            Long("_allmul", (a, b) => unchecked(a * b));
            Long("_alldiv", (a, b) => (ulong)((long)a / (long)b));
            Long("_allrem", (a, b) => (ulong)((long)a % (long)b));
            Long("_aulldiv", (a, b) => a / b);
            Long("_aullrem", (a, b) => a % b);
            // Shifts: value in EDX:EAX, count in CL.
            void Shift(string name, Func<ulong, int, ulong> f) => C(i, name, 0, c =>
            {
                var cpu = process.Cpu;
                return f(((ulong)cpu.Edx << 32) | cpu.Eax, (int)(cpu.Ecx & 0xFF));
            });
            Shift("_allshl", (v, n) => n >= 64 ? 0 : v << n);
            Shift("_allshr", (v, n) => (ulong)((long)v >> Math.Min(n, 63)));
            Shift("_aullshr", (v, n) => n >= 64 ? 0 : v >> n);
        }

        private static double NextAfter(double x, double y)
        {
            if (double.IsNaN(x) || double.IsNaN(y)) return double.NaN;
            if (x == y) return y;
            if (x == 0) return y > 0 ? double.Epsilon : -double.Epsilon;
            var bits = BitConverter.DoubleToInt64Bits(x);
            bits += (x < y) == (x > 0) ? 1 : -1;
            return BitConverter.Int64BitsToDouble(bits);
        }

        private static uint FpClass(double x)
        {
            if (double.IsNaN(x)) return 0x0002;                                  // _FPCLASS_QNAN
            if (double.IsNegativeInfinity(x)) return 0x0004;                     // _FPCLASS_NINF
            if (double.IsPositiveInfinity(x)) return 0x0200;                     // _FPCLASS_PINF
            var negative = BitConverter.DoubleToInt64Bits(x) < 0;
            if (x == 0) return negative ? 0x0020u : 0x0040u;                     // _FPCLASS_NZ / PZ
            if (Math.Abs(x) < 2.2250738585072014E-308) return negative ? 0x0010u : 0x0080u;   // denormals
            return negative ? 0x0008u : 0x0100u;                                 // _FPCLASS_NN / PN
        }

        private static ulong DivResult(int numerator, int denominator)
        {
            if (denominator == 0) return 0;
            var q = numerator / denominator;
            var r = numerator % denominator;
            return (uint)q | ((ulong)(uint)r << 32);   // div_t in EDX:EAX
        }

        /// <summary>
        /// _controlfp: the CRT's control word, mapped onto the x87 control word
        /// (masks, precision, rounding) and the SSE rounding bits.
        /// </summary>
        private uint ControlFp(uint value, uint mask)
        {
            controlWord = (controlWord & ~mask) | (value & mask);
            var fpu = process.Interpreter.Fpu;
            var x87 = 0;
            if ((controlWord & 0x10) != 0) x87 |= 0x01;      // invalid
            if ((controlWord & 0x80000) != 0) x87 |= 0x02;   // denormal
            if ((controlWord & 0x08) != 0) x87 |= 0x04;      // zero divide
            if ((controlWord & 0x04) != 0) x87 |= 0x08;      // overflow
            if ((controlWord & 0x02) != 0) x87 |= 0x10;      // underflow
            if ((controlWord & 0x01) != 0) x87 |= 0x20;      // inexact
            switch (controlWord & 0x30000)
            {
                case 0x20000: break;                          // 24 bits
                case 0x10000: x87 |= 0x200; break;            // 53 bits
                default: x87 |= 0x300; break;                 // 64 bits
            }
            var rounding = (int)(controlWord >> 8) & 3;       // near, down, up, chop: the same order in both words
            x87 |= rounding << 10;
            fpu.Control = (ushort)(x87 | 0x40);
            fpu.Mxcsr = (fpu.Mxcsr & ~0x6000u) | ((uint)rounding << 13);
            return controlWord;
        }
    }
}
