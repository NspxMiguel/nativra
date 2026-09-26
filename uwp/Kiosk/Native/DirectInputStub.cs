using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Kiosk.Native
{
    /// <summary>
    /// A DirectInput 8 with no devices. The console has none: pads reach
    /// games through XInput. SDL's haptic and joystick subsystems create
    /// DirectInput through COM, and a refused class failed SDL_Init as a
    /// whole in Hades ("Haptic error CoCreateInstance"). An object that
    /// initialises and enumerates nothing is the truth, and lets SDL go on.
    /// </summary>
    internal static class DirectInputStub
    {
        private const int S_OK = 0;
        private const int E_NOINTERFACE = unchecked((int)0x80004002);
        private const int E_POINTER = unchecked((int)0x80004003);
        private const int DIERR_DEVICENOTREG = unchecked((int)0x80040154);
        private const int DIERR_UNSUPPORTED = unchecked((int)0x80004001);
        private const int DI_NOTATTACHED = 1;

        public const string ClassId = "25e609e4-b259-11cf-bfc7-444553540000";

        private static readonly Guid Unknown = new Guid("00000000-0000-0000-c000-000000000046");
        private static readonly Guid Wide = new Guid("BF798031-483A-4DA2-AA99-5D64ED369700");
        private static readonly Guid Narrow = new Guid("BF798030-483A-4DA2-AA99-5D64ED369700");

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int QueryDelegate(IntPtr self, IntPtr riid, IntPtr result);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint CountDelegate(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateDeviceDelegate(IntPtr self, IntPtr guid, IntPtr device, IntPtr outer);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumDelegate(IntPtr self, uint type, IntPtr callback, IntPtr context, uint flags);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int StatusDelegate(IntPtr self, IntPtr guid);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int PanelDelegate(IntPtr self, IntPtr window, uint flags);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int InitializeDelegate(IntPtr self, IntPtr instance, uint version);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int FindDelegate(IntPtr self, IntPtr guid, IntPtr name, IntPtr result);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SemanticsDelegate(IntPtr self, IntPtr user, IntPtr format, IntPtr callback, IntPtr context, uint flags);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ConfigureDelegate(IntPtr self, IntPtr callback, IntPtr parameters, uint flags, IntPtr context);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateDelegate(IntPtr instance, uint version, IntPtr riid, IntPtr result, IntPtr outer);

        private static readonly List<Delegate> roots = new List<Delegate>();
        private static readonly object gate = new object();
        private static IntPtr instance;

        public static long Created;

        private static IntPtr Keep(Delegate function)
        {
            roots.Add(function);
            return Marshal.GetFunctionPointerForDelegate(function);
        }

        private static bool Answers(IntPtr riid)
        {
            if (riid == IntPtr.Zero) return false;
            var wanted = Marshal.PtrToStructure<Guid>(riid);
            return wanted == Unknown || wanted == Wide || wanted == Narrow;
        }

        /// <summary>The one object; it lives as long as the process.</summary>
        private static IntPtr Object()
        {
            lock (gate)
            {
                if (instance != IntPtr.Zero) return instance;
                var methods = new[]
                {
                    Keep(new QueryDelegate((self, riid, result) =>
                    {
                        if (result == IntPtr.Zero) return E_POINTER;
                        var ok = Answers(riid);
                        Marshal.WriteIntPtr(result, ok ? self : IntPtr.Zero);
                        return ok ? S_OK : E_NOINTERFACE;
                    })),
                    Keep(new CountDelegate(self => 1)),
                    Keep(new CountDelegate(self => 1)),
                    Keep(new CreateDeviceDelegate((self, guid, device, outer) =>
                    {
                        if (device != IntPtr.Zero) Marshal.WriteIntPtr(device, IntPtr.Zero);
                        return DIERR_DEVICENOTREG;
                    })),
                    Keep(new EnumDelegate((self, type, callback, context, flags) => S_OK)),
                    Keep(new StatusDelegate((self, guid) => DI_NOTATTACHED)),
                    Keep(new PanelDelegate((self, window, flags) => S_OK)),
                    Keep(new InitializeDelegate((self, module, version) => S_OK)),
                    Keep(new FindDelegate((self, guid, name, result) => DIERR_DEVICENOTREG)),
                    Keep(new SemanticsDelegate((self, user, format, callback, context, flags) => S_OK)),
                    Keep(new ConfigureDelegate((self, callback, parameters, flags, context) => DIERR_UNSUPPORTED)),
                };
                var table = Marshal.AllocHGlobal(IntPtr.Size * methods.Length);
                for (var i = 0; i < methods.Length; i++) Marshal.WriteIntPtr(table, i * IntPtr.Size, methods[i]);
                instance = Marshal.AllocHGlobal(IntPtr.Size);
                Marshal.WriteIntPtr(instance, table);
                return instance;
            }
        }

        /// <summary>The object for CLSID_DirectInput8, or zero for any other class.</summary>
        public static IntPtr ClassFor(string clsid)
        {
            if (!string.Equals(clsid, ClassId, StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
            System.Threading.Interlocked.Increment(ref Created);
            return Object();
        }

        public static void Install(SystemImports imports)
        {
            var create = Keep(new CreateDelegate((module, version, riid, result, outer) =>
            {
                if (result == IntPtr.Zero) return E_POINTER;
                if (!Answers(riid))
                {
                    Marshal.WriteIntPtr(result, IntPtr.Zero);
                    return E_NOINTERFACE;
                }
                System.Threading.Interlocked.Increment(ref Created);
                Marshal.WriteIntPtr(result, Object());
                return S_OK;
            }));
            foreach (var module in new[] { "DINPUT8.dll", "dinput8.dll" })
                imports.Overrides[module + "!DirectInput8Create"] = create;
        }
    }
}
