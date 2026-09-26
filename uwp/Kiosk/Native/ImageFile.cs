using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Kiosk.Native
{
    /// <summary>
    /// Where <see cref="PeImage"/> reads an image from, without holding the
    /// whole file in memory: the headers come back as a small array, and each
    /// section is read straight into its place in the reserved image. A game
    /// module can be hundreds of megabytes (Unreal, Unity's UnityPlayer and
    /// GameAssembly), and the old path held it two or three times over (the
    /// WinRT buffer, a managed copy on the large-object heap, a native staging
    /// copy) on top of the mapped image, inside the console's 5 GB budget.
    /// </summary>
    public abstract class ImageFile : IDisposable
    {
        /// <summary>Largest read issued at once, so no single call needs a huge buffer.</summary>
        protected const int Chunk = 4 * 1024 * 1024;

        public abstract long Length { get; }

        /// <summary>Reads <paramref name="count"/> bytes at <paramref name="offset"/> into native memory.</summary>
        public abstract void Read(long offset, IntPtr destination, int count);

        public byte[] ReadBytes(long offset, int count)
        {
            count = (int)Math.Max(0, Math.Min(count, Length - offset));
            var bytes = new byte[count];
            if (count == 0) return bytes;
            var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                Read(offset, handle.AddrOfPinnedObject(), count);
            }
            finally
            {
                handle.Free();
            }
            return bytes;
        }

        public abstract void Dispose();

        /// <summary>
        /// Opens a file by path with the FromApp form of CreateFile, which
        /// reaches a game on a USB drive through the broker; null when that
        /// is refused (the caller then falls back to a WinRT stream).
        /// </summary>
        public static ImageFile TryOpen(string path)
        {
            var handle = CreateFileFromAppW(path, GenericRead, ShareRead, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return null;
            if (!GetFileSizeEx(handle, out var size))
            {
                CloseHandle(handle);
                return null;
            }
            return new HandleFile(handle, size);
        }

        /// <summary>Reads through a seekable stream (a StorageFile opened for reading).</summary>
        public static ImageFile FromStream(Stream stream) => new StreamFile(stream);

        /// <summary>An image already in memory (tests, and callers that have one anyway).</summary>
        public static ImageFile FromBytes(byte[] bytes) => new BytesFile(bytes);

        private const uint GenericRead = 0x80000000, ShareRead = 1, OpenExisting = 3;

        [DllImport("api-ms-win-core-file-fromapp-l1-1-0.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileFromAppW(string name, uint access, uint share, IntPtr security,
            uint disposition, uint flags, IntPtr template);

        [DllImport("api-ms-win-core-file-l1-1-0.dll", SetLastError = true)]
        private static extern bool GetFileSizeEx(IntPtr file, out long size);

        [DllImport("api-ms-win-core-file-l1-1-0.dll", SetLastError = true)]
        private static extern bool SetFilePointerEx(IntPtr file, long distance, IntPtr newPosition, uint method);

        [DllImport("api-ms-win-core-file-l1-1-0.dll", SetLastError = true)]
        private static extern bool ReadFile(IntPtr file, IntPtr buffer, uint count, out uint read, IntPtr overlapped);

        [DllImport("api-ms-win-core-handle-l1-1-0.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        private sealed class HandleFile : ImageFile
        {
            private IntPtr handle;
            private readonly long length;

            public HandleFile(IntPtr handle, long length)
            {
                this.handle = handle;
                this.length = length;
            }

            public override long Length => length;

            public override void Read(long offset, IntPtr destination, int count)
            {
                if (!SetFilePointerEx(handle, offset, IntPtr.Zero, 0))
                    throw new IOException("seek failed (" + Marshal.GetLastWin32Error() + ")");
                var done = 0;
                while (done < count)
                {
                    var want = (uint)Math.Min(count - done, Chunk);
                    if (!ReadFile(handle, destination + done, want, out var got, IntPtr.Zero))
                        throw new IOException("read failed (" + Marshal.GetLastWin32Error() + ")");
                    if (got == 0) throw new EndOfStreamException("image file ends early");
                    done += (int)got;
                }
            }

            public override void Dispose()
            {
                if (handle != IntPtr.Zero) CloseHandle(handle);
                handle = IntPtr.Zero;
            }
        }

        private sealed class StreamFile : ImageFile
        {
            private readonly Stream stream;
            private byte[] buffer;   // one small bounce buffer, reused for every read

            public StreamFile(Stream stream)
            {
                this.stream = stream;
            }

            public override long Length => stream.Length;

            public override void Read(long offset, IntPtr destination, int count)
            {
                if (buffer == null) buffer = new byte[1 << 20];
                stream.Position = offset;
                var done = 0;
                while (done < count)
                {
                    var got = stream.Read(buffer, 0, Math.Min(buffer.Length, count - done));
                    if (got <= 0) throw new EndOfStreamException("image file ends early");
                    Marshal.Copy(buffer, 0, destination + done, got);
                    done += got;
                }
            }

            public override void Dispose()
            {
                buffer = null;
                stream.Dispose();
            }
        }

        private sealed class BytesFile : ImageFile
        {
            private readonly byte[] bytes;

            public BytesFile(byte[] bytes)
            {
                this.bytes = bytes;
            }

            public override long Length => bytes.Length;

            public override void Read(long offset, IntPtr destination, int count)
            {
                if (offset + count > bytes.Length) throw new EndOfStreamException("image file ends early");
                Marshal.Copy(bytes, (int)offset, destination, count);
            }

            public override void Dispose()
            {
            }
        }
    }
}
