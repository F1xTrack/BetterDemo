using BetterDemo.Core.Contracts;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;

namespace BetterDemo.Interop.MediaFoundation;

[SupportedOSPlatform("windows")]
internal sealed class MediaFoundationVideoSource : IVideoSource
{
    private readonly string symbolicLink;
    private readonly VideoFrameFormat format;
    private readonly Channel<VideoFrame> frames = Channel.CreateBounded<VideoFrame>(new BoundedChannelOptions(2)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = false,
        SingleWriter = true
    });
    private readonly object stateGate = new();
    private CancellationTokenSource? cancellation;
    private Task? readerTask;
    private IMFMediaSource? mediaSource;
    private IMFSourceReader? sourceReader;
    private SourceState state = SourceState.Created;
    private bool readerRetired;
    private int acceptingFrames;

    public MediaFoundationVideoSource(VideoDeviceDescriptor device, string symbolicLink, VideoFrameFormat format)
    {
        Device = device;
        this.symbolicLink = symbolicLink;
        this.format = format;
    }

    public VideoDeviceDescriptor Device { get; }

    public SourceState State
    {
        get
        {
            lock (stateGate)
            {
                return state;
            }
        }
    }

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        lock (stateGate)
        {
            ObjectDisposedException.ThrowIf(state == SourceState.Disposed, this);
            if (readerRetired)
            {
                throw new InvalidOperationException("A timed-out camera reader is terminal and cannot be restarted.");
            }
            if (state is SourceState.Running or SourceState.Starting)
            {
                return;
            }

            state = SourceState.Starting;
        }

        cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        readerTask = Task.Factory.StartNew(
            () => ReadLoop(started, cancellation.Token),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        await started.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        Task? task;
        lock (stateGate)
        {
            if (state is SourceState.Disposed or SourceState.Stopped or SourceState.Created)
            {
                if (state == SourceState.Created)
                {
                    state = SourceState.Stopped;
                }
                return;
            }

            if (readerRetired)
            {
                state = SourceState.Unavailable;
                return;
            }

            state = SourceState.Stopping;
            task = readerTask;
        }

        Volatile.Write(ref acceptingFrames, 0);
        cancellation?.Cancel();
        sourceReader?.Flush(MediaFoundationNative.SourceReaderAllStreams);
        mediaSource?.Shutdown();
        var terminalState = SourceState.Stopped;
        try
        {
            if (task is not null)
            {
                terminalState = await WaitForReaderTerminalStateAsync(
                    task,
                    TimeSpan.FromSeconds(2),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            lock (stateGate)
            {
                state = SourceState.Unavailable;
            }
            throw;
        }

        DrainFrames();
        lock (stateGate)
        {
            if (state != SourceState.Disposed)
            {
                readerRetired = terminalState == SourceState.Unavailable;
                state = terminalState;
            }
        }
    }

    public async IAsyncEnumerable<VideoFrame> ReadFramesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await frames.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (frames.Reader.TryRead(out var frame))
            {
                yield return frame;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (State == SourceState.Disposed)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        lock (stateGate)
        {
            state = SourceState.Disposed;
        }
        frames.Writer.TryComplete();
        cancellation?.Dispose();
    }

    private void ReadLoop(TaskCompletionSource started, CancellationToken cancellationToken)
    {
        var comInitialized = MediaFoundationNative.CoInitializeEx(0, 0) >= 0;
        IMFActivate? activate = null;
        IMFSourceReader? reader = null;
        IMFMediaType? mediaType = null;
        IMFAttributes? readerAttributes = null;
        try
        {
            MediaFoundationNative.ThrowIfFailed(
                MediaFoundationNative.MFStartup(MediaFoundationNative.MfVersion, MediaFoundationNative.MfStartupFull),
                "MFStartup");
            activate = FindActivation(symbolicLink, cancellationToken);
            MediaFoundationNative.ThrowIfFailed(
                activate.ActivateObject(MediaFoundationNative.ImfMediaSourceGuid, out var sourceObject),
                "IMFActivate.ActivateObject");
            mediaSource = (IMFMediaSource)sourceObject;
            MediaFoundationNative.ThrowIfFailed(
                MediaFoundationNative.MFCreateAttributes(out readerAttributes, 1),
                "MFCreateAttributes(source reader)");
            MediaFoundationNative.ThrowIfFailed(
                readerAttributes.SetUINT32(MediaFoundationNative.MfSourceReaderEnableAdvancedVideoProcessing, 1),
                "IMFAttributes.SetUINT32(advanced video processing)");
            MediaFoundationNative.ThrowIfFailed(
                MediaFoundationNative.MFCreateSourceReaderFromMediaSource(mediaSource, readerAttributes, out reader),
                "MFCreateSourceReaderFromMediaSource");
            sourceReader = reader;
            MediaFoundationNative.ThrowIfFailed(
                reader.SetStreamSelection(MediaFoundationNative.SourceReaderAllStreams, false),
                "IMFSourceReader.SetStreamSelection(all)");
            MediaFoundationNative.ThrowIfFailed(
                reader.SetStreamSelection(MediaFoundationNative.SourceReaderFirstVideoStream, true),
                "IMFSourceReader.SetStreamSelection(video)");
            MediaFoundationNative.ThrowIfFailed(MediaFoundationNative.MFCreateMediaType(out mediaType), "MFCreateMediaType");
            MediaFoundationNative.ThrowIfFailed(
                mediaType.SetGUID(MediaFoundationNative.MfMtMajorType, MediaFoundationNative.MfMediaTypeVideo),
                "IMFMediaType.SetGUID(major)");
            var subtype = format.PixelFormat == VideoPixelFormat.Nv12
                ? MediaFoundationNative.MfVideoFormatNv12
                : MediaFoundationNative.MfVideoFormatRgb32;
            MediaFoundationNative.ThrowIfFailed(
                mediaType.SetGUID(MediaFoundationNative.MfMtSubtype, subtype),
                "IMFMediaType.SetGUID(subtype)");
            var packedSize = ((ulong)(uint)format.Width << 32) | (uint)format.Height;
            MediaFoundationNative.ThrowIfFailed(
                mediaType.SetUINT64(MediaFoundationNative.MfMtFrameSize, packedSize),
                "IMFMediaType.SetUINT64(frame size)");
            MediaFoundationNative.ThrowIfFailed(
                reader.SetCurrentMediaType(MediaFoundationNative.SourceReaderFirstVideoStream, 0, mediaType),
                "IMFSourceReader.SetCurrentMediaType");

            if (!MediaFoundationNative.QueryPerformanceFrequency(out var frequency))
            {
                throw new InvalidOperationException("QueryPerformanceFrequency failed.");
            }

            SetState(SourceState.Running);
            Volatile.Write(ref acceptingFrames, 1);
            started.TrySetResult();
            ulong sequence = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = reader.ReadSample(
                    MediaFoundationNative.SourceReaderFirstVideoStream,
                    0,
                    out _,
                    out var streamFlags,
                    out _,
                    out var sample);
                try
                {
                    MediaFoundationNative.ThrowIfFailed(result, "IMFSourceReader.ReadSample");
                    if ((streamFlags & (MediaFoundationNative.SourceReaderFlagError | MediaFoundationNative.SourceReaderFlagEndOfStream)) != 0)
                    {
                        throw new COMException("The camera stopped delivering frames.");
                    }

                    if (sample is null)
                    {
                        continue;
                    }

                    if (!MediaFoundationNative.QueryPerformanceCounter(out var ticks))
                    {
                        throw new InvalidOperationException("QueryPerformanceCounter failed.");
                    }

                    Enqueue(CopySample(
                        sample,
                        new VideoFrameStamp(++sequence, new QpcTimestamp(ticks, frequency))));
                }
                finally
                {
                    MediaFoundationNative.FinalRelease(sample);
                }
            }
        }
        catch (Exception exception)
        {
            started.TrySetException(exception);
            if (!cancellationToken.IsCancellationRequested)
            {
                SetState(SourceState.Unavailable);
            }
        }
        finally
        {
            Volatile.Write(ref acceptingFrames, 0);
            mediaSource?.Shutdown();
            MediaFoundationNative.FinalRelease(mediaType);
            MediaFoundationNative.FinalRelease(readerAttributes);
            MediaFoundationNative.FinalRelease(reader);
            MediaFoundationNative.FinalRelease(mediaSource);
            MediaFoundationNative.FinalRelease(activate);
            mediaSource = null;
            sourceReader = null;
            MediaFoundationNative.MFShutdown();
            if (comInitialized)
            {
                MediaFoundationNative.CoUninitialize();
            }

            if (!started.Task.IsCompleted)
            {
                started.TrySetException(new InvalidOperationException("Camera initialization ended unexpectedly."));
            }
        }
    }

    private static IMFActivate FindActivation(string expectedSymbolicLink, CancellationToken cancellationToken)
    {
        IMFAttributes? attributes = null;
        nint activateArray = 0;
        uint count = 0;
        try
        {
            MediaFoundationNative.ThrowIfFailed(MediaFoundationNative.MFCreateAttributes(out attributes, 1), "MFCreateAttributes");
            MediaFoundationNative.ThrowIfFailed(
                attributes.SetGUID(
                    MediaFoundationNative.MfDevSourceAttributeSourceType,
                    MediaFoundationNative.MfDevSourceAttributeSourceTypeVideoCaptureGuid),
                "IMFAttributes.SetGUID");
            MediaFoundationNative.ThrowIfFailed(
                MediaFoundationNative.MFEnumDeviceSources(attributes, out activateArray, out count),
                "MFEnumDeviceSources");
            for (var index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var unknown = Marshal.ReadIntPtr(activateArray, checked((int)(index * (uint)nint.Size)));
                var activate = (IMFActivate)Marshal.GetObjectForIUnknown(unknown);
                Marshal.Release(unknown);
                Marshal.WriteIntPtr(activateArray, checked((int)(index * (uint)nint.Size)), 0);
                var symbolicLink = MediaFoundationNative.GetAllocatedString(
                    activate,
                    MediaFoundationNative.MfDevSourceAttributeSourceTypeVidcapSymbolicLink);
                if (string.Equals(symbolicLink, expectedSymbolicLink, StringComparison.Ordinal))
                {
                    return activate;
                }

                MediaFoundationNative.FinalRelease(activate);
            }

            throw new InvalidOperationException("The selected camera is no longer present.");
        }
        finally
        {
            if (activateArray != 0)
            {
                ReleaseRemainingPointers(activateArray, 0, count);
                Marshal.FreeCoTaskMem(activateArray);
            }
            MediaFoundationNative.FinalRelease(attributes);
        }
    }

    internal static async ValueTask<SourceState> WaitForReaderTerminalStateAsync(
        Task readerTask,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));

        var timeoutTask = Task.Delay(timeout, cancellationToken);
        if (await Task.WhenAny(readerTask, timeoutTask).ConfigureAwait(false) != readerTask)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return SourceState.Unavailable;
        }

        await readerTask.ConfigureAwait(false);
        return SourceState.Stopped;
    }

    private unsafe VideoFrame CopySample(IMFSample sample, VideoFrameStamp stamp)
    {
        IMFMediaBuffer? buffer = null;
        var locked = false;
        try
        {
            MediaFoundationNative.ThrowIfFailed(
                sample.ConvertToContiguousBuffer(out buffer),
                "IMFSample.ConvertToContiguousBuffer");
            MediaFoundationNative.ThrowIfFailed(
                buffer.Lock(out var source, out _, out var currentLength),
                "IMFMediaBuffer.Lock");
            locked = true;

            var requiredLength = VideoFrame.GetRequiredBufferLength(format);
            if (currentLength < requiredLength)
            {
                throw new InvalidDataException(
                    $"The Media Foundation sample contains {currentLength} bytes; {requiredLength} are required for {format.PixelFormat}.");
            }

            var pixels = new ReadOnlySpan<byte>((void*)source, requiredLength);
            return VideoFrame.CopyFrom(Device.Id, format, stamp, pixels);
        }
        finally
        {
            if (locked)
            {
                buffer!.Unlock();
            }
            MediaFoundationNative.FinalRelease(buffer);
        }
    }

    private static void ReleaseRemainingPointers(nint activateArray, uint startIndex, uint count)
    {
        for (var index = startIndex; index < count; index++)
        {
            var unknown = Marshal.ReadIntPtr(activateArray, checked((int)(index * (uint)nint.Size)));
            if (unknown != 0)
            {
                Marshal.Release(unknown);
            }
        }
    }

    private void Enqueue(VideoFrame frame)
    {
        if (Volatile.Read(ref acceptingFrames) == 0)
        {
            frame.Dispose();
            return;
        }

        while (!frames.Writer.TryWrite(frame))
        {
            if (Volatile.Read(ref acceptingFrames) == 0)
            {
                frame.Dispose();
                return;
            }

            if (frames.Reader.TryRead(out var dropped))
            {
                dropped.Dispose();
            }
        }
    }

    private void DrainFrames()
    {
        while (frames.Reader.TryRead(out var frame))
        {
            frame.Dispose();
        }
    }

    private void SetState(SourceState next)
    {
        lock (stateGate)
        {
            state = next;
        }
    }
}
