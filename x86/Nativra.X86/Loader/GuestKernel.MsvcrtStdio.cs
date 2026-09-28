using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Nativra.X86.Cpu;

namespace Nativra.X86.Loader
{
    // msvcrt stdio: file descriptors over the kernel's file handles (text
    // mode translating CR-LF as msvcrt does), FILE streams whose guest
    // structs keep _cnt at zero so the getc/putc macros always fall through
    // to _filbuf/_flsbuf, the file-system calls (_stat, _findfirst,
    // _getcwd…), and the printf and scanf engines with msvcrt's own
    // conventions (three-digit exponents, %S the other width, legacy
    // swprintf without a count).
    public sealed partial class GuestKernel
    {
        private const uint OText = 0x4000, OBinary = 0x8000, OAppend = 0x8, OCreat = 0x100, OTrunc = 0x200, OExcl = 0x400;
        private const uint IoRead = 0x1, IoWrite = 0x2, IoEof = 0x10, IoError = 0x20, IoStrg = 0x40, IoRw = 0x80;

        private sealed class CrtFd
        {
            public uint Handle;
            public bool Text;
            public bool Append;
            public bool Eof;
            public readonly StringBuilder Line = new StringBuilder();   // console output waiting for its newline
        }

        private sealed class CrtStream
        {
            public int Fd;
            public int Unget = -1;
        }

        private readonly Dictionary<int, CrtFd> fds = new Dictionary<int, CrtFd>();
        private readonly Dictionary<uint, CrtStream> streams = new Dictionary<uint, CrtStream>();
        private readonly Dictionary<int, Queue<GuestFileEntry>> crtFinds = new Dictionary<int, Queue<GuestFileEntry>>();
        private int nextFind = 1;

        private void InstallCrtStdio(GuestImports i)
        {
            fds[0] = new CrtFd { Handle = StdInput, Text = true };
            fds[1] = new CrtFd { Handle = StdOutput, Text = true };
            fds[2] = new CrtFd { Handle = StdError, Text = true };
            for (var fd = 0; fd < 3; fd++) streams[iob + (uint)fd * FileSize] = new CrtStream { Fd = fd };

            InstallCrtLowLevel(i);
            InstallCrtStreams(i);
            InstallCrtPrintf(i);
            InstallCrtScanf(i);
            InstallCrtFileSystem(i);
        }

        // --- descriptors ------------------------------------------------------------

        private bool IsConsole(CrtFd f) => f.Handle == StdInput || f.Handle == StdOutput || f.Handle == StdError;

        private int NewFd(CrtFd f)
        {
            var fd = 3;
            while (fds.ContainsKey(fd)) fd++;
            fds[fd] = f;
            return fd;
        }

        private int OpenFd(string path, uint oflag)
        {
            path = FullPath(path);
            var access = (oflag & 3) == 0 ? FileAccess.Read : (oflag & 3) == 1 ? FileAccess.Write : FileAccess.ReadWrite;
            var exists = Files.Stat(path) != null;
            FileMode mode;
            if ((oflag & OCreat) != 0)
            {
                if ((oflag & OExcl) != 0 && exists) { SetErrno(Eexist); return -1; }
                mode = (oflag & OTrunc) != 0 ? FileMode.Create : FileMode.OpenOrCreate;
                if (access == FileAccess.Read) access = FileAccess.ReadWrite;
            }
            else
            {
                if (!exists) { FilesNotFound.Add(path); SetErrno(Enoent); return -1; }
                mode = (oflag & OTrunc) != 0 ? FileMode.Truncate : FileMode.Open;
            }
            Stream stream;
            try { stream = Files.Open(path, mode, access); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                if (e is FileNotFoundException || e is DirectoryNotFoundException) { FilesNotFound.Add(path); SetErrno(Enoent); }
                else SetErrno(Eacces);
                return -1;
            }
            var handle = NewHandle();
            files[handle] = new OpenFile { Path = path, Stream = stream };
            var text = (oflag & OBinary) == 0 && ((oflag & OText) != 0 || (memory.Read32(fmodeVar) & OBinary) == 0);
            return NewFd(new CrtFd { Handle = handle, Text = text, Append = (oflag & OAppend) != 0 });
        }

        private Stream FdStream(CrtFd f) => files.TryGetValue(f.Handle, out var file) ? file.Stream : null;

        /// <summary>Writes bytes to a descriptor (text mode: LF becomes CR-LF). Returns the bytes taken, or -1.</summary>
        private int FdWrite(int fd, byte[] data)
        {
            if (!fds.TryGetValue(fd, out var f)) { SetErrno(Ebadf); return -1; }
            if (IsConsole(f))
            {
                foreach (var ch in Ansi.Decode(data))
                {
                    if (ch == '\n') { Say(f.Line.ToString()); f.Line.Clear(); }
                    else if (ch != '\r') f.Line.Append(ch);
                }
                return data.Length;
            }
            var stream = FdStream(f);
            if (stream == null || !stream.CanWrite) { SetErrno(Ebadf); return -1; }
            if (f.Append) stream.Position = stream.Length;
            if (f.Text)
            {
                var translated = new List<byte>(data.Length + 16);
                foreach (var b in data) { if (b == (byte)'\n') translated.Add((byte)'\r'); translated.Add(b); }
                stream.Write(translated.ToArray(), 0, translated.Count);
            }
            else stream.Write(data, 0, data.Length);
            return data.Length;
        }

        /// <summary>Reads up to count bytes (text mode: CR-LF becomes LF, Ctrl-Z ends the file). Returns the count, or -1.</summary>
        private int FdRead(int fd, uint buffer, uint count)
        {
            if (!fds.TryGetValue(fd, out var f)) { SetErrno(Ebadf); return -1; }
            if (IsConsole(f)) return 0;   // no console input
            var stream = FdStream(f);
            if (stream == null || !stream.CanRead) { SetErrno(Ebadf); return -1; }
            if (count == 0) return 0;
            if (!f.Text)
            {
                var chunk = new byte[Math.Min(count, 1u << 20)];
                uint total = 0;
                while (total < count)
                {
                    var n = stream.Read(chunk, 0, (int)Math.Min((uint)chunk.Length, count - total));
                    if (n <= 0) break;
                    memory.WriteBytes(buffer + total, chunk, 0, n);
                    total += (uint)n;
                }
                return (int)total;
            }
            if (f.Eof) return 0;
            var output = new List<byte>((int)Math.Min(count, 1u << 16));
            var raw = new byte[Math.Min(count, 1u << 16)];
            while (output.Count < count)
            {
                var n = stream.Read(raw, 0, (int)Math.Min((uint)raw.Length, count - (uint)output.Count));
                if (n <= 0) break;
                var stop = false;
                for (var k = 0; k < n; k++)
                {
                    var b = raw[k];
                    if (b == 0x1A) { stream.Position -= n - k; f.Eof = true; stop = true; break; }
                    if (b == (byte)'\r')
                    {
                        int next;
                        if (k + 1 < n) next = raw[k + 1];
                        else { next = stream.ReadByte(); if (next >= 0) stream.Position--; }
                        if (next == '\n') continue;
                    }
                    output.Add(b);
                }
                if (stop) break;
            }
            memory.WriteBytes(buffer, output.ToArray());
            return output.Count;
        }

        private long FdSeek(int fd, long offset, uint origin)
        {
            if (!fds.TryGetValue(fd, out var f)) { SetErrno(Ebadf); return -1; }
            var stream = FdStream(f);
            if (stream == null || !stream.CanSeek) { SetErrno(Ebadf); return -1; }
            long start = origin == 0 ? 0 : origin == 1 ? stream.Position : origin == 2 ? stream.Length : -1;
            if (start < 0 || start + offset < 0) { SetErrno(Einval); return -1; }
            stream.Position = start + offset;
            f.Eof = false;
            return stream.Position;
        }

        private uint FdClose(int fd)
        {
            if (!fds.TryGetValue(fd, out var f)) { SetErrno(Ebadf); return 0xFFFFFFFF; }
            if (f.Line.Length > 0) { Say(f.Line.ToString()); f.Line.Clear(); }
            fds.Remove(fd);
            if (!IsConsole(f)) CloseHandle(f.Handle);
            return 0;
        }

        private void InstallCrtLowLevel(GuestImports i)
        {
            C(i, "_open", 3, c => (uint)OpenFd(ReadText(c.Arg(0), false), c.Arg(1)));
            C(i, "_wopen", 3, c => (uint)OpenFd(ReadText(c.Arg(0), true), c.Arg(1)));
            C(i, "_sopen", 4, c => (uint)OpenFd(ReadText(c.Arg(0), false), c.Arg(1)));
            C(i, "_wsopen", 4, c => (uint)OpenFd(ReadText(c.Arg(0), true), c.Arg(1)));
            C(i, "_sopen_s", 5, c => { var fd = OpenFd(ReadText(c.Arg(1), false), c.Arg(2)); memory.Write32(c.Arg(0), (uint)fd); return fd < 0 ? memory.Read32(ErrnoSlot()) : 0; });
            C(i, "_creat", 2, c => (uint)OpenFd(ReadText(c.Arg(0), false), 1 | OCreat | OTrunc));
            C(i, "_close", 1, c => FdClose((int)c.Arg(0)));
            C(i, "_read", 3, c => (uint)FdRead((int)c.Arg(0), c.Arg(1), c.Arg(2)));
            C(i, "_write", 3, c => (uint)FdWrite((int)c.Arg(0), memory.ReadBytes(c.Arg(1), (int)c.Arg(2))));
            C(i, "_lseek", 3, c => (uint)FdSeek((int)c.Arg(0), (int)c.Arg(1), c.Arg(2)));
            C(i, "_lseeki64", 4, c => (ulong)FdSeek((int)c.Arg(0), (long)c.Arg64(1), c.Arg(3)));
            C(i, "_tell", 1, c => (uint)FdSeek((int)c.Arg(0), 0, 1));
            C(i, "_telli64", 1, c => (ulong)FdSeek((int)c.Arg(0), 0, 1));
            C(i, "_eof", 1, c =>
            {
                if (!fds.TryGetValue((int)c.Arg(0), out var f)) { SetErrno(Ebadf); return 0xFFFFFFFF; }
                var s = FdStream(f);
                return s == null || s.Position >= s.Length ? 1u : 0u;
            });
            C(i, "_filelength", 1, c => fds.TryGetValue((int)c.Arg(0), out var f) && FdStream(f) is Stream s ? (uint)s.Length : 0xFFFFFFFF);
            C(i, "_filelengthi64", 1, c => fds.TryGetValue((int)c.Arg(0), out var f) && FdStream(f) is Stream s ? (ulong)s.Length : ulong.MaxValue);
            C(i, "_chsize", 2, c =>
            {
                if (!fds.TryGetValue((int)c.Arg(0), out var f) || FdStream(f) == null) { SetErrno(Ebadf); return 0xFFFFFFFF; }
                FdStream(f).SetLength(c.Arg(1));
                return 0;
            });
            C(i, "_commit", 1, c => { if (fds.TryGetValue((int)c.Arg(0), out var f)) FdStream(f)?.Flush(); return 0; });
            C(i, "_get_osfhandle", 1, c => fds.TryGetValue((int)c.Arg(0), out var f) ? f.Handle : 0xFFFFFFFF);
            C(i, "_open_osfhandle", 2, c => (uint)NewFd(new CrtFd { Handle = c.Arg(0), Text = (c.Arg(1) & OBinary) == 0 && (c.Arg(1) & OText) != 0, Append = (c.Arg(1) & OAppend) != 0 }));
            C(i, "_dup", 1, c =>
            {
                if (!fds.TryGetValue((int)c.Arg(0), out var f)) { SetErrno(Ebadf); return 0xFFFFFFFF; }
                return (uint)NewFd(new CrtFd { Handle = f.Handle, Text = f.Text, Append = f.Append });
            });
            C(i, "_dup2", 2, c =>
            {
                if (!fds.TryGetValue((int)c.Arg(0), out var f)) { SetErrno(Ebadf); return 0xFFFFFFFF; }
                fds[(int)c.Arg(1)] = new CrtFd { Handle = f.Handle, Text = f.Text, Append = f.Append };
                return 0;
            });
            C(i, "_setmode", 2, c =>
            {
                if (!fds.TryGetValue((int)c.Arg(0), out var f)) { SetErrno(Ebadf); return 0xFFFFFFFF; }
                var old = f.Text ? OText : OBinary;
                f.Text = (c.Arg(1) & OBinary) == 0;
                return old;
            });
            C(i, "_isatty", 1, c => 0);   // a GUI process: no console attached
            C(i, "_locking", 3, c => 0);
        }

        // --- FILE streams -----------------------------------------------------------------

        private bool TryStream(uint file, out CrtStream s)
        {
            if (file != 0 && streams.TryGetValue(file, out s)) return true;
            s = null;
            SetErrno(Einval);
            return false;
        }

        private void SetFlag(uint file, uint set, uint clear = 0)
        {
            var flag = memory.Read32(file + 12);
            memory.Write32(file + 12, (flag & ~clear) | set);
            memory.Write32(file + 4, 0);   // _cnt stays 0: the macros call into the CRT for every character
        }

        /// <summary>fopen's mode string as _open flags: r/w/a, +, b/t (others ignored).</summary>
        private static bool ParseMode(string mode, out uint oflag, out uint ioflag)
        {
            oflag = 0; ioflag = 0;
            if (string.IsNullOrEmpty(mode)) return false;
            switch (mode[0])
            {
                case 'r': oflag = 0; ioflag = IoRead; break;
                case 'w': oflag = 1 | OCreat | OTrunc; ioflag = IoWrite; break;
                case 'a': oflag = 1 | OCreat | OAppend; ioflag = IoWrite; break;
                default: return false;
            }
            for (var n = 1; n < mode.Length && mode[n] != ','; n++)
            {
                switch (mode[n])
                {
                    case '+': oflag = (oflag & ~3u) | 2; ioflag = IoRw; break;
                    case 'b': oflag |= OBinary; break;
                    case 't': oflag |= OText; break;
                }
            }
            return true;
        }

        private uint NewStream(int fd, uint ioflag, uint reuse = 0)
        {
            var file = reuse;
            if (file == 0)
            {
                for (var n = 3; n < IobCount && file == 0; n++)
                {
                    var candidate = iob + (uint)n * FileSize;
                    if (!streams.ContainsKey(candidate)) file = candidate;
                }
                if (file == 0) file = heap.Alloc(FileSize, zero: true);
            }
            memory.WriteBytes(file, new byte[FileSize]);
            memory.Write32(file + 12, ioflag);
            memory.Write32(file + 16, (uint)fd);
            streams[file] = new CrtStream { Fd = fd };
            return file;
        }

        private uint FOpen(string path, string mode, uint reuse = 0)
        {
            if (!ParseMode(mode, out var oflag, out var ioflag)) { SetErrno(Einval); return 0; }
            var fd = OpenFd(path, oflag);
            return fd < 0 ? 0 : NewStream(fd, ioflag, reuse);
        }

        private uint FClose(uint file)
        {
            if (!TryStream(file, out var s)) return 0xFFFFFFFF;
            var result = FdClose(s.Fd);
            streams.Remove(file);
            memory.WriteBytes(file, new byte[FileSize]);
            return result;
        }

        private int GetChar(uint file)
        {
            if (!TryStream(file, out var s)) return -1;
            if (s.Unget >= 0) { var u = s.Unget; s.Unget = -1; return u; }
            var n = FdRead(s.Fd, crtScratch + 48, 1);
            if (n <= 0) { SetFlag(file, n < 0 ? IoError : IoEof); return -1; }
            return memory.Read8(crtScratch + 48);
        }

        private int GetWideChar(uint file)
        {
            var lo = GetChar(file);
            if (lo < 0) return 0xFFFF;
            if (!fds.TryGetValue(streams[file].Fd, out var f) || f.Text) return Ansi.Decode(new[] { (byte)lo })[0];
            var hi = GetChar(file);
            return hi < 0 ? 0xFFFF : lo | (hi << 8);
        }

        private uint PutBytes(uint file, byte[] data)
        {
            if (!TryStream(file, out var s)) return 0;
            var n = FdWrite(s.Fd, data);
            if (n < 0) { SetFlag(file, IoError); return 0; }
            SetFlag(file, 0);
            return (uint)n;
        }

        private uint StreamRead(uint buffer, uint size, uint count, uint file)
        {
            if (!TryStream(file, out var s)) return 0;
            var total = size * count;
            if (total == 0) return 0;
            uint got = 0;
            if (s.Unget >= 0) { memory.Write8(buffer, (byte)s.Unget); s.Unget = -1; got = 1; }
            if (got < total)
            {
                var n = FdRead(s.Fd, buffer + got, total - got);
                if (n < 0) { SetFlag(file, IoError); return got / size; }
                got += (uint)n;
            }
            if (got < total) SetFlag(file, IoEof);
            return got / size;
        }

        private void FlushAllStreams()
        {
            foreach (var f in fds.Values)
            {
                if (f.Line.Length > 0) { Say(f.Line.ToString()); f.Line.Clear(); }
                if (!IsConsole(f)) FdStream(f)?.Flush();
            }
        }

        private void InstallCrtStreams(GuestImports i)
        {
            C(i, "fopen", 2, c => FOpen(ReadText(c.Arg(0), false), ReadText(c.Arg(1), false)));
            C(i, "_wfopen", 2, c => FOpen(ReadText(c.Arg(0), true), ReadText(c.Arg(1), true)));
            C(i, "_fsopen", 3, c => FOpen(ReadText(c.Arg(0), false), ReadText(c.Arg(1), false)));
            C(i, "_wfsopen", 3, c => FOpen(ReadText(c.Arg(0), true), ReadText(c.Arg(1), true)));
            C(i, "fopen_s", 3, c =>
            {
                var f = FOpen(ReadText(c.Arg(1), false), ReadText(c.Arg(2), false));
                memory.Write32(c.Arg(0), f);
                return f == 0 ? memory.Read32(ErrnoSlot()) : 0;
            });
            C(i, "_wfopen_s", 3, c =>
            {
                var f = FOpen(ReadText(c.Arg(1), true), ReadText(c.Arg(2), true));
                memory.Write32(c.Arg(0), f);
                return f == 0 ? memory.Read32(ErrnoSlot()) : 0;
            });
            C(i, "freopen", 3, c =>
            {
                if (streams.ContainsKey(c.Arg(2))) FClose(c.Arg(2));
                return FOpen(ReadText(c.Arg(0), false), ReadText(c.Arg(1), false), c.Arg(2));
            });
            C(i, "_fdopen", 2, c =>
            {
                if (!fds.ContainsKey((int)c.Arg(0)) || !ParseMode(ReadText(c.Arg(1), false), out _, out var io)) { SetErrno(Ebadf); return 0; }
                return NewStream((int)c.Arg(0), io);
            });
            C(i, "fclose", 1, c => FClose(c.Arg(0)));
            C(i, "_fcloseall", 0, c =>
            {
                var closed = 0u;
                foreach (var f in new List<uint>(streams.Keys))
                    if (streams[f].Fd > 2) { FClose(f); closed++; }
                return closed;
            });
            C(i, "fread", 4, c => StreamRead(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3)));
            C(i, "fwrite", 4, c =>
            {
                var total = c.Arg(1) * c.Arg(2);
                if (total == 0) return 0;
                return PutBytes(c.Arg(3), memory.ReadBytes(c.Arg(0), (int)total)) / c.Arg(1);
            });
            HostCall fgetc = c => (uint)GetChar(c.Arg(0));
            C(i, "fgetc", 1, fgetc);
            C(i, "getc", 1, fgetc);
            C(i, "_fgetc_nolock", 1, fgetc);
            C(i, "_filbuf", 1, fgetc);
            C(i, "getchar", 0, c => (uint)GetChar(iob));
            C(i, "_fgetchar", 0, c => (uint)GetChar(iob));
            HostCall fputc = c => PutBytes(c.Arg(1), new[] { (byte)c.Arg(0) }) == 1 ? c.Arg(0) & 0xFF : 0xFFFFFFFF;
            C(i, "fputc", 2, fputc);
            C(i, "putc", 2, fputc);
            C(i, "_fputc_nolock", 2, fputc);
            C(i, "_flsbuf", 2, fputc);
            C(i, "putchar", 1, c => PutBytes(iob + FileSize, new[] { (byte)c.Arg(0) }) == 1 ? c.Arg(0) & 0xFF : 0xFFFFFFFF);
            C(i, "_fputchar", 1, c => PutBytes(iob + FileSize, new[] { (byte)c.Arg(0) }) == 1 ? c.Arg(0) & 0xFF : 0xFFFFFFFF);
            C(i, "fgetwc", 1, c => (uint)GetWideChar(c.Arg(0)));
            C(i, "getwc", 1, c => (uint)GetWideChar(c.Arg(0)));
            C(i, "fputwc", 2, c => PutBytes(c.Arg(1), Ansi.Encode(((char)c.Arg(0)).ToString())) == 1 ? c.Arg(0) & 0xFFFF : 0xFFFF);
            C(i, "putwc", 2, c => PutBytes(c.Arg(1), Ansi.Encode(((char)c.Arg(0)).ToString())) == 1 ? c.Arg(0) & 0xFFFF : 0xFFFF);
            C(i, "fgets", 3, c =>
            {
                uint n = 0;
                while (n + 1 < c.Arg(1))
                {
                    var ch = GetChar(c.Arg(2));
                    if (ch < 0) break;
                    memory.Write8(c.Arg(0) + n++, (byte)ch);
                    if (ch == '\n') break;
                }
                if (n == 0 && c.Arg(1) > 1) return 0;
                if (c.Arg(1) > 0) memory.Write8(c.Arg(0) + n, 0);
                return c.Arg(0);
            });
            C(i, "fgetws", 3, c =>
            {
                uint n = 0;
                while (n + 1 < c.Arg(1))
                {
                    var ch = GetWideChar(c.Arg(2));
                    if (ch == 0xFFFF) break;
                    memory.Write16(c.Arg(0) + n++ * 2, (ushort)ch);
                    if (ch == '\n') break;
                }
                if (n == 0 && c.Arg(1) > 1) return 0;
                if (c.Arg(1) > 0) memory.Write16(c.Arg(0) + n * 2, 0);
                return c.Arg(0);
            });
            C(i, "gets", 1, c => 0);   // no console input
            C(i, "fputs", 2, c => PutBytes(c.Arg(1), CBytes(c.Arg(0))) == StrLen(c.Arg(0)) ? 0u : 0xFFFFFFFF);
            C(i, "fputws", 2, c => { PutBytes(c.Arg(1), Ansi.Encode(WString(c.Arg(0)))); return 0; });
            C(i, "puts", 1, c => { PutBytes(iob + FileSize, CBytes(c.Arg(0))); PutBytes(iob + FileSize, new[] { (byte)'\n' }); return 0; });
            C(i, "_putws", 1, c => { PutBytes(iob + FileSize, Ansi.Encode(WString(c.Arg(0)) + "\n")); return 0; });
            C(i, "ungetc", 2, c =>
            {
                if (c.Arg(0) == 0xFFFFFFFF || !TryStream(c.Arg(1), out var s)) return 0xFFFFFFFF;
                s.Unget = (int)(c.Arg(0) & 0xFF);
                SetFlag(c.Arg(1), 0, IoEof);
                return c.Arg(0) & 0xFF;
            });
            C(i, "ungetwc", 2, c =>
            {
                if (c.Arg(0) == 0xFFFF || !TryStream(c.Arg(1), out var s)) return 0xFFFF;
                s.Unget = Ansi.Encode(((char)c.Arg(0)).ToString())[0];
                SetFlag(c.Arg(1), 0, IoEof);
                return c.Arg(0) & 0xFFFF;
            });
            C(i, "fseek", 3, c => StreamSeek(c.Arg(0), (int)c.Arg(1), c.Arg(2)));
            C(i, "_fseeki64", 4, c => StreamSeek(c.Arg(0), (long)c.Arg64(1), c.Arg(3)));
            C(i, "ftell", 1, c => (uint)StreamTell(c.Arg(0)));
            C(i, "_ftelli64", 1, c => (ulong)StreamTell(c.Arg(0)));
            C(i, "fgetpos", 2, c => { var p = StreamTell(c.Arg(0)); if (p < 0) return 0xFFFFFFFF; memory.Write64(c.Arg(1), (ulong)p); return 0; });
            C(i, "fsetpos", 2, c => StreamSeek(c.Arg(0), (long)memory.Read64(c.Arg(1)), 0));
            C(i, "rewind", 1, c => { StreamSeek(c.Arg(0), 0, 0); if (streams.ContainsKey(c.Arg(0))) SetFlag(c.Arg(0), 0, IoError); return 0; });
            C(i, "feof", 1, c => c.Arg(0) != 0 && streams.ContainsKey(c.Arg(0)) ? memory.Read32(c.Arg(0) + 12) & IoEof : 0);
            C(i, "ferror", 1, c => c.Arg(0) != 0 && streams.ContainsKey(c.Arg(0)) ? memory.Read32(c.Arg(0) + 12) & IoError : 0);
            C(i, "clearerr", 1, c => { if (streams.ContainsKey(c.Arg(0))) SetFlag(c.Arg(0), 0, IoEof | IoError); return 0; });
            C(i, "fflush", 1, c =>
            {
                if (c.Arg(0) == 0) { FlushAllStreams(); return 0; }
                if (!TryStream(c.Arg(0), out var s)) return 0xFFFFFFFF;
                if (fds.TryGetValue(s.Fd, out var f) && !IsConsole(f)) FdStream(f)?.Flush();
                return 0;
            });
            C(i, "_flushall", 0, c => { FlushAllStreams(); return (uint)streams.Count; });
            C(i, "setvbuf", 4, c => 0);
            C(i, "setbuf", 2, c => 0);
            C(i, "_fileno", 1, c => TryStream(c.Arg(0), out var s) ? (uint)s.Fd : 0xFFFFFFFF);
            C(i, "_lock_file", 1, c => 0);
            C(i, "_unlock_file", 1, c => 0);
            C(i, "tmpfile", 0, c => FOpen(Folder(ExePath) + $"tmp{(uint)Milliseconds & 0xFFFF:X4}{streams.Count}.tmp", "w+b"));
            C(i, "tmpnam", 1, c =>
            {
                var name = $"\\s{(uint)Milliseconds & 0xFFFF:x}.{nextFind++}";
                var buffer = c.Arg(0) != 0 ? c.Arg(0) : crtScratch;
                WriteText(buffer, name, false);
                return buffer;
            });
            C(i, "perror", 1, c =>
            {
                var prefix = c.Arg(0) != 0 ? CString(c.Arg(0)) + ": " : "";
                var errno = (int)memory.Read32(ErrnoSlot());
                Say(prefix + (errno >= 0 && errno < ErrorMessages.Length ? ErrorMessages[errno] : "Unknown error"));
                return 0;
            });
            C(i, "_getch", 0, c => 0xFFFFFFFF);
            C(i, "_getche", 0, c => 0xFFFFFFFF);
            C(i, "_kbhit", 0, c => 0);
            C(i, "_putch", 1, c => { FdWrite(1, new[] { (byte)c.Arg(0) }); return c.Arg(0); });
            C(i, "_cputs", 1, c => { FdWrite(1, CBytes(c.Arg(0))); return 0; });
        }

        private uint StreamSeek(uint file, long offset, uint origin)
        {
            if (!TryStream(file, out var s)) return 0xFFFFFFFF;
            if (origin == 1 && s.Unget >= 0) offset--;
            s.Unget = -1;
            if (FdSeek(s.Fd, offset, origin) < 0) return 0xFFFFFFFF;
            SetFlag(file, 0, IoEof);
            return 0;
        }

        private long StreamTell(uint file)
        {
            if (!TryStream(file, out var s)) return -1;
            if (!fds.TryGetValue(s.Fd, out var f)) return -1;
            var stream = FdStream(f);
            if (stream == null) return 0;
            return stream.Position - (s.Unget >= 0 ? 1 : 0);
        }

        // --- printf --------------------------------------------------------------------------

        /// <summary>Walks a cdecl argument list (or a va_list, which on x86 is a pointer to one).</summary>
        private sealed class ArgCursor
        {
            private readonly GuestMemory memory;
            public uint At;
            public ArgCursor(GuestMemory memory, uint at) { this.memory = memory; At = at; }
            public uint Next32() { var v = memory.Read32(At); At += 4; return v; }
            public ulong Next64() { var v = memory.Read64(At); At += 8; return v; }
            public double NextDouble() => BitConverter.Int64BitsToDouble((long)Next64());
        }

        private ArgCursor Args(GuestCall c, int fixedArgs) => new ArgCursor(memory, c.ArgBase + (uint)fixedArgs * 4);

        private ArgCursor VaList(uint list) => new ArgCursor(memory, list);

        /// <summary>
        /// printf's engine: flags (-+ #0), width and precision (with *),
        /// sizes h l ll L I I32 I64 w, conversions d i u o x X c C s S e E f g G p n %.
        /// In the wide functions %s/%c are wide and %S/%C narrow; the narrow ones the reverse.
        /// </summary>
        private string FormatPrintf(string format, ArgCursor args, bool wideCall)
        {
            var output = new StringBuilder();
            for (var n = 0; n < format.Length; n++)
            {
                var ch = format[n];
                if (ch != '%') { output.Append(ch); continue; }
                if (++n >= format.Length) break;
                if (format[n] == '%') { output.Append('%'); continue; }

                bool left = false, plus = false, space = false, alt = false, zero = false;
                for (; n < format.Length; n++)
                {
                    var f = format[n];
                    if (f == '-') left = true; else if (f == '+') plus = true; else if (f == ' ') space = true;
                    else if (f == '#') alt = true; else if (f == '0') zero = true; else break;
                }
                var width = 0;
                if (n < format.Length && format[n] == '*')
                {
                    width = (int)args.Next32();
                    if (width < 0) { left = true; width = -width; }
                    n++;
                }
                else while (n < format.Length && char.IsDigit(format[n])) width = width * 10 + (format[n++] - '0');
                var precision = -1;
                if (n < format.Length && format[n] == '.')
                {
                    n++;
                    precision = 0;
                    if (n < format.Length && format[n] == '*') { precision = (int)args.Next32(); if (precision < 0) precision = -1; n++; }
                    else while (n < format.Length && char.IsDigit(format[n])) precision = precision * 10 + (format[n++] - '0');
                }
                var size = 0;   // 0 default, 1 h, 2 l, 3 64-bit, 4 w (wide)
                while (n < format.Length)
                {
                    var s = format[n];
                    if (s == 'h') { size = 1; n++; }
                    else if (s == 'l') { size = size == 2 ? 3 : 2; n++; }
                    else if (s == 'L' || s == 'q' || s == 'j') { size = 3; n++; }
                    else if (s == 'z' || s == 't') { n++; }
                    else if (s == 'w') { size = 4; n++; }
                    else if (s == 'I')
                    {
                        if (n + 2 < format.Length && format[n + 1] == '6' && format[n + 2] == '4') { size = 3; n += 3; }
                        else if (n + 2 < format.Length && format[n + 1] == '3' && format[n + 2] == '2') { size = 0; n += 3; }
                        else { n++; }
                    }
                    else break;
                }
                if (n >= format.Length) break;
                var conversion = format[n];

                string body;
                var prefix = "";
                var numeric = false;
                switch (conversion)
                {
                    case 'd':
                    case 'i':
                    {
                        long v = size == 3 ? (long)args.Next64() : size == 1 ? (short)args.Next32() : (int)args.Next32();
                        body = Digits((ulong)(v < 0 ? -v : v), 10, false, precision);
                        prefix = v < 0 ? "-" : plus ? "+" : space ? " " : "";
                        numeric = true;
                        break;
                    }
                    case 'u':
                    case 'o':
                    case 'x':
                    case 'X':
                    {
                        ulong v = size == 3 ? args.Next64() : size == 1 ? (ushort)args.Next32() : args.Next32();
                        var radix = conversion == 'u' ? 10 : conversion == 'o' ? 8 : 16;
                        body = Digits(v, radix, conversion == 'X', precision);
                        if (alt && v != 0 && radix == 16) prefix = conversion == 'X' ? "0X" : "0x";
                        if (alt && radix == 8 && !body.StartsWith("0", StringComparison.Ordinal)) body = "0" + body;
                        numeric = true;
                        break;
                    }
                    case 'p':
                        body = args.Next32().ToString("X8", CultureInfo.InvariantCulture);
                        break;
                    case 'c':
                    case 'C':
                    {
                        var value = args.Next32();
                        var wideChar = size == 4 || size == 2 || (size != 1 && (conversion == 'c') == wideCall);
                        body = wideChar ? ((char)value).ToString() : Ansi.Decode(new[] { (byte)value });
                        break;
                    }
                    case 's':
                    case 'S':
                    case 'Z':
                    {
                        var p = args.Next32();
                        var wideString = size == 4 || size == 2 || (size != 1 && (conversion == 's') == wideCall);
                        if (conversion == 'Z')
                        {
                            // ANSI_STRING / UNICODE_STRING: Length, MaximumLength, Buffer.
                            var length = p != 0 ? memory.Read16(p) : 0;
                            var buffer = p != 0 ? memory.Read32(p + 4) : 0;
                            body = buffer == 0 ? "(null)" : wideString
                                ? memory.ReadUnicode(buffer, length / 2)
                                : Ansi.Decode(memory.ReadBytes(buffer, length));
                        }
                        else if (p == 0) body = wideString ? "(null)" : "(null)";
                        else if (precision >= 0)
                        {
                            // Only up to precision characters are read, the string need not be terminated.
                            var s = new StringBuilder();
                            for (var k = 0; k < precision; k++)
                            {
                                var v = wideString ? memory.Read16(p + (uint)k * 2) : memory.Read8(p + (uint)k);
                                if (v == 0) break;
                                s.Append(wideString ? (char)v : Ansi.Decode(new[] { (byte)v })[0]);
                            }
                            body = s.ToString();
                        }
                        else body = wideString ? WString(p) : CString(p);
                        break;
                    }
                    case 'e':
                    case 'E':
                    case 'f':
                    case 'F':
                    case 'g':
                    case 'G':
                    case 'a':
                    case 'A':
                    {
                        var v = args.NextDouble();
                        var negative = v < 0 || (v == 0 && 1 / v < 0);
                        body = FormatFloat(Math.Abs(v), conversion, precision, alt, double.IsNaN(v));
                        prefix = negative && !double.IsNaN(v) ? "-" : plus ? "+" : space ? " " : "";
                        numeric = !double.IsInfinity(v) && !double.IsNaN(v);
                        break;
                    }
                    case 'n':
                    {
                        var p = args.Next32();
                        if (p != 0) { if (size == 1) memory.Write16(p, (ushort)output.Length); else memory.Write32(p, (uint)output.Length); }
                        continue;
                    }
                    default:
                        output.Append(conversion);
                        continue;
                }

                var total = prefix.Length + body.Length;
                if (total >= width) { output.Append(prefix).Append(body); continue; }
                var pad = width - total;
                if (left) output.Append(prefix).Append(body).Append(' ', pad);
                else if (zero && numeric && !(precision >= 0 && "diouxX".IndexOf(conversion) >= 0)) output.Append(prefix).Append('0', pad).Append(body);
                else output.Append(' ', pad).Append(prefix).Append(body);
            }
            return output.ToString();
        }

        private static string Digits(ulong value, int radix, bool upper, int precision)
        {
            if (precision == 0 && value == 0) return "";
            var s = new StringBuilder();
            var digits = upper ? "0123456789ABCDEF" : "0123456789abcdef";
            do { s.Insert(0, digits[(int)(value % (ulong)radix)]); value /= (ulong)radix; } while (value != 0);
            while (s.Length < precision) s.Insert(0, '0');
            return s.ToString();
        }

        /// <summary>
        /// A non-negative double in printf's forms, as msvcrt writes them:
        /// exponents with at least three digits, 1.#INF and 1.#QNAN for the
        /// non-finite values.
        /// </summary>
        internal static string FormatFloat(double v, char conversion, int precision, bool alt, bool nan)
        {
            if (precision < 0) precision = 6;
            var upper = char.IsUpper(conversion);
            if (nan || double.IsInfinity(v))
            {
                var special = nan ? "1.#QNAN" : "1.#INF";
                if (precision > 0 && char.ToLowerInvariant(conversion) != 'g')
                    special = (special + new string('0', precision)).Substring(0, Math.Max(special.Length, 2 + precision));
                return upper ? special.ToUpperInvariant() : special;
            }
            switch (char.ToLowerInvariant(conversion))
            {
                case 'f':
                {
                    var s = v.ToString("F" + precision, CultureInfo.InvariantCulture);
                    return alt && precision == 0 ? s + "." : s;
                }
                case 'e':
                    return Exponential(v, precision, alt, upper);
                case 'a':
                {
                    // Hexadecimal: 0x1.hhhp+d.
                    if (v == 0) return upper ? "0X0.0P+0" : "0x0.0p+0";
                    var bits = BitConverter.DoubleToInt64Bits(v);
                    var exponent = (int)((bits >> 52) & 0x7FF) - 1023;
                    var mantissa = (bits & 0xFFFFFFFFFFFFFL).ToString("x13", CultureInfo.InvariantCulture).TrimEnd('0');
                    var text = $"0x1{(mantissa.Length > 0 ? "." + mantissa : "")}p{(exponent >= 0 ? "+" : "")}{exponent}";
                    return upper ? text.ToUpperInvariant() : text;
                }
                default:
                {
                    // %g: %e when the exponent is below -4 or at least the precision, else %f; trailing zeros go without #.
                    if (precision == 0) precision = 1;
                    if (v == 0) return alt ? "0." + new string('0', precision - 1) : "0";
                    var e = Exponential(v, precision - 1, false, upper);
                    var exponent = int.Parse(e.Substring(e.IndexOfAny(new[] { 'e', 'E' }) + 1), CultureInfo.InvariantCulture);
                    string result;
                    if (exponent < -4 || exponent >= precision) result = Exponential(v, precision - 1, alt, upper);
                    else result = v.ToString("F" + Math.Max(0, precision - 1 - exponent), CultureInfo.InvariantCulture);
                    if (!alt && result.IndexOf('.') >= 0)
                    {
                        var at = result.IndexOfAny(new[] { 'e', 'E' });
                        var mantissa = at >= 0 ? result.Substring(0, at) : result;
                        var tail = at >= 0 ? result.Substring(at) : "";
                        mantissa = mantissa.TrimEnd('0').TrimEnd('.');
                        result = mantissa + tail;
                    }
                    return result;
                }
            }
        }

        private static string Exponential(double v, int precision, bool alt, bool upper)
        {
            // .NET's "E" form has at least three exponent digits: msvcrt's form.
            var s = v.ToString((upper ? "E" : "e") + precision, CultureInfo.InvariantCulture);
            if (alt && precision == 0) s = s.Insert(1, ".");
            return s;
        }

        private string FormatNarrow(GuestCall c, int formatArg, ArgCursor args) => FormatPrintf(CString(c.Arg(formatArg)) ?? "", args, false);

        private string FormatWide(GuestCall c, int formatArg, ArgCursor args) => FormatPrintf(WString(c.Arg(formatArg)) ?? "", args, true);

        /// <summary>Writes formatted text into a caller buffer; returns what the particular printf variant returns.</summary>
        private uint Emit(string text, uint buffer, uint capacity, bool wide, int variant)
        {
            // variant 0: sprintf (unbounded); 1: _snprintf (-1 when cut, no NUL when full); 2: C99 snprintf; 3: _s forms.
            var units = wide ? text.Length : Ansi.Encode(text).Length;
            var unit = wide ? 2u : 1u;
            if (variant == 0 || (variant == 1 && units < capacity) || (variant == 2 && units < capacity) || (variant == 3 && units < capacity))
            {
                WriteText(buffer, text, wide);
                return (uint)units;
            }
            if (variant == 3)
            {
                if (buffer != 0 && capacity > 0) WriteText(buffer, "", wide);
                SetErrno(Erange);
                return 0xFFFFFFFF;
            }
            if (buffer != 0 && capacity > 0)
            {
                var cut = (int)Math.Min((uint)text.Length, variant == 2 ? capacity - 1 : capacity);
                var head = text.Substring(0, cut);
                if (wide) for (var k = 0; k < head.Length; k++) memory.Write16(buffer + (uint)k * 2, head[k]);
                else memory.WriteBytes(buffer, Ansi.Encode(head));
                if (variant == 2) { if (wide) memory.Write16(buffer + (uint)cut * unit, 0); else memory.Write8(buffer + (uint)cut, 0); }
                else if (units == capacity) return (uint)units;   // _snprintf: exactly full, no NUL
            }
            return variant == 2 ? (uint)units : 0xFFFFFFFF;
        }

        private uint PrintTo(uint file, string text, bool wide)
        {
            var bytes = Ansi.Encode(text);
            PutBytes(file, bytes);
            return (uint)(wide ? text.Length : bytes.Length);
        }

        private void InstallCrtPrintf(GuestImports i)
        {
            var stdout = iob + FileSize;
            C(i, "printf", 1, c => PrintTo(stdout, FormatNarrow(c, 0, Args(c, 1)), false));
            C(i, "vprintf", 2, c => PrintTo(stdout, FormatNarrow(c, 0, VaList(c.Arg(1))), false));
            C(i, "wprintf", 1, c => PrintTo(stdout, FormatWide(c, 0, Args(c, 1)), true));
            C(i, "vwprintf", 2, c => PrintTo(stdout, FormatWide(c, 0, VaList(c.Arg(1))), true));
            C(i, "fprintf", 2, c => PrintTo(c.Arg(0), FormatNarrow(c, 1, Args(c, 2)), false));
            C(i, "vfprintf", 3, c => PrintTo(c.Arg(0), FormatNarrow(c, 1, VaList(c.Arg(2))), false));
            C(i, "fwprintf", 2, c => PrintTo(c.Arg(0), FormatWide(c, 1, Args(c, 2)), true));
            C(i, "vfwprintf", 3, c => PrintTo(c.Arg(0), FormatWide(c, 1, VaList(c.Arg(2))), true));
            C(i, "fprintf_s", 2, c => PrintTo(c.Arg(0), FormatNarrow(c, 1, Args(c, 2)), false));
            C(i, "printf_s", 1, c => PrintTo(stdout, FormatNarrow(c, 0, Args(c, 1)), false));

            C(i, "sprintf", 2, c => Emit(FormatNarrow(c, 1, Args(c, 2)), c.Arg(0), 0, false, 0));
            C(i, "vsprintf", 3, c => Emit(FormatNarrow(c, 1, VaList(c.Arg(2))), c.Arg(0), 0, false, 0));
            C(i, "_snprintf", 3, c => Emit(FormatNarrow(c, 2, Args(c, 3)), c.Arg(0), c.Arg(1), false, 1));
            C(i, "_vsnprintf", 4, c => Emit(FormatNarrow(c, 2, VaList(c.Arg(3))), c.Arg(0), c.Arg(1), false, 1));
            C(i, "snprintf", 3, c => Emit(FormatNarrow(c, 2, Args(c, 3)), c.Arg(0), c.Arg(1), false, 2));
            C(i, "vsnprintf", 4, c => Emit(FormatNarrow(c, 2, VaList(c.Arg(3))), c.Arg(0), c.Arg(1), false, 2));
            C(i, "sprintf_s", 3, c => Emit(FormatNarrow(c, 2, Args(c, 3)), c.Arg(0), c.Arg(1), false, 3));
            C(i, "vsprintf_s", 4, c => Emit(FormatNarrow(c, 2, VaList(c.Arg(3))), c.Arg(0), c.Arg(1), false, 3));
            C(i, "_snprintf_s", 4, c => Emit(FormatNarrow(c, 3, Args(c, 4)), c.Arg(0), Math.Min(c.Arg(1), c.Arg(2) + 1), false, c.Arg(2) == 0xFFFFFFFF ? 1 : 3));
            C(i, "_vsnprintf_s", 5, c => Emit(FormatNarrow(c, 3, VaList(c.Arg(4))), c.Arg(0), Math.Min(c.Arg(1), c.Arg(2) + 1), false, c.Arg(2) == 0xFFFFFFFF ? 1 : 3));
            C(i, "_scprintf", 1, c => (uint)Ansi.Encode(FormatNarrow(c, 0, Args(c, 1))).Length);
            C(i, "_vscprintf", 2, c => (uint)Ansi.Encode(FormatNarrow(c, 0, VaList(c.Arg(1)))).Length);

            // msvcrt's own swprintf and vswprintf take no count (the pre-C99 forms).
            C(i, "swprintf", 2, c => Emit(FormatWide(c, 1, Args(c, 2)), c.Arg(0), 0, true, 0));
            C(i, "_swprintf", 2, c => Emit(FormatWide(c, 1, Args(c, 2)), c.Arg(0), 0, true, 0));
            C(i, "vswprintf", 3, c => Emit(FormatWide(c, 1, VaList(c.Arg(2))), c.Arg(0), 0, true, 0));
            C(i, "_vswprintf", 3, c => Emit(FormatWide(c, 1, VaList(c.Arg(2))), c.Arg(0), 0, true, 0));
            C(i, "_snwprintf", 3, c => Emit(FormatWide(c, 2, Args(c, 3)), c.Arg(0), c.Arg(1), true, 1));
            C(i, "_vsnwprintf", 4, c => Emit(FormatWide(c, 2, VaList(c.Arg(3))), c.Arg(0), c.Arg(1), true, 1));
            C(i, "swprintf_s", 3, c => Emit(FormatWide(c, 2, Args(c, 3)), c.Arg(0), c.Arg(1), true, 3));
            C(i, "vswprintf_s", 4, c => Emit(FormatWide(c, 2, VaList(c.Arg(3))), c.Arg(0), c.Arg(1), true, 3));
            C(i, "_snwprintf_s", 4, c => Emit(FormatWide(c, 3, Args(c, 4)), c.Arg(0), Math.Min(c.Arg(1), c.Arg(2) + 1), true, c.Arg(2) == 0xFFFFFFFF ? 1 : 3));
            C(i, "_vsnwprintf_s", 5, c => Emit(FormatWide(c, 3, VaList(c.Arg(4))), c.Arg(0), Math.Min(c.Arg(1), c.Arg(2) + 1), true, c.Arg(2) == 0xFFFFFFFF ? 1 : 3));
            C(i, "_scwprintf", 1, c => (uint)FormatWide(c, 0, Args(c, 1)).Length);
            C(i, "_vscwprintf", 2, c => (uint)FormatWide(c, 0, VaList(c.Arg(1))).Length);
        }

        // --- scanf ------------------------------------------------------------------------------

        private abstract class ScanSource
        {
            public int Consumed;
            public abstract int Peek();
            public int Read() { var ch = Peek(); if (ch >= 0) { Advance(); Consumed++; } return ch; }
            protected abstract void Advance();
            public virtual void Finish() { }
        }

        private sealed class TextSource : ScanSource
        {
            private readonly string text;
            private int at;
            public TextSource(string text) { this.text = text ?? ""; }
            public override int Peek() => at < text.Length ? text[at] : -1;
            protected override void Advance() => at++;
        }

        private sealed class StreamSource : ScanSource
        {
            private readonly GuestKernel kernel;
            private readonly uint file;
            private int peeked = -2;
            public StreamSource(GuestKernel kernel, uint file) { this.kernel = kernel; this.file = file; }
            public override int Peek()
            {
                if (peeked == -2) peeked = kernel.GetChar(file);
                return peeked < 0 ? -1 : Ansi.Decode(new[] { (byte)peeked })[0];
            }
            protected override void Advance() => peeked = -2;
            public override void Finish()
            {
                // The character looked at and not taken goes back.
                if (peeked >= 0 && kernel.streams.TryGetValue(file, out var s)) s.Unget = peeked;
            }
        }

        /// <summary>scanf's engine. Returns the fields assigned, or -1 when the input ended before the first conversion.</summary>
        private uint Scan(ScanSource source, string format, ArgCursor args, bool wideCall)
        {
            var assigned = 0;
            var anyConversion = false;
            for (var n = 0; n < format.Length; n++)
            {
                var ch = format[n];
                if (char.IsWhiteSpace(ch)) { while (source.Peek() >= 0 && char.IsWhiteSpace((char)source.Peek())) source.Read(); continue; }
                if (ch != '%' || (n + 1 < format.Length && format[n + 1] == '%'))
                {
                    if (ch == '%') n++;
                    if (source.Peek() != format[n]) break;
                    source.Read();
                    continue;
                }
                n++;
                var suppress = n < format.Length && format[n] == '*';
                if (suppress) n++;
                var width = 0;
                while (n < format.Length && char.IsDigit(format[n])) width = width * 10 + (format[n++] - '0');
                if (width == 0) width = int.MaxValue;
                var size = 0;   // 1 h, 2 l, 3 64-bit, 4 w
                while (n < format.Length)
                {
                    var s = format[n];
                    if (s == 'h') { size = 1; n++; }
                    else if (s == 'l') { size = size == 2 ? 3 : 2; n++; }
                    else if (s == 'L') { size = 3; n++; }
                    else if (s == 'w') { size = 4; n++; }
                    else if (s == 'I' && n + 2 < format.Length && format[n + 1] == '6' && format[n + 2] == '4') { size = 3; n += 3; }
                    else break;
                }
                if (n >= format.Length) break;
                var conversion = format[n];

                if (conversion == 'n')
                {
                    if (!suppress) memory.Write32(args.Next32(), (uint)source.Consumed);
                    continue;
                }
                if (conversion != 'c' && conversion != 'C' && conversion != '[')
                    while (source.Peek() >= 0 && char.IsWhiteSpace((char)source.Peek())) source.Read();
                if (source.Peek() < 0) { if (!anyConversion) { source.Finish(); return 0xFFFFFFFF; } break; }

                var matched = true;
                switch (conversion)
                {
                    case 'd': case 'i': case 'u': case 'o': case 'x': case 'X': case 'p':
                    {
                        var radix = conversion == 'd' || conversion == 'u' ? 10 : conversion == 'o' ? 8 : conversion == 'i' ? 0 : 16;
                        var text = new StringBuilder();
                        if (width > 0 && (source.Peek() == '+' || source.Peek() == '-')) { text.Append((char)source.Read()); width--; }
                        if (radix == 0 || radix == 16)
                        {
                            if (width > 0 && source.Peek() == '0')
                            {
                                text.Append((char)source.Read()); width--;
                                if (width > 0 && (source.Peek() == 'x' || source.Peek() == 'X')) { text.Append((char)source.Read()); width--; radix = 16; }
                                else if (radix == 0) radix = 8;
                            }
                            else if (radix == 0) radix = 10;
                        }
                        while (width > 0 && source.Peek() >= 0 && IsDigitIn((char)source.Peek(), radix)) { text.Append((char)source.Read()); width--; }
                        var digitsOnly = text.ToString().TrimStart('+', '-');
                        if (digitsOnly.Length == 0 || digitsOnly == "0x" || digitsOnly == "0X") { matched = digitsOnly.Length > 0; }
                        if (!matched) break;
                        var value = ParseInteger(text.ToString(), radix, true, 64, out _, false);
                        if (!suppress)
                        {
                            var p = args.Next32();
                            if (size == 3) memory.Write64(p, (ulong)value);
                            else if (size == 1) memory.Write16(p, (ushort)value);
                            else memory.Write32(p, (uint)value);
                            assigned++;
                        }
                        break;
                    }
                    case 'e': case 'E': case 'f': case 'g': case 'G': case 'a':
                    {
                        var text = new StringBuilder();
                        while (width > 0 && source.Peek() >= 0 && IsFloatChar((char)source.Peek(), text)) { text.Append((char)source.Read()); width--; }
                        var value = ParseDouble(text.ToString(), out var used);
                        if (used == 0) { matched = false; break; }
                        if (!suppress)
                        {
                            var p = args.Next32();
                            if (size == 2 || size == 3) memory.Write64(p, (ulong)BitConverter.DoubleToInt64Bits(value));
                            else memory.Write32(p, (uint)Bits.SingleToInt32Bits((float)value));
                            assigned++;
                        }
                        break;
                    }
                    case 's': case 'S': case 'c': case 'C': case '[':
                    {
                        var wide = size == 4 || size == 2 || (size != 1 && (conversion == 's' || conversion == 'c' || conversion == '[') == wideCall);
                        if (conversion == 'S' || conversion == 'C') wide = size == 1 ? false : size == 2 || size == 4 || !wideCall;
                        string set = null;
                        var negate = false;
                        if (conversion == '[')
                        {
                            n++;
                            if (n < format.Length && format[n] == '^') { negate = true; n++; }
                            var start = n;
                            if (n < format.Length && format[n] == ']') n++;
                            while (n < format.Length && format[n] != ']') n++;
                            set = ExpandSet(format.Substring(start, n - start));
                        }
                        var isChar = conversion == 'c' || conversion == 'C';
                        if (isChar && width == int.MaxValue) width = 1;
                        var text = new StringBuilder();
                        while (width > 0 && source.Peek() >= 0)
                        {
                            var next = (char)source.Peek();
                            if (!isChar && set == null && char.IsWhiteSpace(next)) break;
                            if (set != null && (set.IndexOf(next) >= 0) == negate) break;
                            text.Append((char)source.Read());
                            width--;
                        }
                        if (text.Length == 0) { matched = false; break; }
                        if (!suppress)
                        {
                            var p = args.Next32();
                            var s = text.ToString();
                            if (wide) { for (var k = 0; k < s.Length; k++) memory.Write16(p + (uint)k * 2, s[k]); if (!isChar) memory.Write16(p + (uint)s.Length * 2, 0); }
                            else { memory.WriteBytes(p, Ansi.Encode(s)); if (!isChar) memory.Write8(p + (uint)s.Length, 0); }
                            assigned++;
                        }
                        break;
                    }
                    default:
                        matched = false;
                        break;
                }
                if (!matched) break;
                anyConversion = true;
            }
            source.Finish();
            return (uint)assigned;
        }

        private static bool IsDigitIn(char ch, int radix)
        {
            var d = HexDigit(ch);
            return d >= 0 && d < radix;
        }

        private static bool IsFloatChar(char ch, StringBuilder sofar)
        {
            if (char.IsDigit(ch) && ch < 128) return true;
            var s = sofar.ToString();
            if (ch == '+' || ch == '-') return s.Length == 0 || s.EndsWith("e", StringComparison.OrdinalIgnoreCase);
            if (ch == '.') return s.IndexOf('.') < 0 && s.IndexOfAny(new[] { 'e', 'E' }) < 0;
            if (ch == 'e' || ch == 'E') return s.IndexOfAny(new[] { 'e', 'E' }) < 0 && s.Length > 0 && char.IsDigit(s[s.Length - 1]);
            return false;
        }

        /// <summary>A scanset with its ranges spelled out ("a-z0-9").</summary>
        private static string ExpandSet(string set)
        {
            var s = new StringBuilder();
            for (var n = 0; n < set.Length; n++)
            {
                if (n + 2 < set.Length && set[n + 1] == '-' && set[n] <= set[n + 2])
                {
                    for (var ch = set[n]; ch <= set[n + 2]; ch++) s.Append(ch);
                    n += 2;
                }
                else s.Append(set[n]);
            }
            return s.ToString();
        }

        private void InstallCrtScanf(GuestImports i)
        {
            C(i, "sscanf", 2, c => Scan(new TextSource(CString(c.Arg(0))), CString(c.Arg(1)), Args(c, 2), false));
            C(i, "sscanf_s", 2, c => Scan(new TextSource(CString(c.Arg(0))), CString(c.Arg(1)), Args(c, 2), false));
            C(i, "vsscanf", 3, c => Scan(new TextSource(CString(c.Arg(0))), CString(c.Arg(1)), VaList(c.Arg(2)), false));
            C(i, "swscanf", 2, c => Scan(new TextSource(WString(c.Arg(0))), WString(c.Arg(1)), Args(c, 2), true));
            C(i, "fscanf", 2, c => Scan(new StreamSource(this, c.Arg(0)), CString(c.Arg(1)), Args(c, 2), false));
            C(i, "fwscanf", 2, c => Scan(new StreamSource(this, c.Arg(0)), WString(c.Arg(1)), Args(c, 2), true));
            C(i, "scanf", 1, c => 0xFFFFFFFF);   // no console input
            C(i, "wscanf", 1, c => 0xFFFFFFFF);
        }

        // --- the file system -----------------------------------------------------------------------

        private void InstallCrtFileSystem(GuestImports i)
        {
            C(i, "_access", 2, c => Access(ReadText(c.Arg(0), false), c.Arg(1)));
            C(i, "_waccess", 2, c => Access(ReadText(c.Arg(0), true), c.Arg(1)));
            C(i, "_access_s", 2, c => Access(ReadText(c.Arg(0), false), c.Arg(1)) == 0 ? 0 : memory.Read32(ErrnoSlot()));
            foreach (var name in new[] { "remove", "_unlink", "unlink" })
                C(i, name, 1, c => RemovePath(ReadText(c.Arg(0), false)));
            C(i, "_wremove", 1, c => RemovePath(ReadText(c.Arg(0), true)));
            C(i, "_wunlink", 1, c => RemovePath(ReadText(c.Arg(0), true)));
            C(i, "rename", 2, c => CopyFile(ReadText(c.Arg(0), false), ReadText(c.Arg(1), false), true, true) != 0 ? 0 : RenameFailed());
            C(i, "_wrename", 2, c => CopyFile(ReadText(c.Arg(0), true), ReadText(c.Arg(1), true), true, true) != 0 ? 0 : RenameFailed());
            C(i, "_mkdir", 1, c => MakeDirectory(ReadText(c.Arg(0), false)));
            C(i, "_wmkdir", 1, c => MakeDirectory(ReadText(c.Arg(0), true)));
            C(i, "_rmdir", 1, c => RemovePath(ReadText(c.Arg(0), false)));
            C(i, "_wrmdir", 1, c => RemovePath(ReadText(c.Arg(0), true)));
            C(i, "_chdir", 1, c => ChangeDirectory(ReadText(c.Arg(0), false)));
            C(i, "_wchdir", 1, c => ChangeDirectory(ReadText(c.Arg(0), true)));
            C(i, "_getcwd", 2, c => CopyOrAllocate(CurrentDirectory.TrimEnd('\\').Length == 2 ? CurrentDirectory : CurrentDirectory.TrimEnd('\\'), c.Arg(0), c.Arg(1), false));
            C(i, "_wgetcwd", 2, c => CopyOrAllocate(CurrentDirectory.TrimEnd('\\').Length == 2 ? CurrentDirectory : CurrentDirectory.TrimEnd('\\'), c.Arg(0), c.Arg(1), true));
            C(i, "_getdcwd", 3, c => CopyOrAllocate(CurrentDirectory.TrimEnd('\\'), c.Arg(1), c.Arg(2), false));
            C(i, "_wgetdcwd", 3, c => CopyOrAllocate(CurrentDirectory.TrimEnd('\\'), c.Arg(1), c.Arg(2), true));
            C(i, "_fullpath", 3, c => CopyOrAllocate(FullPath(ReadText(c.Arg(1), false)), c.Arg(0), c.Arg(2) == 0 ? 260 : c.Arg(2), false));
            C(i, "_wfullpath", 3, c => CopyOrAllocate(FullPath(ReadText(c.Arg(1), true)), c.Arg(0), c.Arg(2) == 0 ? 260 : c.Arg(2), true));
            C(i, "_splitpath", 5, c => { SplitPath(ReadText(c.Arg(0), false), c.Arg(1), c.Arg(2), c.Arg(3), c.Arg(4), false); return 0; });
            C(i, "_wsplitpath", 5, c => { SplitPath(ReadText(c.Arg(0), true), c.Arg(1), c.Arg(2), c.Arg(3), c.Arg(4), true); return 0; });
            C(i, "_makepath", 5, c => { MakePath(c, false); return 0; });
            C(i, "_wmakepath", 5, c => { MakePath(c, true); return 0; });
            C(i, "_getdrive", 0, c => 3);   // C:
            C(i, "_chdrive", 1, c => c.Arg(0) == 3 ? 0u : 0xFFFFFFFF);
            C(i, "_chmod", 2, c => Access(ReadText(c.Arg(0), false), 0));
            C(i, "_wchmod", 2, c => Access(ReadText(c.Arg(0), true), 0));
            C(i, "_utime", 2, c => Access(ReadText(c.Arg(0), false), 0));
            C(i, "_umask", 1, c => 0);

            C(i, "_stat", 2, c => StatPath(ReadText(c.Arg(0), false), c.Arg(1), 32));
            C(i, "_stat32", 2, c => StatPath(ReadText(c.Arg(0), false), c.Arg(1), 32));
            C(i, "_wstat", 2, c => StatPath(ReadText(c.Arg(0), true), c.Arg(1), 32));
            C(i, "_stati64", 2, c => StatPath(ReadText(c.Arg(0), false), c.Arg(1), 3264));
            C(i, "_stat32i64", 2, c => StatPath(ReadText(c.Arg(0), false), c.Arg(1), 3264));
            C(i, "_wstati64", 2, c => StatPath(ReadText(c.Arg(0), true), c.Arg(1), 3264));
            C(i, "_stat64", 2, c => StatPath(ReadText(c.Arg(0), false), c.Arg(1), 64));
            C(i, "_wstat64", 2, c => StatPath(ReadText(c.Arg(0), true), c.Arg(1), 64));
            C(i, "_fstat", 2, c => StatFd((int)c.Arg(0), c.Arg(1), 32));
            C(i, "_fstat32", 2, c => StatFd((int)c.Arg(0), c.Arg(1), 32));
            C(i, "_fstati64", 2, c => StatFd((int)c.Arg(0), c.Arg(1), 3264));
            C(i, "_fstat64", 2, c => StatFd((int)c.Arg(0), c.Arg(1), 64));

            C(i, "_findfirst", 2, c => FindFirstCrt(ReadText(c.Arg(0), false), c.Arg(1), false, false));
            C(i, "_findfirst32", 2, c => FindFirstCrt(ReadText(c.Arg(0), false), c.Arg(1), false, false));
            C(i, "_findfirsti64", 2, c => FindFirstCrt(ReadText(c.Arg(0), false), c.Arg(1), false, true));
            C(i, "_wfindfirst", 2, c => FindFirstCrt(ReadText(c.Arg(0), true), c.Arg(1), true, false));
            C(i, "_wfindfirsti64", 2, c => FindFirstCrt(ReadText(c.Arg(0), true), c.Arg(1), true, true));
            C(i, "_findnext", 2, c => FindNextCrt((int)c.Arg(0), c.Arg(1), false, false));
            C(i, "_findnext32", 2, c => FindNextCrt((int)c.Arg(0), c.Arg(1), false, false));
            C(i, "_findnexti64", 2, c => FindNextCrt((int)c.Arg(0), c.Arg(1), false, true));
            C(i, "_wfindnext", 2, c => FindNextCrt((int)c.Arg(0), c.Arg(1), true, false));
            C(i, "_wfindnexti64", 2, c => FindNextCrt((int)c.Arg(0), c.Arg(1), true, true));
            C(i, "_findclose", 1, c => crtFinds.Remove((int)c.Arg(0)) ? 0u : 0xFFFFFFFF);
        }

        private uint Access(string path, uint mode)
        {
            var entry = Files.Stat(FullPath(path));
            if (entry == null) { SetErrno(Enoent); return 0xFFFFFFFF; }
            if ((mode & 2) != 0 && (entry.Attributes & FileAttributes.ReadOnly) != 0) { SetErrno(Eacces); return 0xFFFFFFFF; }
            return 0;
        }

        private uint RemovePath(string path)
        {
            if (Files.Delete(FullPath(path))) return 0;
            SetErrno(Enoent);
            return 0xFFFFFFFF;
        }

        private uint RenameFailed()
        {
            SetErrno(process.LastError == ErrorFileNotFound ? Enoent : Eacces);
            return 0xFFFFFFFF;
        }

        private uint MakeDirectory(string path)
        {
            path = FullPath(path);
            if (Files.Stat(path) != null) { SetErrno(Eexist); return 0xFFFFFFFF; }
            if (Files.CreateDirectory(path)) return 0;
            SetErrno(Enoent);
            return 0xFFFFFFFF;
        }

        private uint ChangeDirectory(string path)
        {
            path = FullPath(path);
            var entry = Files.Stat(path);
            if (entry == null || (entry.Attributes & FileAttributes.Directory) == 0) { SetErrno(Enoent); return 0xFFFFFFFF; }
            currentDirectory = path.EndsWith("\\", StringComparison.Ordinal) ? path : path + "\\";
            return 0;
        }

        /// <summary>_getcwd and _fullpath: a NULL buffer is allocated with malloc (at least the size asked).</summary>
        private uint CopyOrAllocate(string text, uint buffer, uint size, bool wide)
        {
            var unit = wide ? 2u : 1u;
            if (buffer == 0)
            {
                buffer = CrtAlloc(Math.Max(size, (uint)text.Length + 1) * unit, false);
                if (buffer == 0) return 0;
            }
            else if (size < text.Length + 1) { SetErrno(Erange); return 0; }
            WriteText(buffer, text, wide);
            return buffer;
        }

        private void SplitPath(string path, uint drive, uint dir, uint name, uint ext, bool wide)
        {
            var d = path.Length >= 2 && path[1] == ':' ? path.Substring(0, 2) : "";
            var rest = path.Substring(d.Length);
            var slash = rest.LastIndexOfAny(new[] { '\\', '/' });
            var folder = slash >= 0 ? rest.Substring(0, slash + 1) : "";
            var file = rest.Substring(slash + 1);
            var dot = file.LastIndexOf('.');
            var baseName = dot >= 0 ? file.Substring(0, dot) : file;
            var extension = dot >= 0 ? file.Substring(dot) : "";
            if (drive != 0) WriteText(drive, d, wide);
            if (dir != 0) WriteText(dir, folder, wide);
            if (name != 0) WriteText(name, baseName, wide);
            if (ext != 0) WriteText(ext, extension, wide);
        }

        private void MakePath(GuestCall c, bool wide)
        {
            var s = new StringBuilder();
            var drive = ReadText(c.Arg(1), wide);
            if (drive.Length > 0) s.Append(drive[0]).Append(':');
            var dir = ReadText(c.Arg(2), wide);
            if (dir.Length > 0) { s.Append(dir); if (!dir.EndsWith("\\", StringComparison.Ordinal) && !dir.EndsWith("/", StringComparison.Ordinal)) s.Append('\\'); }
            s.Append(ReadText(c.Arg(3), wide));
            var ext = ReadText(c.Arg(4), wide);
            if (ext.Length > 0) { if (ext[0] != '.') s.Append('.'); s.Append(ext); }
            WriteText(c.Arg(0), s.ToString(), wide);
        }

        private static uint Mode(GuestFileEntry e)
        {
            var mode = (e.Attributes & FileAttributes.Directory) != 0 ? 0x4000u | 0x49 : 0x8000u;   // _S_IFDIR (+exec), _S_IFREG
            mode |= 0x124;                                                                        // read for all
            if ((e.Attributes & FileAttributes.ReadOnly) == 0) mode |= 0x92;                    // write for all
            return mode;
        }

        /// <summary>struct _stat (32), _stati64 (3264: 64-bit size, 32-bit times) or _stat64 (64).</summary>
        private void WriteStat(uint p, GuestFileEntry e, int layout)
        {
            var size = layout == 32 ? 36 : layout == 3264 ? 48 : 56;
            memory.WriteBytes(p, new byte[size]);
            var time = (long)Math.Max(0, (e.WriteTimeUtc - Epoch).TotalSeconds);
            memory.Write32(p, 2);                 // st_dev: drive C
            memory.Write16(p + 6, (ushort)Mode(e));
            memory.Write16(p + 8, 1);             // st_nlink
            memory.Write32(p + 16, 2);            // st_rdev
            if (layout == 32)
            {
                memory.Write32(p + 20, (uint)Math.Min(e.Size, int.MaxValue));
                memory.Write32(p + 24, (uint)time); memory.Write32(p + 28, (uint)time); memory.Write32(p + 32, (uint)time);
            }
            else if (layout == 3264)
            {
                memory.Write64(p + 24, (ulong)e.Size);
                memory.Write32(p + 32, (uint)time); memory.Write32(p + 36, (uint)time); memory.Write32(p + 40, (uint)time);
            }
            else
            {
                memory.Write64(p + 24, (ulong)e.Size);
                memory.Write64(p + 32, (ulong)time); memory.Write64(p + 40, (ulong)time); memory.Write64(p + 48, (ulong)time);
            }
        }

        private uint StatPath(string path, uint buffer, int layout)
        {
            var entry = Files.Stat(FullPath(path.TrimEnd('\\', '/').Length == 2 ? path : path.TrimEnd('\\', '/')));
            if (entry == null) { SetErrno(Enoent); return 0xFFFFFFFF; }
            WriteStat(buffer, entry, layout);
            return 0;
        }

        private uint StatFd(int fd, uint buffer, int layout)
        {
            if (!fds.TryGetValue(fd, out var f)) { SetErrno(Ebadf); return 0xFFFFFFFF; }
            var entry = files.TryGetValue(f.Handle, out var file) ? Files.Stat(file.Path) : null;
            if (entry == null) entry = new GuestFileEntry { Name = "", Size = 0, WriteTimeUtc = UtcNow };
            if (FdStream(f) is Stream s) entry = new GuestFileEntry { Name = entry.Name, Attributes = entry.Attributes, Size = s.Length, WriteTimeUtc = entry.WriteTimeUtc };
            WriteStat(buffer, entry, layout);
            if (IsConsole(f)) memory.Write16(buffer + 6, 0x2000);   // _S_IFCHR
            return 0;
        }

        private uint FindFirstCrt(string pattern, uint data, bool wide, bool size64)
        {
            var full = FullPath(pattern);
            var slash = full.LastIndexOf('\\');
            var folder = slash >= 0 ? full.Substring(0, slash) : CurrentDirectory;
            var mask = slash >= 0 ? full.Substring(slash + 1) : full;
            var entries = Files.List(folder);
            var queue = new Queue<GuestFileEntry>();
            if (entries != null)
            {
                if (mask == "*" || mask == "*.*")
                {
                    queue.Enqueue(new GuestFileEntry { Name = ".", Attributes = FileAttributes.Directory, WriteTimeUtc = UtcNow });
                    queue.Enqueue(new GuestFileEntry { Name = "..", Attributes = FileAttributes.Directory, WriteTimeUtc = UtcNow });
                }
                foreach (var e in entries) if (Wildcard(mask, e.Name)) queue.Enqueue(e);
            }
            if (queue.Count == 0) { SetErrno(Enoent); return 0xFFFFFFFF; }
            var handle = nextFind++;
            crtFinds[handle] = queue;
            WriteFindCrt(data, queue.Dequeue(), wide, size64);
            return (uint)handle;
        }

        private uint FindNextCrt(int handle, uint data, bool wide, bool size64)
        {
            if (!crtFinds.TryGetValue(handle, out var queue)) { SetErrno(Einval); return 0xFFFFFFFF; }
            if (queue.Count == 0) { SetErrno(Enoent); return 0xFFFFFFFF; }
            WriteFindCrt(data, queue.Dequeue(), wide, size64);
            return 0;
        }

        /// <summary>_finddata_t: attrib, three 32-bit times, then the size (32-bit, or 64-bit at 16) and the name.</summary>
        private void WriteFindCrt(uint p, GuestFileEntry e, bool wide, bool size64)
        {
            var time = (uint)Math.Max(0, (e.WriteTimeUtc - Epoch).TotalSeconds);
            memory.Write32(p, Win32Attributes(e));
            memory.Write32(p + 4, time); memory.Write32(p + 8, time); memory.Write32(p + 12, time);
            uint nameAt;
            if (size64) { memory.Write64(p + 16, (ulong)e.Size); nameAt = p + 24; }
            else { memory.Write32(p + 16, (uint)Math.Min(e.Size, uint.MaxValue)); nameAt = p + 20; }
            var name = e.Name.Length > 259 ? e.Name.Substring(0, 259) : e.Name;
            WriteText(nameAt, name, wide);
        }
    }
}
