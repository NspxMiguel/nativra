using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Kiosk.Native
{
    /// <summary>
    /// The user folders a PC game keeps its saves and settings in: Saved
    /// Games, Documents, AppData. The console has no such folders, and the
    /// shell calls that name them were stubs, so Hades got nothing back and
    /// wrote its saves to a relative "Saved Games\Hades" that could not be
    /// created. Each one is answered with a folder inside the app's own
    /// storage, which updates keep: saves survive a new build.
    /// </summary>
    internal static class UserFolders
    {
        private const int S_OK = 0;
        private const int E_FAIL = unchecked((int)0x80004005);
        private const int E_INVALIDARG = unchecked((int)0x80070057);
        private const int MaxPath = 260;

        /// <summary>LocalState\profile, where every one of them lives.</summary>
        public static string Root;

        private static readonly Dictionary<Guid, string> ById = new Dictionary<Guid, string>
        {
            [new Guid("4C5C32FF-BB9D-43b0-B5B4-2D72E54EAAA4")] = "Saved Games",
            [new Guid("FDD39AD0-238F-46AF-ADB4-6C85480369C7")] = "Documents",
            [new Guid("F1B32785-6FBA-4FCF-9D55-7B8E7F157091")] = @"AppData\Local",
            [new Guid("3EB685DB-65F9-4CF6-A03A-E3EF65729F3D")] = @"AppData\Roaming",
            [new Guid("A520A1A4-1780-4FF6-BD18-167343C5AF16")] = @"AppData\LocalLow",
            [new Guid("5E6C858F-0E22-4760-9AFE-EA3317B67173")] = "",
            [new Guid("B4BFCC3A-DB2C-424C-B029-7FE99A87C641")] = "Desktop",
            [new Guid("33E28130-4E1E-4676-835A-98395C3BC3BB")] = "Pictures",
            [new Guid("B7BEDE81-DF94-4682-A7D8-57A52620B86F")] = @"Pictures\Screenshots",
            [new Guid("4BD8D571-6D19-48D3-BE97-422220080E43")] = "Music",
            [new Guid("18989B1D-99B5-455B-841C-AB7C74E4DDFC")] = "Videos",
            [new Guid("374DE290-123F-4565-9164-39C4925E467B")] = "Downloads",
            [new Guid("62AB5D82-FDC1-4DC3-A9DD-070D1D495D97")] = "ProgramData",
            [new Guid("ED4824AF-DCE4-45A8-81E2-FC7965083634")] = @"Public\Documents",
        };

        private static readonly Dictionary<int, string> ByCsidl = new Dictionary<int, string>
        {
            [0x0000] = "Desktop",
            [0x0005] = "Documents",
            [0x000D] = "Music",
            [0x000E] = "Videos",
            [0x001A] = @"AppData\Roaming",
            [0x001C] = @"AppData\Local",
            [0x0023] = "ProgramData",
            [0x0027] = "Pictures",
            [0x0028] = "",
            [0x002E] = @"Public\Documents",
        };

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int KnownDelegate(IntPtr id, uint flags, IntPtr token, IntPtr result);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FolderDelegate(IntPtr window, int csidl, IntPtr token, uint flags, IntPtr path);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SpecialDelegate(IntPtr window, IntPtr path, int csidl, int create);

        private static readonly List<Delegate> roots = new List<Delegate>();

        /// <summary>The folder for a name, created, or null for one we do not know.</summary>
        public static string PathFor(string relative)
        {
            if (Root == null || relative == null) return null;
            var path = relative.Length == 0 ? Root : Path.Combine(Root, relative);
            try
            {
                Directory.CreateDirectory(path);
            }
            catch
            {
                // Handed back all the same: the game creates what it needs.
            }
            return path;
        }

        private static void WriteWide(IntPtr buffer, string text)
        {
            var fit = Math.Min(text.Length, MaxPath - 1);
            for (var i = 0; i < fit; i++) Marshal.WriteInt16(buffer, i * 2, text[i]);
            Marshal.WriteInt16(buffer, fit * 2, 0);
        }

        private static void WriteAnsi(IntPtr buffer, string text)
        {
            var bytes = System.Text.Encoding.Default.GetBytes(text);
            var fit = Math.Min(bytes.Length, MaxPath - 1);
            Marshal.Copy(bytes, 0, buffer, fit);
            Marshal.WriteByte(buffer, fit, 0);
        }

        private static IntPtr Keep(Delegate function)
        {
            roots.Add(function);
            return Marshal.GetFunctionPointerForDelegate(function);
        }

        public static void Install(SystemImports imports, string localState)
        {
            Root = Path.Combine(localState, "profile");

            var known = Keep(new KnownDelegate((id, flags, token, result) =>
            {
                if (result == IntPtr.Zero) return E_INVALIDARG;
                Marshal.WriteIntPtr(result, IntPtr.Zero);
                if (id == IntPtr.Zero) return E_INVALIDARG;
                var guid = Marshal.PtrToStructure<Guid>(id);
                if (!ById.TryGetValue(guid, out var relative)) return E_FAIL;
                var path = PathFor(relative);
                if (path == null) return E_FAIL;
                // CoTaskMemAlloc memory: the caller frees it with CoTaskMemFree.
                Marshal.WriteIntPtr(result, Marshal.StringToCoTaskMemUni(path));
                return S_OK;
            }));
            Func<int, string> byCsidl = csidl =>
                ByCsidl.TryGetValue(csidl & 0xFF, out var relative) ? PathFor(relative) : null;
            var folderW = Keep(new FolderDelegate((window, csidl, token, flags, buffer) =>
            {
                var path = byCsidl(csidl);
                if (path == null || buffer == IntPtr.Zero) return E_FAIL;
                WriteWide(buffer, path);
                return S_OK;
            }));
            var folderA = Keep(new FolderDelegate((window, csidl, token, flags, buffer) =>
            {
                var path = byCsidl(csidl);
                if (path == null || buffer == IntPtr.Zero) return E_FAIL;
                WriteAnsi(buffer, path);
                return S_OK;
            }));
            var specialW = Keep(new SpecialDelegate((window, buffer, csidl, create) =>
            {
                var path = byCsidl(csidl);
                if (path == null || buffer == IntPtr.Zero) return 0;
                WriteWide(buffer, path);
                return 1;
            }));
            var specialA = Keep(new SpecialDelegate((window, buffer, csidl, create) =>
            {
                var path = byCsidl(csidl);
                if (path == null || buffer == IntPtr.Zero) return 0;
                WriteAnsi(buffer, path);
                return 1;
            }));

            foreach (var module in new[] { "SHELL32.dll", "shell32.dll", "api-ms-win-shell-shellfolders-l1-1-0.dll" })
            {
                imports.Overrides[module + "!SHGetKnownFolderPath"] = known;
                imports.Overrides[module + "!SHGetFolderPathW"] = folderW;
                imports.Overrides[module + "!SHGetFolderPathA"] = folderA;
                imports.Overrides[module + "!SHGetSpecialFolderPathW"] = specialW;
                imports.Overrides[module + "!SHGetSpecialFolderPathA"] = specialA;
            }
        }
    }
}
