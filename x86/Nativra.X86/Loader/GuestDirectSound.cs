using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Nativra.X86.Cpu;

namespace Nativra.X86.Loader
{
    public interface IGuestSoundOutput : IDisposable
    {
        int SampleRate { get; }
        void Start();
        void Write(float[] interleavedStereo);
        long FramesPlayed { get; }
        int FramesQueued { get; }
        void Stop();
    }

    /// <summary>DirectSound objects and their PCM mixer for a 32-bit guest.</summary>
    public sealed class GuestDirectSound : IDisposable
    {
        private const string Module = "dsound.dll", Methods = "nativra-dsound.dll";
        private const uint Invalid = 0x80004005, BadParam = 0x80070057, NoInterface = 0x80004002;
        private const uint NoDriver = 0x88780078;   // DSERR_NODRIVER
        private static readonly Guid Sound = new Guid("47d4d946-62e8-11cf-93bc-444553540000");
        private static readonly Guid Sound8 = new Guid("3901cc3f-84b5-4fa4-ba35-aa8172b8a09b");
        private static readonly Guid SoundIid = new Guid("a05aeec1-fefb-11d0-9953-00a0c925cd16");
        private static readonly Guid Sound8Iid = new Guid("c50a7e93-f395-4834-9ef6-7fa99de50966");
        private static readonly Guid BufferIid = new Guid("279afa85-4981-11ce-a521-0020af0be560");
        private static readonly Guid Buffer8Iid = new Guid("6825a449-7524-4d82-920f-50e36ab3ab1e");
        private static readonly Guid NotifyIid = new Guid("b0210783-89cd-11d0-af08-00a0c925cd16");
        private static readonly Guid UnknownIid = new Guid("00000000-0000-0000-c000-000000000046");
        private static readonly Guid FactoryIid = new Guid("00000001-0000-0000-c000-000000000046");
        private readonly GuestProcess process;
        private readonly GuestKernel kernel;
        private readonly GuestMemory memory;
        private readonly IGuestSoundOutput output;
        private readonly Dictionary<uint, Item> items = new Dictionary<uint, Item>();
        private readonly List<Item> buffers = new List<Item>();
        private readonly object gate = new object();
        private Thread mixer;
        private bool running;
        private uint deviceTable, bufferTable, notifyTable, factoryTable, factory;
        private long framesMixed, underruns;
        private int restarts;
        private const int MaxRestarts = 5;
        private int buffersCreated;
        private sealed class Notice { public uint Offset, Event; public long Last = -1; }
        private sealed class Item
        {
            public uint Address, Data, Bytes, Flags, Frequency, BlockAlign;
            public int Channels, Bits, Tag, Volume, Pan, RefCount = 1;
            public bool Primary, Playing, Looping, Notify;
            public double Position;
            public double PlayBasePosition;
            public long StartFrame, LastFrame;
            public readonly List<Notice> Notices = new List<Notice>();
        }
        public int BuffersCreated => buffersCreated;
        public long FramesMixed => Interlocked.Read(ref framesMixed);
        public long Underruns => Interlocked.Read(ref underruns);
        public GuestDirectSound(GuestProcess process, GuestKernel kernel, IGuestSoundOutput output)
        {
            this.process = process; this.kernel = kernel; memory = process.Memory; this.output = output;
        }
        private Guid Id(uint p) => new Guid(memory.ReadBytes(p, 16));
        private bool IsSound(uint p) => p != 0 && (Id(p) == Sound || Id(p) == Sound8);
        private uint Make(uint table, Item item)
        {
            var address = kernel.Heap.Alloc(8, true);
            memory.Write32(address, table); item.Address = address; items[address] = item;
            return address;
        }
        private uint Slot(string face, int slot, int args, HostCall call)
        {
            var name = face + "#" + slot;
            process.Imports.Register(Methods, name, CallConv.Stdcall, args + 1, call);
            return process.Imports.Bind(Methods, name, -1);
        }
        private uint Table(string face, int[] argc, Func<int, HostCall> body)
        {
            var table = kernel.Heap.Alloc((uint)argc.Length * 4);
            for (int n = 0; n < argc.Length; n++) memory.Write32(table + (uint)n * 4, Slot(face, n, argc[n], body(n)));
            return table;
        }
        private Item Get(GuestCall c) { items.TryGetValue(c.Arg(0), out var item); return item; }
        private uint A(GuestCall c, int n) => c.Arg(n + 1);
        private uint QI(GuestCall c, int kind)
        {
            var item = Get(c); var result = A(c, 1);
            if (item == null || result == 0) return BadParam;
            var id = Id(A(c, 0));
            var accepted = id == UnknownIid || (kind == 0 ? id == SoundIid || id == Sound8Iid :
                kind == 1 ? id == BufferIid || id == Buffer8Iid : id == NotifyIid);
            if (kind == 1 && id == NotifyIid) accepted = true;
            if (!accepted) { memory.Write32(result, 0); return NoInterface; }
            if (kind == 1 && id == NotifyIid)
            {
                var notification = new Item { Data = item.Address, Notify = true };
                memory.Write32(result, Make(notifyTable, notification));
            }
            else { item.RefCount++; memory.Write32(result, item.Address); }
            return 0;
        }
        private uint Ref(GuestCall c, bool add)
        {
            var item = Get(c); if (item == null) return 0;
            if (add) return (uint)++item.RefCount;
            var count = --item.RefCount;
            if (count == 0)
            {
                if (!item.Notify && item.Data != 0 && item.Bytes != 0) { lock (gate) buffers.Remove(item); kernel.Heap.Free(item.Data); }
                items.Remove(item.Address); kernel.Heap.Free(item.Address);
            }
            return (uint)Math.Max(0, count);
        }
        private uint Create(uint result)
        {
            if (result == 0) return BadParam;
            if (!Started()) { memory.Write32(result, 0); return NoDriver; }
            memory.Write32(result, Make(deviceTable, new Item()));
            return 0;
        }
        public void Install()
        {
            process.HostServed.Add(Module);
            // Arguments after `this` per slot, in dsound.h order; a stdcall callee pops
            // them, so one wrong count misaligns the game's stack.
            deviceTable = Table("device", new[] { 2, 0, 0, 3, 1, 2, 2, 0, 1, 1, 1, 1 }, Device);
            bufferTable = Table("buffer", new[] { 2, 0, 0, 1, 2, 3, 1, 1, 1, 1, 2, 7, 3, 1, 1, 1, 1, 1, 0, 4, 0, 3, 3, 4 }, Buffer);
            notifyTable = Table("notify", new[] { 2, 0, 0, 2 }, Notify);
            factoryTable = Table("factory", new[] { 2, 0, 0, 3, 1 }, Factory);
            factory = Make(factoryTable, new Item());
            var i = process.Imports;
            i.Register(Module, "DirectSoundCreate", CallConv.Stdcall, 3, c => Create(c.Arg(1)));
            i.Register(Module, "DirectSoundCreate8", CallConv.Stdcall, 3, c => Create(c.Arg(1)));
            i.Register(Module, "DirectSoundEnumerateA", CallConv.Stdcall, 2, c => Enumerate(c.Arg(0), c.Arg(1), false));
            i.Register(Module, "DirectSoundEnumerateW", CallConv.Stdcall, 2, c => Enumerate(c.Arg(0), c.Arg(1), true));
            i.Register(Module, "GetDeviceID", CallConv.Stdcall, 2, c => { if (c.Arg(1) == 0) return BadParam; memory.WriteBytes(c.Arg(1), Guid.Empty.ToByteArray()); return 0; });
            i.Register(Module, "DllGetClassObject", CallConv.Stdcall, 3, c =>
            {
                if (!IsSound(c.Arg(0)) || c.Arg(1) == 0 || c.Arg(2) == 0) return BadParam;
                var id = Id(c.Arg(1));
                if (id != FactoryIid && id != UnknownIid) return NoInterface;
                items[factory].RefCount++;
                memory.Write32(c.Arg(2), factory);
                return 0;
            });
            var previous = kernel.CoCreateInstance;
            kernel.CoCreateInstance = (clsid, iid, result) => IsSound(clsid)
                ? (iid != 0 && Id(iid) != UnknownIid && Id(iid) != SoundIid && Id(iid) != Sound8Iid ? NoInterface : Create(result))
                : previous(clsid, iid, result);
            kernel.PollAudio += PollNotifications;
            kernel.AudioWaitActive = () => buffers.Exists(b => b.Playing && b.Notices.Count != 0);
        }

        /// <summary>
        /// The output and its mixer start with the first device a game makes, not
        /// with the layer: most 32-bit games never ask for DirectSound, and one whose
        /// output cannot start is told there is no driver, as Windows would.
        /// </summary>
        private bool Started()
        {
            if (running) return true;
            if (Failure != null) return false;
            try { output.Start(); }
            catch (Exception e) { Failure = "output did not start: " + e.Message; return false; }
            running = true;
            mixer = new Thread(MixLoop) { IsBackground = true, Name = "Guest DirectSound" };
            mixer.Start();
            return true;
        }

        /// <summary>Why the sound stopped or never started, for the report; null while it plays.</summary>
        public string Failure { get; private set; }
        private uint Enumerate(uint callback, uint context, bool wide)
        {
            if (callback != 0)
            {
                var description = Encoding.UTF8.GetBytes("Primary Sound Driver\0");
                var module = Encoding.UTF8.GetBytes("dsound.dll\0");
                if (wide) { description = Encoding.Unicode.GetBytes("Primary Sound Driver\0"); module = Encoding.Unicode.GetBytes("dsound.dll\0"); }
                var descAddress = kernel.Heap.Alloc((uint)description.Length);
                var moduleAddress = kernel.Heap.Alloc((uint)module.Length);
                memory.WriteBytes(descAddress, description); memory.WriteBytes(moduleAddress, module);
                kernel.CallGuestStdcall(callback, 0, descAddress, moduleAddress, context);
                kernel.Heap.Free(descAddress); kernel.Heap.Free(moduleAddress);
            }
            return 0;
        }
        private HostCall Factory(int slot)
        {
            switch (slot)
            {
                case 0:
                    return c =>
                {
                    var id = Id(A(c, 0));
                    if (id != FactoryIid && id != UnknownIid) return NoInterface;
                    items[factory].RefCount++;
                    memory.Write32(A(c, 1), factory);
                    return 0;
                };
                case 1: return c => Ref(c, true);
                case 2: return c => Ref(c, false);
                case 3: return c => A(c, 0) == 0 ? Create(A(c, 2)) : BadParam;
                default: return c => 0;
            }
        }
        private HostCall Device(int slot)
        {
            switch (slot)
            {
                case 0: return c => QI(c, 0);
                case 1: return c => Ref(c, true);
                case 2: return c => Ref(c, false);
                case 3: return c => CreateBuffer(c);
                case 4: return c => { var p = A(c, 0); if (p != 0) { memory.WriteBytes(p, new byte[96]); memory.Write32(p, 96); memory.Write32(p + 4, 1); memory.Write32(p + 8, 1); } return 0; };
                case 5:
                    return c =>
                {
                    if (!items.TryGetValue(A(c, 0), out var old) || A(c, 1) == 0) return BadParam;
                    var copy = new Item
                    {
                        Primary = old.Primary,
                        Bytes = old.Bytes,
                        Flags = old.Flags,
                        Frequency = old.Frequency,
                        Channels = old.Channels,
                        Bits = old.Bits,
                        Tag = old.Tag,
                        BlockAlign = old.BlockAlign,
                        Volume = old.Volume,
                        Pan = old.Pan
                    };
                    if (old.Bytes != 0)
                    {
                        copy.Data = kernel.Heap.Alloc(old.Bytes);
                        if (copy.Data == 0) return Invalid;
                        memory.WriteBytes(copy.Data, memory.ReadBytes(old.Data, (int)old.Bytes));
                    }
                    memory.Write32(A(c, 1), Make(bufferTable, copy));
                    lock (gate) buffers.Add(copy);
                    buffersCreated++;
                    return 0;
                };
                case 8: return c => { memory.Write32(A(c, 0), 2); return 0; };
                default: return c => 0;
            }
        }
        private uint CreateBuffer(GuestCall c)
        {
            var desc = A(c, 0); var result = A(c, 1);
            if (desc == 0 || result == 0) return BadParam;
            var flags = memory.Read32(desc + 4);
            var primary = (flags & 1) != 0;
            var size = primary ? 0u : memory.Read32(desc + 8);
            if (!primary && (size < 4 || size > 64 * 1024 * 1024)) return BadParam;
            var item = new Item { Primary = primary, Bytes = size, Flags = flags, Frequency = (uint)output.SampleRate, Channels = 2, Bits = 16, Tag = 1, BlockAlign = 4 };
            if (!primary)
            {
                var format = memory.Read32(desc + 16);
                if (format == 0 || !ReadFormat(item, format)) return BadParam;
                item.Data = kernel.Heap.Alloc(size, true);
                if (item.Data == 0) return Invalid;
            }
            memory.Write32(result, Make(bufferTable, item));
            lock (gate) buffers.Add(item);
            buffersCreated++;
            return 0;
        }
        private bool ReadFormat(Item item, uint address)
        {
            var tag = memory.Read16(address); var channels = memory.Read16(address + 2);
            var rate = memory.Read32(address + 4); var bits = memory.Read16(address + 14);
            if (tag == 0xFFFE)
            {
                tag = memory.Read16(address + 24);
                if (memory.Read16(address + 16) < 22) return false;
            }
            if ((tag != 1 && tag != 3) || channels < 1 || channels > 2 || rate < 8000 || rate > 192000 ||
                (tag == 1 && bits != 8 && bits != 16) || (tag == 3 && bits != 32)) return false;
            item.Tag = tag; item.Channels = channels; item.Frequency = rate; item.Bits = bits;
            item.BlockAlign = (uint)(channels * bits / 8); return true;
        }
        private uint Position(Item item)
        {
            if (item.Bytes == 0) return 0;
            var frame = item.Playing ? item.PlayBasePosition + Math.Max(0, output.FramesPlayed - item.StartFrame) *
                (double)item.Frequency / output.SampleRate : item.PlayBasePosition;
            if (!item.Looping) frame = Math.Min(frame, item.Bytes / item.BlockAlign - 1);
            return (uint)(((long)frame * item.BlockAlign) % item.Bytes);
        }
        private HostCall Buffer(int slot)
        {
            switch (slot)
            {
                case 0: return c => QI(c, 1);
                case 1: return c => Ref(c, true);
                case 2: return c => Ref(c, false);
                case 3: return c => { var i = Get(c); var p = A(c, 0); memory.Write32(p, 20); memory.Write32(p + 4, i.Flags); memory.Write32(p + 8, i.Bytes); memory.Write32(p + 12, 0); memory.Write32(p + 16, 0); return 0; };
                case 4: return c => { var i = Get(c); if (A(c, 0) != 0) memory.Write32(A(c, 0), Position(i)); if (A(c, 1) != 0) memory.Write32(A(c, 1), i.Bytes == 0 ? 0 : (Position(i) + Math.Min(i.Bytes / 4, i.Frequency * i.BlockAlign / 25)) % i.Bytes); return 0; };
                case 5: return c => { var i = Get(c); var p = A(c, 0); if (A(c, 2) != 0) memory.Write32(A(c, 2), 18); if (p != 0 && A(c, 1) >= 18) WriteFormat(i, p); return 0; };
                case 6: return c => { memory.Write32(A(c, 0), (uint)Get(c).Volume); return 0; };
                case 7: return c => { memory.Write32(A(c, 0), (uint)Get(c).Pan); return 0; };
                case 8: return c => { memory.Write32(A(c, 0), Get(c).Frequency); return 0; };
                case 9: return c => { var i = Get(c); memory.Write32(A(c, 0), (uint)((i.Playing ? 1 : 0) | (i.Playing && i.Looping ? 4 : 0))); return 0; };
                case 11: return c => Lock(c);
                case 12: return c => { var i = Get(c); lock (gate) { if (!i.Playing) { i.PlayBasePosition = i.Position; i.StartFrame = output.FramesPlayed; i.LastFrame = i.StartFrame; } i.Playing = true; i.Looping = (A(c, 2) & 1) != 0; } return 0; };
                case 13: return c => { var i = Get(c); lock (gate) { if (i.BlockAlign != 0) { i.Position = i.PlayBasePosition = (A(c, 0) % Math.Max(1, i.Bytes)) / i.BlockAlign; i.StartFrame = output.FramesPlayed; i.LastFrame = i.StartFrame; } } return 0; };
                case 14: return c => ReadFormat(Get(c), A(c, 0)) ? 0u : BadParam;
                case 15: return c => { Get(c).Volume = Math.Max(-10000, Math.Min(0, (int)A(c, 0))); return 0; };
                case 16: return c => { Get(c).Pan = Math.Max(-10000, Math.Min(10000, (int)A(c, 0))); return 0; };
                case 17: return c => { var i = Get(c); lock (gate) { i.PlayBasePosition = Position(i) / Math.Max(1, i.BlockAlign); i.StartFrame = output.FramesPlayed; i.Frequency = A(c, 0) == 0 ? (uint)output.SampleRate : A(c, 0); } return 0; };
                case 18: return c => { var i = Get(c); lock (gate) { i.PlayBasePosition = Position(i) / Math.Max(1, i.BlockAlign); i.Position = i.PlayBasePosition; i.Playing = false; } StopNotice(i); return 0; };
                case 19: return c => 0;
                default: return c => 0;
            }
        }
        private void WriteFormat(Item i, uint p)
        {
            memory.Write16(p, (ushort)i.Tag); memory.Write16(p + 2, (ushort)i.Channels);
            memory.Write32(p + 4, i.Frequency); memory.Write32(p + 8, i.Frequency * i.BlockAlign);
            memory.Write16(p + 12, (ushort)i.BlockAlign); memory.Write16(p + 14, (ushort)i.Bits);
            memory.Write16(p + 16, 0);
        }
        private uint Lock(GuestCall c)
        {
            var i = Get(c); if (i == null || i.Primary || i.Bytes == 0) return BadParam;
            var offset = (A(c, 6) & 1) != 0 ? Position(i) : A(c, 0);
            var length = (A(c, 6) & 2) != 0 ? i.Bytes : A(c, 1);
            if (offset >= i.Bytes || length > i.Bytes) return BadParam;
            var first = Math.Min(length, i.Bytes - offset);
            memory.Write32(A(c, 2), i.Data + offset); memory.Write32(A(c, 3), first);
            if (A(c, 4) != 0) memory.Write32(A(c, 4), length == first ? 0 : i.Data);
            if (A(c, 5) != 0) memory.Write32(A(c, 5), length - first);
            return 0;
        }
        private HostCall Notify(int slot)
        {
            if (slot == 0) return c => QI(c, 2);
            if (slot == 1) return c => Ref(c, true);
            if (slot == 2) return c => Ref(c, false);
            return c =>
            {
                var face = Get(c); if (face == null || !items.TryGetValue(face.Data, out var buffer)) return BadParam;
                buffer.Notices.Clear();
                var count = A(c, 0); var p = A(c, 1);
                if (count > 256 || (count != 0 && p == 0)) return BadParam;
                for (uint n = 0; n < count; n++) buffer.Notices.Add(new Notice { Offset = memory.Read32(p + n * 8), Event = memory.Read32(p + n * 8 + 4) });
                return 0;
            };
        }
        private void StopNotice(Item item)
        {
            foreach (var notice in item.Notices) if (notice.Offset == 0xFFFFFFFF) kernel.SignalAudioEvent(notice.Event);
        }
        public void PollNotifications()
        {
            var clock = output.FramesPlayed;
            foreach (var i in buffers)
            {
                if (!i.Playing || i.Bytes == 0 || i.BlockAlign == 0) continue;
                var frames = i.Bytes / i.BlockAlign;
                var from = i.LastFrame; var to = clock;
                if (to <= from) continue;
                var start = (long)i.PlayBasePosition;
                var now = start + (long)((to - i.StartFrame) * (double)i.Frequency / output.SampleRate);
                var old = now - (long)((to - from) * (double)i.Frequency / output.SampleRate);
                foreach (var notice in i.Notices)
                {
                    if (notice.Offset == 0xFFFFFFFF || notice.Offset >= i.Bytes) continue;
                    var offset = notice.Offset / i.BlockAlign;
                    if ((old - offset) / (long)frames < (now - offset) / (long)frames || (old < offset && now >= offset)) kernel.SignalAudioEvent(notice.Event);
                }
                if (!i.Looping && now >= frames) { i.Playing = false; i.PlayBasePosition = frames - 1; StopNotice(i); }
                i.LastFrame = to;
            }
        }
        private float Sample(Item i, int frame, int channel)
        {
            var total = (int)(i.Bytes / i.BlockAlign);
            if (total == 0) return 0;
            frame %= total; if (frame < 0) frame += total;
            var at = i.Data + (uint)frame * i.BlockAlign + (uint)Math.Min(channel, i.Channels - 1) * (uint)(i.Bits / 8);
            if (i.Tag == 3) return Bits.Int32BitsToSingle((int)memory.Read32(at));
            if (i.Bits == 8) return (memory.Read8(at) - 128) / 128f;
            return (short)memory.Read16(at) / 32768f;
        }
        public float[] Mix(int frames)
        {
            var data = new float[frames * 2];
            lock (gate)
            {
                foreach (var i in buffers)
                {
                    if (i.Primary || !i.Playing || i.Bytes == 0) continue;
                    var length = i.Bytes / i.BlockAlign;
                    var gain = (float)Math.Pow(10, i.Volume / 2000.0);
                    var left = gain * (i.Pan > 0 ? 1 - i.Pan / 10000f : 1);
                    var right = gain * (i.Pan < 0 ? 1 + i.Pan / 10000f : 1);
                    var step = (double)i.Frequency / output.SampleRate;
                    for (int n = 0; n < frames; n++)
                    {
                        if (i.Position >= length)
                        {
                            if (!i.Looping) break;
                            i.Position %= length;
                        }
                        var floor = (int)i.Position; var part = (float)(i.Position - floor);
                        var next = floor + 1 < length || i.Looping ? floor + 1 : floor;
                        data[n * 2] += (Sample(i, floor, 0) + (Sample(i, next, 0) - Sample(i, floor, 0)) * part) * left;
                        data[n * 2 + 1] += (Sample(i, floor, 1) + (Sample(i, next, 1) - Sample(i, floor, 1)) * part) * right;
                        i.Position += step;
                    }
                }
            }
            for (int n = 0; n < data.Length; n++) data[n] = Math.Max(-1, Math.Min(1, data[n]));
            Interlocked.Add(ref framesMixed, frames);
            return data;
        }
        private void MixLoop()
        {
            while (running)
            {
                try
                {
                    if (output.FramesQueued < output.SampleRate / 10) output.Write(Mix(512));
                    else Thread.Sleep(4);
                }
                catch (Exception e)
                {
                    // A voice that refuses a buffer (the console's audio session
                    // changing under it) is rebuilt; only one that keeps failing
                    // silences the game.
                    Interlocked.Increment(ref underruns);
                    Failure = "output failed: " + e.Message;
                    if (++restarts > MaxRestarts) { Failure = "mixer stopped: " + e.Message; running = false; break; }
                    try { output.Stop(); Thread.Sleep(100); output.Start(); }
                    catch (Exception again) { Failure = "mixer stopped: " + again.Message; running = false; }
                }
            }
        }
        public void Dispose()
        {
            var started = mixer != null;
            running = false; mixer?.Join(500); kernel.PollAudio -= PollNotifications; kernel.AudioWaitActive = null;
            if (started) output.Stop();
            output.Dispose();
        }
    }
}
