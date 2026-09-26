using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Nativra.X86.Cpu;

namespace Nativra.X86.Loader
{
    // The smaller system DLLs games import, each as a whole surface:
    // winmm (waveOut playing in real time, with WOM_DONE through callbacks,
    // events or messages; multimedia timers; joysticks as unplugged),
    // advapi32 (a registry kept in the game folder, users, tokens, hashing
    // and random numbers), shell32 (known folders inside the game's user
    // folder, command-line splitting), version (VS_VERSIONINFO read from
    // mapped images, synthesised for system DLLs), oleaut32 (BSTR, VARIANT),
    // comctl32, comdlg32, gdiplus's start-up, and the network as absent:
    // ws2_32 and wininet start and answer "not connected", so a game takes
    // its offline path.
    public sealed partial class GuestKernel
    {
        /// <summary>A known folder as the guest sees it: under C:\\users\\Player, as on Wine.</summary>
        private static string KnownFolderPath(string name)
        {
            switch (name)
            {
                case "": return GuestProfile;
                case "SavedGames": return GuestProfile + "\\Saved Games";
                case "AppData": return GuestProfile + "\\AppData\\Roaming";
                case "LocalAppData": return GuestProfile + "\\AppData\\Local";
                case "LocalAppDataLow": return GuestProfile + "\\AppData\\LocalLow";
                case "ProgramData": return "C:\\ProgramData";
                case "PublicDocuments": return "C:\\users\\Public\\Documents";
                default: return GuestProfile + "\\" + name;   // Documents, Desktop, Music, Pictures, Videos
            }
        }

        private void InstallLibraries(GuestImports i)
        {
            InstallServiceThreads(i);
            InstallWinmm(i);
            InstallRegistry(i);
            InstallAdvapi(i);
            InstallShell32(i);
            InstallVersion(i);
            InstallOleAut(i);
            InstallSmallDlls(i);
            InstallNetwork(i);
        }

        // --- service threads ------------------------------------------------------
        // A guest thread whose start routine is a host sentinel: the host decides
        // at each step whether it waits, calls a guest callback (which returns
        // to the sentinel) or ends. Timer and waveOut callbacks run on these, on
        // a guest thread of their own as on Windows, never nested in a host call.

        private struct ServiceStep
        {
            public int Kind;          // 0 wait, 1 call, 2 exit
            public uint Milliseconds;
            public uint Function;
            public uint[] Args;
            public static ServiceStep Wait(uint ms) => new ServiceStep { Kind = 0, Milliseconds = ms };
            public static ServiceStep Call(uint f, params uint[] a) => new ServiceStep { Kind = 1, Function = f, Args = a };
            public static ServiceStep Exit => new ServiceStep { Kind = 2 };
        }

        private readonly Dictionary<uint, Func<ServiceStep>> services = new Dictionary<uint, Func<ServiceStep>>();
        private uint serviceSentinel;
        private uint nextService = 1;

        private void InstallServiceThreads(GuestImports i)
        {
            serviceSentinel = i.Bind("nativra.dll", "Service", -1);
            i.Register("nativra.dll", "Service", CallConv.Stdcall, 1, c => RunService());
        }

        private uint StartService(Func<ServiceStep> next)
        {
            var id = nextService++;
            services[id] = next;
            process.CreateThread(serviceSentinel, id, 64 * 1024, false, attach: false);
            return id;
        }

        private ulong RunService()
        {
            // [ESP] is the thread's exit address and [ESP+4] the service id,
            // at the start and after every stdcall callback has returned.
            var cpu = process.Cpu;
            var id = memory.Read32(cpu.Esp + 4);
            if (!services.TryGetValue(id, out var next)) { process.ExitCurrentThread(0); return 0; }
            var step = next();
            switch (step.Kind)
            {
                case 0:
                    if (!process.WaitTimedOut(step.Milliseconds)) { process.Block(); return 0; }
                    process.Yield();
                    // Waited: ask again at once rather than return.
                    cpu.Eip = serviceSentinel;
                    process.Jumped();
                    return 0;
                case 1:
                {
                    var esp = cpu.Esp - (uint)(step.Args.Length + 1) * 4;
                    memory.Write32(esp, serviceSentinel);
                    for (var n = 0; n < step.Args.Length; n++) memory.Write32(esp + 4 + (uint)n * 4, step.Args[n]);
                    cpu.Esp = esp;
                    cpu.Eip = step.Function;
                    process.Jumped();
                    return 0;
                }
                default:
                    services.Remove(id);
                    process.ExitCurrentThread(0);
                    return 0;
            }
        }

        // --- winmm -----------------------------------------------------------------------

        private sealed class WaveOut
        {
            public uint Handle, Callback, Instance, CallbackType;
            public int BytesPerSecond, BlockAlign;
            public readonly Queue<KeyValuePair<uint, long>> Playing = new Queue<KeyValuePair<uint, long>>();
            public readonly Queue<uint[]> Notices = new Queue<uint[]>();   // message, param1
            public long Cursor, PlayedBytes, PausedAt = -1;
            public uint Volume = 0xFFFFFFFF;
            public bool Closed;
        }

        private readonly Dictionary<uint, WaveOut> waveOuts = new Dictionary<uint, WaveOut>();
        private uint nextWave = 0x00AA0010;

        private sealed class MmTimer { public uint Id, Delay, Procedure, User, Flags; public bool Killed; public long Due; }
        private readonly Dictionary<uint, MmTimer> mmTimers = new Dictionary<uint, MmTimer>();
        private uint nextMmTimer = 1;

        private void InstallWinmm(GuestImports i)
        {
            const string w = "winmm.dll";
            i.Register(w, "timeGetDevCaps", CallConv.Stdcall, 2, c => { memory.Write32(c.Arg(0), 1); memory.Write32(c.Arg(0) + 4, 1000000); return 0; });
            i.Register(w, "timeGetSystemTime", CallConv.Stdcall, 2, c => { memory.Write32(c.Arg(0), 1); memory.Write32(c.Arg(0) + 4, (uint)Milliseconds); return 0; });
            i.Register(w, "timeSetEvent", CallConv.Stdcall, 5, c => SetMmTimer(c.Arg(0), c.Arg(2), c.Arg(3), c.Arg(4)));
            i.Register(w, "timeKillEvent", CallConv.Stdcall, 1, c =>
            {
                if (!mmTimers.TryGetValue(c.Arg(0), out var t)) return 97;   // MMSYSERR_INVALPARAM
                t.Killed = true;
                mmTimers.Remove(c.Arg(0));
                return 0;
            });

            i.Register(w, "waveOutGetNumDevs", CallConv.Stdcall, 0, c => 1);
            i.Register(w, "waveOutGetDevCapsA", CallConv.Stdcall, 3, c => WaveCaps(c.Arg(1), false));
            i.Register(w, "waveOutGetDevCapsW", CallConv.Stdcall, 3, c => WaveCaps(c.Arg(1), true));
            i.Register(w, "waveOutOpen", CallConv.Stdcall, 6, c => WaveOpen(c));
            i.Register(w, "waveOutClose", CallConv.Stdcall, 1, c => WaveClose(c.Arg(0)));
            i.Register(w, "waveOutPrepareHeader", CallConv.Stdcall, 3, c => { memory.Write32(c.Arg(1) + 16, memory.Read32(c.Arg(1) + 16) | 2); return 0; });
            i.Register(w, "waveOutUnprepareHeader", CallConv.Stdcall, 3, c =>
            {
                var flags = memory.Read32(c.Arg(1) + 16);
                if ((flags & 0x10) != 0) return 33;   // WAVERR_STILLPLAYING
                memory.Write32(c.Arg(1) + 16, flags & ~2u);
                return 0;
            });
            i.Register(w, "waveOutWrite", CallConv.Stdcall, 3, c => WaveWrite(c.Arg(0), c.Arg(1)));
            i.Register(w, "waveOutReset", CallConv.Stdcall, 1, c =>
            {
                if (!waveOuts.TryGetValue(c.Arg(0), out var d)) return 5;   // MMSYSERR_INVALHANDLE
                while (d.Playing.Count > 0) FinishBuffer(d, d.Playing.Dequeue().Key);
                d.Cursor = Milliseconds;
                d.PlayedBytes = 0;
                d.PausedAt = -1;
                return 0;
            });
            i.Register(w, "waveOutPause", CallConv.Stdcall, 1, c => { if (waveOuts.TryGetValue(c.Arg(0), out var d) && d.PausedAt < 0) d.PausedAt = Milliseconds; return 0; });
            i.Register(w, "waveOutRestart", CallConv.Stdcall, 1, c =>
            {
                if (!waveOuts.TryGetValue(c.Arg(0), out var d) || d.PausedAt < 0) return 0;
                var shift = Milliseconds - d.PausedAt;
                var moved = new List<KeyValuePair<uint, long>>();
                while (d.Playing.Count > 0) { var p = d.Playing.Dequeue(); moved.Add(new KeyValuePair<uint, long>(p.Key, p.Value + shift)); }
                foreach (var p in moved) d.Playing.Enqueue(p);
                d.Cursor += shift;
                d.PausedAt = -1;
                return 0;
            });
            i.Register(w, "waveOutBreakLoop", CallConv.Stdcall, 1, c => 0);
            i.Register(w, "waveOutGetPosition", CallConv.Stdcall, 3, c => WavePosition(c.Arg(0), c.Arg(1)));
            i.Register(w, "waveOutGetVolume", CallConv.Stdcall, 2, c => { memory.Write32(c.Arg(1), waveOuts.TryGetValue(c.Arg(0), out var d) ? d.Volume : 0xFFFFFFFF); return 0; });
            i.Register(w, "waveOutSetVolume", CallConv.Stdcall, 2, c => { if (waveOuts.TryGetValue(c.Arg(0), out var d)) d.Volume = c.Arg(1); return 0; });
            i.Register(w, "waveOutGetID", CallConv.Stdcall, 2, c => { memory.Write32(c.Arg(1), 0); return 0; });
            i.Register(w, "waveOutMessage", CallConv.Stdcall, 4, c => 0);
            i.Register(w, "waveOutGetErrorTextA", CallConv.Stdcall, 3, c => { CopyTruncated("Undefined external error.", c.Arg(1), c.Arg(2), false); return 0; });
            i.Register(w, "waveOutGetErrorTextW", CallConv.Stdcall, 3, c => { CopyTruncated("Undefined external error.", c.Arg(1), c.Arg(2), true); return 0; });
            foreach (var none in new[] { "waveInGetNumDevs", "midiOutGetNumDevs", "midiInGetNumDevs", "mixerGetNumDevs", "auxGetNumDevs" })
                i.Register(w, none, CallConv.Stdcall, 0, c => 0);
            i.Register(w, "waveInOpen", CallConv.Stdcall, 6, c => 2);   // MMSYSERR_BADDEVICEID
            i.Register(w, "waveInGetDevCapsA", CallConv.Stdcall, 3, c => 2);
            i.Register(w, "waveInGetDevCapsW", CallConv.Stdcall, 3, c => 2);
            foreach (var name in new[] { "waveInClose", "waveInStart", "waveInStop", "waveInReset" })
                i.Register(w, name, CallConv.Stdcall, 1, c => 5);   // MMSYSERR_INVALHANDLE: nothing is open
            foreach (var name in new[] { "waveInPrepareHeader", "waveInUnprepareHeader", "waveInAddBuffer" })
                i.Register(w, name, CallConv.Stdcall, 3, c => 5);
            i.Register(w, "waveInGetPosition", CallConv.Stdcall, 3, c => 5);
            i.Register(w, "waveInGetID", CallConv.Stdcall, 2, c => 5);
            i.Register(w, "midiOutOpen", CallConv.Stdcall, 5, c => 2);
            i.Register(w, "mixerOpen", CallConv.Stdcall, 5, c => 2);
            i.Register(w, "joyGetNumDevs", CallConv.Stdcall, 0, c => 16);
            i.Register(w, "joyGetPos", CallConv.Stdcall, 2, c => 167);     // JOYERR_UNPLUGGED
            i.Register(w, "joyGetPosEx", CallConv.Stdcall, 2, c => 167);
            i.Register(w, "joyGetDevCapsA", CallConv.Stdcall, 3, c => 167);
            i.Register(w, "joyGetDevCapsW", CallConv.Stdcall, 3, c => 167);
            i.Register(w, "joySetCapture", CallConv.Stdcall, 4, c => 167);
            i.Register(w, "joyReleaseCapture", CallConv.Stdcall, 1, c => 0);
            i.Register(w, "PlaySoundA", CallConv.Stdcall, 3, c => 1);
            i.Register(w, "PlaySoundW", CallConv.Stdcall, 3, c => 1);
            i.Register(w, "sndPlaySoundA", CallConv.Stdcall, 2, c => 1);
            i.Register(w, "sndPlaySoundW", CallConv.Stdcall, 2, c => 1);
            i.Register(w, "mciSendStringA", CallConv.Stdcall, 4, c => 0x113);   // MCIERR_DEVICE_NOT_INSTALLED-ish: no MCI devices
            i.Register(w, "mciSendStringW", CallConv.Stdcall, 4, c => 0x113);
            i.Register(w, "mciSendCommandA", CallConv.Stdcall, 4, c => 0x113);
            i.Register(w, "mciSendCommandW", CallConv.Stdcall, 4, c => 0x113);
            i.Register(w, "mciGetErrorStringA", CallConv.Stdcall, 3, c => 0);
            i.Register(w, "mmioOpenA", CallConv.Stdcall, 3, c => 0);
            i.Register(w, "mmioOpenW", CallConv.Stdcall, 3, c => 0);
        }

        private uint SetMmTimer(uint delay, uint procedure, uint user, uint flags)
        {
            var t = new MmTimer { Id = nextMmTimer++, Delay = Math.Max(1, delay), Procedure = procedure, User = user, Flags = flags, Due = Milliseconds + Math.Max(1, delay) };
            mmTimers[t.Id] = t;
            var fire = false;
            StartService(() =>
            {
                if (t.Killed) return ServiceStep.Exit;
                if (fire)
                {
                    fire = false;
                    if ((t.Flags & 1) == 0) t.Killed = true;   // TIME_ONESHOT
                    if ((t.Flags & 0x30) != 0)
                    {
                        // TIME_CALLBACK_EVENT_SET / _PULSE: the "procedure" is an event.
                        if (waitables.TryGetValue(t.Procedure, out var e) && e is GuestEvent ev) ev.Signaled = true;
                        return ServiceStep.Wait(0);
                    }
                    return ServiceStep.Call(t.Procedure, t.Id, 0, t.User, 0, 0);
                }
                var left = t.Due - Milliseconds;
                if (left > 0) return ServiceStep.Wait((uint)left);
                t.Due += t.Delay;
                fire = true;
                return ServiceStep.Wait(0);
            });
            return t.Id;
        }

        private uint WaveCaps(uint caps, bool wide)
        {
            memory.WriteBytes(caps, new byte[wide ? 84 : 52]);
            memory.Write16(caps, 1);                 // MM_MICROSOFT
            memory.Write16(caps + 2, 2);
            memory.Write32(caps + 4, 0x0100);
            WriteText(caps + 8, "Nativra Audio", wide);
            var after = caps + (wide ? 72u : 40u);
            memory.Write32(after, 0x000FFFFF);       // every WAVE_FORMAT_ rate/width
            memory.Write16(after + 4, 2);            // stereo
            memory.Write32(after + 8, 0x0C);         // WAVECAPS_VOLUME | LRVOLUME
            return 0;
        }

        private uint WaveOpen(GuestCall c)
        {
            // (phwo, device, format, callback, instance, flags)
            const uint FormatQuery = 1;
            var format = c.Arg(2);
            if (format == 0) return 11;   // MMSYSERR_INVALPARAM
            var tag = memory.Read16(format);
            if (tag != 1 && tag != 3 && tag != 0xFFFE) return 32;   // WAVERR_BADFORMAT: PCM, float and extensible only
            if ((c.Arg(5) & FormatQuery) != 0) return 0;
            var d = new WaveOut
            {
                Handle = nextWave,
                Callback = c.Arg(3),
                Instance = c.Arg(4),
                CallbackType = c.Arg(5) & 0x70000,
                BytesPerSecond = (int)Math.Max(1, memory.Read32(format + 8)),
                BlockAlign = Math.Max(1, (int)memory.Read16(format + 12)),
                Cursor = Milliseconds,
            };
            nextWave += 4;
            waveOuts[d.Handle] = d;
            if (c.Arg(0) != 0) memory.Write32(c.Arg(0), d.Handle);
            Notify(d, 0x3BB, 0);   // WOM_OPEN
            StartService(() => WaveService(d));
            return 0;
        }

        /// <summary>The device's own thread: retires finished buffers, delivers notices, sleeps a few milliseconds.</summary>
        private ServiceStep WaveService(WaveOut d)
        {
            Advance(d);
            if (d.Notices.Count > 0)
            {
                var n = d.Notices.Dequeue();
                if (d.CallbackType == 0x30000 && d.Callback != 0)   // CALLBACK_FUNCTION
                    return ServiceStep.Call(d.Callback, d.Handle, n[0], d.Instance, n[1], 0);
                return ServiceStep.Wait(0);
            }
            if (d.Closed) return ServiceStep.Exit;
            return ServiceStep.Wait(5);
        }

        private void Notify(WaveOut d, uint message, uint param)
        {
            switch (d.CallbackType)
            {
                case 0x30000: d.Notices.Enqueue(new[] { message, param }); break;   // function: on the device thread
                case 0x50000:                                                       // CALLBACK_EVENT
                    if (waitables.TryGetValue(d.Callback, out var e) && e is GuestEvent ev) ev.Signaled = true;
                    break;
                case 0x10000: Post(d.Callback, message, d.Handle, param); break;    // CALLBACK_WINDOW
                case 0x20000:                                                       // CALLBACK_THREAD
                    Queue(d.Callback).Enqueue(new QueuedMessage { Message = message, WParam = d.Handle, LParam = param });
                    break;
            }
        }

        private uint WaveWrite(uint handle, uint header)
        {
            if (!waveOuts.TryGetValue(handle, out var d)) return 5;
            var flags = memory.Read32(header + 16);
            if ((flags & 2) == 0) return 34;   // WAVERR_UNPREPARED
            memory.Write32(header + 16, (flags | 0x10) & ~1u);   // WHDR_INQUEUE, not WHDR_DONE
            var length = memory.Read32(header + 4);
            var start = Math.Max(Milliseconds, d.Cursor);
            var end = start + (long)length * 1000 / d.BytesPerSecond;
            d.Cursor = end;
            d.Playing.Enqueue(new KeyValuePair<uint, long>(header, end));
            return 0;
        }

        private void Advance(WaveOut d)
        {
            if (d.PausedAt >= 0) return;
            var now = Milliseconds;
            while (d.Playing.Count > 0 && d.Playing.Peek().Value <= now) FinishBuffer(d, d.Playing.Dequeue().Key);
        }

        private void FinishBuffer(WaveOut d, uint header)
        {
            memory.Write32(header + 16, (memory.Read32(header + 16) & ~0x10u) | 1);   // WHDR_DONE
            d.PlayedBytes += memory.Read32(header + 4);
            Notify(d, 0x3BD, header);   // WOM_DONE
        }

        private uint WaveClose(uint handle)
        {
            if (!waveOuts.TryGetValue(handle, out var d)) return 5;
            Advance(d);
            if (d.Playing.Count > 0) return 33;   // WAVERR_STILLPLAYING
            Notify(d, 0x3BC, 0);                  // WOM_CLOSE
            d.Closed = true;
            waveOuts.Remove(handle);
            return 0;
        }

        private uint WavePosition(uint handle, uint time)
        {
            if (!waveOuts.TryGetValue(handle, out var d)) return 5;
            Advance(d);
            var bytes = d.PlayedBytes;
            if (d.Playing.Count > 0)
            {
                // Part of the buffer now playing.
                var head = d.Playing.Peek();
                var length = memory.Read32(head.Key + 4);
                var duration = (long)length * 1000 / d.BytesPerSecond;
                var elapsed = Math.Max(0, (d.PausedAt >= 0 ? d.PausedAt : Milliseconds) - (head.Value - duration));
                bytes += Math.Min(length, elapsed * d.BytesPerSecond / 1000) / d.BlockAlign * d.BlockAlign;
            }
            switch (memory.Read32(time))
            {
                case 1: memory.Write32(time + 4, (uint)(bytes * 1000 / d.BytesPerSecond)); break;   // TIME_MS
                case 2: memory.Write32(time + 4, (uint)(bytes / d.BlockAlign)); break;               // TIME_SAMPLES
                default: memory.Write32(time, 4); memory.Write32(time + 4, (uint)bytes); break;       // TIME_BYTES
            }
            return 0;
        }

        // --- registry --------------------------------------------------------------------------

        private sealed class RegKey
        {
            public readonly Dictionary<string, KeyValuePair<uint, byte[]>> Values =
                new Dictionary<string, KeyValuePair<uint, byte[]>>(StringComparer.OrdinalIgnoreCase);
        }

        private readonly Dictionary<string, RegKey> registry = new Dictionary<string, RegKey>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<uint, string> regHandles = new Dictionary<uint, string>();
        private uint nextRegHandle = 0x00F00010;
        private bool registryLoaded;

        private string RegistryFile => Folder(ExePath) + "nativra-registry.txt";

        private static string RootName(uint key)
        {
            switch (key)
            {
                case 0x80000000: return "HKCR";
                case 0x80000001: return "HKCU";
                case 0x80000002: return "HKLM";
                case 0x80000003: return "HKU";
                case 0x80000005: return "HKCC";
                default: return null;
            }
        }

        private void EnsureRegistry()
        {
            if (registryLoaded) return;
            registryLoaded = true;
            void Sz(string key, string name, string value) => SetRegValue(key, name, 1, Encoding.Unicode.GetBytes(value + "\0"), false);
            void Dword(string key, string name, uint value) => SetRegValue(key, name, 4, BitConverter.GetBytes(value), false);
            const string Nt = "HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion";
            Sz(Nt, "ProductName", "Windows 10 Pro");
            Sz(Nt, "CurrentVersion", "6.3");
            Sz(Nt, "CurrentBuild", "26100");
            Sz(Nt, "CurrentBuildNumber", "26100");
            Dword(Nt, "CurrentMajorVersionNumber", 10);
            Dword(Nt, "CurrentMinorVersionNumber", 0);
            Sz("HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion", "ProgramFilesDir", "C:\\Program Files");
            Sz("HKLM\\SOFTWARE\\Microsoft\\DirectX", "Version", "4.09.00.0904");
            Dword("HKLM\\SOFTWARE\\Microsoft\\DirectX", "InstalledVersion", 0x00090000);
            Sz("HKLM\\HARDWARE\\DESCRIPTION\\System\\CentralProcessor\\0", "ProcessorNameString", "AMD Custom APU 0405");
            Dword("HKLM\\HARDWARE\\DESCRIPTION\\System\\CentralProcessor\\0", "~MHz", 3800);
            EnsureKey("HKCU\\Software");
            EnsureKey("HKCU\\Control Panel\\Desktop");
            EnsureKey("HKCR");
            EnsureKey("HKU");

            // What the game wrote before, from the game folder.
            try
            {
                using (var stream = Files.Open(FullPath(RegistryFile), FileMode.Open, FileAccess.Read))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string current = null;
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                        {
                            current = line.Substring(1, line.Length - 2);
                            EnsureKey(current);
                            continue;
                        }
                        var eq = line.IndexOf('=');
                        if (current == null || eq < 0) continue;
                        var parts = line.Substring(eq + 1).Split(new[] { ':' }, 2);
                        if (parts.Length != 2 || !uint.TryParse(parts[0], out var type)) continue;
                        var hex = parts[1];
                        var data = new byte[hex.Length / 2];
                        for (var n = 0; n < data.Length; n++) data[n] = Convert.ToByte(hex.Substring(n * 2, 2), 16);
                        SetRegValue(current, Uri.UnescapeDataString(line.Substring(0, eq)), type, data, false);
                    }
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
        }

        private void SaveRegistry()
        {
            var text = new StringBuilder();
            foreach (var pair in registry)
            {
                if (!pair.Key.StartsWith("HKCU", StringComparison.OrdinalIgnoreCase) &&
                    !pair.Key.StartsWith("HKLM\\SOFTWARE", StringComparison.OrdinalIgnoreCase)) continue;
                text.Append('[').Append(pair.Key).Append("]\n");
                foreach (var v in pair.Value.Values)
                    text.Append(Uri.EscapeDataString(v.Key)).Append('=').Append(v.Value.Key).Append(':')
                        .Append(BitConverter.ToString(v.Value.Value).Replace("-", "")).Append('\n');
            }
            try
            {
                using (var stream = Files.Open(FullPath(RegistryFile), FileMode.Create, FileAccess.Write))
                {
                    var bytes = Encoding.UTF8.GetBytes(text.ToString());
                    stream.Write(bytes, 0, bytes.Length);
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
        }

        private RegKey EnsureKey(string path)
        {
            path = path.TrimEnd('\\');
            if (registry.TryGetValue(path, out var key)) return key;
            var slash = path.LastIndexOf('\\');
            if (slash > 0) EnsureKey(path.Substring(0, slash));
            key = new RegKey();
            registry[path] = key;
            return key;
        }

        private void SetRegValue(string path, string name, uint type, byte[] data, bool save)
        {
            EnsureKey(path).Values[name ?? ""] = new KeyValuePair<uint, byte[]>(type, data);
            if (save) SaveRegistry();
        }

        /// <summary>A key handle's path joined with a subkey; null when the handle is not a key.</summary>
        private string RegPath(uint key, uint subKey, bool wide)
        {
            EnsureRegistry();
            var root = RootName(key) ?? (regHandles.TryGetValue(key, out var p) ? p : null);
            if (root == null) return null;
            var sub = subKey != 0 ? ReadText(subKey, wide).Trim('\\') : "";
            return sub.Length == 0 ? root : root + "\\" + sub;
        }

        private uint OpenKeyResult(string path, uint result)
        {
            var handle = nextRegHandle;
            nextRegHandle += 4;
            regHandles[handle] = path;
            memory.Write32(result, handle);
            return 0;
        }

        private void InstallRegistry(GuestImports i)
        {
            const string a = "advapi32.dll";
            foreach (var wide in new[] { false, true })
            {
                var w = wide;
                var s = wide ? "W" : "A";
                i.Register(a, "RegOpenKeyEx" + s, CallConv.Stdcall, 5, c => OpenKey(c.Arg(0), c.Arg(1), c.Arg(4), w, false, 0));
                i.Register(a, "RegOpenKey" + s, CallConv.Stdcall, 3, c => OpenKey(c.Arg(0), c.Arg(1), c.Arg(2), w, false, 0));
                i.Register(a, "RegCreateKeyEx" + s, CallConv.Stdcall, 9, c => OpenKey(c.Arg(0), c.Arg(1), c.Arg(7), w, true, c.Arg(8)));
                i.Register(a, "RegCreateKey" + s, CallConv.Stdcall, 3, c => OpenKey(c.Arg(0), c.Arg(1), c.Arg(2), w, true, 0));
                i.Register(a, "RegQueryValueEx" + s, CallConv.Stdcall, 6, c => QueryValue(RegPath(c.Arg(0), 0, w), c.Arg(1), c.Arg(3), c.Arg(4), c.Arg(5), w));
                i.Register(a, "RegQueryValue" + s, CallConv.Stdcall, 4, c => QueryValue(RegPath(c.Arg(0), c.Arg(1), w), 0, 0, c.Arg(2), c.Arg(3), w));
                i.Register(a, "RegGetValue" + s, CallConv.Stdcall, 7, c => QueryValue(RegPath(c.Arg(0), c.Arg(1), w), c.Arg(2), c.Arg(4), c.Arg(5), c.Arg(6), w));
                i.Register(a, "RegSetValueEx" + s, CallConv.Stdcall, 6, c => SetValue(RegPath(c.Arg(0), 0, w), c.Arg(1), c.Arg(3), c.Arg(4), c.Arg(5), w));
                i.Register(a, "RegSetValue" + s, CallConv.Stdcall, 5, c => SetValue(RegPath(c.Arg(0), c.Arg(1), w), 0, c.Arg(2), c.Arg(3), c.Arg(4), w));
                i.Register(a, "RegDeleteValue" + s, CallConv.Stdcall, 2, c =>
                {
                    var path = RegPath(c.Arg(0), 0, w);
                    if (path == null || !registry.TryGetValue(path, out var key)) return 6;
                    if (!key.Values.Remove(c.Arg(1) != 0 ? ReadText(c.Arg(1), w) : "")) return 2;
                    SaveRegistry();
                    return 0;
                });
                i.Register(a, "RegDeleteKey" + s, CallConv.Stdcall, 2, c => DeleteKey(RegPath(c.Arg(0), c.Arg(1), w)));
                i.Register(a, "RegDeleteKeyEx" + s, CallConv.Stdcall, 4, c => DeleteKey(RegPath(c.Arg(0), c.Arg(1), w)));
                i.Register(a, "RegDeleteTree" + s, CallConv.Stdcall, 2, c => DeleteKey(RegPath(c.Arg(0), c.Arg(1), w)));
                i.Register(a, "RegEnumKeyEx" + s, CallConv.Stdcall, 8, c => EnumKey(c.Arg(0), c.Arg(1), c.Arg(2), memory.Read32(c.Arg(3)), c.Arg(3), w));
                i.Register(a, "RegEnumKey" + s, CallConv.Stdcall, 4, c => EnumKey(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3), 0, w));
                i.Register(a, "RegEnumValue" + s, CallConv.Stdcall, 8, c => EnumValue(c, w));
                i.Register(a, "RegQueryInfoKey" + s, CallConv.Stdcall, 12, c => QueryInfoKey(c, w));
                i.Register(a, "RegConnectRegistry" + s, CallConv.Stdcall, 3, c => 53);   // ERROR_BAD_NETPATH
                i.Register(a, "RegLoadKey" + s, CallConv.Stdcall, 3, c => 5);
            }
            i.Register(a, "RegCloseKey", CallConv.Stdcall, 1, c => regHandles.Remove(c.Arg(0)) || RootName(c.Arg(0)) != null ? 0u : 6u);
            i.Register(a, "RegFlushKey", CallConv.Stdcall, 1, c => 0);
            i.Register(a, "RegOpenCurrentUser", CallConv.Stdcall, 2, c => { memory.Write32(c.Arg(1), 0x80000001); return 0; });
            i.Register(a, "RegNotifyChangeKeyValue", CallConv.Stdcall, 5, c => 0);
            i.Register(a, "RegOverridePredefKey", CallConv.Stdcall, 2, c => 0);
            i.Register(a, "RegDisablePredefinedCache", CallConv.Stdcall, 0, c => 0);
        }

        private uint OpenKey(uint key, uint subKey, uint result, bool wide, bool create, uint disposition)
        {
            var path = RegPath(key, subKey, wide);
            if (path == null) return 6;   // ERROR_INVALID_HANDLE
            var exists = registry.ContainsKey(path);
            if (!exists && !create) return 2;   // ERROR_FILE_NOT_FOUND
            if (!exists) { EnsureKey(path); SaveRegistry(); }
            if (disposition != 0) memory.Write32(disposition, exists ? 2u : 1u);   // REG_OPENED_EXISTING_KEY / REG_CREATED_NEW_KEY
            return OpenKeyResult(path, result);
        }

        private static bool IsText(uint type) => type == 1 || type == 2 || type == 7;

        private uint QueryValue(string path, uint name, uint typeOut, uint data, uint sizeOut, bool wide)
        {
            if (path == null) return 6;
            if (!registry.TryGetValue(path, out var key)) return 2;
            var valueName = name != 0 ? ReadText(name, wide) : "";
            if (!key.Values.TryGetValue(valueName, out var value)) return 2;
            var bytes = value.Value;
            if (!wide && IsText(value.Key)) bytes = Ansi.Encode(Encoding.Unicode.GetString(bytes));
            if (typeOut != 0) memory.Write32(typeOut, value.Key);
            if (sizeOut == 0) return data == 0 ? 0u : 87u;
            var room = memory.Read32(sizeOut);
            memory.Write32(sizeOut, (uint)bytes.Length);
            if (data == 0) return 0;
            if (room < bytes.Length) return 234;   // ERROR_MORE_DATA
            memory.WriteBytes(data, bytes);
            return 0;
        }

        private uint SetValue(string path, uint name, uint type, uint data, uint size, bool wide)
        {
            if (path == null) return 6;
            var bytes = memory.ReadBytes(data, (int)size);
            if (!wide && IsText(type)) bytes = Encoding.Unicode.GetBytes(Ansi.Decode(bytes));
            if (IsText(type) && (bytes.Length < 2 || bytes[bytes.Length - 1] != 0 || bytes[bytes.Length - 2] != 0))
            {
                var terminated = new byte[bytes.Length + 2];
                Array.Copy(bytes, terminated, bytes.Length);
                bytes = terminated;
            }
            SetRegValue(path, name != 0 ? ReadText(name, wide) : "", type, bytes, true);
            return 0;
        }

        private uint DeleteKey(string path)
        {
            if (path == null) return 6;
            if (!registry.ContainsKey(path)) return 2;
            foreach (var k in new List<string>(registry.Keys))
                if (k.Equals(path, StringComparison.OrdinalIgnoreCase) || k.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase)) registry.Remove(k);
            SaveRegistry();
            return 0;
        }

        private List<string> SubKeys(string path)
        {
            var list = new List<string>();
            foreach (var k in registry.Keys)
                if (k.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase) && k.IndexOf('\\', path.Length + 1) < 0)
                    list.Add(k.Substring(path.Length + 1));
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        private uint EnumKey(uint key, uint index, uint name, uint size, uint sizeOut, bool wide)
        {
            var path = RegPath(key, 0, wide);
            if (path == null) return 6;
            var subs = SubKeys(path);
            if (index >= subs.Count) return 259;   // ERROR_NO_MORE_ITEMS
            var text = subs[(int)index];
            if (size < text.Length + 1) return 234;
            WriteText(name, text, wide);
            if (sizeOut != 0) memory.Write32(sizeOut, (uint)text.Length);
            return 0;
        }

        private uint EnumValue(GuestCall c, bool wide)
        {
            // (key, index, name, nameSize*, reserved, type*, data, dataSize*)
            var path = RegPath(c.Arg(0), 0, wide);
            if (path == null) return 6;
            if (!registry.TryGetValue(path, out var key) || c.Arg(1) >= key.Values.Count) return 259;
            var n = 0;
            foreach (var pair in key.Values)
            {
                if (n++ != c.Arg(1)) continue;
                var room = memory.Read32(c.Arg(3));
                if (room < pair.Key.Length + 1) return 234;
                WriteText(c.Arg(2), pair.Key, wide);
                memory.Write32(c.Arg(3), (uint)pair.Key.Length);
                var name = heap.Alloc((uint)(pair.Key.Length + 1) * 2);
                WriteText(name, pair.Key, true);
                var result = QueryValue(path, name, c.Arg(5), c.Arg(6), c.Arg(7), wide);
                heap.Free(name);
                return result;
            }
            return 259;
        }

        private uint QueryInfoKey(GuestCall c, bool wide)
        {
            var path = RegPath(c.Arg(0), 0, wide);
            if (path == null || !registry.TryGetValue(path, out var key)) return 6;
            var subs = SubKeys(path);
            var maxSub = 0; foreach (var s in subs) maxSub = Math.Max(maxSub, s.Length);
            int maxName = 0, maxData = 0;
            foreach (var v in key.Values) { maxName = Math.Max(maxName, v.Key.Length); maxData = Math.Max(maxData, v.Value.Value.Length); }
            if (c.Arg(2) != 0) memory.Write32(c.Arg(2), 0);
            if (c.Arg(4) != 0) memory.Write32(c.Arg(4), (uint)subs.Count);
            if (c.Arg(5) != 0) memory.Write32(c.Arg(5), (uint)maxSub);
            if (c.Arg(6) != 0) memory.Write32(c.Arg(6), 0);
            if (c.Arg(7) != 0) memory.Write32(c.Arg(7), (uint)key.Values.Count);
            if (c.Arg(8) != 0) memory.Write32(c.Arg(8), (uint)maxName);
            if (c.Arg(9) != 0) memory.Write32(c.Arg(9), (uint)maxData);
            if (c.Arg(10) != 0) memory.Write32(c.Arg(10), 0);
            if (c.Arg(11) != 0) memory.Write64(c.Arg(11), (ulong)UtcNow.ToFileTimeUtc());
            return 0;
        }

        // --- advapi32: users, tokens, crypto -----------------------------------------------------

        private readonly Dictionary<uint, HashAlgorithm> hashes = new Dictionary<uint, HashAlgorithm>();
        private readonly Dictionary<uint, MemoryStream> hashInput = new Dictionary<uint, MemoryStream>();
        private readonly RandomNumberGenerator random = RandomNumberGenerator.Create();

        private void FillRandom(uint buffer, uint length)
        {
            var bytes = new byte[length];
            random.GetBytes(bytes);
            memory.WriteBytes(buffer, bytes);
        }

        private void InstallAdvapi(GuestImports i)
        {
            const string a = "advapi32.dll";
            i.Register(a, "GetUserNameA", CallConv.Stdcall, 2, c => NameInto("Player", c.Arg(0), c.Arg(1), false) != 0 ? CountWithNul(c.Arg(1)) : 0u);
            i.Register(a, "GetUserNameW", CallConv.Stdcall, 2, c => NameInto("Player", c.Arg(0), c.Arg(1), true) != 0 ? CountWithNul(c.Arg(1)) : 0u);
            i.Register(a, "SystemFunction036", CallConv.Stdcall, 2, c => { FillRandom(c.Arg(0), c.Arg(1)); return 1; });   // RtlGenRandom
            i.Register(a, "CryptAcquireContextA", CallConv.Stdcall, 5, c => { memory.Write32(c.Arg(0), 0x00C0FFEE); return 1; });
            i.Register(a, "CryptAcquireContextW", CallConv.Stdcall, 5, c => { memory.Write32(c.Arg(0), 0x00C0FFEE); return 1; });
            i.Register(a, "CryptReleaseContext", CallConv.Stdcall, 2, c => 1);
            i.Register(a, "CryptGenRandom", CallConv.Stdcall, 3, c => { FillRandom(c.Arg(2), c.Arg(1)); return 1; });
            i.Register(a, "CryptCreateHash", CallConv.Stdcall, 5, c =>
            {
                HashAlgorithm h;
                switch (c.Arg(1))
                {
                    case 0x8003: h = MD5.Create(); break;
                    case 0x8004: h = SHA1.Create(); break;
                    case 0x800C: h = SHA256.Create(); break;
                    case 0x800D: h = SHA384.Create(); break;
                    case 0x800E: h = SHA512.Create(); break;
                    default: process.LastError = 0x80090008; return 0;   // NTE_BAD_ALGID
                }
                var handle = NewHandle();
                hashes[handle] = h;
                hashInput[handle] = new MemoryStream();
                memory.Write32(c.Arg(4), handle);
                return 1;
            });
            i.Register(a, "CryptHashData", CallConv.Stdcall, 4, c =>
            {
                if (!hashInput.TryGetValue(c.Arg(0), out var input)) return 0;
                var bytes = memory.ReadBytes(c.Arg(1), (int)c.Arg(2));
                input.Write(bytes, 0, bytes.Length);
                return 1;
            });
            i.Register(a, "CryptGetHashParam", CallConv.Stdcall, 5, c =>
            {
                if (!hashes.TryGetValue(c.Arg(0), out var h)) return 0;
                var digest = h.ComputeHash(hashInput[c.Arg(0)].ToArray());
                byte[] answer = c.Arg(1) == 4 ? BitConverter.GetBytes(digest.Length) : c.Arg(1) == 2 ? digest : null;   // HP_HASHSIZE / HP_HASHVAL
                if (answer == null) return 0;
                if (c.Arg(2) == 0) { memory.Write32(c.Arg(3), (uint)answer.Length); return 1; }
                if (memory.Read32(c.Arg(3)) < answer.Length) { memory.Write32(c.Arg(3), (uint)answer.Length); process.LastError = 234; return 0; }
                memory.WriteBytes(c.Arg(2), answer);
                memory.Write32(c.Arg(3), (uint)answer.Length);
                return 1;
            });
            i.Register(a, "CryptDestroyHash", CallConv.Stdcall, 1, c => { hashes.Remove(c.Arg(0)); hashInput.Remove(c.Arg(0)); return 1; });
            i.Register(a, "OpenProcessToken", CallConv.Stdcall, 3, c => { memory.Write32(c.Arg(2), 0x00C0FFE0); return 1; });
            i.Register(a, "OpenThreadToken", CallConv.Stdcall, 4, c => { process.LastError = 1008; return 0; });   // ERROR_NO_TOKEN
            i.Register(a, "GetTokenInformation", CallConv.Stdcall, 5, c =>
            {
                if (c.Arg(1) == 20 && c.Arg(3) >= 4) { memory.Write32(c.Arg(2), 0); memory.Write32(c.Arg(4), 4); return 1; }   // TokenElevation: not elevated
                process.LastError = ErrorNotSupported;
                return 0;
            });
            i.Register(a, "AllocateAndInitializeSid", CallConv.Stdcall, 11, c => { memory.Write32(c.Arg(10), heap.Alloc(16, zero: true)); return 1; });
            i.Register(a, "FreeSid", CallConv.Stdcall, 1, c => { heap.Free(c.Arg(0)); return 0; });
            i.Register(a, "CheckTokenMembership", CallConv.Stdcall, 3, c => { memory.Write32(c.Arg(2), 0); return 1; });
            i.Register(a, "EqualSid", CallConv.Stdcall, 2, c => 0);
            i.Register(a, "IsValidSid", CallConv.Stdcall, 1, c => 1);
            i.Register(a, "LookupPrivilegeValueA", CallConv.Stdcall, 3, c => 1);
            i.Register(a, "LookupPrivilegeValueW", CallConv.Stdcall, 3, c => 1);
            i.Register(a, "AdjustTokenPrivileges", CallConv.Stdcall, 6, c => 1);
            i.Register(a, "InitializeSecurityDescriptor", CallConv.Stdcall, 2, c => 1);
            i.Register(a, "SetSecurityDescriptorDacl", CallConv.Stdcall, 4, c => 1);
            i.Register(a, "RegisterEventSourceA", CallConv.Stdcall, 2, c => 0x00C0FFE4);
            i.Register(a, "RegisterEventSourceW", CallConv.Stdcall, 2, c => 0x00C0FFE4);
            i.Register(a, "ReportEventA", CallConv.Stdcall, 9, c => 1);
            i.Register(a, "ReportEventW", CallConv.Stdcall, 9, c => 1);
            i.Register(a, "DeregisterEventSource", CallConv.Stdcall, 1, c => 1);
            i.Register(a, "OpenSCManagerA", CallConv.Stdcall, 3, c => { process.LastError = ErrorAccessDenied; return 0; });
            i.Register(a, "OpenSCManagerW", CallConv.Stdcall, 3, c => { process.LastError = ErrorAccessDenied; return 0; });
            i.Register(a, "EventRegister", CallConv.Stdcall, 4, c => { memory.Write64(c.Arg(3), 1); return 0; });
            i.Register(a, "EventUnregister", CallConv.Stdcall, 2, c => 0);
            i.Register(a, "EventWrite", CallConv.Stdcall, 5, c => 0);
            i.Register(a, "EventEnabled", CallConv.Stdcall, 3, c => 0);
            i.Register(a, "EventSetInformation", CallConv.Stdcall, 5, c => 0);
        }

        private uint CountWithNul(uint sizePtr)
        {
            memory.Write32(sizePtr, memory.Read32(sizePtr) + 1);   // GetUserName counts the NUL, unlike GetComputerName
            return 1;
        }

        // --- shell32 -------------------------------------------------------------------------------

        private static string CsidlFolder(uint csidl)
        {
            switch (csidl & 0xFF)
            {
                case 0x05: case 0x0C: return "Documents";
                case 0x1A: return "AppData";
                case 0x1C: return "LocalAppData";
                case 0x23: return "ProgramData";
                case 0x00: case 0x10: return "Desktop";
                case 0x0D: return "Music";
                case 0x27: return "Pictures";
                case 0x0E: return "Videos";
                case 0x28: return "";
                case 0x2E: return "PublicDocuments";
                default: return null;
            }
        }

        private static readonly Dictionary<Guid, string> KnownFolderIds = new Dictionary<Guid, string>
        {
            [new Guid("FDD39AD0-238F-46AF-ADB4-6C85480369C7")] = "Documents",
            [new Guid("4C5C32FF-BB9D-43B0-B5B4-2D72E54EAAA4")] = "SavedGames",
            [new Guid("F1B32785-6FBA-4FCF-9D55-7B8E7F157091")] = "LocalAppData",
            [new Guid("3EB685DB-65F9-4CF6-A03A-E3EF65729F3D")] = "AppData",
            [new Guid("62AB5D82-FDC1-4DC3-A9DD-070D1D495D97")] = "ProgramData",
            [new Guid("B4BFCC3A-DB2C-424C-B029-7FE99A87C641")] = "Desktop",
            [new Guid("A520A1A4-1780-4FF6-BD18-167343C5AF16")] = "LocalAppDataLow",
            [new Guid("5E6C858F-0E22-4760-9AFE-EA3317B67173")] = "",
        };

        /// <summary>The known folder's guest path, created (with its parents) so the game can write there.</summary>
        private string EnsureKnownFolder(string name)
        {
            var path = KnownFolderPath(name);
            CreateTree(path);
            return path;
        }

        /// <summary>Creates a folder and its missing parents; the guard answers for the ones above what the guest can reach.</summary>
        private bool CreateTree(string path)
        {
            path = FullPath(path).TrimEnd('\\');
            var at = path.IndexOf('\\', 3);
            while (at > 0)
            {
                var part = path.Substring(0, at);
                if (Files.Stat(part) == null) Files.CreateDirectory(part);
                at = path.IndexOf('\\', at + 1);
            }
            return Files.Stat(path) != null || Files.CreateDirectory(path);
        }

        private void InstallShell32(GuestImports i)
        {
            const string s = "shell32.dll";
            foreach (var wide in new[] { false, true })
            {
                var w = wide;
                var x = wide ? "W" : "A";
                i.Register(s, "SHGetFolderPath" + x, CallConv.Stdcall, 5, c =>
                {
                    var name = CsidlFolder(c.Arg(1));
                    if (name == null) return 0x80070002;   // E_FAIL-ish: HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND)
                    WriteText(c.Arg(4), EnsureKnownFolder(name), w);
                    return 0;
                });
                i.Register(s, "SHGetSpecialFolderPath" + x, CallConv.Stdcall, 4, c =>
                {
                    var name = CsidlFolder(c.Arg(2));
                    if (name == null) return 0;
                    WriteText(c.Arg(1), EnsureKnownFolder(name), w);
                    return 1;
                });
                i.Register(s, "SHCreateDirectoryEx" + x, CallConv.Stdcall, 3, c =>
                {
                    var path = FullPath(ReadText(c.Arg(1), w)).TrimEnd('\\');
                    if (Files.Stat(path) != null) return 183;   // ERROR_ALREADY_EXISTS
                    return CreateTree(path) ? 0u : 3u;          // ERROR_PATH_NOT_FOUND
                });
                i.Register(s, "ShellExecute" + x, CallConv.Stdcall, 6, c => { Say("x86: ShellExecute " + ReadText(c.Arg(2), w)); return 42; });
                i.Register(s, "ShellExecuteEx" + x, CallConv.Stdcall, 1, c => { memory.Write32(c.Arg(0) + 32, 42); return 1; });
                i.Register(s, "SHFileOperation" + x, CallConv.Stdcall, 1, c => 0);
                i.Register(s, "DragQueryFile" + x, CallConv.Stdcall, 4, c => 0);
                i.Register(s, "ExtractIcon" + x, CallConv.Stdcall, 3, c => 0);
                i.Register(s, "SHGetFileInfo" + x, CallConv.Stdcall, 5, c => 0);
                i.Register(s, "SHBrowseForFolder" + x, CallConv.Stdcall, 1, c => 0);
                i.Register(s, "SHGetPathFromIDList" + x, CallConv.Stdcall, 2, c => 0);
                i.Register(s, "Shell_NotifyIcon" + x, CallConv.Stdcall, 2, c => 1);
            }
            i.Register(s, "SHGetKnownFolderPath", CallConv.Stdcall, 4, c =>
            {
                var id = new Guid(memory.ReadBytes(c.Arg(0), 16));
                if (!KnownFolderIds.TryGetValue(id, out var name)) { memory.Write32(c.Arg(3), 0); return 0x80070002; }
                var path = EnsureKnownFolder(name);
                var text = heap.Alloc((uint)(path.Length + 1) * 2);
                WriteText(text, path, true);
                memory.Write32(c.Arg(3), text);
                return 0;
            });
            i.Register(s, "SHGetFolderLocation", CallConv.Stdcall, 5, c => 0x80004005);
            i.Register(s, "SHGetMalloc", CallConv.Stdcall, 1, c => 0x80004001);   // E_NOTIMPL
            i.Register(s, "DragAcceptFiles", CallConv.Stdcall, 2, c => 0);
            i.Register(s, "DragFinish", CallConv.Stdcall, 1, c => 0);
            i.Register(s, "SHAppBarMessage", CallConv.Stdcall, 2, c => 0);
            i.Register(s, "IsUserAnAdmin", CallConv.Stdcall, 0, c => 0);
            i.Register(s, "CommandLineToArgvW", CallConv.Stdcall, 2, c =>
            {
                var args = SplitCommandLine(ReadText(c.Arg(0), true));
                if (c.Arg(0) != 0 && memory.Read16(c.Arg(0)) == 0) args = new List<string> { ExePath };
                uint size = (uint)(args.Count + 1) * 4;
                foreach (var a in args) size += (uint)(a.Length + 1) * 2;
                var block = heap.Alloc(size, zero: true);
                var text = block + (uint)(args.Count + 1) * 4;
                for (var n = 0; n < args.Count; n++)
                {
                    memory.Write32(block + (uint)n * 4, text);
                    WriteText(text, args[n], true);
                    text += (uint)(args[n].Length + 1) * 2;
                }
                memory.Write32(c.Arg(1), (uint)args.Count);
                return block;
            });
        }

        // --- version -------------------------------------------------------------------------------

        /// <summary>The VS_VERSIONINFO block of a file: a mapped image's own resource, or one made up for a system DLL.</summary>
        private byte[] VersionBlock(string path)
        {
            var name = Trim(path);
            foreach (var image in process.Images)
            {
                if (!string.Equals(image.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                var entry = FindResource(image.BaseAddress, 16, 1, true);   // RT_VERSION, id 1
                if (entry == 0) return null;
                return memory.ReadBytes(image.BaseAddress + memory.Read32(entry), (int)memory.Read32(entry + 4));
            }
            if (Files.Stat(FullPath(path)) != null) return null;   // a game file with no version we can read
            // A system DLL: version 10.0.26100.1.
            var block = new byte[92];
            void W16(int at, int v) { block[at] = (byte)v; block[at + 1] = (byte)(v >> 8); }
            void W32(int at, uint v) { for (var k = 0; k < 4; k++) block[at + k] = (byte)(v >> (8 * k)); }
            W16(0, 92); W16(2, 52); W16(4, 0);
            var key = Encoding.Unicode.GetBytes("VS_VERSION_INFO\0");
            Array.Copy(key, 0, block, 6, key.Length);
            W32(40, 0xFEEF04BD); W32(44, 0x00010000);
            W32(48, 0x000A0000); W32(52, 0x65F40001);   // file 10.0.26100.1
            W32(56, 0x000A0000); W32(60, 0x65F40001);
            W32(64, 0x3F); W32(72, 0x00040004); W32(76, name.EndsWith(".exe", StringComparison.Ordinal) ? 1u : 2u);
            return block;
        }

        private void InstallVersion(GuestImports i)
        {
            const string v = "version.dll";
            foreach (var wide in new[] { false, true })
            {
                var w = wide;
                var x = wide ? "W" : "A";
                i.Register(v, "GetFileVersionInfoSize" + x, CallConv.Stdcall, 2, c =>
                {
                    if (c.Arg(1) != 0) memory.Write32(c.Arg(1), 0);
                    var block = VersionBlock(ReadText(c.Arg(0), w));
                    if (block == null) { process.LastError = ErrorResourceTypeNotFound; return 0; }
                    return (uint)block.Length * 2;   // room for VerQueryValueA's converted strings
                });
                i.Register(v, "GetFileVersionInfoSizeEx" + x, CallConv.Stdcall, 3, c =>
                {
                    var block = VersionBlock(ReadText(c.Arg(1), w));
                    if (block == null) { process.LastError = ErrorResourceTypeNotFound; return 0; }
                    return (uint)block.Length * 2;
                });
                i.Register(v, "GetFileVersionInfo" + x, CallConv.Stdcall, 4, c => CopyVersion(ReadText(c.Arg(0), w), c.Arg(2), c.Arg(3)));
                i.Register(v, "GetFileVersionInfoEx" + x, CallConv.Stdcall, 5, c => CopyVersion(ReadText(c.Arg(1), w), c.Arg(3), c.Arg(4)));
                i.Register(v, "VerQueryValue" + x, CallConv.Stdcall, 4, c => VerQueryValue(c.Arg(0), ReadText(c.Arg(1), w), c.Arg(2), c.Arg(3), w));
            }
        }

        private uint CopyVersion(string path, uint size, uint data)
        {
            var block = VersionBlock(path);
            if (block == null) { process.LastError = ErrorResourceTypeNotFound; return 0; }
            memory.WriteBytes(data, block, 0, (int)Math.Min(size, (uint)block.Length));
            return 1;
        }

        /// <summary>Walks the version block's tree ("\", "\VarFileInfo\Translation", "\StringFileInfo\040904b0\ProductVersion").</summary>
        private uint VerQueryValue(uint block, string subBlock, uint bufferOut, uint lengthOut, bool wide)
        {
            var node = block;
            foreach (var part in subBlock.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var found = 0u;
                var end = node + memory.Read16(node);
                for (var child = ChildrenOf(node); child + 6 < end;)
                {
                    var length = memory.Read16(child);
                    if (length == 0) break;
                    if (string.Equals(memory.ReadUnicode(child + 6, 64), part, StringComparison.OrdinalIgnoreCase)) { found = child; break; }
                    child = (child + length + 3) & ~3u;
                }
                if (found == 0) return 0;
                node = found;
            }
            var key = memory.ReadUnicode(node + 6, 64);
            var value = (node + 6 + (uint)(key.Length + 1) * 2 + 3) & ~3u;
            var valueLength = memory.Read16(node + 2);
            var isText = memory.Read16(node + 4) == 1;
            if (isText && !wide)
            {
                var text = memory.ReadUnicode(value, valueLength);
                var copy = heap.Alloc((uint)text.Length + 1);
                WriteText(copy, text, false);
                value = copy;
            }
            memory.Write32(bufferOut, value);
            if (lengthOut != 0) memory.Write32(lengthOut, valueLength);
            return valueLength > 0 || subBlock == "\\" ? 1u : 0u;
        }

        private uint ChildrenOf(uint node)
        {
            var key = memory.ReadUnicode(node + 6, 64);
            var value = (node + 6 + (uint)(key.Length + 1) * 2 + 3) & ~3u;
            var valueBytes = memory.Read16(node + 2) * (memory.Read16(node + 4) == 1 ? 2u : 1u);
            return (value + valueBytes + 3) & ~3u;
        }

        // --- oleaut32 ------------------------------------------------------------------------------

        private uint AllocBstr(uint source, uint bytes)
        {
            var block = heap.Alloc(bytes + 6);
            if (block == 0) return 0;
            memory.Write32(block, bytes);
            if (source != 0) MoveMemory(block + 4, source, bytes); else FillMemory(block + 4, bytes, 0);
            memory.Write16(block + 4 + bytes, 0);
            if ((bytes & 1) != 0) memory.Write8(block + 5 + bytes, 0);
            return block + 4;
        }

        private void FreeBstr(uint bstr) { if (bstr != 0) heap.Free(bstr - 4); }

        private void InstallOleAut(GuestImports i)
        {
            const string o = "oleaut32.dll";
            void Both(string name, int ordinal, int args, HostCall body)
            {
                i.Register(o, name, CallConv.Stdcall, args, body);
                i.RegisterOrdinal(o, ordinal, CallConv.Stdcall, args, body);
            }
            Both("SysAllocString", 2, 1, c => c.Arg(0) == 0 ? 0 : AllocBstr(c.Arg(0), WcsLen(c.Arg(0)) * 2));
            Both("SysReAllocString", 3, 2, c =>
            {
                var fresh = AllocBstr(c.Arg(1), c.Arg(1) == 0 ? 0 : WcsLen(c.Arg(1)) * 2);
                FreeBstr(memory.Read32(c.Arg(0)));
                memory.Write32(c.Arg(0), fresh);
                return 1;
            });
            Both("SysAllocStringLen", 4, 2, c => AllocBstr(c.Arg(0), c.Arg(1) * 2));
            Both("SysReAllocStringLen", 5, 3, c =>
            {
                var fresh = AllocBstr(c.Arg(1), c.Arg(2) * 2);
                FreeBstr(memory.Read32(c.Arg(0)));
                memory.Write32(c.Arg(0), fresh);
                return 1;
            });
            Both("SysFreeString", 6, 1, c => { FreeBstr(c.Arg(0)); return 0; });
            Both("SysStringLen", 7, 1, c => c.Arg(0) == 0 ? 0 : memory.Read32(c.Arg(0) - 4) / 2);
            Both("SysStringByteLen", 149, 1, c => c.Arg(0) == 0 ? 0 : memory.Read32(c.Arg(0) - 4));
            Both("SysAllocStringByteLen", 150, 2, c => AllocBstr(c.Arg(0), c.Arg(1)));
            Both("VariantInit", 8, 1, c => { FillMemory(c.Arg(0), 16, 0); return 0; });
            Both("VariantClear", 9, 1, c =>
            {
                if (memory.Read16(c.Arg(0)) == 8) FreeBstr(memory.Read32(c.Arg(0) + 8));   // VT_BSTR
                FillMemory(c.Arg(0), 16, 0);
                return 0;
            });
            Both("VariantCopy", 10, 2, c =>
            {
                MoveMemory(c.Arg(0), c.Arg(1), 16);
                if (memory.Read16(c.Arg(1)) == 8)
                {
                    var source = memory.Read32(c.Arg(1) + 8);
                    memory.Write32(c.Arg(0) + 8, source == 0 ? 0 : AllocBstr(source, memory.Read32(source - 4)));
                }
                return 0;
            });
            Both("VariantChangeType", 12, 4, c =>
            {
                if (memory.Read16(c.Arg(1)) != (ushort)c.Arg(3)) return 0x80020005;   // DISP_E_TYPEMISMATCH
                MoveMemory(c.Arg(0), c.Arg(1), 16);
                return 0;
            });
        }

        // --- comctl32, comdlg32, gdiplus -------------------------------------------------------------

        private void InstallSmallDlls(GuestImports i)
        {
            i.Register("gdi32.dll", "TranslateCharsetInfo", CallConv.Stdcall, 3, c =>
            {
                // Everything is the Western (1252) character set here.
                memory.WriteBytes(c.Arg(1), new byte[32]);
                memory.Write32(c.Arg(1), 0);          // ANSI_CHARSET
                memory.Write32(c.Arg(1) + 4, 1252);
                memory.Write32(c.Arg(1) + 24, 1);     // FS_LATIN1
                return 1;
            });
            i.Register("user32.dll", "GetRawInputDeviceInfoA", CallConv.Stdcall, 4, c => 0xFFFFFFFF);
            i.Register("user32.dll", "GetRawInputDeviceInfoW", CallConv.Stdcall, 4, c => 0xFFFFFFFF);
            i.Register("comctl32.dll", "InitCommonControls", CallConv.Stdcall, 0, c => 0);
            i.RegisterOrdinal("comctl32.dll", 17, CallConv.Stdcall, 0, c => 0);
            i.Register("comctl32.dll", "InitCommonControlsEx", CallConv.Stdcall, 1, c => 1);
            i.Register("comctl32.dll", "_TrackMouseEvent", CallConv.Stdcall, 1, c => 1);
            foreach (var name in new[] { "GetOpenFileNameA", "GetOpenFileNameW", "GetSaveFileNameA", "GetSaveFileNameW", "ChooseColorA", "ChooseColorW", "ChooseFontA", "ChooseFontW", "PrintDlgA", "PrintDlgW" })
                i.Register("comdlg32.dll", name, CallConv.Stdcall, 1, c => 0);   // cancelled
            i.Register("comdlg32.dll", "CommDlgExtendedError", CallConv.Stdcall, 0, c => 0);
            i.Register("gdiplus.dll", "GdiplusStartup", CallConv.Stdcall, 3, c => { memory.Write32(c.Arg(0), 1); if (c.Arg(2) != 0) memory.WriteBytes(c.Arg(2), new byte[8]); return 0; });
            i.Register("gdiplus.dll", "GdiplusShutdown", CallConv.Stdcall, 1, c => 0);
            i.Register("gdiplus.dll", "GdipAlloc", CallConv.Stdcall, 1, c => heap.Alloc(c.Arg(0)));
            i.Register("gdiplus.dll", "GdipFree", CallConv.Stdcall, 1, c => { heap.Free(c.Arg(0)); return 0; });
        }

        // --- the network, absent ------------------------------------------------------------------------

        private uint socketError;

        private void InstallNetwork(GuestImports i)
        {
            const string ws = "ws2_32.dll";
            const uint NetDown = 10050, HostNotFound = 11001, NotInitialised = 10093;
            var started = false;
            void Both(string name, int ordinal, int args, HostCall body)
            {
                i.Register(ws, name, CallConv.Stdcall, args, body);
                i.RegisterOrdinal(ws, ordinal, CallConv.Stdcall, args, body);
                i.Register("wsock32.dll", name, CallConv.Stdcall, args, body);
                i.RegisterOrdinal("wsock32.dll", ordinal, CallConv.Stdcall, args, body);
            }
            uint Fail(uint error) { socketError = started ? error : NotInitialised; return 0xFFFFFFFF; }

            Both("WSAStartup", 115, 2, c =>
            {
                started = true;
                var p = c.Arg(1);
                memory.WriteBytes(p, new byte[400]);
                memory.Write16(p, (ushort)Math.Min(c.Arg(0) & 0xFFFF, 0x0202));
                memory.Write16(p + 2, 0x0202);
                WriteText(p + 4, "WinSock 2.0", false);
                WriteText(p + 261, "Running", false);
                return 0;
            });
            Both("WSACleanup", 116, 0, c => { started = false; return 0; });
            Both("WSAGetLastError", 111, 0, c => socketError);
            Both("WSASetLastError", 112, 1, c => { socketError = c.Arg(0); return 0; });
            Both("socket", 23, 3, c => Fail(NetDown));
            Both("closesocket", 3, 1, c => 0);
            Both("connect", 4, 3, c => Fail(NetDown));
            Both("bind", 2, 3, c => Fail(NetDown));
            Both("listen", 13, 2, c => Fail(NetDown));
            Both("accept", 1, 3, c => Fail(NetDown));
            Both("send", 19, 4, c => Fail(NetDown));
            Both("recv", 16, 4, c => Fail(NetDown));
            Both("sendto", 20, 6, c => Fail(NetDown));
            Both("recvfrom", 17, 6, c => Fail(NetDown));
            Both("shutdown", 22, 2, c => Fail(NetDown));
            Both("select", 18, 5, c => Fail(NetDown));
            Both("ioctlsocket", 10, 3, c => Fail(NetDown));
            Both("setsockopt", 21, 5, c => Fail(NetDown));
            Both("getsockopt", 7, 5, c => Fail(NetDown));
            Both("getsockname", 6, 3, c => Fail(NetDown));
            Both("getpeername", 5, 3, c => Fail(NetDown));
            Both("gethostbyname", 52, 1, c => { socketError = HostNotFound; return 0; });
            Both("gethostbyaddr", 51, 3, c => { socketError = HostNotFound; return 0; });
            Both("getprotobyname", 53, 1, c => 0);
            Both("getservbyname", 55, 2, c => 0);
            Both("gethostname", 57, 2, c => { CopyTruncated("xbox", c.Arg(0), c.Arg(1), false); return 0; });
            Both("htons", 9, 1, c => (uint)(ushort)((c.Arg(0) >> 8) | (c.Arg(0) << 8)));
            Both("ntohs", 15, 1, c => (uint)(ushort)((c.Arg(0) >> 8) | (c.Arg(0) << 8)));
            Both("htonl", 8, 1, c => ((c.Arg(0) & 0xFF) << 24) | ((c.Arg(0) & 0xFF00) << 8) | ((c.Arg(0) >> 8) & 0xFF00) | (c.Arg(0) >> 24));
            Both("ntohl", 14, 1, c => ((c.Arg(0) & 0xFF) << 24) | ((c.Arg(0) & 0xFF00) << 8) | ((c.Arg(0) >> 8) & 0xFF00) | (c.Arg(0) >> 24));
            Both("inet_addr", 11, 1, c =>
            {
                var parts = (CString(c.Arg(0)) ?? "").Split('.');
                if (parts.Length != 4) return 0xFFFFFFFF;
                uint value = 0;
                for (var n = 0; n < 4; n++) { if (!byte.TryParse(parts[n], out var b)) return 0xFFFFFFFF; value |= (uint)b << (8 * n); }
                return value;
            });
            Both("inet_ntoa", 12, 1, c =>
            {
                var a = c.Arg(0);
                WriteText(crtScratch, $"{a & 0xFF}.{(a >> 8) & 0xFF}.{(a >> 16) & 0xFF}.{a >> 24}", false);
                return crtScratch;
            });
            Both("__WSAFDIsSet", 151, 2, c => 0);
            i.Register(ws, "WSASocketA", CallConv.Stdcall, 6, c => Fail(NetDown));
            i.Register(ws, "WSASocketW", CallConv.Stdcall, 6, c => Fail(NetDown));
            i.Register(ws, "WSAIoctl", CallConv.Stdcall, 9, c => Fail(NetDown));
            i.Register(ws, "WSASend", CallConv.Stdcall, 7, c => Fail(NetDown));
            i.Register(ws, "WSARecv", CallConv.Stdcall, 7, c => Fail(NetDown));
            i.Register(ws, "WSAEventSelect", CallConv.Stdcall, 3, c => Fail(NetDown));
            i.Register(ws, "WSAAsyncSelect", CallConv.Stdcall, 4, c => Fail(NetDown));
            i.Register(ws, "WSACreateEvent", CallConv.Stdcall, 0, c => CreateEvent(true, false));
            i.Register(ws, "WSACloseEvent", CallConv.Stdcall, 1, c => CloseHandle(c.Arg(0)));
            i.Register(ws, "WSAResetEvent", CallConv.Stdcall, 1, c => { if (waitables.TryGetValue(c.Arg(0), out var e) && e is GuestEvent ev) ev.Signaled = false; return 1; });
            i.Register(ws, "getaddrinfo", CallConv.Stdcall, 4, c => { memory.Write32(c.Arg(3), 0); return HostNotFound; });
            i.Register(ws, "GetAddrInfoW", CallConv.Stdcall, 4, c => { memory.Write32(c.Arg(3), 0); return HostNotFound; });
            i.Register(ws, "freeaddrinfo", CallConv.Stdcall, 1, c => 0);
            i.Register(ws, "FreeAddrInfoW", CallConv.Stdcall, 1, c => 0);
            i.Register(ws, "getnameinfo", CallConv.Stdcall, 7, c => HostNotFound);
            i.Register(ws, "inet_pton", CallConv.Stdcall, 3, c => 0);

            const string net = "wininet.dll";
            const uint CannotConnect = 12029;
            foreach (var x in new[] { "A", "W" })
            {
                i.Register(net, "InternetOpen" + x, CallConv.Stdcall, 5, c => NewHandle());
                i.Register(net, "InternetOpenUrl" + x, CallConv.Stdcall, 6, c => { process.LastError = CannotConnect; return 0; });
                i.Register(net, "InternetConnect" + x, CallConv.Stdcall, 8, c => { process.LastError = CannotConnect; return 0; });
                i.Register(net, "HttpOpenRequest" + x, CallConv.Stdcall, 8, c => { process.LastError = CannotConnect; return 0; });
                i.Register(net, "HttpSendRequest" + x, CallConv.Stdcall, 5, c => { process.LastError = CannotConnect; return 0; });
                i.Register(net, "HttpQueryInfo" + x, CallConv.Stdcall, 5, c => { process.LastError = 12150; return 0; });   // ERROR_HTTP_HEADER_NOT_FOUND
                i.Register(net, "InternetCheckConnection" + x, CallConv.Stdcall, 3, c => { process.LastError = CannotConnect; return 0; });
                i.Register(net, "InternetSetOption" + x, CallConv.Stdcall, 4, c => 1);
                i.Register(net, "InternetQueryOption" + x, CallConv.Stdcall, 4, c => 0);
                i.Register(net, "InternetCrackUrl" + x, CallConv.Stdcall, 4, c => 0);
                i.Register(net, "InternetGetLastResponseInfo" + x, CallConv.Stdcall, 3, c => { memory.Write32(c.Arg(0), 0); return 1; });
            }
            foreach (var x in new[] { "A", "W" })
            {
                var wide = x == "W";
                i.Register(net, "InternetCanonicalizeUrl" + x, CallConv.Stdcall, 4, c =>
                {
                    var url = ReadText(c.Arg(0), wide).Replace(" ", "%20");
                    var room = memory.Read32(c.Arg(2));
                    memory.Write32(c.Arg(2), (uint)url.Length + (room < url.Length + 1 ? 1u : 0u));
                    if (room < url.Length + 1) { process.LastError = 122; return 0; }
                    WriteText(c.Arg(1), url, wide);
                    return 1;
                });
            }
            i.Register(net, "InternetReadFile", CallConv.Stdcall, 4, c => { if (c.Arg(3) != 0) memory.Write32(c.Arg(3), 0); process.LastError = CannotConnect; return 0; });
            i.Register(net, "InternetCloseHandle", CallConv.Stdcall, 1, c => 1);
            i.Register(net, "InternetGetConnectedState", CallConv.Stdcall, 2, c => { if (c.Arg(0) != 0) memory.Write32(c.Arg(0), 0x20); return 0; });   // INTERNET_CONNECTION_OFFLINE
            i.Register(net, "InternetAttemptConnect", CallConv.Stdcall, 1, c => CannotConnect);
            i.Register(net, "InternetSetStatusCallback", CallConv.Stdcall, 2, c => 0);
        }
    }
}
