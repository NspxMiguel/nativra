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
        private const string EndpointInterface = "1be09788-6894-4089-8586-9a2a6c265ac5";
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

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int PropertyDelegate(IntPtr self, IntPtr key, IntPtr value);

        // Held so the collector cannot take what native code is holding.
        private static EndpointDelegate endpoint;
        private static ActivateDelegate activate;
        private static OneOutDelegate identity;
        private static OneOutDelegate condition;
        private static OneOutDelegate dataFlow;
        private static StoreDelegate store;
        private static OneOutDelegate propertyCount;
        private static ItemDelegate propertyAt;
        private static PropertyDelegate propertyValue;
        private static PropertyDelegate propertySet;
        private static OneOutDelegate commit;
        private static IntPtr properties;
        private static TwoInDelegate listener;
        private static EnumerateDelegate enumerate;
        private static OneOutDelegate count;
        private static ItemDelegate item;
        private static TwoInDelegate missing;

        /// <summary>
        /// Whether to answer for the audio class at all. Sound is not needed to
        /// put a picture on screen, and an audio path that half works is worse
        /// than one that plainly does not: a game told "no such device" falls
        /// back to silence and carries on, while a game handed a device that
        /// disappoints it half way through stops where it stands.
        /// </summary>
        public static bool Enabled = true;
        public static bool ClientAcquired;
        public static int LastActivationResult;

        private static readonly ComProxy Proxy = new ComProxy();
        private static IntPtr enumerator;
        private static IntPtr device;


        private const string StoreInterface = "886d8eeb-8cf2-4446-8d02-cdba1dbdcf99";

        // The properties a game reads off an endpoint before it will use it.
        private static readonly Guid FriendlyNameGroup =
            new Guid("a45c254e-df1c-4efd-8020-67d146a850e0");
        private static readonly Guid InterfaceNameGroup =
            new Guid("026e516e-b814-414b-83cd-856d6fef4822");
        private static readonly Guid DeviceFormatGroup =
            new Guid("f19f064d-082c-4e27-bc73-6882a1bb8e4c");
        private static readonly Guid OemFormatGroup =
            new Guid("e4870e26-3cc5-4cd2-ba46-ca0a9a70ed04");
        private static readonly Guid EndpointGroup =
            new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e");

        /// <summary>
        /// The format the console mixes at, written as Windows writes it.
        ///
        /// Stereo, 48 kHz, floating point — which is what a console's audio
        /// engine actually runs, so a game that builds its pipeline from this
        /// builds the right one and never resamples.
        /// </summary>
        private static IntPtr MixFormat(out int size)
        {
            size = 40;                                   // WAVEFORMATEXTENSIBLE
            var at = Marshal.AllocHGlobal(size);
            Marshal.WriteInt16(at, 0, unchecked((short)0xFFFE));  // extensible
            Marshal.WriteInt16(at, 2, 2);                // channels
            Marshal.WriteInt32(at, 4, 48000);            // samples per second
            Marshal.WriteInt32(at, 8, 48000 * 8);        // average bytes per second
            Marshal.WriteInt16(at, 12, 8);               // block align
            Marshal.WriteInt16(at, 14, 32);              // bits per sample
            Marshal.WriteInt16(at, 16, 22);              // extra bytes
            Marshal.WriteInt16(at, 18, 32);              // valid bits
            Marshal.WriteInt32(at, 20, 3);               // front left and right
            Marshal.StructureToPtr(
                new Guid("00000003-0000-0010-8000-00aa00389b71"), at + 24, false);
            return at;
        }

        private static bool Is(IntPtr key, Guid group, int id)
        {
            if (key == IntPtr.Zero) return false;
            try
            {
                return Marshal.PtrToStructure<Guid>(key) == group
                    && Marshal.ReadInt32(key, 16) == id;
            }
            catch
            {
                return false;
            }
        }

        private static void Empty(IntPtr value)
        {
            if (value == IntPtr.Zero) return;
            Marshal.WriteInt16(value, 0, 0);             // VT_EMPTY
            Marshal.WriteInt16(value, 2, 0);
            Marshal.WriteInt16(value, 4, 0);
            Marshal.WriteInt16(value, 6, 0);
            Marshal.WriteInt64(value, 8, 0);
            Marshal.WriteInt64(value, 16, 0);
        }

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

                CompletedDelegate completed = (self, operation) =>
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
                        LastActivationResult = Marshal.ReadInt32(code);
                        if (Marshal.ReadInt32(code) == S_OK)
                        {
                            got = Marshal.ReadIntPtr(slot);
                            ClientAcquired = got != IntPtr.Zero;
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

                // Completion runs on an MTA worker and may arrive after a
                // timeout. Keep each callback alive independently and expose
                // IAgileObject so the platform can invoke it across apartments.
                Proxy.Keep(completed);
                var handler = Proxy.Create(
                    new[] { Marshal.GetFunctionPointerForDelegate(completed) },
                    new[]
                    {
                        "41d949ab-9862-444a-80f6-c261334da5eb",
                        "94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90", // IAgileObject
                    });

                var code2 = ActivateAudioInterfaceAsync(
                    path, ref wanted, IntPtr.Zero, handler, out _);
                if (code2 != S_OK)
                {
                    LastActivationResult = code2;
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

            propertyCount = (self, result) =>
            {
                if (result != IntPtr.Zero) Marshal.WriteInt32(result, 4);
                return S_OK;
            };

            propertyAt = (self, index, result) =>
            {
                if (result == IntPtr.Zero) return E_FAIL;
                if (index == 0)
                {
                    Marshal.StructureToPtr(DeviceFormatGroup, result, false);
                    Marshal.WriteInt32(result, 16, 0);
                    return S_OK;
                }
                if (index == 1)
                {
                    Marshal.StructureToPtr(FriendlyNameGroup, result, false);
                    Marshal.WriteInt32(result, 16, 14);
                    return S_OK;
                }
                if (index == 2)
                {
                    Marshal.StructureToPtr(FriendlyNameGroup, result, false);
                    Marshal.WriteInt32(result, 16, 2); // PKEY_Device_DeviceDesc
                    return S_OK;
                }
                if (index == 3)
                {
                    Marshal.StructureToPtr(InterfaceNameGroup, result, false);
                    Marshal.WriteInt32(result, 16, 2); // PKEY_DeviceInterface_FriendlyName
                    return S_OK;
                }
                return E_FAIL;
            };

            propertyValue = (self, key, value) =>
            {
                if (value == IntPtr.Zero) return E_FAIL;
                Empty(value);

                if (Is(key, DeviceFormatGroup, 0) || Is(key, OemFormatGroup, 3))
                {
                    var blob = MixFormat(out var size);
                    Marshal.WriteInt16(value, 0, 65);    // VT_BLOB
                    Marshal.WriteInt32(value, 8, size);
                    Marshal.WriteIntPtr(value, 16, blob);
                    Note("mix format handed over");
                    return S_OK;
                }

                // DeviceDesc and FriendlyName are distinct properties. FMOD
                // reads the description before attempting client activation.
                if (Is(key, FriendlyNameGroup, 14) || Is(key, FriendlyNameGroup, 2)
                    || Is(key, InterfaceNameGroup, 2))
                {
                    var name = Is(key, FriendlyNameGroup, 2) ? "Speakers" : "Xbox";
                    var text = Marshal.AllocHGlobal((name.Length + 1) * 2);
                    for (var i = 0; i < name.Length; i++)
                    {
                        Marshal.WriteInt16(text, i * 2, name[i]);
                    }
                    Marshal.WriteInt16(text, name.Length * 2, 0);
                    Marshal.WriteInt16(value, 0, 31);    // VT_LPWSTR
                    Marshal.WriteIntPtr(value, 8, text);
                    return S_OK;
                }

                if (Is(key, EndpointGroup, 0))
                {
                    Marshal.WriteInt16(value, 0, 19);    // VT_UI4
                    Marshal.WriteInt32(value, 8, 1);     // speakers
                    return S_OK;
                }

                // Which speakers exist, and whether the endpoint can be driven
                // by events rather than polled. A game that plans its buffering
                // around these reads them before it opens anything.
                if (Is(key, EndpointGroup, 3))
                {
                    Marshal.WriteInt16(value, 0, 19);
                    Marshal.WriteInt32(value, 8, 3);     // front left and right
                    return S_OK;
                }
                if (Is(key, EndpointGroup, 7))
                {
                    Marshal.WriteInt16(value, 0, 19);
                    Marshal.WriteInt32(value, 8, 1);     // yes
                    return S_OK;
                }

                // Everything else, named out loud. A store that quietly says
                // "nothing here, and it went fine" hands back an empty value
                // that a caller may read as a pointer — which is how a program
                // ends up reading address zero.
                try
                {
                    Note("property " + Marshal.PtrToStructure<Guid>(key)
                        + ":" + Marshal.ReadInt32(key, 16));
                }
                catch
                {
                    Note("property (unreadable key)");
                }
                return unchecked((int)0x80070490);   // ERROR_NOT_FOUND
            };

            propertySet = (self, key, value) => S_OK;
            commit = (self, result) => S_OK;

            properties = Proxy.Create(
                new[]
                {
                    Marshal.GetFunctionPointerForDelegate(propertyCount),
                    Marshal.GetFunctionPointerForDelegate(propertyAt),
                    Marshal.GetFunctionPointerForDelegate(propertyValue),
                    Marshal.GetFunctionPointerForDelegate(propertySet),
                    Marshal.GetFunctionPointerForDelegate(commit),
                },
                new[] { StoreInterface });

            store = (self, access, result) =>
            {
                if (result == IntPtr.Zero) return E_FAIL;
                Note("property store asked for");
                Marshal.WriteIntPtr(result, properties);
                return S_OK;
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

            // IMMEndpoint has a different vtable from IMMDevice: its first
            // method writes an EDataFlow, rather than activating a client.
            dataFlow = (self, result) =>
            {
                if (result == IntPtr.Zero) return unchecked((int)0x80004003);
                Marshal.WriteInt32(result, 0); // eRender: this is an output endpoint.
                Note("endpoint render flow handed over");
                return S_OK;
            };
            var endpointView = Proxy.Create(
                new[] { Marshal.GetFunctionPointerForDelegate(dataFlow) },
                new[] { EndpointInterface });
            Proxy.LinkInterfacePair(device, DeviceInterface, endpointView, EndpointInterface);

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
            if (!Enabled) return IntPtr.Zero;
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
