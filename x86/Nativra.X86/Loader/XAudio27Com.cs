using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Nativra.X86.Loader
{
    /// <summary>
    /// XAudio 2.7 for a 32-bit game, served by the 64-bit xaudio2_7 shim the
    /// app already carries (which runs on XAudio 2.9).
    ///
    /// PCM audio stays in guest memory; ADPCM blocks are decoded into host-owned
    /// buffers that survive until OnBufferEnd. The bridge also translates structures that
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
        private readonly Dictionary<IntPtr, AdpcmDecoder> sourceFormats = new Dictionary<IntPtr, AdpcmDecoder>();
        private readonly Dictionary<long, DecodedBuffer> decodedBuffers = new Dictionary<long, DecodedBuffer>();
        private long nextBufferId = 0x100000000L;
        private AdpcmDecoder creatingFormat;
        private IntPtr creatingPcm;

        private sealed class DecodedBuffer
        {
            public IntPtr Data;
            public IntPtr Voice;
            public uint GuestContext;
        }

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
            com.XAudio27 = this;
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
                    memory.Write32(guest, GuestContext(Marshal.ReadIntPtr(host)));
                    memory.Write32(guest + 4, (uint)Marshal.ReadInt32(host, 8));
                    memory.Write64(guest + 8, (ulong)Marshal.ReadInt64(host, 16));
                });
                return host;
            });

            // A guest IXAudio2VoiceCallback: a host stand-in that queues each call.
            com.AddArgument('K', (guest, slot, after) => CallbackProxy(guest));
        }

        internal unsafe void PrepareSourceVoice(uint guestFormat, IntPtr* args)
        {
            creatingFormat = null;
            creatingPcm = IntPtr.Zero;
            if (guestFormat == 0) return;
            var memory = process.Memory;
            var tag = memory.Read16(guestFormat);
            if (tag != 2 && tag != 0x11) return;
            var length = 18 + memory.Read16(guestFormat + 16);
            var decoder = new AdpcmDecoder(memory.ReadBytes(guestFormat, length));
            var pcm = Marshal.AllocHGlobal(18);
            Marshal.WriteInt16(pcm, 0, 1);
            Marshal.WriteInt16(pcm, 2, (short)decoder.Channels);
            var rate = memory.Read32(guestFormat + 4);
            Marshal.WriteInt32(pcm, 4, (int)rate);
            Marshal.WriteInt32(pcm, 8, checked((int)(rate * (uint)(decoder.Channels * 2))));
            Marshal.WriteInt16(pcm, 12, (short)(decoder.Channels * 2));
            Marshal.WriteInt16(pcm, 14, 16);
            Marshal.WriteInt16(pcm, 16, 0);
            args[2] = pcm;
            creatingFormat = decoder;
            creatingPcm = pcm;
        }

        internal unsafe void FinishSourceVoice(int result, IntPtr* args)
        {
            if (creatingPcm == IntPtr.Zero) return;
            if (result >= 0)
            {
                var voice = Marshal.ReadIntPtr(args[1]);
                if (voice != IntPtr.Zero) sourceFormats[voice] = creatingFormat;
            }
            Marshal.FreeHGlobal(creatingPcm);
            creatingPcm = IntPtr.Zero;
            creatingFormat = null;
        }

        internal unsafe void PrepareSourceBuffer(IntPtr voice, IntPtr* args)
        {
            if (!sourceFormats.TryGetValue(voice, out var format) || args[1] == IntPtr.Zero) return;
            var buffer = args[1];
            var length = Marshal.ReadInt32(buffer, 4);
            if (length < 0) throw new ArgumentException("Invalid ADPCM buffer length");
            var source = Marshal.ReadIntPtr(buffer, 8);
            var encoded = new byte[length];
            if (length != 0) Marshal.Copy(source, encoded, 0, length);
            var pcm = format.Decode(encoded);
            var data = Marshal.AllocHGlobal(Math.Max(1, pcm.Length));
            if (pcm.Length != 0) Marshal.Copy(pcm, 0, data, pcm.Length);
            var context = (uint)Marshal.ReadInt64(buffer, 40);
            long token;
            lock (decodedBuffers)
            {
                token = nextBufferId++;
                decodedBuffers[token] = new DecodedBuffer { Data = data, Voice = voice, GuestContext = context };
            }
            Marshal.WriteInt32(buffer, 4, pcm.Length);
            Marshal.WriteIntPtr(buffer, 8, data);
            Marshal.WriteIntPtr(buffer, 40, new IntPtr(token));
        }

        internal unsafe void FinishSourceBuffer(int result, IntPtr* args)
        {
            if (result < 0 && args[1] != IntPtr.Zero) ReleaseDecoded(Marshal.ReadIntPtr(args[1], 40));
        }

        private uint GuestContext(IntPtr context)
        {
            lock (decodedBuffers)
                return decodedBuffers.TryGetValue(context.ToInt64(), out var buffer)
                    ? buffer.GuestContext : (uint)context.ToInt64();
        }

        private uint ReleaseDecoded(IntPtr context)
        {
            lock (decodedBuffers)
            {
                if (!decodedBuffers.TryGetValue(context.ToInt64(), out var buffer)) return (uint)context.ToInt64();
                decodedBuffers.Remove(context.ToInt64());
                Marshal.FreeHGlobal(buffer.Data);
                return buffer.GuestContext;
            }
        }

        internal void ForgetVoice(IntPtr voice)
        {
            sourceFormats.Remove(voice);
            lock (decodedBuffers)
            {
                var tokens = new List<long>();
                foreach (var entry in decodedBuffers)
                    if (entry.Value.Voice == voice) tokens.Add(entry.Key);
                foreach (var token in tokens)
                {
                    Marshal.FreeHGlobal(decodedBuffers[token].Data);
                    decodedBuffers.Remove(token);
                }
            }
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
            if (guest != 0) EnsurePump();
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
                new Context((self, context) => Queue(self, 3, 1, GuestContext(context), 0)),
                new Context((self, context) => Queue(self, 4, 1, ReleaseDecoded(context), 0)),
                new Context((self, context) => Queue(self, 5, 1, GuestContext(context), 0)),
                new Error((self, context, error) => Queue(self, 6, 2, GuestContext(context), (uint)error)),
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
            if (target == 0) return;
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
