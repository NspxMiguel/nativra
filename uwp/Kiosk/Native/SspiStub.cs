using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Kiosk.Native
{
    /// <summary>
    /// The Security Support Provider Interface, present and declining.
    ///
    /// libcurl's start-up (curl_global_init) loads secur32 and asks it for
    /// InitSecurityInterface, and treats a missing table as a failed start:
    /// Unreal Engine 4 then stops with a fatal error in CurlHttpManager, as
    /// Little Nightmares did. curl only keeps the table at start-up; it would
    /// use it for NTLM or Kerberos authentication, which a game on a console
    /// does not do. So the table is real and every function in it answers
    /// SEC_E_UNSUPPORTED_FUNCTION.
    /// </summary>
    internal static class SspiStub
    {
        private const int Unsupported = unchecked((int)0x80090302);
        // dwVersion, then the function pointers (28 in the latest table; a
        // few spare slots cost nothing).
        private const int Functions = 32;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int AnyDelegate(IntPtr a, IntPtr b, IntPtr c, IntPtr d, IntPtr e, IntPtr f, IntPtr g, IntPtr h,
            IntPtr i, IntPtr j, IntPtr k, IntPtr l);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr InitDelegate();

        private static readonly List<Delegate> roots = new List<Delegate>();
        private static IntPtr table;

        public static void Install(SystemImports imports)
        {
            AnyDelegate decline = (a, b, c, d, e, f, g, h, i, j, k, l) => Unsupported;
            roots.Add(decline);
            var declinePointer = Marshal.GetFunctionPointerForDelegate(decline);
            table = Marshal.AllocHGlobal(8 + Functions * IntPtr.Size);
            Marshal.WriteInt64(table, 0, 1);   // SECURITY_SUPPORT_PROVIDER_INTERFACE_VERSION
            for (var n = 0; n < Functions; n++) Marshal.WriteIntPtr(table, 8 + n * IntPtr.Size, declinePointer);

            InitDelegate init = () => table;
            roots.Add(init);
            var initPointer = Marshal.GetFunctionPointerForDelegate(init);
            foreach (var module in new[] { "secur32.dll", "SECUR32.dll", "security.dll", "SECURITY.dll", "sspicli.dll", "SSPICLI.dll" })
            {
                imports.Overrides[module + "!InitSecurityInterfaceW"] = initPointer;
                imports.Overrides[module + "!InitSecurityInterfaceA"] = initPointer;
            }
        }
    }
}
