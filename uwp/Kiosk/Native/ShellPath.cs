using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Kiosk.Native
{
    /// <summary>
    /// The path and string helpers of SHLWAPI, which the console does not have.
    ///
    /// Stubbed, they all answered zero, and zero from PathFileExistsW means
    /// "no such file": Adobe AIR asked whether its application descriptor
    /// existed, was told no, and gave up on a file that was right there.
    /// These are small, exactly specified functions, so they are answered for
    /// real rather than guessed at.
    /// </summary>
    internal static class ShellPath
    {
        private const int MaxPath = 260;
        private const int E_FAIL = unchecked((int)0x80004005);
        private const int E_NOINTERFACE = unchecked((int)0x80004002);
        private const int E_POINTER = unchecked((int)0x80004003);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int OnePtr(IntPtr a);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int TwoPtr(IntPtr a, IntPtr b);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr FindDelegate(IntPtr a, IntPtr b);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr FindLastDelegate(IntPtr start, IntPtr end, IntPtr search);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr DupDelegate(IntPtr a);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int AssocDelegate(uint flags, uint str, IntPtr assoc, IntPtr extra, IntPtr output, IntPtr size);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr MemStreamDelegate(IntPtr init, uint size);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int QiSearchDelegate(IntPtr self, IntPtr table, IntPtr riid, IntPtr result);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint AddRefDelegate(IntPtr self);

        private static readonly List<Delegate> roots = new List<Delegate>();

        private static string Wide(IntPtr text) => text == IntPtr.Zero ? null : Marshal.PtrToStringUni(text);

        private static void WriteWide(IntPtr buffer, string text)
        {
            var fit = Math.Min(text.Length, MaxPath - 1);
            for (var i = 0; i < fit; i++) Marshal.WriteInt16(buffer, i * 2, text[i]);
            Marshal.WriteInt16(buffer, fit * 2, 0);
        }

        private static bool Exists(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                var clean = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path.Substring(4) : path;
                return FileWatch.PathExists(clean);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>PathAppend: joins with one backslash, and ".." walks up.</summary>
        private static string Append(string path, string more)
        {
            path = path ?? string.Empty;
            more = more ?? string.Empty;
            if (more.Length > 0 && (more.StartsWith("\\") || (more.Length > 1 && more[1] == ':')))
                return more.Length > 1 && more[1] == ':' ? more : path.TrimEnd('\\') + more;
            var parts = new List<string>(path.TrimEnd('\\').Split('\\'));
            foreach (var piece in more.Split('\\'))
            {
                if (piece.Length == 0 || piece == ".") continue;
                if (piece == "..") { if (parts.Count > 1) parts.RemoveAt(parts.Count - 1); continue; }
                parts.Add(piece);
            }
            return string.Join("\\", parts);
        }

        private static IntPtr Keep(Delegate function)
        {
            roots.Add(function);
            return Marshal.GetFunctionPointerForDelegate(function);
        }

        public static void Install(SystemImports imports)
        {
            var ours = new Dictionary<string, IntPtr>
            {
                ["PathFileExistsW"] = Keep(new OnePtr(path => Exists(Wide(path)) ? 1 : 0)),
                ["PathFileExistsA"] = Keep(new OnePtr(path => Exists(Marshal.PtrToStringAnsi(path)) ? 1 : 0)),
                ["PathRemoveFileSpecW"] = Keep(new OnePtr(buffer =>
                {
                    var text = Wide(buffer);
                    if (string.IsNullOrEmpty(text)) return 0;
                    var cut = text.LastIndexOf('\\');
                    if (cut < 0) { Marshal.WriteInt16(buffer, 0, 0); return 1; }
                    // "C:\" keeps its backslash; anything deeper loses the last part.
                    var kept = cut == 2 && text.Length > 1 && text[1] == ':' ? text.Substring(0, 3) : text.Substring(0, cut);
                    if (kept == text) return 0;
                    WriteWide(buffer, kept);
                    return 1;
                })),
                ["PathAppendW"] = Keep(new TwoPtr((buffer, more) =>
                {
                    if (buffer == IntPtr.Zero) return 0;
                    WriteWide(buffer, Append(Wide(buffer), Wide(more)));
                    return 1;
                })),
                ["PathAppendA"] = Keep(new TwoPtr((buffer, more) =>
                {
                    if (buffer == IntPtr.Zero) return 0;
                    var joined = Append(Marshal.PtrToStringAnsi(buffer), Marshal.PtrToStringAnsi(more));
                    var bytes = Encoding.Default.GetBytes(joined);
                    var fit = Math.Min(bytes.Length, MaxPath - 1);
                    Marshal.Copy(bytes, 0, buffer, fit);
                    Marshal.WriteByte(buffer, fit, 0);
                    return 1;
                })),
                ["StrStrIW"] = Keep(new FindDelegate((text, search) =>
                {
                    var haystack = Wide(text);
                    var needle = Wide(search);
                    if (haystack == null || needle == null) return IntPtr.Zero;
                    var at = haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
                    return at < 0 ? IntPtr.Zero : text + at * 2;
                })),
                ["StrRStrIW"] = Keep(new FindLastDelegate((start, end, search) =>
                {
                    var haystack = Wide(start);
                    var needle = Wide(search);
                    if (haystack == null || needle == null) return IntPtr.Zero;
                    if (end != IntPtr.Zero)
                    {
                        var length = (int)((end.ToInt64() - start.ToInt64()) / 2);
                        if (length >= 0 && length < haystack.Length) haystack = haystack.Substring(0, length);
                    }
                    var at = haystack.LastIndexOf(needle, StringComparison.OrdinalIgnoreCase);
                    return at < 0 ? IntPtr.Zero : start + at * 2;
                })),
                ["StrDupW"] = Keep(new DupDelegate(text =>
                {
                    var value = Wide(text);
                    if (value == null) return IntPtr.Zero;
                    // LocalAlloc memory, which is what the caller frees with LocalFree.
                    var copy = Marshal.AllocHGlobal((value.Length + 1) * 2);
                    for (var i = 0; i < value.Length; i++) Marshal.WriteInt16(copy, i * 2, value[i]);
                    Marshal.WriteInt16(copy, value.Length * 2, 0);
                    return copy;
                })),
                ["StrCmpW"] = Keep(new TwoPtr((a, b) =>
                    Math.Sign(string.CompareOrdinal(Wide(a) ?? string.Empty, Wide(b) ?? string.Empty)))),
                ["StrCmpIW"] = Keep(new TwoPtr((a, b) =>
                    Math.Sign(string.Compare(Wide(a) ?? string.Empty, Wide(b) ?? string.Empty, StringComparison.OrdinalIgnoreCase)))),
                // No file associations on a console; callers treat failure as "none".
                ["AssocQueryStringW"] = Keep(new AssocDelegate((f, s, a, e, o, n) => E_FAIL)),
                // #12 SHCreateMemStream: no stream offered; callers check for null.
                ["#12"] = Keep(new MemStreamDelegate((init, size) => IntPtr.Zero)),
                // #219 QISearch: the table-driven QueryInterface helper. A zero
                // here would claim success with no object written.
                ["#219"] = Keep(new QiSearchDelegate((self, table, riid, result) =>
                {
                    if (result == IntPtr.Zero) return E_POINTER;
                    Marshal.WriteIntPtr(result, IntPtr.Zero);
                    if (self == IntPtr.Zero || table == IntPtr.Zero || riid == IntPtr.Zero) return E_NOINTERFACE;
                    var wanted = new byte[16];
                    Marshal.Copy(riid, wanted, 0, 16);
                    var unknown = new Guid("00000000-0000-0000-c000-000000000046").ToByteArray();
                    var asksUnknown = true;
                    for (var i = 0; i < 16; i++) if (wanted[i] != unknown[i]) { asksUnknown = false; break; }
                    // QITAB: { const IID* piid; DWORD dwOffset; } padded to 16 bytes, null-terminated.
                    for (var entry = table; ; entry += 16)
                    {
                        var iid = Marshal.ReadIntPtr(entry);
                        if (iid == IntPtr.Zero) break;
                        var candidate = new byte[16];
                        Marshal.Copy(iid, candidate, 0, 16);
                        var same = true;
                        for (var i = 0; i < 16; i++) if (candidate[i] != wanted[i]) { same = false; break; }
                        if (!same && !asksUnknown) continue;
                        var found = self + Marshal.ReadInt32(entry, 8);
                        var addRef = Marshal.GetDelegateForFunctionPointer<AddRefDelegate>(
                            Marshal.ReadIntPtr(Marshal.ReadIntPtr(found), IntPtr.Size));
                        addRef(found);
                        Marshal.WriteIntPtr(result, found);
                        return 0;
                    }
                    return E_NOINTERFACE;
                })),
            };
            foreach (var module in new[] { "SHLWAPI.dll", "shlwapi.dll" })
                foreach (var pair in ours)
                    imports.Overrides[module + "!" + pair.Key] = pair.Value;
        }
    }
}
