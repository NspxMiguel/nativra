using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Kiosk.Native
{
    /// <summary>
    /// The libraries a game imports and barely uses.
    ///
    /// Human interface devices, input methods, file version resources, window
    /// decoration. A game links all of them and, on a machine with one screen,
    /// one controller and one language, touches almost none. What each one
    /// needs is a definite answer rather than a recorded shrug — "there is no
    /// such device", "no composition is in progress", "no version resource" —
    /// because a caller told nothing at all keeps asking.
    ///
    /// The two here that write into memory are written out properly. A handle
    /// can be invented; a buffer cannot.
    /// </summary>
    public static class PlainAnswers
    {
        private const int HIDP_STATUS_INVALID_PREPARSED_DATA = unchecked((int)0xC0110001);
        private const int E_FAIL = unchecked((int)0x80004005);
        private const int ERROR_FILE_NOT_FOUND = 2;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void GuidOutDelegate(IntPtr target);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int PathDelegate(IntPtr target, IntPtr source);

        private static GuidOutDelegate hidGuid;
        private static PathDelegate canonical;

        public static void Install(SystemImports imports)
        {
            // The interface class that identifies human interface devices. It
            // is a constant, and a game that gets a blank one enumerates
            // nothing forever.
            hidGuid = target =>
            {
                if (target == IntPtr.Zero) return;
                Marshal.StructureToPtr(
                    new Guid("4D1E55B2-F16F-11CF-88CB-001111000030"), target, false);
            };

            // Tidying a path. Nothing here needs tidying — the paths come from
            // this app — so the honest implementation is to hand it back.
            canonical = (target, source) =>
            {
                if (target == IntPtr.Zero || source == IntPtr.Zero) return 0;
                var text = Marshal.PtrToStringUni(source);
                if (text == null) return 0;
                for (var i = 0; i < text.Length; i++)
                {
                    Marshal.WriteInt16(target, i * 2, text[i]);
                }
                Marshal.WriteInt16(target, text.Length * 2, 0);
                return 1;
            };

            imports.Overrides["HID.DLL!HidD_GetHidGuid"] =
                Marshal.GetFunctionPointerForDelegate(hidGuid);
            imports.Overrides["hid.dll!HidD_GetHidGuid"] =
                Marshal.GetFunctionPointerForDelegate(hidGuid);
            imports.Overrides["SHLWAPI.dll!PathCanonicalizeW"] =
                Marshal.GetFunctionPointerForDelegate(canonical);
            imports.Overrides["shlwapi.dll!PathCanonicalizeW"] =
                Marshal.GetFunctionPointerForDelegate(canonical);

            var byLibrary = new Dictionary<string, Dictionary<string, long>>
            {
                ["HID.DLL"] = new Dictionary<string, long>
                {
                    { "HidD_GetAttributes", 0 },
                    { "HidD_GetPreparsedData", 0 },
                    { "HidD_FreePreparsedData", 1 },
                    { "HidD_GetProductString", 0 },
                    { "HidD_GetManufacturerString", 0 },
                    { "HidD_GetSerialNumberString", 0 },
                    { "HidP_GetCaps", HIDP_STATUS_INVALID_PREPARSED_DATA },
                    { "HidP_GetButtonCaps", HIDP_STATUS_INVALID_PREPARSED_DATA },
                    { "HidP_GetValueCaps", HIDP_STATUS_INVALID_PREPARSED_DATA },
                    { "HidP_GetData", HIDP_STATUS_INVALID_PREPARSED_DATA },
                    { "HidP_SetUsages", HIDP_STATUS_INVALID_PREPARSED_DATA },
                    { "HidP_SetUsageValue", HIDP_STATUS_INVALID_PREPARSED_DATA },
                    { "HidP_MaxDataListLength", 0 },
                },
                ["IMM32.dll"] = new Dictionary<string, long>
                {
                    { "ImmGetContext", 0 },
                    { "ImmReleaseContext", 1 },
                    { "ImmAssociateContext", 0 },
                    { "ImmAssociateContextEx", 1 },
                    { "ImmGetCompositionStringW", 0 },
                    { "ImmSetCompositionStringW", 1 },
                    { "ImmGetConversionStatus", 1 },
                    { "ImmNotifyIME", 1 },
                },
                ["VERSION.dll"] = new Dictionary<string, long>
                {
                    { "GetFileVersionInfoSizeA", 0 },
                    { "GetFileVersionInfoSizeW", 0 },
                    { "GetFileVersionInfoA", 0 },
                    { "GetFileVersionInfoW", 0 },
                    { "VerQueryValueA", 0 },
                    { "VerQueryValueW", 0 },
                },
                ["SHLWAPI.dll"] = new Dictionary<string, long>
                {
                    { "SHDeleteKeyW", ERROR_FILE_NOT_FOUND },
                },
                ["dwmapi.dll"] = new Dictionary<string, long>
                {
                    { "DwmGetWindowAttribute", E_FAIL },
                    { "DwmSetWindowAttribute", E_FAIL },
                    { "DwmIsCompositionEnabled", E_FAIL },
                    { "DwmFlush", E_FAIL },
                },
                ["PSAPI.DLL"] = new Dictionary<string, long>
                {
                    { "GetModuleFileNameExW", 0 },
                    { "GetModuleFileNameExA", 0 },
                },
                // DirectX redistributable pieces the console lacks. They return
                // HRESULTs and fill an out pointer: a stub's zero is S_OK with
                // nothing written, which the caller then reads. E_NOTIMPL is
                // the truth, and a game can take the path it has for failure.
                ["d3dx9_43.dll"] = new Dictionary<string, long>
                {
                    { "D3DXGetShaderConstantTableEx", 0x80004001 },
                    { "D3DXGetShaderConstantTable", 0x80004001 },
                    { "D3DXCompileShader", 0x80004001 },
                    { "D3DXCreateTextureFromFileA", 0x80004001 },
                    { "D3DXCreateTextureFromFileW", 0x80004001 },
                    { "D3DXCreateTextureFromFileInMemory", 0x80004001 },
                },
                ["XAPOFX1_5.dll"] = new Dictionary<string, long>
                {
                    { "CreateFX", 0x80004001 },
                },
                ["SETUPAPI.dll"] = new Dictionary<string, long>
                {
                    // No device set to walk, which is what an invalid handle means.
                    { "SetupDiGetClassDevsW", -1 },
                    { "SetupDiGetClassDevsA", -1 },
                    { "SetupDiEnumDeviceInterfaces", 0 },
                    { "SetupDiGetDeviceInterfaceDetailW", 0 },
                    { "SetupDiDestroyDeviceInfoList", 1 },
                },
            };

            foreach (var library in byLibrary)
            {
                foreach (var spelling in new[]
                {
                    library.Key, library.Key.ToLowerInvariant(), library.Key.ToUpperInvariant(),
                })
                {
                    foreach (var pair in library.Value)
                    {
                        imports.Answers[spelling + "!" + pair.Key] = pair.Value;
                    }
                }
            }
        }
    }
}
