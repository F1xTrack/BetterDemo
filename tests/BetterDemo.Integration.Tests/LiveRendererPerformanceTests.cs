using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;
using BetterDemo.App.Rendering;
using BetterDemo.Capture;
using BetterDemo.Core.Contracts;
using BetterDemo.Core.Scene;
using BetterDemo.Interop.D3D11;
using BetterDemo.Interop.MediaFoundation;
using BetterDemo.Interop.Win32;
using Xunit;
using Xunit.Abstractions;

namespace BetterDemo.Integration.Tests;

[Collection("Live capture")]
public sealed class LiveRendererPerformanceTests
{
    private readonly ITestOutputHelper output;

    public LiveRendererPerformanceTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task Live_obs_and_physical_camera_meet_30_fps_at_720p_and_1080p_when_explicitly_enabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("BETTERDEMO_LIVE_RENDER"), "1", StringComparison.Ordinal))
        {
            output.WriteLine("Live D3D11 performance probe disabled. Set BETTERDEMO_LIVE_RENDER=1 to run it.");
            return;
        }

        var seconds = int.TryParse(Environment.GetEnvironmentVariable("BETTERDEMO_LIVE_RENDER_SECONDS"), out var configuredSeconds)
            ? Math.Clamp(configuredSeconds, 5, 180)
            : 15;
        await using var captureAdapter = new MediaFoundationVideoAdapter();
        var devices = await captureAdapter.EnumerateDevicesAsync();
        var obsDevice = devices.FirstOrDefault(device =>
            device.Kind == VideoDeviceKind.ObsVirtualCamera && device.Formats.Any(format => format.PixelFormat == VideoPixelFormat.Bgra32));
        var physicalDevice = devices.FirstOrDefault(device =>
            device.Kind == VideoDeviceKind.PhysicalCamera && device.Formats.Any(format => format.PixelFormat == VideoPixelFormat.Bgra32));
        Assert.NotNull(obsDevice);
        Assert.NotNull(physicalDevice);
        var obsFormat = BestBgraFormat(obsDevice.Formats);
        var physicalFormat = BestBgraFormat(physicalDevice.Formats);

        await using var obsCapture = new ObsVirtualCameraCapture(captureAdapter, obsFormat);
        await using var physicalCapture = new PhysicalCameraCapture(captureAdapter, physicalDevice.Id, physicalFormat);
        using var pumpCancellation = new CancellationTokenSource();
        using var obsFrames = new LatestFrameSlot();
        using var physicalFrames = new LatestFrameSlot();
        Task obsPump = Task.CompletedTask;
        Task physicalPump = Task.CompletedTask;
        var scenarios = new[]
        {
            (Width: 1280, Height: 720, Mode: SceneMode.Screen, Name: "720p OBS"),
            (Width: 1280, Height: 720, Mode: SceneMode.ScreenPlusPhysicalCameraCorner, Name: "720p CORNER"),
            (Width: 1920, Height: 1080, Mode: SceneMode.Screen, Name: "1080p OBS"),
            (Width: 1920, Height: 1080, Mode: SceneMode.ScreenPlusPhysicalCameraCorner, Name: "1080p CORNER")
        };
        try
        {
            await obsCapture.StartAsync();
            Assert.Equal(SourceState.Running, obsCapture.State);
            obsPump = PumpFramesAsync(obsCapture, obsFrames, pumpCancellation.Token);
            await physicalCapture.StartAsync();
            Assert.Equal(SourceState.Running, physicalCapture.State);
            physicalPump = PumpFramesAsync(physicalCapture, physicalFrames, pumpCancellation.Token);
            using var startupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            await WaitForFramesAsync(obsFrames, physicalFrames, startupTimeout.Token);
            using var uiThread = new TestUiThread();

            foreach (var scenario in scenarios)
            {
                await RunScenarioAsync(
                    uiThread,
                    obsCapture,
                    physicalCapture,
                    obsFrames,
                    physicalFrames,
                    scenario.Width,
                    scenario.Height,
                    scenario.Mode,
                    scenario.Name,
                    TimeSpan.FromSeconds(seconds));
            }
        }
        finally
        {
            try { await obsCapture.StopAsync(); }
            finally
            {
                try { await physicalCapture.StopAsync(); }
                finally
                {
                    pumpCancellation.Cancel();
                    await Task.WhenAll(obsPump, physicalPump).WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
        }

        Assert.Equal(SourceState.Stopped, obsCapture.State);
        Assert.Equal(SourceState.Stopped, physicalCapture.State);
        output.WriteLine($"LIVE_CAPTURE_DROPS obs={obsCapture.DroppedFrameCount} physical={physicalCapture.DroppedFrameCount}");
    }

    private async Task RunScenarioAsync(
        TestUiThread uiThread,
        ObsVirtualCameraCapture obsCapture,
        PhysicalCameraCapture physicalCapture,
        LatestFrameSlot obsFrames,
        LatestFrameSlot physicalFrames,
        int width,
        int height,
        SceneMode mode,
        string name,
        TimeSpan duration)
    {
        var id = new OutputWindowId($"live-render-{Guid.NewGuid():N}");
        await using var windows = new Win32WindowAdapter(uiThread.Enqueue, $"BetterDemo {name} performance probe", width, height);
        await windows.CreateOutputWindowAsync(id);
        await windows.ShowAsync(id);
        await using var adapter = new D3D11RenderAdapter(windows);
        await using var renderer = new D3D11Renderer(adapter, id);
        await renderer.InitializeAsync(width, height);

        VideoFrame? currentObs = null;
        VideoFrame? currentPhysical = null;
        try
        {
            if (obsFrames.TryTakeLatest(out var initialObs)) currentObs = initialObs;
            if (physicalFrames.TryTakeLatest(out var initialPhysical)) currentPhysical = initialPhysical;
            Assert.NotNull(currentObs);
            Assert.NotNull(currentPhysical);
            var renderOptions = new SceneRenderOptions(width, height);
            var layerFrames = new Dictionary<SceneLayerId, VideoFrame>(2);

            using var warmupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var warmup = Stopwatch.StartNew();
            while (warmup.Elapsed < TimeSpan.FromSeconds(2))
            {
                var frameStart = Stopwatch.GetTimestamp();
                UpdateLatest(obsFrames, physicalFrames, ref currentObs, ref currentPhysical);
                UpdateLayerFrames(layerFrames, currentObs, currentPhysical);
                var document = SceneDocumentFactory.CreatePreset(mode, renderOptions.CameraCorner);
                var result = await renderer.RenderAsync(document, layerFrames, renderOptions, warmupTimeout.Token);
                Assert.Empty(result.Diagnostics);
                await FramePacer.WaitUntilNextFrameAsync(frameStart, warmupTimeout.Token);
            }

            var replacedObsAtStart = obsFrames.ReplacedFrameCount;
            var replacedPhysicalAtStart = physicalFrames.ReplacedFrameCount;
            var droppedObsAtStart = obsCapture.DroppedFrameCount;
            var droppedPhysicalAtStart = physicalCapture.DroppedFrameCount;
            using var process = Process.GetCurrentProcess();
            var cpuAtStart = process.TotalProcessorTime;
            long rendered = 0;
            long totalRenderTicks = 0;
            long maximumRenderTicks = 0;
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < duration)
            {
                var frameStart = Stopwatch.GetTimestamp();
                UpdateLatest(obsFrames, physicalFrames, ref currentObs, ref currentPhysical);
                UpdateLayerFrames(layerFrames, currentObs, currentPhysical);
                var document = SceneDocumentFactory.CreatePreset(mode, renderOptions.CameraCorner);
                var renderStart = Stopwatch.GetTimestamp();
                var composition = await renderer.RenderAsync(document, layerFrames, renderOptions);
                var renderTicks = Stopwatch.GetTimestamp() - renderStart;
                Assert.Empty(composition.Diagnostics);
                rendered++;
                totalRenderTicks += renderTicks;
                maximumRenderTicks = Math.Max(maximumRenderTicks, renderTicks);
                await FramePacer.WaitUntilNextFrameAsync(frameStart);
            }
            timer.Stop();
            process.Refresh();
            var cpuCores = (process.TotalProcessorTime - cpuAtStart).TotalSeconds / timer.Elapsed.TotalSeconds;
            var fps = rendered / timer.Elapsed.TotalSeconds;
            var averageRenderMs = totalRenderTicks * 1000.0 / Stopwatch.Frequency / rendered;
            var maximumRenderMs = maximumRenderTicks * 1000.0 / Stopwatch.Frequency;
            var replacedObs = obsFrames.ReplacedFrameCount - replacedObsAtStart;
            var replacedPhysical = physicalFrames.ReplacedFrameCount - replacedPhysicalAtStart;
            var droppedObs = obsCapture.DroppedFrameCount - droppedObsAtStart;
            var droppedPhysical = physicalCapture.DroppedFrameCount - droppedPhysicalAtStart;
            output.WriteLine(
                $"LIVE_RENDER {name} fps={fps:F2} avgRenderMs={averageRenderMs:F2} maxRenderMs={maximumRenderMs:F2} " +
                $"cpuCores={cpuCores:F2} latestFrameReplaced=OBS:{replacedObs},CAMERA:{replacedPhysical} " +
                $"captureDropped=OBS:{droppedObs},CAMERA:{droppedPhysical}");
            Assert.True(fps >= 27, $"{name} rendered {fps:F2} FPS; expected at least 27 FPS.");
            Assert.True(averageRenderMs < 25, $"{name} averaged {averageRenderMs:F2} ms per composition; expected below 25 ms.");
            Assert.Equal(0, droppedObs);
            Assert.Equal(0, droppedPhysical);
        }
        finally
        {
            currentObs?.Dispose();
            currentPhysical?.Dispose();
        }
    }

    private static void UpdateLatest(
        LatestFrameSlot obsFrames,
        LatestFrameSlot physicalFrames,
        ref VideoFrame? currentObs,
        ref VideoFrame? currentPhysical)
    {
        if (obsFrames.TryTakeLatest(out var nextObs))
        {
            currentObs?.Dispose();
            currentObs = nextObs;
        }
        if (physicalFrames.TryTakeLatest(out var nextPhysical))
        {
            currentPhysical?.Dispose();
            currentPhysical = nextPhysical;
        }
    }

    private static void UpdateLayerFrames(
        Dictionary<SceneLayerId, VideoFrame> layerFrames,
        VideoFrame? obsFrame,
        VideoFrame? physicalFrame)
    {
        layerFrames.Clear();
        if (obsFrame is not null) layerFrames[SceneDocumentFactory.ObsVirtualCameraLayerId] = obsFrame;
        if (physicalFrame is not null) layerFrames[SceneDocumentFactory.PhysicalCameraLayerId] = physicalFrame;
    }

    private static async Task PumpFramesAsync(IVideoSource source, LatestFrameSlot destination, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in source.ReadFramesAsync(cancellationToken).ConfigureAwait(false))
                destination.Publish(frame);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || source.State is SourceState.Stopped or SourceState.Disposed)
        {
        }
    }

    private static async Task WaitForFramesAsync(LatestFrameSlot obsFrames, LatestFrameSlot physicalFrames, CancellationToken cancellationToken)
    {
        while (!obsFrames.HasFrame || !physicalFrames.HasFrame)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(25, cancellationToken);
        }
    }

    private static VideoFrameFormat BestBgraFormat(IReadOnlyList<VideoFrameFormat> formats) => formats
        .Where(format => format.PixelFormat == VideoPixelFormat.Bgra32)
        .OrderByDescending(format => (long)format.Width * format.Height)
        .First();

    private sealed class LatestFrameSlot : IDisposable
    {
        private readonly object gate = new();
        private VideoFrame? frame;
        private long replacedFrameCount;

        public long ReplacedFrameCount => Interlocked.Read(ref replacedFrameCount);
        public bool HasFrame { get { lock (gate) return frame is not null; } }

        public void Publish(VideoFrame next)
        {
            VideoFrame? previous;
            lock (gate)
            {
                previous = frame;
                frame = next;
            }
            if (previous is not null)
            {
                previous.Dispose();
                Interlocked.Increment(ref replacedFrameCount);
            }
        }

        public bool TryTakeLatest(out VideoFrame? value)
        {
            lock (gate)
            {
                value = frame;
                frame = null;
                return value is not null;
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                frame?.Dispose();
                frame = null;
            }
        }
    }

    private sealed class TestUiThread : IDisposable
    {
        private readonly BlockingCollection<Action> actions = new();
        private readonly Thread thread;
        private bool disposed;

        public TestUiThread()
        {
            thread = new Thread(() =>
            {
                foreach (var action in actions.GetConsumingEnumerable()) action();
            })
            {
                IsBackground = true,
                Name = "BetterDemo live-render test window thread"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        public bool Enqueue(Action action)
        {
            if (disposed || actions.IsAddingCompleted) return false;
            actions.Add(action);
            return true;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            actions.CompleteAdding();
            if (!thread.Join(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("The live-render test UI thread did not stop.");
            actions.Dispose();
        }
    }
}
