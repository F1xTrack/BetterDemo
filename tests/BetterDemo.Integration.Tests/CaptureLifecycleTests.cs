using BetterDemo.Capture;
using BetterDemo.Core.Contracts;
using BetterDemo.Interop.MediaFoundation;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using Xunit;

namespace BetterDemo.Integration.Tests;

public sealed class CaptureLifecycleTests
{
    private static readonly VideoFrameFormat Format = new(4, 2, VideoPixelFormat.Nv12, 4);
    private static readonly VideoDeviceDescriptor ObsDevice = new(
        new VideoDeviceId("mf://obs"),
        VideoDeviceKind.ObsVirtualCamera,
        "OBS Virtual Camera",
        [Format]);
    private static readonly VideoDeviceDescriptor PhysicalDevice = new(
        new VideoDeviceId("mf://physical"),
        VideoDeviceKind.PhysicalCamera,
        "USB Camera",
        [Format]);

    [Fact]
    public async Task Obs_and_physical_captures_start_and_stop_independently()
    {
        await using var adapter = new FakeMediaFoundationAdapter([ObsDevice, PhysicalDevice]);
        await using var obs = new ObsVirtualCameraCapture(adapter, Format);
        await using var physical = new PhysicalCameraCapture(adapter, PhysicalDevice.Id, Format);

        await obs.StartAsync();
        await physical.StartAsync();
        await physical.StopAsync();

        Assert.Equal(SourceState.Running, obs.State);
        Assert.Equal(SourceState.Stopped, physical.State);
        Assert.Equal(1, adapter.Sources[ObsDevice.Id].StartCount);
        Assert.Equal(1, adapter.Sources[PhysicalDevice.Id].StopCount);
    }

    [Fact]
    public async Task Missing_obs_becomes_unavailable_without_opening_physical_fallback()
    {
        var diagnostics = new RecordingDiagnosticsSink();
        await using var adapter = new FakeMediaFoundationAdapter([PhysicalDevice]);
        await using var obs = new ObsVirtualCameraCapture(adapter, Format, diagnostics);

        await obs.StartAsync();

        Assert.Equal(SourceState.Unavailable, obs.State);
        Assert.Empty(adapter.OpenedDeviceIds);
        Assert.Contains(diagnostics.Events, item => item.Code == DiagnosticCode.SourceUnavailable);
    }

    [Fact]
    public async Task Unplug_drains_queued_frames_and_does_not_stop_other_source()
    {
        await using var adapter = new FakeMediaFoundationAdapter([ObsDevice, PhysicalDevice]);
        await using var obs = new ObsVirtualCameraCapture(adapter, Format);
        await using var physical = new PhysicalCameraCapture(adapter, PhysicalDevice.Id, Format);
        await obs.StartAsync();
        await physical.StartAsync();

        adapter.Sources[PhysicalDevice.Id].Emit(1);
        await WaitUntilAsync(() => physical.QueuedFrameCount == 1);
        adapter.Sources[PhysicalDevice.Id].Unplug();
        await WaitUntilAsync(() => physical.State == SourceState.Unavailable);

        Assert.Equal(0, physical.QueuedFrameCount);
        Assert.Equal(SourceState.Running, obs.State);
        Assert.Equal(0, adapter.Sources[ObsDevice.Id].StopCount);
    }

    [Fact]
    public async Task Queue_is_bounded_and_reports_disposed_dropped_frames()
    {
        var diagnostics = new RecordingDiagnosticsSink();
        await using var adapter = new FakeMediaFoundationAdapter([ObsDevice]);
        await using var obs = new ObsVirtualCameraCapture(adapter, Format, diagnostics, queueCapacity: 2);
        await obs.StartAsync();

        var source = adapter.Sources[ObsDevice.Id];
        source.Emit(1);
        source.Emit(2);
        source.Emit(3);
        await WaitUntilAsync(() => obs.DroppedFrameCount == 1);

        Assert.Equal(2, obs.QueuedFrameCount);
        Assert.True(source.EmittedFrames[0].IsDisposed);
        Assert.Contains(diagnostics.Events, item => item.Code == DiagnosticCode.FrameDropped);
    }

    [Fact]
    public async Task Delivered_frames_preserve_strict_qpc_timestamp_ordering()
    {
        await using var adapter = new FakeMediaFoundationAdapter([ObsDevice]);
        await using var obs = new ObsVirtualCameraCapture(adapter, Format);
        await obs.StartAsync();
        adapter.Sources[ObsDevice.Id].Emit(10);
        adapter.Sources[ObsDevice.Id].Emit(11);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var frames = obs.ReadFramesAsync(cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        Assert.True(await frames.MoveNextAsync());
        using var first = frames.Current;
        Assert.True(await frames.MoveNextAsync());
        using var second = frames.Current;

        Assert.True(VideoFrameOrdering.IsStrictlyNewer(second.Stamp, first.Stamp));
        Assert.Equal(10_000_000, first.Timestamp.Frequency);
    }

    [Fact]
    public async Task Unsupported_format_faults_without_opening_device()
    {
        var unsupported = new VideoFrameFormat(640, 480, VideoPixelFormat.Bgra32, 2560);
        await using var adapter = new FakeMediaFoundationAdapter([ObsDevice]);
        await using var obs = new ObsVirtualCameraCapture(adapter, unsupported);

        await Assert.ThrowsAsync<NotSupportedException>(() => obs.StartAsync().AsTask());

        Assert.Equal(SourceState.Faulted, obs.State);
        Assert.Empty(adapter.OpenedDeviceIds);
    }

    [Fact]
    public async Task Stop_completes_when_a_source_read_is_hung_and_late_frames_are_stale()
    {
        await using var adapter = new FakeMediaFoundationAdapter([ObsDevice], hangAfterStart: true);
        await using var obs = new ObsVirtualCameraCapture(adapter, Format, stopTimeout: TimeSpan.FromMilliseconds(50));
        await obs.StartAsync();

        await obs.StopAsync();
        adapter.Sources[ObsDevice.Id].EmitAfterHungRead(1);

        Assert.Equal(SourceState.Stopped, obs.State);
        Assert.Equal(0, obs.QueuedFrameCount);
    }

    [Fact]
    public async Task Repeated_interruption_can_restart_and_disposal_releases_each_session()
    {
        await using var adapter = new FakeMediaFoundationAdapter([ObsDevice]);
        var obs = new ObsVirtualCameraCapture(adapter, Format);

        await obs.StartAsync();
        adapter.Sources[ObsDevice.Id].Unplug();
        await WaitUntilAsync(() => obs.State == SourceState.Unavailable);
        await obs.StartAsync();
        adapter.Sources[ObsDevice.Id].Unplug();
        await WaitUntilAsync(() => obs.State == SourceState.Unavailable);
        await obs.DisposeAsync();

        Assert.Equal(SourceState.Disposed, obs.State);
        Assert.Equal(2, adapter.CreatedSources.Count);
        Assert.All(adapter.CreatedSources, source => Assert.True(source.IsDisposed));
    }

    [Fact]
    public async Task Busy_device_becomes_unavailable_with_diagnostic()
    {
        var diagnostics = new RecordingDiagnosticsSink();
        await using var adapter = new FakeMediaFoundationAdapter(
            [ObsDevice],
            openException: new InvalidOperationException("device busy"));
        await using var obs = new ObsVirtualCameraCapture(adapter, Format, diagnostics);

        await obs.StartAsync();

        Assert.Equal(SourceState.Unavailable, obs.State);
        Assert.Contains(diagnostics.Events, item =>
            item.Code == DiagnosticCode.SourceUnavailable && item.Message.Contains("device busy", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Failed_camera_start_disposes_the_opened_source()
    {
        var diagnostics = new RecordingDiagnosticsSink();
        await using var adapter = new FakeMediaFoundationAdapter(
            [ObsDevice],
            startException: new InvalidOperationException("media type negotiation failed"));
        await using var obs = new ObsVirtualCameraCapture(adapter, Format, diagnostics);

        await obs.StartAsync();

        var failedSource = Assert.Single(adapter.CreatedSources);
        Assert.Equal(SourceState.Unavailable, obs.State);
        Assert.True(failedSource.IsDisposed);
        Assert.Equal(1, failedSource.StopCount);
        Assert.Contains(diagnostics.Events, item =>
            item.Code == DiagnosticCode.SourceUnavailable && item.Message.Contains("media type negotiation failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stop_failure_still_disposes_source_drains_frames_and_restores_lifecycle_state()
    {
        await using var adapter = new FakeMediaFoundationAdapter(
            [ObsDevice],
            stopException: new InvalidOperationException("native stop failed"));
        await using var obs = new ObsVirtualCameraCapture(adapter, Format);
        await obs.StartAsync();
        adapter.Sources[ObsDevice.Id].Emit(1);
        await WaitUntilAsync(() => obs.QueuedFrameCount == 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => obs.StopAsync().AsTask());

        Assert.Equal(SourceState.Stopped, obs.State);
        Assert.Equal(0, obs.QueuedFrameCount);
        Assert.True(adapter.Sources[ObsDevice.Id].IsDisposed);
    }

    [Fact]
    public async Task Cancelled_stop_still_disposes_source_drains_frames_and_restores_lifecycle_state()
    {
        await using var adapter = new FakeMediaFoundationAdapter([ObsDevice], waitForStopCancellation: true);
        await using var obs = new ObsVirtualCameraCapture(adapter, Format);
        await obs.StartAsync();
        adapter.Sources[ObsDevice.Id].Emit(1);
        await WaitUntilAsync(() => obs.QueuedFrameCount == 1);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => obs.StopAsync(cancellation.Token).AsTask());

        Assert.Equal(SourceState.Stopped, obs.State);
        Assert.Equal(0, obs.QueuedFrameCount);
        Assert.True(adapter.Sources[ObsDevice.Id].IsDisposed);
    }

    [Fact]
    public async Task Cancellation_during_enumeration_returns_to_stopped_state()
    {
        await using var adapter = new FakeMediaFoundationAdapter(
            [ObsDevice],
            enumerationDelay: TimeSpan.FromSeconds(1));
        await using var obs = new ObsVirtualCameraCapture(adapter, Format);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => obs.StartAsync(cancellation.Token).AsTask());

        Assert.Equal(SourceState.Stopped, obs.State);
    }

    [Fact]
    public async Task Stale_timestamp_is_dropped_and_polling_cancellation_does_not_stop_source()
    {
        await using var adapter = new FakeMediaFoundationAdapter([ObsDevice]);
        await using var obs = new ObsVirtualCameraCapture(adapter, Format);
        await obs.StartAsync();
        var source = adapter.Sources[ObsDevice.Id];
        source.Emit(10);
        source.Emit(9);
        await WaitUntilAsync(() => obs.DroppedFrameCount == 1);

        using (var drainTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1)))
        {
            await using var queuedFrames = obs.ReadFramesAsync(drainTimeout.Token).GetAsyncEnumerator(drainTimeout.Token);
            Assert.True(await queuedFrames.MoveNextAsync());
            queuedFrames.Current.Dispose();
        }

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await using var frames = obs.ReadFramesAsync(cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => frames.MoveNextAsync().AsTask());

        Assert.Equal(SourceState.Running, obs.State);
        Assert.True(source.EmittedFrames[1].IsDisposed);
    }

    [Fact]
    public async Task Bgra_source_can_start_without_affecting_nv12_source()
    {
        var bgra = new VideoFrameFormat(2, 2, VideoPixelFormat.Bgra32, 8);
        var physicalWithBgra = PhysicalDevice with { Formats = [bgra] };
        await using var adapter = new FakeMediaFoundationAdapter([ObsDevice, physicalWithBgra]);
        await using var obs = new ObsVirtualCameraCapture(adapter, Format);
        await using var physical = new PhysicalCameraCapture(adapter, physicalWithBgra.Id, bgra);

        await obs.StartAsync();
        await physical.StartAsync();

        Assert.Equal(VideoPixelFormat.Nv12, obs.Device.Formats.Single().PixelFormat);
        Assert.Equal(VideoPixelFormat.Bgra32, physical.Device.Formats.Single().PixelFormat);
        Assert.Equal(SourceState.Running, obs.State);
        Assert.Equal(SourceState.Running, physical.State);
    }

    [Fact]
    public async Task Nv12_and_bgra_payload_bytes_and_planes_survive_capture_queues()
    {
        var bgraFormat = new VideoFrameFormat(2, 2, VideoPixelFormat.Bgra32, 8);
        var physicalWithBgra = PhysicalDevice with { Formats = [bgraFormat] };
        await using var adapter = new FakeMediaFoundationAdapter([ObsDevice, physicalWithBgra]);
        await using var obs = new ObsVirtualCameraCapture(adapter, Format);
        await using var physical = new PhysicalCameraCapture(adapter, physicalWithBgra.Id, bgraFormat);
        await obs.StartAsync();
        await physical.StartAsync();
        byte[] nv12Bytes = [1, 2, 3, 4, 5, 6, 7, 8, 21, 22, 23, 24];
        byte[] bgraBytes = [1, 2, 3, 255, 4, 5, 6, 255, 7, 8, 9, 255, 10, 11, 12, 255];
        adapter.Sources[ObsDevice.Id].Emit(10, nv12Bytes);
        adapter.Sources[PhysicalDevice.Id].Emit(10, bgraBytes);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var obsFrames = obs.ReadFramesAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        await using var physicalFrames = physical.ReadFramesAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        Assert.True(await obsFrames.MoveNextAsync());
        Assert.True(await physicalFrames.MoveNextAsync());
        using var nv12 = obsFrames.Current;
        using var bgra = physicalFrames.Current;

        Assert.Equal(nv12Bytes, nv12.Data.ToArray());
        Assert.Equal(2, nv12.Planes.Count);
        Assert.Equal(new VideoFramePlane(0, 8, 4, 4, 2), nv12.Planes[0]);
        Assert.Equal(new VideoFramePlane(8, 4, 4, 2, 1), nv12.Planes[1]);
        Assert.Equal(nv12Bytes[..8], nv12.GetPlane(0).ToArray());
        Assert.Equal(nv12Bytes[8..], nv12.GetPlane(1).ToArray());
        Assert.Equal(bgraBytes, bgra.Data.ToArray());
        Assert.Single(bgra.Planes);
        Assert.Equal(new VideoFramePlane(0, 16, 8, 2, 2), bgra.Planes[0]);
    }

    [Fact]
    public async Task Stop_unplug_drop_and_stale_reclaim_owned_payloads()
    {
        await using var adapter = new FakeMediaFoundationAdapter([ObsDevice]);
        await using var obs = new ObsVirtualCameraCapture(adapter, Format, queueCapacity: 1);
        await obs.StartAsync();
        var source = adapter.Sources[ObsDevice.Id];
        source.Emit(10);
        source.Emit(11);
        await WaitUntilAsync(() => obs.DroppedFrameCount == 1);
        AssertPayloadReleased(source.EmittedFrames[0]);

        source.Emit(9);
        await WaitUntilAsync(() => obs.DroppedFrameCount == 2);
        AssertPayloadReleased(source.EmittedFrames[2]);

        await obs.StopAsync();
        AssertPayloadReleased(source.EmittedFrames[1]);
        source.EmittedFrames[1].Dispose();
        AssertPayloadReleased(source.EmittedFrames[1]);

        await obs.StartAsync();
        var restarted = adapter.Sources[ObsDevice.Id];
        restarted.Emit(20);
        await WaitUntilAsync(() => obs.QueuedFrameCount == 1);
        restarted.Unplug();
        await WaitUntilAsync(() => obs.State == SourceState.Unavailable);
        AssertPayloadReleased(restarted.EmittedFrames[0]);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task Native_reader_timeout_probe_completes_without_hanging()
    {
        var neverCompletes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = DateTime.UtcNow;

        var terminalState = await MediaFoundationVideoSource.WaitForReaderTerminalStateAsync(
            neverCompletes.Task,
            TimeSpan.FromMilliseconds(25),
            CancellationToken.None);

        Assert.Equal(SourceState.Unavailable, terminalState);
        Assert.NotEqual(SourceState.Stopping, terminalState);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(1));
    }

    private static void AssertPayloadReleased(VideoFrame frame)
    {
        Assert.True(frame.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => _ = frame.Data);
        Assert.Throws<ObjectDisposedException>(() => frame.GetPlane(0));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class RecordingDiagnosticsSink : IDiagnosticsSink
    {
        public ConcurrentQueue<DiagnosticEvent> Events { get; } = new();
        public void Report(DiagnosticEvent diagnostic) => Events.Enqueue(diagnostic);
    }

    private sealed class FakeMediaFoundationAdapter : IMediaFoundationVideoAdapter
    {
        private readonly IReadOnlyList<VideoDeviceDescriptor> devices;
        private readonly bool hangAfterStart;
        private readonly Exception? openException;
        private readonly Exception? startException;
        private readonly Exception? stopException;
        private readonly TimeSpan? enumerationDelay;
        private readonly bool waitForStopCancellation;

        public FakeMediaFoundationAdapter(
            IReadOnlyList<VideoDeviceDescriptor> devices,
            bool hangAfterStart = false,
            Exception? openException = null,
            TimeSpan? enumerationDelay = null,
            Exception? startException = null,
            Exception? stopException = null,
            bool waitForStopCancellation = false)
        {
            this.devices = devices;
            this.hangAfterStart = hangAfterStart;
            this.openException = openException;
            this.enumerationDelay = enumerationDelay;
            this.startException = startException;
            this.stopException = stopException;
            this.waitForStopCancellation = waitForStopCancellation;
        }

        public Dictionary<VideoDeviceId, FakeVideoSource> Sources { get; } = [];
        public List<FakeVideoSource> CreatedSources { get; } = [];
        public List<VideoDeviceId> OpenedDeviceIds { get; } = [];

        public async ValueTask<IReadOnlyList<VideoDeviceDescriptor>> EnumerateDevicesAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (enumerationDelay is { } delay)
            {
                await Task.Delay(delay, cancellationToken);
            }
            return devices;
        }

        public ValueTask<IVideoSource> OpenSourceAsync(VideoDeviceId deviceId, VideoFrameFormat format, CancellationToken cancellationToken = default)
        {
            if (openException is not null)
            {
                throw openException;
            }

            OpenedDeviceIds.Add(deviceId);
            var device = devices.Single(item => item.Id == deviceId);
            var source = new FakeVideoSource(device, format, hangAfterStart, startException, stopException, waitForStopCancellation);
            Sources[deviceId] = source;
            CreatedSources.Add(source);
            return ValueTask.FromResult<IVideoSource>(source);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeVideoSource : IVideoSource
    {
        private readonly System.Threading.Channels.Channel<VideoFrame> frames =
            System.Threading.Channels.Channel.CreateUnbounded<VideoFrame>();
        private readonly bool hangAfterStart;
        private readonly VideoFrameFormat format;
        private readonly Exception? startException;
        private readonly Exception? stopException;
        private readonly bool waitForStopCancellation;
        private readonly TaskCompletionSource hungRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ulong sequence;

        public FakeVideoSource(
            VideoDeviceDescriptor device,
            VideoFrameFormat format,
            bool hangAfterStart,
            Exception? startException,
            Exception? stopException,
            bool waitForStopCancellation)
        {
            Device = device;
            this.format = format;
            this.hangAfterStart = hangAfterStart;
            this.startException = startException;
            this.stopException = stopException;
            this.waitForStopCancellation = waitForStopCancellation;
        }

        public VideoDeviceDescriptor Device { get; }
        public SourceState State { get; private set; } = SourceState.Created;
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public bool IsDisposed { get; private set; }
        public List<VideoFrame> EmittedFrames { get; } = [];

        public ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            StartCount++;
            if (startException is not null)
            {
                throw startException;
            }

            State = SourceState.Running;
            return ValueTask.CompletedTask;
        }

        public async ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopCount++;
            if (stopException is not null)
            {
                throw stopException;
            }

            if (waitForStopCancellation)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            State = SourceState.Stopped;
        }

        public async IAsyncEnumerable<VideoFrame> ReadFramesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (hangAfterStart)
            {
                await hungRead.Task;
            }

            await foreach (var frame in frames.Reader.ReadAllAsync(cancellationToken))
            {
                yield return frame;
            }
        }

        public void Emit(long qpcTicks, byte[]? payload = null)
        {
            payload ??= Enumerable.Range(0, VideoFrame.GetRequiredBufferLength(format))
                .Select(index => (byte)(index % 251))
                .ToArray();
            var frame = VideoFrame.CopyFrom(
                Device.Id,
                format,
                new VideoFrameStamp(++sequence, new QpcTimestamp(qpcTicks, 10_000_000)),
                payload);
            EmittedFrames.Add(frame);
            frames.Writer.TryWrite(frame);
        }

        public void EmitAfterHungRead(long qpcTicks)
        {
            Emit(qpcTicks);
            hungRead.TrySetResult();
        }

        public void Unplug()
        {
            State = SourceState.Unavailable;
            frames.Writer.TryComplete();
            hungRead.TrySetResult();
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            State = SourceState.Disposed;
            frames.Writer.TryComplete();
            hungRead.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
