using System;
using System.Collections.Generic;
using Nativra.X86.Cpu;

namespace Nativra.X86.Loader
{
    // setupapi's device enumeration. Programs walk it to find hardware (a
    // webcam, a network card, a game controller). The guest has no plug and
    // play tree: the host's devices are not something an x86 program can or
    // should open, so every device information set is real (a handle, an
    // enumeration cursor, destroy) but holds no devices, and enumerating it
    // ends at once with ERROR_NO_MORE_ITEMS, the answer of a machine that has
    // none of that class. The class names and GUIDs themselves are Windows'.
    public sealed partial class GuestKernel
    {
        private const uint ErrorNoMoreItems = 259, ErrorInvalidUserBuffer = 1784, ErrorInvalidClass = 0xE0000203;

        private static readonly Dictionary<string, Guid> DeviceClasses = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase)
        {
            ["1394"] = new Guid("6BDD1FC1-810F-11D0-BEC7-08002BE2092F"),
            ["CDROM"] = new Guid("4D36E965-E325-11CE-BFC1-08002BE10318"),
            ["DiskDrive"] = new Guid("4D36E967-E325-11CE-BFC1-08002BE10318"),
            ["Display"] = new Guid("4D36E968-E325-11CE-BFC1-08002BE10318"),
            ["HIDClass"] = new Guid("745A17A0-74D3-11D0-B6FE-00A0C90F57DA"),
            ["Image"] = new Guid("6BDD1FC6-810F-11D0-BEC7-08002BE2092F"),
            ["Keyboard"] = new Guid("4D36E96B-E325-11CE-BFC1-08002BE10318"),
            ["Media"] = new Guid("4D36E96C-E325-11CE-BFC1-08002BE10318"),
            ["Monitor"] = new Guid("4D36E96E-E325-11CE-BFC1-08002BE10318"),
            ["Mouse"] = new Guid("4D36E96F-E325-11CE-BFC1-08002BE10318"),
            ["Net"] = new Guid("4D36E972-E325-11CE-BFC1-08002BE10318"),
            ["Ports"] = new Guid("4D36E978-E325-11CE-BFC1-08002BE10318"),
            ["Processor"] = new Guid("50127DC3-0F36-415E-A6CC-4CB3BE910B65"),
            ["System"] = new Guid("4D36E97D-E325-11CE-BFC1-08002BE10318"),
            ["USB"] = new Guid("36FC9E60-C465-11CF-8056-444553540000"),
        };

        /// <summary>Device information sets by handle; each would list its devices, and none has any.</summary>
        private readonly Dictionary<uint, List<object>> deviceSets = new Dictionary<uint, List<object>>();

        private void InstallSetupApi(GuestImports i)
        {
            const string s = "setupapi.dll";
            foreach (var wide in new[] { false, true })
            {
                var w = wide;
                var x = wide ? "W" : "A";
                i.Register(s, "SetupDiClassGuidsFromName" + x, CallConv.Stdcall, 4, c =>
                {
                    // Every class of the name (there is at most one here); the size is in GUIDs.
                    var found = DeviceClasses.TryGetValue(ReadText(c.Arg(0), w), out var guid) ? 1u : 0u;
                    memory.Write32(c.Arg(3), found);
                    if (found > c.Arg(2)) return Fail(ErrorInsufficientBuffer);
                    if (found == 1) memory.WriteBytes(c.Arg(1), guid.ToByteArray());
                    return 1;
                });
                i.Register(s, "SetupDiClassNameFromGuid" + x, CallConv.Stdcall, 4, c =>
                {
                    var guid = new Guid(memory.ReadBytes(c.Arg(0), 16));
                    foreach (var known in DeviceClasses)
                    {
                        if (known.Value != guid) continue;
                        var size = c.Arg(2);
                        if (c.Arg(3) != 0) memory.Write32(c.Arg(3), (uint)known.Key.Length + 1);
                        if (size < known.Key.Length + 1) return Fail(ErrorInsufficientBuffer);
                        WriteText(c.Arg(1), known.Key, w);
                        return 1;
                    }
                    return Fail(ErrorInvalidClass);
                });
                i.Register(s, "SetupDiGetClassDevs" + x, CallConv.Stdcall, 4, c =>
                {
                    const uint allClasses = 0x4;
                    if (c.Arg(0) == 0 && (c.Arg(3) & allClasses) == 0 && c.Arg(1) == 0) { process.LastError = ErrorInvalidParameter; return InvalidHandleValue; }
                    var handle = NewHandle();
                    deviceSets[handle] = new List<object>();
                    return handle;
                });
                i.Register(s, "SetupDiGetDeviceInstanceId" + x, CallConv.Stdcall, 5, c => DeviceOfInfoData(c.Arg(0), c.Arg(1)));
                i.Register(s, "SetupDiGetDeviceRegistryProperty" + x, CallConv.Stdcall, 7, c => DeviceOfInfoData(c.Arg(0), c.Arg(1)));
            }
            i.Register(s, "SetupDiEnumDeviceInfo", CallConv.Stdcall, 3, c =>
            {
                if (!deviceSets.TryGetValue(c.Arg(0), out var set)) return Fail(ErrorInvalidHandle);
                if (c.Arg(2) == 0 || memory.Read32(c.Arg(2)) != 28) return Fail(ErrorInvalidUserBuffer);   // sizeof(SP_DEVINFO_DATA)
                return Fail(c.Arg(1) < set.Count ? ErrorInvalidParameter : ErrorNoMoreItems);
            });
            i.Register(s, "SetupDiEnumDeviceInterfaces", CallConv.Stdcall, 5, c =>
            {
                if (!deviceSets.ContainsKey(c.Arg(0))) return Fail(ErrorInvalidHandle);
                if (c.Arg(4) == 0 || memory.Read32(c.Arg(4)) != 28) return Fail(ErrorInvalidUserBuffer);   // sizeof(SP_DEVICE_INTERFACE_DATA)
                return Fail(ErrorNoMoreItems);
            });
            i.Register(s, "SetupDiDestroyDeviceInfoList", CallConv.Stdcall, 1, c => deviceSets.Remove(c.Arg(0)) ? 1u : Fail(ErrorInvalidHandle));
        }

        /// <summary>A device of a set by its SP_DEVINFO_DATA; no set has devices, so the data can only be one that was never filled in.</summary>
        private uint DeviceOfInfoData(uint set, uint data)
        {
            if (!deviceSets.ContainsKey(set)) return Fail(ErrorInvalidHandle);
            if (data == 0 || memory.Read32(data) != 28) return Fail(ErrorInvalidUserBuffer);
            return Fail(ErrorInvalidParameter);
        }
    }
}
