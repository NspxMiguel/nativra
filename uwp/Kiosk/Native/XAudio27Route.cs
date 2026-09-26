using System;
using System.Runtime.InteropServices;

namespace Kiosk.Native
{
    /// <summary>
    /// XAudio 2.7, created the way the DirectX SDK made games create it:
    /// CoCreateInstance of its CLSID. The console registers no such class; the
    /// package carries xaudio2_7.dll (native/directx-redist), which runs the
    /// 2.7 interface over the console's XAudio 2.9, and this hands its class
    /// factory the request. Little Nightmares' audio starts here.
    /// </summary>
    internal static class XAudio27Route
    {
        private const int S_OK = 0;
        private const int REGDB_E_CLASSNOTREG = unchecked((int)0x80040154);

        // CLSID_XAudio2 and CLSID_XAudio2_Debug of XAudio 2.7.
        private const string Release = "5a508685-a254-4fba-9b82-9a24b00306af";
        private const string Debug = "db05ea35-0329-4d4b-a53a-6dead03d3852";
        private static readonly Guid ClassFactory = new Guid("00000001-0000-0000-C000-000000000046");

        [DllImport("api-ms-win-core-libraryloader-l2-1-0.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadPackagedLibrary(string name, uint reserved);

        [DllImport("api-ms-win-core-libraryloader-l1-2-0.dll", CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetClassObjectDelegate(IntPtr clsid, IntPtr riid, out IntPtr factory);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CreateInstanceDelegate(IntPtr self, IntPtr outer, IntPtr riid, IntPtr result);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint ReleaseDelegate(IntPtr self);

        private static GetClassObjectDelegate getClassObject;
        public static string Note = "not asked";

        public static bool Serves(string clsid) =>
            string.Equals(clsid, Release, StringComparison.OrdinalIgnoreCase)
            || string.Equals(clsid, Debug, StringComparison.OrdinalIgnoreCase);

        /// <summary>Creates the object through xaudio2_7.dll's class factory.</summary>
        public static int Create(IntPtr clsid, IntPtr riid, IntPtr result)
        {
            if (result == IntPtr.Zero) return REGDB_E_CLASSNOTREG;
            Marshal.WriteIntPtr(result, IntPtr.Zero);
            try
            {
                if (getClassObject == null)
                {
                    var module = LoadPackagedLibrary("xaudio2_7.dll", 0);
                    if (module == IntPtr.Zero)
                    {
                        Note = "xaudio2_7.dll did not load (" + Marshal.GetLastWin32Error() + ")";
                        return REGDB_E_CLASSNOTREG;
                    }
                    var entry = GetProcAddress(module, "DllGetClassObject");
                    if (entry == IntPtr.Zero)
                    {
                        Note = "xaudio2_7.dll has no DllGetClassObject";
                        return REGDB_E_CLASSNOTREG;
                    }
                    getClassObject = Marshal.GetDelegateForFunctionPointer<GetClassObjectDelegate>(entry);
                }

                var factoryId = Marshal.AllocHGlobal(16);
                try
                {
                    Marshal.StructureToPtr(ClassFactory, factoryId, false);
                    var code = getClassObject(clsid, factoryId, out var factory);
                    if (code != S_OK || factory == IntPtr.Zero)
                    {
                        Note = "class factory refused 0x" + code.ToString("X8");
                        return code != S_OK ? code : REGDB_E_CLASSNOTREG;
                    }
                    var table = Marshal.ReadIntPtr(factory);
                    // IClassFactory: QueryInterface, AddRef, Release, CreateInstance.
                    var create = Marshal.GetDelegateForFunctionPointer<CreateInstanceDelegate>(
                        Marshal.ReadIntPtr(table, 3 * IntPtr.Size));
                    var made = create(factory, IntPtr.Zero, riid, result);
                    Marshal.GetDelegateForFunctionPointer<ReleaseDelegate>(Marshal.ReadIntPtr(table, 2 * IntPtr.Size))(factory);
                    Note = "created 0x" + made.ToString("X8");
                    return made;
                }
                finally
                {
                    Marshal.FreeHGlobal(factoryId);
                }
            }
            catch (Exception error)
            {
                Note = error.GetType().Name;
                return REGDB_E_CLASSNOTREG;
            }
        }
    }
}
