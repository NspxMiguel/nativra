namespace Nativra.X86.Loader
{
    public sealed partial class GuestKernel
    {
        private const uint SecEUnsupportedFunction = 0x80090302;
        private uint securityFunctionsA;
        private uint securityFunctionsW;

        private void InstallSspi(GuestImports imports)
        {
            foreach (var module in new[] { "security.dll", "secur32.dll" })
            {
                imports.Register(module, "InitSecurityInterfaceA", CallConv.Stdcall, 0, c => SecurityFunctions(imports, false));
                imports.Register(module, "InitSecurityInterfaceW", CallConv.Stdcall, 0, c => SecurityFunctions(imports, true));
            }
        }

        private uint SecurityFunctions(GuestImports imports, bool wide)
        {
            if (wide && securityFunctionsW != 0) return securityFunctionsW;
            if (!wide && securityFunctionsA != 0) return securityFunctionsA;

            // SECURITY_FUNCTION_TABLE from sspi.h, in 32-bit pointer layout.
            // Unsupported operations return an SSPI error instead of claiming a
            // credential or TLS context that the host cannot actually use.
            var names = new[]
            {
                "EnumerateSecurityPackages", "QueryCredentialsAttributes", "AcquireCredentialsHandle",
                "FreeCredentialsHandle", null, "InitializeSecurityContext", "AcceptSecurityContext",
                "CompleteAuthToken", "DeleteSecurityContext", "ApplyControlToken", "QueryContextAttributes",
                "ImpersonateSecurityContext", "RevertSecurityContext", "MakeSignature", "VerifySignature",
                "FreeContextBuffer", "QuerySecurityPackageInfo", null, null, "ExportSecurityContext",
                "ImportSecurityContext", "AddCredentials", null, "QuerySecurityContextToken",
                "EncryptMessage", "DecryptMessage", "SetContextAttributes", "SetCredentialsAttributes",
                "ChangeAccountPassword"
            };
            var arguments = new[] { 2, 3, 9, 1, 0, 12, 9, 2, 1, 2, 3, 1, 1, 4, 4, 1, 2, 0, 0, 4, 3, 8, 0, 2, 4, 4, 4, 4, 8 };
            var table = heap.Alloc((uint)(names.Length + 1) * 4, zero: true);
            memory.Write32(table, 1); // SECURITY_SUPPORT_PROVIDER_INTERFACE_VERSION
            for (var index = 0; index < names.Length; index++)
            {
                if (names[index] == null) continue;
                var function = names[index] + (wide ? "W" : "A");
                var count = arguments[index];
                imports.Register("security.dll", function, CallConv.Stdcall, count, c => SecEUnsupportedFunction);
                memory.Write32(table + (uint)(index + 1) * 4, imports.Bind("security.dll", function, -1));
            }
            if (wide) securityFunctionsW = table; else securityFunctionsA = table;
            return table;
        }
    }
}
