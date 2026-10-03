using System.Collections.Generic;
using System;
using System.Threading;
using Nativra.X86.Cpu;
using Nativra.X86.Loader;
using Xunit;

namespace Nativra.X86.Tests
{
    public sealed class GuestDirectSoundTests : IDisposable
    {
        private sealed class Output : IGuestSoundOutput
        {
            public int SampleRate => 48000;
            public long FramesPlayed { get; set; }
            public int FramesQueued => 48000;
            public void Start() { }
            public void Write(float[] samples) { }
            public void Stop() { }
            public void Dispose() { }
        }
        private readonly GuestProcess process = new GuestProcess(new GuestMemory(), useJit: false);
        private readonly GuestKernel kernel;
        private readonly GuestDirectSound sound;
        private readonly Output output = new Output();
        public GuestDirectSoundTests()
        {
            kernel = new GuestKernel(process);
            kernel.Install();
            sound = new GuestDirectSound(process, kernel, output);
            sound.Install();
        }
        public void Dispose() { sound.Dispose(); process.Dispose(); }
        private uint P(uint size = 128) => kernel.Heap.Alloc(size, true);
        private uint Call(string module, string name, params uint[] args)
        {
            var result = process.Call(process.Imports.Bind(module, name, -1), out var value, 1000000, args);
            Assert.True(result.Ok, result.ToString()); return value;
        }
        private uint M(uint obj, int slot, params uint[] args)
        {
            var all = new uint[args.Length + 1]; all[0] = obj; Array.Copy(args, 0, all, 1, args.Length);
            var table = process.Memory.Read32(obj);
            var result = process.Call(process.Memory.Read32(table + (uint)slot * 4), out var value, 1000000, all);
            Assert.True(result.Ok, result.ToString());
            return value;
        }

        /// <summary>
        /// Calls <paramref name="function"/> from guest code that measures ESP around
        /// the call, the way a compiled caller relies on it: zero when the callee
        /// popped exactly its arguments. (process.Call unwinds its own frame, so it
        /// cannot tell.)
        /// </summary>
        private int StackLeftOver(uint function, uint[] args)
        {
            var code = new List<byte> { 0x89, 0xE3 };                    // mov ebx, esp
            for (var n = args.Length - 1; n >= 0; n--) { code.Add(0x68); code.AddRange(BitConverter.GetBytes(args[n])); }   // push imm32
            code.Add(0xB8); code.AddRange(BitConverter.GetBytes(function));   // mov eax, function
            code.AddRange(new byte[] { 0xFF, 0xD0, 0x29, 0xE3, 0x89, 0xD8, 0xC3 });   // call eax; sub ebx, esp; mov eax, ebx; ret
            var at = P((uint)code.Count);
            process.Memory.WriteBytes(at, code.ToArray());
            var result = process.Call(at, out var leftOver, 1000000);
            Assert.True(result.Ok, result.ToString());
            return (int)leftOver;
        }

        [Fact]
        public void EveryMethodPopsTheArgumentsDsoundHDeclares()
        {
            // dsound.h, arguments after `this`. SetCooperativeLevel served with one
            // argument instead of two sent OpenAL Soft's mixer to address zero.
            var device = new[] { 2, 0, 0, 3, 1, 2, 2, 0, 1, 1, 1, 1 };
            var buffer = new[] { 2, 0, 0, 1, 2, 3, 1, 1, 1, 1, 2, 7, 3, 1, 1, 1, 1, 1, 0, 4, 0, 3, 3, 4 };
            var d = Device("DirectSoundCreate8");
            var b = Buffer();
            foreach (var (obj, counts) in new[] { (d, device), (b, buffer) })
                for (var slot = 3; slot < counts.Length; slot++)
                {
                    if (obj == d && slot == 3) continue;   // CreateSoundBuffer needs a real description
                    var args = new uint[counts[slot] + 1];
                    args[0] = obj;
                    for (var n = 1; n < args.Length; n++) args[n] = P(256);   // somewhere valid to write answers
                    var method = process.Memory.Read32(process.Memory.Read32(obj) + (uint)slot * 4);
                    Assert.True(StackLeftOver(method, args) == 0, $"slot {slot} left the stack unbalanced");
                }
        }
        private uint Device(string name)
        {
            var p = P(); Assert.Equal(0u, Call("dsound.dll", name, 0, p, 0)); return process.Memory.Read32(p);
        }
        private uint Buffer(uint flags = 0, uint size = 64, ushort bits = 16, ushort channels = 2)
        {
            var device = Device("DirectSoundCreate8"); var desc = P(); var fmt = P(); var result = P();
            process.Memory.Write32(desc, 20); process.Memory.Write32(desc + 4, flags);
            process.Memory.Write32(desc + 8, size); process.Memory.Write32(desc + 16, fmt);
            process.Memory.Write16(fmt, 1); process.Memory.Write16(fmt + 2, channels);
            process.Memory.Write32(fmt + 4, 48000); process.Memory.Write16(fmt + 12, (ushort)(channels * bits / 8));
            process.Memory.Write16(fmt + 14, bits);
            Assert.Equal(0u, M(device, 3, desc, result, 0));
            return process.Memory.Read32(result);
        }
        [Fact]
        public void ExportsAndInterfacesCreateObjects()
        {
            var one = Device("DirectSoundCreate"); var two = Device("DirectSoundCreate8");
            Assert.NotEqual(0u, one); Assert.NotEqual(0u, two);
            var caps = P(); Assert.Equal(0u, M(two, 4, caps)); Assert.Equal(96u, process.Memory.Read32(caps));
            var format = P(); process.Memory.Write16(format, 1); process.Memory.Write16(format + 2, 2);
            process.Memory.Write32(format + 4, 44100); process.Memory.Write16(format + 14, 16);
            var primary = Buffer(1, 0); Assert.Equal(0u, M(primary, 14, format));
            var iid = P(); var result = P();
            process.Memory.WriteBytes(iid, new Guid("6825a449-7524-4d82-920f-50e36ab3ab1e").ToByteArray());
            Assert.Equal(0u, M(primary, 0, iid, result)); Assert.Equal(primary, process.Memory.Read32(result));
            Assert.Equal(1u, M(primary, 2)); Assert.Equal(0u, M(primary, 2));
        }
        [Fact]
        public void LockWrapsAndClockControlsPosition()
        {
            var buffer = Buffer(); var p1 = P(); var p2 = P(); var n1 = P(); var n2 = P();
            Assert.Equal(0u, M(buffer, 11, 60, 16, p1, n1, p2, n2, 0));
            Assert.Equal(4u, process.Memory.Read32(n1)); Assert.Equal(12u, process.Memory.Read32(n2));
            Assert.Equal(0u, M(buffer, 19, process.Memory.Read32(p1), 4, process.Memory.Read32(p2), 12));
            Assert.Equal(0u, M(buffer, 12, 0, 0, 1));
            output.FramesPlayed = 3;
            var play = P(); var write = P(); M(buffer, 4, play, write);
            Assert.Equal(12u, process.Memory.Read32(play));
            Assert.Equal(0u, M(buffer, 18)); var status = P(); M(buffer, 9, status);
            Assert.Equal(0u, process.Memory.Read32(status));
        }
        [Fact]
        public void SeekingWhilePlayingMovesTheCursorAndTheMixerTogether()
        {
            var buffer = Buffer(0, 64, 16, 1);
            var data = P(); var length = P();
            M(buffer, 11, 0, 64, data, length, 0, 0, 0);
            var samples = process.Memory.Read32(data);
            process.Memory.Write16(samples, 8192);
            process.Memory.Write16(samples + 16, 16384);
            M(buffer, 12, 0, 0, 1);
            output.FramesPlayed = 3;
            Assert.Equal(0u, M(buffer, 13, 16));
            var play = P();
            M(buffer, 4, play, 0);
            Assert.Equal(16u, process.Memory.Read32(play));
            Assert.InRange(sound.Mix(1)[0], 0.49f, 0.51f);
            output.FramesPlayed = 4;
            M(buffer, 4, play, 0);
            Assert.Equal(18u, process.Memory.Read32(play));
        }
        [Fact]
        public void RepeatedPlayDoesNotRestartAnAlreadyPlayingBuffer()
        {
            var buffer = Buffer();
            M(buffer, 12, 0, 0, 1);
            output.FramesPlayed = 4;
            M(buffer, 12, 0, 0, 1);
            var play = P();
            M(buffer, 4, play, 0);
            Assert.Equal(16u, process.Memory.Read32(play));
        }
        [Fact]
        public void NonLoopingPlaybackStopsAtTheLastFrame()
        {
            var buffer = Buffer();
            M(buffer, 12, 0, 0, 0);
            output.FramesPlayed = 30;
            var play = P();
            M(buffer, 4, play, 0);
            Assert.Equal(60u, process.Memory.Read32(play));
            sound.PollNotifications();
            var status = P();
            M(buffer, 9, status);
            Assert.Equal(0u, process.Memory.Read32(status));
            M(buffer, 4, play, 0);
            Assert.Equal(60u, process.Memory.Read32(play));
        }
        [Fact]
        public void LoopingPlaybackWrapsWithoutStopping()
        {
            var buffer = Buffer();
            M(buffer, 12, 0, 0, 1);
            output.FramesPlayed = 18;
            var play = P();
            M(buffer, 4, play, 0);
            Assert.Equal(8u, process.Memory.Read32(play));
            sound.PollNotifications();
            var status = P();
            M(buffer, 9, status);
            Assert.Equal(5u, process.Memory.Read32(status));
        }
        [Fact]
        public void NotifyInterfaceSignalsCrossedOffsetsAndStop()
        {
            var buffer = Buffer(0x18000); var iid = P(); var result = P();
            process.Memory.WriteBytes(iid, new Guid("b0210783-89cd-11d0-af08-00a0c925cd16").ToByteArray());
            Assert.Equal(0u, M(buffer, 0, iid, result)); var notify = process.Memory.Read32(result);
            var event1 = Call("kernel32.dll", "CreateEventW", 0, 0, 0, 0);
            var event2 = Call("kernel32.dll", "CreateEventW", 0, 0, 0, 0);
            var stop = Call("kernel32.dll", "CreateEventW", 0, 0, 0, 0);
            var list = P(); process.Memory.Write32(list, 8); process.Memory.Write32(list + 4, event1);
            process.Memory.Write32(list + 8, 24); process.Memory.Write32(list + 12, event2);
            process.Memory.Write32(list + 16, 0xffffffff); process.Memory.Write32(list + 20, stop);
            Assert.Equal(0u, M(notify, 3, 3, list));
            M(buffer, 12, 0, 0, 1);
            output.FramesPlayed = 3;
            Assert.Equal(0u, Call("kernel32.dll", "WaitForSingleObject", event1, 0));
            Assert.Equal(0x102u, Call("kernel32.dll", "WaitForSingleObject", event2, 0));
            output.FramesPlayed = 7;
            Assert.Equal(0u, Call("kernel32.dll", "WaitForSingleObject", event2, 0));
            M(buffer, 18); Assert.Equal(0u, Call("kernel32.dll", "WaitForSingleObject", stop, 0));
        }
        [Fact]
        public void MixesPcmAndVolume()
        {
            var buffer = Buffer(0, 64, 16, 1); var p1 = P(); var p2 = P(); var n1 = P(); var n2 = P();
            M(buffer, 11, 0, 64, p1, n1, p2, n2, 0);
            var data = process.Memory.Read32(p1);
            for (uint n = 0; n < 64; n += 2) process.Memory.Write16(data + n, 16384);
            M(buffer, 19, data, 64, 0, 0); M(buffer, 15, unchecked((uint)-600));
            M(buffer, 12, 0, 0, 1);
            var samples = sound.Mix(1);
            Assert.InRange(samples[0], 0.24f, 0.26f);
            Assert.InRange(samples[1], 0.24f, 0.26f);
        }
        [Fact]
        public void MixesEightBitStereoWithPan()
        {
            var buffer = Buffer(0, 64, 8, 2); var p1 = P(); var p2 = P(); var n1 = P(); var n2 = P();
            M(buffer, 11, 0, 64, p1, n1, p2, n2, 0);
            var data = process.Memory.Read32(p1);
            process.Memory.Write8(data, 255); process.Memory.Write8(data + 1, 255);
            M(buffer, 19, data, 64, 0, 0);
            M(buffer, 16, 10000); M(buffer, 12, 0, 0, 1);
            var sample = sound.Mix(1);
            Assert.Equal(0f, sample[0]);
            Assert.InRange(sample[1], 0.99f, 1f);
        }
        [Fact]
        public void CoCreateInstancePreservesOtherClasses()
        {
            var clsid = P(); var iid = P(); var result = P();
            process.Memory.WriteBytes(clsid, new Guid("3901cc3f-84b5-4fa4-ba35-aa8172b8a09b").ToByteArray());
            process.Memory.WriteBytes(iid, new Guid("c50a7e93-f395-4834-9ef6-7fa99de50966").ToByteArray());
            Assert.Equal(0u, Call("ole32.dll", "CoCreateInstance", clsid, 0, 1, iid, result));
            Assert.NotEqual(0u, process.Memory.Read32(result));
            process.Memory.WriteBytes(clsid, Guid.NewGuid().ToByteArray());
            Assert.Equal(0x80040154u, Call("ole32.dll", "CoCreateInstance", clsid, 0, 1, iid, result));
        }
        [Fact]
        public void ClassFactoryCreatesDirectSound()
        {
            var clsid = P(); var iid = P(); var result = P();
            process.Memory.WriteBytes(clsid, new Guid("47d4d946-62e8-11cf-93bc-444553540000").ToByteArray());
            process.Memory.WriteBytes(iid, new Guid("00000001-0000-0000-c000-000000000046").ToByteArray());
            Assert.Equal(0u, Call("dsound.dll", "DllGetClassObject", clsid, iid, result));
            var factory = process.Memory.Read32(result);
            Assert.NotEqual(0u, factory);
            process.Memory.WriteBytes(iid, new Guid("a05aeec1-fefb-11d0-9953-00a0c925cd16").ToByteArray());
            Assert.Equal(0u, M(factory, 3, 0, iid, result));
            Assert.NotEqual(factory, process.Memory.Read32(result));
            Assert.Equal(1u, M(factory, 2));
        }
        [Fact]
        public void GuestWaitForMultipleObjectsWakesAsPlaybackPassesNotification()
        {
            var buffer = Buffer(0x18000);
            var iid = P(); var result = P();
            process.Memory.WriteBytes(iid, new Guid("b0210783-89cd-11d0-af08-00a0c925cd16").ToByteArray());
            M(buffer, 0, iid, result);
            var notify = process.Memory.Read32(result);
            var first = Call("kernel32.dll", "CreateEventW", 0, 0, 0, 0);
            var second = Call("kernel32.dll", "CreateEventW", 0, 0, 0, 0);
            var notices = P(); process.Memory.Write32(notices, 40); process.Memory.Write32(notices + 4, first);
            process.Memory.Write32(notices + 8, 8); process.Memory.Write32(notices + 12, second);
            M(notify, 3, 2, notices); M(buffer, 12, 0, 0, 1);
            const uint code = 0x600000, data = 0x601000;
            process.Memory.Map(code, 0x2000);
            process.Memory.Write32(data, first); process.Memory.Write32(data + 4, second);
            process.Memory.Write32(data + 8, process.Imports.Bind("kernel32.dll", "WaitForMultipleObjects", -1));
            // push INFINITE; push FALSE; push handles; push 2; call [WaitForMultipleObjects]; ret
            process.Memory.WriteBytes(code, new byte[] { 0x6a, 0xff, 0x6a, 0, 0x68, 0, 0x10, 0x60, 0,
                0x6a, 2, 0xff, 0x15, 8, 0x10, 0x60, 0, 0xc3 });
            var advance = new Thread(() => { Thread.Sleep(20); output.FramesPlayed = 3; });
            advance.Start();
            var run = process.Call(code, out var value, 1000000);
            advance.Join();
            Assert.True(run.Ok, run.ToString());
            Assert.Equal(1u, value);
        }
    }
}
