using BetterDemo.Core.Contracts;
using BetterDemo.Interop.MediaFoundation;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace BetterDemo.Capture;

public abstract class CameraCaptureBase : IVideoSource
{
    private readonly IMediaFoundationVideoAdapter adapter;
    private readonly IDiagnosticsSink diagnostics;
    private readonly VideoFrameFormat requestedFormat;
    private readonly Channel<VideoFrame> frames;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly object stateGate = new();
    private readonly TimeSpan stopTimeout;
    private IVideoSource? activeSource;
    private CancellationTokenSource? pumpCancellation;
    private Task? pumpTask;
    private VideoDeviceDescriptor device;
    private SourceState state = SourceState.Created;
    private VideoFrameStamp? lastAcceptedStamp;
    private long generation;
    private int queuedFrameCount;
    private long droppedFrameCount;

    protected CameraCaptureBase(
        IMediaFoundationVideoAdapter adapter,
        VideoDeviceDescriptor initialDevice,
        VideoFrameFormat requestedFormat,
        IDiagnosticsSink? diagnostics,
        int queueCapacity,
        TimeSpan? stopTimeout)
    {
        if (queueCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(queueCapacity));

        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        device = initialDevice;
        this.requestedFormat = requestedFormat;
        this.diagnostics = diagnostics ?? NullDiagnosticsSink.Instance;
        this.stopTimeout = stopTimeout ?? TimeSpan.FromSeconds(2);
        frames = Channel.CreateBounded<VideoFrame>(new BoundedChannelOptions(queueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });
    }

    public VideoDeviceDescriptor Device
    {
        get
        {
            lock (stateGate)
            {
                return device;
            }
        }
    }

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

    public int QueuedFrameCount => Volatile.Read(ref queuedFrameCount);
    public long DroppedFrameCount => Interlocked.Read(ref droppedFrameCount);

    protected abstract VideoDeviceDescriptor? SelectDevice(IReadOnlyList<VideoDeviceDescriptor> devices);
    protected abstract string ComponentName { get; }

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (State is SourceState.Running or SourceState.Starting)
            {
                return;
            }

            if (activeSource is not null)
            {
                await StopCoreAsync(cancellationToken, SourceState.Stopped).ConfigureAwait(false);
            }

            SetState(SourceState.Starting);
            DrainFrames();
            lastAcceptedStamp = null;

            IReadOnlyList<VideoDeviceDescriptor> devices;
            try
            {
                devices = await adapter.EnumerateDevicesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                SetState(SourceState.Stopped);
                throw;
            }
            var selectedDevice = SelectDevice(devices);
            if (selectedDevice is null)
            {
                SetUnavailable("The selected camera is not present.");
                return;
            }

            if (!selectedDevice.Formats.Contains(requestedFormat))
            {
                SetState(SourceState.Faulted);
                throw new NotSupportedException(
                    $"Camera '{selectedDevice.DisplayName}' does not support {requestedFormat.Width}x{requestedFormat.Height} {requestedFormat.PixelFormat}.");
            }

            lock (stateGate)
            {
                device = selectedDevice;
            }

            IVideoSource? source = null;
            try
            {
                source = await adapter.OpenSourceAsync(selectedDevice.Id, requestedFormat, cancellationToken).ConfigureAwait(false);
                await source.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (source is not null)
                {
                    await CleanupFailedStartAsync(source).ConfigureAwait(false);
                }

                SetState(SourceState.Stopped);
                throw;
            }
            catch (Exception exception)
            {
                if (source is not null)
                {
                    await CleanupFailedStartAsync(source).ConfigureAwait(false);
                }

                SetUnavailable($"Camera open or start failed: {exception.Message}");
                return;
            }

            // A successful OpenSourceAsync and StartAsync guarantee a non-null source.
            var startedSource = source ?? throw new InvalidOperationException("The camera adapter returned no source.");
            activeSource = startedSource;
            pumpCancellation = new CancellationTokenSource();
            var currentGeneration = Interlocked.Increment(ref generation);
            SetState(SourceState.Running);
            pumpTask = PumpFramesAsync(startedSource, currentGeneration, pumpCancellation.Token);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State == SourceState.Disposed)
            {
                return;
            }

            await StopCoreAsync(cancellationToken, SourceState.Stopped).ConfigureAwait(false);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async IAsyncEnumerable<VideoFrame> ReadFramesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await frames.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (frames.Reader.TryRead(out var frame))
            {
                Interlocked.Decrement(ref queuedFrameCount);
                if (State == SourceState.Running)
                {
                    yield return frame;
                }
                else
                {
                    frame.Dispose();
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State == SourceState.Disposed)
            {
                return;
            }

            await StopCoreAsync(CancellationToken.None, SourceState.Stopped).ConfigureAwait(false);
            SetState(SourceState.Disposed);
            frames.Writer.TryComplete();
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private async Task PumpFramesAsync(IVideoSource source, long sourceGeneration, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in source.ReadFramesAsync(cancellationToken).ConfigureAwait(false))
            {
                if (sourceGeneration != Interlocked.Read(ref generation) || State != SourceState.Running)
                {
                    frame.Dispose();
                    continue;
                }

                if (lastAcceptedStamp is { } previous && !VideoFrameOrdering.IsStrictlyNewer(frame.Stamp, previous))
                {
                    DropFrame(frame, "A stale or out-of-order frame was rejected.");
                    continue;
                }

                lastAcceptedStamp = frame.Stamp;
                Enqueue(frame);
            }

            if (sourceGeneration == Interlocked.Read(ref generation) && State == SourceState.Running)
            {
                DrainFrames();
                SetUnavailable("Camera frame delivery stopped.", DiagnosticCode.DeviceLost);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (sourceGeneration == Interlocked.Read(ref generation) && State == SourceState.Running)
            {
                DrainFrames();
                SetUnavailable($"Camera frame delivery failed: {exception.Message}", DiagnosticCode.DeviceLost);
            }
        }
    }

    private void Enqueue(VideoFrame frame)
    {
        while (!frames.Writer.TryWrite(frame))
        {
            if (!frames.Reader.TryRead(out var dropped))
            {
                continue;
            }

            Interlocked.Decrement(ref queuedFrameCount);
            DropFrame(dropped, "The bounded capture queue discarded its oldest frame.");
        }

        Interlocked.Increment(ref queuedFrameCount);
    }

    private void DropFrame(VideoFrame frame, string message)
    {
        frame.Dispose();
        var count = Interlocked.Increment(ref droppedFrameCount);
        diagnostics.Report(new DiagnosticEvent(
            DiagnosticSeverity.Warning,
            DiagnosticCode.FrameDropped,
            ComponentName,
            $"{message} Total dropped frames: {count}."));
    }

    private async ValueTask StopCoreAsync(CancellationToken cancellationToken, SourceState finalState)
    {
        if (State is SourceState.Created or SourceState.Stopped)
        {
            SetState(finalState);
            DrainFrames();
            return;
        }

        Interlocked.Increment(ref generation);
        if (State is not SourceState.Unavailable and not SourceState.Faulted)
        {
            SetState(SourceState.Stopping);
        }

        var cancellation = pumpCancellation;
        var source = activeSource;
        var pump = pumpTask;
        pumpCancellation = null;
        activeSource = null;
        pumpTask = null;

        Exception? stopFailure = null;
        try
        {
            try
            {
                cancellation?.Cancel();
            }
            catch (Exception exception)
            {
                stopFailure = exception;
            }

            if (source is not null)
            {
                try
                {
                    await source.StopAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    stopFailure = exception;
                }

                try
                {
                    await source.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    stopFailure ??= exception;
                }
            }

            if (pump is not null)
            {
                var completedTask = await Task.WhenAny(
                    pump,
                    Task.Delay(stopTimeout, cancellationToken)).ConfigureAwait(false);
                if (completedTask == pump)
                {
                    try
                    {
                        await pump.ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        stopFailure ??= exception;
                    }
                }
            }
        }
        finally
        {
            cancellation?.Dispose();
            DrainFrames();
            SetState(finalState);
        }

        if (stopFailure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(stopFailure).Throw();
        }
    }

    private async ValueTask CleanupFailedStartAsync(IVideoSource source)
    {
        try
        {
            await source.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ReportCleanupFailure("Stopping a camera after a failed start", exception);
        }

        try
        {
            await source.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ReportCleanupFailure("Disposing a camera after a failed start", exception);
        }
    }

    private void ReportCleanupFailure(string operation, Exception exception) =>
        diagnostics.Report(new DiagnosticEvent(
            DiagnosticSeverity.Warning,
            DiagnosticCode.DeviceLost,
            ComponentName,
            $"{operation} failed: {exception.Message}"));

    private void DrainFrames()
    {
        while (frames.Reader.TryRead(out var frame))
        {
            Interlocked.Decrement(ref queuedFrameCount);
            frame.Dispose();
        }
    }

    private void SetUnavailable(string message, DiagnosticCode code = DiagnosticCode.SourceUnavailable)
    {
        SetState(SourceState.Unavailable);
        diagnostics.Report(new DiagnosticEvent(DiagnosticSeverity.Error, code, ComponentName, message));
    }

    private void SetState(SourceState next)
    {
        lock (stateGate)
        {
            state = next;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(State == SourceState.Disposed, this);
    }

    private sealed class NullDiagnosticsSink : IDiagnosticsSink
    {
        public static NullDiagnosticsSink Instance { get; } = new();
        public void Report(DiagnosticEvent diagnostic) { }
    }
}
