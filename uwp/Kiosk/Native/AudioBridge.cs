using System;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.Media.Devices;

namespace Kiosk.Native
{
    /// <summary>
    /// Sound, by way of a device that exists but cannot be found.
    ///
    /// A Windows game finds its speakers by asking COM for a device
    /// enumerator, walking to the default endpoint, and asking that endpoint
    /// for an audio client. A packaged app is not allowed to create that
    /// enumerator — the class is not registered for it — so every game stops
    /// at the first step, even though the console's speakers are right there
    /// and its own audio engine is running.
    ///
    /// What is missing is only the finding. So the finding is what is built
    /// here: an enumerator and an endpoint that exist solely to be walked
    /// through, and that hand over, at the end of the walk, the console's
    /// real audio client — the same object a console game would get. From
    /// there nothing is pretend. The mixing, the buffers, the clock and the
    /// sound are the platform's own.
    /// </summary>
    public static class AudioBridge
    {
        private const int S_OK = 0;
        private const int E_NOTIMPL = unchecked((int)0x80004001);
        private const int E_FAIL = unchecked((int)0x80004005);

        private const string EnumeratorClass = "bcde0395-e52f-467c-8e3d-c4579291692e";
        private const string EnumeratorInterface = "a95664d2-9614-4f35-a746-de8db63617e6";
        private const string DeviceInterface = "d666063f-1587-4e43-81f1-b948e807363f";
        private const string CollectionInterface = "0bd7a1be-7a1a-44db-8397-cc5392387b5e";

        [DllImport("Mmdevapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int ActivateAudioInterfaceAsync(
            string path, ref Guid riid, IntPtr parameters, IntPtr handler, out IntPtr operation);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CompletedDelegate(IntPtr self, IntPtr operation);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ResultDelegate(IntPtr self, IntPtr code, IntPtr result);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int EndpointDelegate(IntPtr self, int flow, int role, IntPtr device);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ActivateDelegate(
            IntPtr self, IntPtr riid, uint context, IntPtr parameters, IntPtr result);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int OneOutDelegate(IntPtr self, IntPtr result);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int TwoInDelegate(IntPtr self, IntPtr first, IntPtr second);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int StoreDelegate(IntPtr self, uint access, IntPtr result);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int EnumerateDelegate(
            IntPtr self, int flow, uint stateMask, IntPtr result);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ItemDelegate(IntPtr self, uint index, IntPtr result);

        // Held so the collector cannot take what native code is holding.
        private static CompletedDelegate completed;
        private static EndpointDelegate endpoint;
        private static ActivateDelegate activate;
        private static OneOutDelegate identity;
        private static OneOutDelegate condition;
        private static StoreDelegate store;
        private static TwoInDelegate listener;
        private static EnumerateDelegate enumerate;
        private static OneOutDelegate count;
        private static ItemDelegate item;
        private static TwoInDelegate missing;

        private static readonly ComProxy Proxy = new ComProxy();
        private static IntPtr enumerator;
        private static IntPtr device;

        /// <summary>What the bridge did, in order, for the report.</summary>
        public static readonly System.Collections.Generic.List<string> Notes =
            new System.Collections.Generic.List<string>();

        private static void Note(string line)
        {
            lock (Notes)
            {
                if (Notes.Count < 30) Notes.Add(line);
            }
        }

        /// <summary>
        /// The console's own audio client, obtained the way a console app
        /// obtains it: asynchronously, against the default render device.
        /// The wait is short and on the game's thread, never the interface's.
        /// </summary>
        private static IntPtr RealClient(Guid wanted)
        {
            var got = IntPtr.Zero;
            var done = new ManualResetEventSlim(false);
            try
            {
                var path = MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default);
                if (string.IsNullOrEmpty(path))
                {
                    Note("no default render device");
                    return IntPtr.Zero;
                }

                completed = (self, operation) =>
                {
                    var code = Marshal.AllocHGlobal(4);
                    var slot = Marshal.AllocHGlobal(IntPtr.Size);
                    try
                    {
                        Marshal.WriteInt32(code, E_FAIL);
                        Marshal.WriteIntPtr(slot, IntPtr.Zero);
                        var read = Marshal.GetDelegateForFunctionPointer<ResultDelegate>(
                            ComProxy.Method(operation, 3));
                        read(operation, code, slot);
                        if (Marshal.ReadInt32(code) == S_OK)
                        {
                            got = Marshal.ReadIntPtr(slot);
                        }
                        else
                        {
                            Note("activation: 0x" + Marshal.ReadInt32(code).ToString("X8"));
                        }
                    }
                    catch (Exception error)
                    {
                        Note("activation: " + error.GetType().Name);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(code);
                        Marshal.FreeHGlobal(slot);
                        done.Set();
                    }
                    return S_OK;
                };

                var handler = Proxy.Create(
                    new[] { Marshal.GetFunctionPointerForDelegate(completed) },
                    new[] { "41d949ab-9862-444a-80f6-c261334da5eb" });

                var code2 = ActivateAudioInterfaceAsync(
                    path, ref wanted, IntPtr.Zero, handler, out _);
                if (code2 != S_OK)
                {
                    Note("activate call: 0x" + code2.ToString("X8"));
                    return IntPtr.Zero;
                }

                done.Wait(4000);
                Note(got != IntPtr.Zero ? "audio client ready" : "audio client timed out");
                return got;
            }
            catch (Exception error)
            {
                Note("audio: " + error.GetType().Name + " " + error.Message);
                return IntPtr.Zero;
            }
        }

        private static void Build()
        {
            if (enumerator != IntPtr.Zero) return;

            activate = (self, riid, context, parameters, result) =>
            {
                if (result == IntPtr.Zero || riid == IntPtr.Zero) return E_FAIL;
                Marshal.WriteIntPtr(result, IntPtr.Zero);
                try
                {
                    var wanted = Marshal.PtrToStructure<Guid>(riid);
                    Note("activate " + wanted);
                    var client = RealClient(wanted);
                    if (client == IntPtr.Zero) return E_FAIL;
                    Marshal.WriteIntPtr(result, client);
                    return S_OK;
                }
                catch
                {
                    return E_FAIL;
                }
            };

            identity = (self, result) =>
            {
                if (result == IntPtr.Zero) return E_FAIL;
                try
                {
                    var path = MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default)
                               ?? string.Empty;
                    // The caller frees this with CoTaskMemFree, which this app
                    // answers with the matching free.
                    var text = Marshal.AllocHGlobal((path.Length + 1) * 2);
                    for (var i = 0; i < path.Length; i++)
                    {
                        Marshal.WriteInt16(text, i * 2, path[i]);
                    }
                    Marshal.WriteInt16(text, path.Length * 2, 0);
                    Marshal.WriteIntPtr(result, text);
                    return S_OK;
                }
                catch
                {
                    return E_FAIL;
                }
            };

            // DEVICE_STATE_ACTIVE.
            condition = (self, result) =>
            {
                if (result != IntPtr.Zero) Marshal.WriteInt32(result, 1);
                return S_OK;
            };

            store = (self, access, result) =>
            {
                if (result != IntPtr.Zero) Marshal.WriteIntPtr(result, IntPtr.Zero);
                return E_NOTIMPL;
            };

            device = Proxy.Create(
                new[]
                {
                    Marshal.GetFunctionPointerForDelegate(activate),
                    Marshal.GetFunctionPointerForDelegate(store),
                    Marshal.GetFunctionPointerForDelegate(identity),
                    Marshal.GetFunctionPointerForDelegate(condition),
                },
                new[] { DeviceInterface });

            endpoint = (self, flow, role, result) =>
            {
                if (result == IntPtr.Zero) return E_FAIL;
                Note("default endpoint asked for");
                Marshal.WriteIntPtr(result, device);
                return S_OK;
            };

            // A list with one device on it. Refusing to enumerate is what sent
            // the last attempt round in circles: a caller that cannot count
            // the speakers assumes it asked too early and asks again.
            count = (self, result) =>
            {
                if (result != IntPtr.Zero) Marshal.WriteInt32(result, 1);
                Note("device count asked for");
                return S_OK;
            };
            item = (self, index, result) =>
            {
                if (result == IntPtr.Zero) return E_FAIL;
                if (index != 0) return E_FAIL;
                Marshal.WriteIntPtr(result, device);
                return S_OK;
            };

            var devices = Proxy.Create(
                new[]
                {
                    Marshal.GetFunctionPointerForDelegate(count),
                    Marshal.GetFunctionPointerForDelegate(item),
                },
                new[] { CollectionInterface });

            enumerate = (self, flow, stateMask, result) =>
            {
                if (result == IntPtr.Zero) return E_FAIL;
                Note("endpoints enumerated");
                Marshal.WriteIntPtr(result, devices);
                return S_OK;
            };

            // Asking for a named device: the only one there is answers.
            missing = (self, first, second) =>
            {
                if (second == IntPtr.Zero) return E_FAIL;
                Marshal.WriteIntPtr(second, device);
                return S_OK;
            };
            listener = (self, first, second) => S_OK;

            enumerator = Proxy.Create(
                new[]
                {
                    Marshal.GetFunctionPointerForDelegate(enumerate),    // EnumAudioEndpoints
                    Marshal.GetFunctionPointerForDelegate(endpoint),     // GetDefaultAudioEndpoint
                    Marshal.GetFunctionPointerForDelegate(missing),      // GetDevice
                    Marshal.GetFunctionPointerForDelegate(listener),     // Register
                    Marshal.GetFunctionPointerForDelegate(listener),     // Unregister
                },
                new[] { EnumeratorInterface });
        }

        /// <summary>
        /// The one class this app answers for. Everything else COM is asked
        /// for is genuinely not here, and says so.
        /// </summary>
        public static IntPtr ClassFor(string clsid)
        {
            if (!string.Equals(clsid, EnumeratorClass, StringComparison.OrdinalIgnoreCase))
            {
                return IntPtr.Zero;
            }
            try
            {
                Build();
                Note("handed over the endpoint enumerator");
                return enumerator;
            }
            catch (Exception error)
            {
                Note("enumerator: " + error.GetType().Name);
                return IntPtr.Zero;
            }
        }
    }
}
