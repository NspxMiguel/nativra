using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nativra.X86.Cpu;
using Nativra.X86.Loader;
using Xunit;

namespace Nativra.X86.Tests
{
    /// <summary>
    /// XAudio 2.7 through the COM bridge against a host engine built from
    /// managed delegates: creation through CoCreateInstance, the buffer
    /// structure's translation, and a voice callback raised "on the audio
    /// thread" and delivered to guest code by the delivery thread.
    /// </summary>
    public sealed class XAudio27ComTests : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint SelfFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CreateSourceFn(IntPtr self, IntPtr voice, IntPtr format, uint flags, float ratio,
            IntPtr callback, IntPtr sends, IntPtr effects);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SubmitFn(IntPtr self, IntPtr buffer, IntPtr wma);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void ContextFn(IntPtr self, IntPtr context);

        private const uint Code = 0x00600000, Data = 0x00601000;
        private const uint Spin = Code, OnBufferEnd = Code + 0x0F, Ret4 = Code + 0x1B, Ret8 = Code + 0x1E, Ret12 = Code + 0x21;

        // spin: while ([0x601040] == 0) {} return [0x601040]
        // OnBufferEnd(this, context): [0x601040] = context; ret 8
        // then ret 4 / ret 8 / ret 12 stubs for the other callback methods.
        private static readonly byte[] Program =
        {
            0x83, 0x3D, 0x40, 0x10, 0x60, 0x00, 0x00, 0x74, 0xF7, 0xA1, 0x40, 0x10, 0x60, 0x00, 0xC3, 0x8B,
            0x44, 0x24, 0x08, 0xA3, 0x40, 0x10, 0x60, 0x00, 0xC2, 0x08, 0x00, 0xC2, 0x04, 0x00, 0xC2, 0x08,
            0x00, 0xC2, 0x0C, 0x00,
        };

        private readonly List<Delegate> keep = new List<Delegate>();
        private readonly List<IntPtr> blocks = new List<IntPtr>();
        private readonly GuestProcess p;
        private readonly GuestKernel kernel;
        private IntPtr hostCallback;
        private float seenRatio;
        private int seenBytes;
        private long seenData, seenContext;

        public XAudio27ComTests()
        {
            p = new GuestProcess(new GuestMemory(native: true), useJit: false);
            kernel = new GuestKernel(p);
            kernel.Install();
            p.Memory.Map(Code, 0x3000);
            p.Memory.WriteBytes(Code, Program);
        }

        public void Dispose()
        {
            foreach (var b in blocks) Marshal.FreeHGlobal(b);
            p.Dispose();
        }

        private IntPtr Object(params Delegate[] methods)
        {
            keep.AddRange(methods);
            var table = Marshal.AllocHGlobal(methods.Length * IntPtr.Size);
            for (var n = 0; n < methods.Length; n++)
                Marshal.WriteIntPtr(table, n * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(methods[n]));
            var obj = Marshal.AllocHGlobal(16);
            Marshal.WriteIntPtr(obj, table);
            blocks.Add(table);
            blocks.Add(obj);
            return obj;
        }

        private uint Call(uint function, params uint[] args)
        {
            var result = p.Call(function, out var eax, 1_000_000, args);
            Assert.True(result.Ok, result.ToString());
            return eax;
        }

        private uint Method(uint obj, int slot, params uint[] args)
        {
            var all = new uint[args.Length + 1];
            all[0] = obj;
            Array.Copy(args, 0, all, 1, args.Length);
            return Call(p.Memory.Read32(p.Memory.Read32(obj) + (uint)slot * 4), all);
        }

        [Fact]
        public void SourceVoiceBuffersTranslateAndCallbacksReachTheGuest()
        {
            var noop = new SelfFn(self => 0);
            var voiceMethods = new Delegate[29];
            for (var n = 0; n < voiceMethods.Length; n++) voiceMethods[n] = noop;
            voiceMethods[21] = new SubmitFn((self, buffer, wma) =>
            {
                seenBytes = Marshal.ReadInt32(buffer, 4);
                seenData = Marshal.ReadInt64(buffer, 8);
                seenContext = Marshal.ReadInt64(buffer, 40);
                return 0;
            });
            var voice = Object(voiceMethods);

            var engineMethods = new Delegate[16];
            for (var n = 0; n < engineMethods.Length; n++) engineMethods[n] = noop;
            engineMethods[8] = new CreateSourceFn((self, slot, format, flags, ratio, callback, sends, effects) =>
            {
                seenRatio = ratio;
                hostCallback = callback;
                Marshal.WriteIntPtr(slot, voice);
                return 0;
            });
            var engine = Object(engineMethods);

            var com = new GuestCom(p, kernel);
            var xaudio = new XAudio27Com(p, kernel, com);
            xaudio.Install((clsid, iid, result) => { Marshal.WriteIntPtr(result, engine); return 0; });

            // CoCreateInstance(CLSID_XAudio2, NULL, CLSCTX_INPROC_SERVER, IID_IXAudio2, &engine)
            var clsid = kernel.Heap.Alloc(16);
            p.Memory.WriteBytes(clsid, XAudio27Com.ReleaseClass.ToByteArray());
            var iid = kernel.Heap.Alloc(16);
            p.Memory.WriteBytes(iid, com.Find("IXAudio2").Iid.ToByteArray());
            var out1 = kernel.Heap.Alloc(4);
            Assert.Equal(0u, Call(p.Imports.Bind("ole32.dll", "CoCreateInstance", -1), clsid, 0, 1, iid, out1));
            var guestEngine = p.Memory.Read32(out1);
            Assert.NotEqual(0u, guestEngine);

            // The guest's callback object: a vtable of seven methods.
            var table = Data + 0x200;
            uint[] entries = { Ret8, Ret4, Ret4, Ret8, OnBufferEnd, Ret8, Ret12 };
            for (var n = 0; n < entries.Length; n++) p.Memory.Write32(table + (uint)n * 4, entries[n]);
            var callback = Data + 0x100;
            p.Memory.Write32(callback, table);

            var format = kernel.Heap.Alloc(18, zero: true);
            var voiceSlot = kernel.Heap.Alloc(4);
            var ratio = BitConverter.ToUInt32(BitConverter.GetBytes(2.0f), 0);
            Assert.Equal(0u, Method(guestEngine, 8, voiceSlot, format, 0, ratio, callback, 0, 0));
            var guestVoice = p.Memory.Read32(voiceSlot);
            Assert.NotEqual(0u, guestVoice);
            Assert.Equal(2.0f, seenRatio);
            Assert.NotEqual(IntPtr.Zero, hostCallback);

            // SubmitSourceBuffer: a 36-byte XAUDIO2_BUFFER becomes the host's 48 bytes.
            var audio = kernel.Heap.Alloc(4096);
            var buffer = kernel.Heap.Alloc(36, zero: true);
            p.Memory.Write32(buffer + 4, 4096);      // AudioBytes
            p.Memory.Write32(buffer + 8, audio);     // pAudioData
            p.Memory.Write32(buffer + 32, 0x77);     // pContext
            Assert.Equal(0u, Method(guestVoice, 21, buffer, 0));
            Assert.Equal(4096, seenBytes);
            Assert.Equal(p.Memory.HostBase.ToInt64() + audio, seenData);   // the data is read in place
            Assert.Equal(0x77L, seenContext);

            // The audio thread finishes the buffer; the guest hears about it.
            // (The stand-in's methods are managed here, so the pointer comes
            // back as their own delegate type: call it as such.)
            var onBufferEnd = Marshal.GetDelegateForFunctionPointer(
                Marshal.ReadIntPtr(Marshal.ReadIntPtr(hostCallback), 4 * IntPtr.Size), typeof(ContextFn));
            onBufferEnd.DynamicInvoke(hostCallback, new IntPtr(0x77));
            Assert.Equal(0x77u, Call(Spin));
            Assert.Equal(1, xaudio.CallbacksDelivered);
        }
    }
}
