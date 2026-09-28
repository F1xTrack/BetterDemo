using System.Runtime.Versioning;
using System.Security.Cryptography;
using BetterDemo.Capture;
using BetterDemo.Core.Contracts;
using BetterDemo.Core.Scene;
using BetterDemo.Interop.MediaFoundation;
using Xunit;
using Xunit.Abstractions;

namespace BetterDemo.Integration.Tests;

[Collection("Live capture")]
public sealed class CaptureLiveCompositionTests
{
    private readonly ITestOutputHelper output;

    public CaptureLiveCompositionTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task Compose_a_live_physical_camera_frame_without_a_source_placeholder_when_explicitly_enabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("BETTERDEMO_LIVE_CAPTURE"), "1", StringComparison.Ordinal))
        {
            output.WriteLine("Live camera composition disabled. Set BETTERDEMO_LIVE_CAPTURE=1 to run it.");
            return;
        }

        await using var adapter = new MediaFoundationVideoAdapter();
        var device = (await adapter.EnumerateDevicesAsync())
            .Where(item => item.Kind == VideoDeviceKind.PhysicalCamera)
            .Where(item => item.Formats.Any(format => format.PixelFormat == VideoPixelFormat.Bgra32))
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        Assert.NotNull(device);
        var format = device.Formats
            .Where(item => item.PixelFormat == VideoPixelFormat.Bgra32)
            .OrderByDescending(item => (long)item.Width * item.Height)
            .First();

        await using var capture = new PhysicalCameraCapture(adapter, device.Id, format);
        await capture.StartAsync();
        Assert.Equal(SourceState.Running, capture.State);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var frames = capture.ReadFramesAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        Assert.True(await frames.MoveNextAsync());
        using var frame = frames.Current;

        var composition = new SceneCompositor().Render(
            SceneMode.PhysicalCamera,
            obsFrame: null,
            physicalFrame: frame,
            new SceneRenderOptions(640, 360));

        Assert.Empty(composition.Diagnostics);
        Assert.Equal(640 * 360 * 4, composition.Pixels.Length);
        Assert.True(composition.Pixels.Span.IndexOfAnyExcept(composition.Pixels.Span[0]) >= 0);
        output.WriteLine(
            $"PHYSICAL_COMPOSITION device={device.DisplayName} input={format.Width}x{format.Height}/{format.PixelFormat} " +
            $"output={composition.Width}x{composition.Height} sha256={Convert.ToHexString(SHA256.HashData(composition.Pixels.Span))}");

        await capture.StopAsync();
        Assert.Equal(SourceState.Stopped, capture.State);
    }
}
