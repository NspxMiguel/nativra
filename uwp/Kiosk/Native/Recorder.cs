using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace Kiosk.Native
{
    /// <summary>
    /// Records what the game draws to an MP4 on the console.
    ///
    /// Screenshots pulled through Device Portal arrive at about 17 a second, at
    /// uneven intervals, and the load of taking them shows in the game: no use
    /// for showing how a game runs. The mirror already copies every finished
    /// frame to the CPU to put it on screen, so the recorder takes those same
    /// frames, stamps each with the time it was drawn, and hands them to the
    /// console's H.264 encoder through a MediaStreamSource. Only the game's own
    /// picture is recorded; the app's overlays are not part of it.
    ///
    /// Started by a record.txt in LocalState holding a length in seconds (0 or
    /// empty records until stopped). It waits for a game if none is drawing
    /// yet, so it can be armed before a launch. record-stop.txt, or the game
    /// ending, stops it. Files land in LocalState\recordings, and what happened
    /// is written to recorder-note.txt.
    /// </summary>
    internal static class Recorder
    {
        private const int PoolSize = 8;
        private const uint Bitrate = 16000000;
        private const string StartFile = "record.txt";
        private const string StopFile = "record-stop.txt";

        // MF_MT_DEFAULT_STRIDE. Media Foundation reads uncompressed RGB as
        // bottom-up unless the stride is given as positive.
        private static readonly Guid DefaultStride = new Guid("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");

        private struct Frame
        {
            public byte[] Pixels;
            public TimeSpan At;
        }

        private static readonly object gate = new object();
        private static readonly Queue<Frame> ready = new Queue<Frame>();
        private static readonly Stack<byte[]> free = new Stack<byte[]>();
        private static MediaStreamSourceSampleRequest pending;
        private static MediaStreamSourceSampleRequestDeferral pendingDeferral;
        private static bool active;
        private static bool ending;
        private static bool watching;
        private static int frameWidth;
        private static int frameHeight;
        private static long firstTicks;

        public static long Frames;
        public static long Dropped;
        public static string Note = "idle";

        public static bool Active => active;

        /// <summary>
        /// Takes one frame from the mirror: BGRA, opaque, wide by high. Copied
        /// at once, so the caller may reuse its buffer. Dropped, and counted,
        /// when the encoder has every pooled buffer.
        /// </summary>
        public static void Offer(byte[] pixels, int wide, int high)
        {
            if (!active || ending) return;
            if (wide != frameWidth || high != frameHeight)
            {
                Dropped++;
                return;
            }

            var now = Stopwatch.GetTimestamp();
            byte[] copy;
            lock (gate)
            {
                if (free.Count == 0)
                {
                    Dropped++;
                    return;
                }
                copy = free.Pop();
            }
            Buffer.BlockCopy(pixels, 0, copy, 0, copy.Length);

            if (firstTicks == 0) firstTicks = now;
            var frame = new Frame
            {
                Pixels = copy,
                At = TimeSpan.FromTicks((long)((now - firstTicks) * (TimeSpan.TicksPerSecond / (double)Stopwatch.Frequency))),
            };

            MediaStreamSourceSampleRequest request;
            MediaStreamSourceSampleRequestDeferral deferral;
            lock (gate)
            {
                if (pending == null)
                {
                    ready.Enqueue(frame);
                    return;
                }
                request = pending;
                deferral = pendingDeferral;
                pending = null;
                pendingDeferral = null;
            }
            Serve(request, frame);
            deferral.Complete();
        }

        private static void Serve(MediaStreamSourceSampleRequest request, Frame frame)
        {
            var pixels = frame.Pixels;
            var sample = MediaStreamSample.CreateFromBuffer(pixels.AsBuffer(), frame.At);
            // The buffer goes back to the pool once the encoder is done with it.
            sample.Processed += (sender, args) =>
            {
                lock (gate) free.Push(pixels);
            };
            request.Sample = sample;
            Frames++;
        }

        private static void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
        {
            var request = args.Request;
            Frame frame;
            lock (gate)
            {
                if (ready.Count > 0)
                {
                    frame = ready.Dequeue();
                }
                else if (ending)
                {
                    // No sample is the end of the stream: the file is finished.
                    request.Sample = null;
                    return;
                }
                else
                {
                    pending = request;
                    pendingDeferral = request.GetDeferral();
                    return;
                }
            }
            Serve(request, frame);
        }

        /// <summary>Finishes the file with the frames already taken.</summary>
        public static void Stop()
        {
            MediaStreamSourceSampleRequest request = null;
            MediaStreamSourceSampleRequestDeferral deferral = null;
            lock (gate)
            {
                if (!active || ending) return;
                ending = true;
                if (pending != null)
                {
                    request = pending;
                    deferral = pendingDeferral;
                    pending = null;
                    pendingDeferral = null;
                }
            }
            if (request != null)
            {
                request.Sample = null;
                deferral.Complete();
            }
        }

        /// <summary>Starts a recording of the game on screen; a zero limit runs until stopped.</summary>
        public static async Task<bool> StartAsync(TimeSpan limit)
        {
            if (active) return false;
            var wide = FrameMirror.PixelWidth;
            var high = FrameMirror.PixelHeight;
            if (!FrameMirror.Running || wide <= 0 || high <= 0)
            {
                Note = "no game frames to record";
                await WriteNoteAsync();
                return false;
            }

            try
            {
                var folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(
                    "recordings", CreationCollisionOption.OpenIfExists);
                var file = await folder.CreateFileAsync(
                    "nativra-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".mp4",
                    CreationCollisionOption.GenerateUniqueName);

                var input = VideoEncodingProperties.CreateUncompressed(
                    MediaEncodingSubtypes.Bgra8, (uint)wide, (uint)high);
                input.FrameRate.Numerator = 60;
                input.FrameRate.Denominator = 1;
                input.Properties[DefaultStride] = (uint)(wide * 4);
                var source = new MediaStreamSource(new VideoStreamDescriptor(input));
                source.BufferTime = TimeSpan.Zero;
                source.SampleRequested += OnSampleRequested;

                var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
                profile.Audio = null;
                profile.Video.Width = (uint)wide;
                profile.Video.Height = (uint)high;
                profile.Video.FrameRate.Numerator = 60;
                profile.Video.FrameRate.Denominator = 1;
                profile.Video.Bitrate = Bitrate;

                var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
                var transcoder = new MediaTranscoder
                {
                    HardwareAccelerationEnabled = true,
                    AlwaysReencode = true,
                };
                var prepared = await transcoder.PrepareMediaStreamSourceTranscodeAsync(source, stream, profile);
                if (!prepared.CanTranscode)
                {
                    source.SampleRequested -= OnSampleRequested;
                    stream.Dispose();
                    await file.DeleteAsync();
                    Note = "cannot encode: " + prepared.FailureReason;
                    await WriteNoteAsync();
                    return false;
                }

                lock (gate)
                {
                    ready.Clear();
                    free.Clear();
                    pending = null;
                    pendingDeferral = null;
                    for (var i = 0; i < PoolSize; i++) free.Push(new byte[wide * high * 4]);
                    frameWidth = wide;
                    frameHeight = high;
                    firstTicks = 0;
                    Frames = 0;
                    Dropped = 0;
                    ending = false;
                    active = true;
                }

                var name = file.Name;
                Note = "recording " + name + " " + wide + "x" + high;
                await WriteNoteAsync();

                var encoding = Task.Run(async () =>
                {
                    try
                    {
                        await prepared.TranscodeAsync();
                        await stream.FlushAsync();
                        Note = "saved recordings\\" + name + " frames=" + Frames + " dropped=" + Dropped;
                    }
                    catch (Exception error)
                    {
                        Note = "encode failed: " + error.GetType().Name + " 0x" + error.HResult.ToString("X8")
                            + " frames=" + Frames + " dropped=" + Dropped;
                    }
                    finally
                    {
                        stream.Dispose();
                        source.SampleRequested -= OnSampleRequested;
                        lock (gate)
                        {
                            active = false;
                            ending = false;
                            ready.Clear();
                            free.Clear();
                        }
                        await WriteNoteAsync();
                    }
                });

                if (limit > TimeSpan.Zero)
                {
                    var timer = Task.Run(async () =>
                    {
                        await Task.Delay(limit);
                        Stop();
                    });
                }
                return true;
            }
            catch (Exception error)
            {
                Note = "start failed: " + error.GetType().Name + " 0x" + error.HResult.ToString("X8") + " " + error.Message;
                await WriteNoteAsync();
                return false;
            }
        }

        /// <summary>
        /// Follows the start and stop files once a second, for as long as the
        /// app runs. Recording is a development tool, driven from outside.
        /// </summary>
        public static void Watch()
        {
            if (watching) return;
            watching = true;
            var loop = Task.Run(async () =>
            {
                var local = ApplicationData.Current.LocalFolder;
                while (true)
                {
                    try
                    {
                        if (!active)
                        {
                            // Left in place until a game draws, so it can be
                            // armed before the launch.
                            if (FrameMirror.Running && await local.TryGetItemAsync(StartFile) is StorageFile ask)
                            {
                                var text = (await FileIO.ReadTextAsync(ask)).Trim();
                                await ask.DeleteAsync();
                                int seconds;
                                int.TryParse(text, out seconds);
                                await StartAsync(TimeSpan.FromSeconds(Math.Max(0, seconds)));
                            }
                        }
                        else if (await local.TryGetItemAsync(StopFile) is StorageFile stop)
                        {
                            await stop.DeleteAsync();
                            Stop();
                        }
                        else if (!NativeProbe.GameRunning || !FrameMirror.Running)
                        {
                            Stop();
                        }
                    }
                    catch
                    {
                        // A missed poll is caught by the next one.
                    }
                    await Task.Delay(1000);
                }
            });
        }

        private static async Task WriteNoteAsync()
        {
            try
            {
                var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    "recorder-note.txt", CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + Note + "\r\n");
            }
            catch
            {
                // Diagnostics only.
            }
        }
    }
}
