using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Nativra.X86.Loader
{
    /// <summary>
    /// XAudio 2.7 for a 32-bit game, served by the 64-bit xaudio2_7 shim the
    /// app already carries (which runs on XAudio 2.9).
    ///
    /// Audio data stays where the game put it: XAUDIO2_BUFFER.pAudioData is a
    /// guest pointer, and guest memory is host memory at the host base, so the
    /// engine reads it in place. What needs translating is the structures that
    /// hold pointers (buffers, send lists, voice state) and the voice
    /// callbacks, which run the other way: the host engine calls them on its
    /// own audio thread, where guest code cannot run. A host stand-in for each
    /// guest callback object queues the calls, and a guest thread (a green
    /// thread like the game's own) delivers them in order.
    ///
    /// Effect chains (reverb and the like, guest XAPO objects) are dropped: the
    /// voices play dry. The engine-wide callback registration is accepted and
    /// never called.
    /// </summary>
    public sealed class XAudio27Com
    {
        public static readonly Guid ReleaseClass = new Guid("5a508685-a254-4fba-9b82-9a24b00306af");
        public static readonly Guid DebugClass = new Guid("db05ea35-0329-4d4b-a53a-6dead03d3852");

        private readonly GuestProcess process;
        private readonly GuestKernel kernel;
        private readonly GuestCom com;
        private readonly Dictionary<uint, IntPtr> callbackProxies = new Dictionary<uint, IntPtr>();
        private readonly Queue<Callback> pending = new Queue<Callback>();
        private readonly List<Delegate> keep = new List<Delegate>();
        private IntPtr callbackVtable;
        private uint pumpSentinel;
        private GuestThread pump;
        private const int MaxPending = 4096;

        public long CallbacksDelivered { get; private set; }
        public long CallbacksDropped { get; private set; }
        public int EffectChainsDropped { get; private set; }

        private struct Callback
        {
            public uint Target;   // guest IXAudio2VoiceCallback*
            public int Slot;      // method index in its vtable
            public uint A, B;
            public int Arguments;
        }

        public XAudio27Com(GuestProcess process, GuestKernel kernel, GuestCom com)
        {
            this.process = process;
            this.kernel = kernel;
            this.com = com;
        }

        /// <summary>
        /// Declares the interfaces and hands CoCreateInstance of the XAudio 2.7
        /// classes to <paramref name="create"/> (the host's class factory).
        /// </summary>
        public void Install(GuestCom.ClassFactory create)
        {
            AddTranslators();

            var engine = com.Define("IXAudio2", new Guid("8bcf1f58-9fe7-4583-8ac6-e2adc465c8bb"), null, true,
                "GetDeviceCount(p)", "GetDeviceDetails(u,p)", "Initialize(u,u)",
                "RegisterForCallbacks(u):skip", "UnregisterForCallbacks(u):skip",
                "CreateSourceVoice(o:IXAudio2SourceVoice,p,u,u,K,S,E)",
                "CreateSubmixVoice(o:IXAudio2SubmixVoice,u,u,u,u,S,E)",
                "CreateMasteringVoice(o:IXAudio2MasteringVoice,u,u,u,u,E)",
                "StartEngine()", "StopEngine()", "CommitChanges(u)", "GetPerformanceData(p)",
                "SetDebugConfiguration(p,x)");

            const string V = "IXAudio2Voice";
            com.Define(V, Guid.Empty, null, false,
                "GetVoiceDetails(p)", "SetOutputVoices(S)", "SetEffectChain(E)", "EnableEffect(u,u)",
                "DisableEffect(u,u)", "GetEffectState(u,p)", "SetEffectParameters(u,p,u,u)",
                "GetEffectParameters(u,p,u)", "SetFilterParameters(p,u)", "GetFilterParameters(p)",
                "SetOutputFilterParameters(i:" + V + ",p,u)", "GetOutputFilterParameters(i:" + V + ",p)",
                "SetVolume(f,u)", "GetVolume(p)", "SetChannelVolumes(u,p,u)", "GetChannelVolumes(u,p)",
                "SetOutputMatrix(i:" + V + ",u,u,p,u)", "GetOutputMatrix(i:" + V + ",u,u,p)", "DestroyVoice()");
            com.Define("IXAudio2SourceVoice", Guid.Empty, V, false,
                "Start(u,u)", "Stop(u,u)", "SubmitSourceBuffer(X,x)", "FlushSourceBuffers()", "Discontinuity()",
                "ExitLoop(u)", "GetState(T)", "SetFrequencyRatio(f,u)", "GetFrequencyRatio(p)",
                "SetSourceSampleRate(u)");
            com.Define("IXAudio2SubmixVoice", Guid.Empty, V, false);
            com.Define("IXAudio2MasteringVoice", Guid.Empty, V, false);

            com.RegisterClass(ReleaseClass, create, engine);
            com.RegisterClass(DebugClass, create, engine);
        }

        // --- structures -----------------------------------------------------

        private void AddTranslators()
        {
            // XAUDIO2_VOICE_SENDS { UINT32 SendCount; XAUDIO2_SEND_DESCRIPTOR* pSends },
            // each descriptor { UINT32 Flags; IXAudio2Voice* pOutputVoice }: 8 bytes here, 16 on the host.
            com.AddArgument('S', (guest, slot, after) =>
            {
                if (guest == 0) return IntPtr.Zero;
                var memory = process.Memory;
                var count = Math.Min(memory.Read32(guest), 32u);
                var list = memory.Read32(guest + 4);
                var sends = slot(2);
                var array = count > 0 ? slot((int)count * 2) : IntPtr.Zero;
                Marshal.WriteInt32(sends, (int)count);
                Marshal.WriteIntPtr(sends, 8, array);
                for (uint n = 0; n < count; n++)
                {
                    Marshal.WriteInt32(array, (int)n * 16, (int)memory.Read32(list + n * 8));
                    Marshal.WriteIntPtr(array, (int)n * 16 + 8, com.Unwrap(memory.Read32(list + n * 8 + 4)));
                }
                return sends;
            });

            // XAUDIO2_EFFECT_CHAIN: guest XAPO objects cannot run on the host; the voice plays dry.
            com.AddArgument('E', (guest, slot, after) =>
            {
                if (guest != 0 && process.Memory.Read32(guest) != 0) EffectChainsDropped++;
                return IntPtr.Zero;
            });

            // XAUDIO2_BUFFER: pAudioData and pContext are 4 bytes here, 8 on the host.
            com.AddArgument('X', (guest, slot, after) =>
            {
                if (guest == 0) return IntPtr.Zero;
                var memory = process.Memory;
                var host = slot(6);
                var hostBase = memory.HostBase.ToInt64();
                var data = memory.Read32(guest + 8);
                Marshal.WriteInt32(host, 0, (int)memory.Read32(guest));        // Flags
                Marshal.WriteInt32(host, 4, (int)memory.Read32(guest + 4));    // AudioBytes
                Marshal.WriteIntPtr(host, 8, data == 0 ? IntPtr.Zero : new IntPtr(hostBase + data));
                for (uint n = 0; n < 5; n++)                                    // PlayBegin … LoopCount
                    Marshal.WriteInt32(host, 16 + (int)n * 4, (int)memory.Read32(guest + 12 + n * 4));
                Marshal.WriteIntPtr(host, 40, new IntPtr(memory.Read32(guest + 32)));   // pContext, handed back as is
                return host;
            });

            // XAUDIO2_VOICE_STATE out: { void* pCurrentBufferContext; UINT32 BuffersQueued; UINT64 SamplesPlayed }.
            com.AddArgument('T', (guest, slot, after) =>
            {
                if (guest == 0) return IntPtr.Zero;
                var host = slot(3);
                after.Add(() =>
                {
                    var memory = process.Memory;
                    memory.Write32(guest, (uint)Marshal.ReadInt64(host));
                    memory.Write32(guest + 4, (uint)Marshal.ReadInt32(host, 8));
                    memory.Write64(guest + 8, (ulong)Marshal.ReadInt64(host, 16));
                });
                return host;
            });

            // A guest IXAudio2VoiceCallback: a host stand-in that queues each call.
            com.AddArgument('K', (guest, slot, after) => guest == 0 ? IntPtr.Zero : CallbackProxy(guest));
        }

        // --- voice callbacks ------------------------------------------------

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void PassStart(IntPtr self, uint bytes);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void NoArgument(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void Context(IntPtr self, IntPtr context);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void Error(IntPtr self, IntPtr context, int error);

        private IntPtr CallbackProxy(uint guest)
        {
            if (callbackProxies.TryGetValue(guest, out var known)) return known;
            if (callbackVtable == IntPtr.Zero) BuildCallbackVtable();
            EnsurePump();
            var proxy = Marshal.AllocHGlobal(16);
            Marshal.WriteIntPtr(proxy, callbackVtable);
            Marshal.WriteInt64(proxy, 8, guest);
            callbackProxies[guest] = proxy;
            return proxy;
        }

        private void BuildCallbackVtable()
        {
            // IXAudio2VoiceCallback: OnVoiceProcessingPassStart, OnVoiceProcessingPassEnd,
            // OnStreamEnd, OnBufferStart, OnBufferEnd, OnLoopEnd, OnVoiceError.
            var methods = new Delegate[]
            {
                new PassStart((self, bytes) => Queue(self, 0, 1, bytes, 0)),
                new NoArgument(self => Queue(self, 1, 0, 0, 0)),
                new NoArgument(self => Queue(self, 2, 0, 0, 0)),
                new Context((self, context) => Queue(self, 3, 1, (uint)context.ToInt64(), 0)),
                new Context((self, context) => Queue(self, 4, 1, (uint)context.ToInt64(), 0)),
                new Context((self, context) => Queue(self, 5, 1, (uint)context.ToInt64(), 0)),
                new Error((self, context, error) => Queue(self, 6, 2, (uint)context.ToInt64(), (uint)error)),
            };
            keep.AddRange(methods);
            callbackVtable = Marshal.AllocHGlobal(methods.Length * IntPtr.Size);
            for (var n = 0; n < methods.Length; n++)
                Marshal.WriteIntPtr(callbackVtable, n * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(methods[n]));
        }

        /// <summary>On the host's audio thread: remember the call for the guest's delivery thread.</summary>
        private void Queue(IntPtr self, int slot, int arguments, uint a, uint b)
        {
            var target = (uint)Marshal.ReadInt64(self, 8);
            lock (pending)
            {
                if (pending.Count >= MaxPending)
                {
                    // The guest is far behind: processing-pass notices are the
                    // ones that can go (the next pass brings a new one).
                    if (slot <= 1) { CallbacksDropped++; return; }
                    pending.Dequeue();
                    CallbacksDropped++;
                }
                pending.Enqueue(new Callback { Target = target, Slot = slot, A = a, B = b, Arguments = arguments });
            }
        }

        /// <summary>
        /// The guest thread that delivers callbacks. It sits on a sentinel:
        /// with nothing queued it waits; otherwise it calls the guest's
        /// method with the sentinel as the return address, and so comes back
        /// for the next one.
        /// </summary>
        private void EnsurePump()
        {
            if (pump != null) return;
            process.Imports.Register("nativra-com.dll", "XAudioCallbackPump", CallConv.Stdcall, 0, c =>
            {
                Callback next;
                lock (pending)
                {
                    if (pending.Count == 0)
                    {
                        process.Block();
                        return 0;
                    }
                    next = pending.Dequeue();
                }
                var memory = process.Memory;
                var cpu = process.Cpu;
                var esp = cpu.Esp;
                if (next.Arguments > 1) { esp -= 4; memory.Write32(esp, next.B); }
                if (next.Arguments > 0) { esp -= 4; memory.Write32(esp, next.A); }
                esp -= 4; memory.Write32(esp, next.Target);   // this
                esp -= 4; memory.Write32(esp, pumpSentinel);   // back here afterwards
                cpu.Esp = esp;
                cpu.Eip = memory.Read32(memory.Read32(next.Target) + (uint)next.Slot * 4);
                CallbacksDelivered++;
                process.Jumped();
                return 0;
            });
            pumpSentinel = process.Imports.Bind("nativra-com.dll", "XAudioCallbackPump", -1);
            pump = process.CreateThread(pumpSentinel, 0, 64 * 1024, suspended: false, attach: false);
        }
    }
}
