using BetterDemo.Core.Contracts;
using BetterDemo.Interop.MediaFoundation;
using System.Runtime.Versioning;
using Xunit;
using Xunit.Abstractions;

namespace BetterDemo.Integration.Tests;

public sealed class CaptureObsTestCardTests
{
    private const int ChannelTolerance = 16;
    private readonly ITestOutputHelper output;

    public CaptureObsTestCardTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task Controlled_obs_four_color_card_matches_bgra_regions()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("BETTERDEMO_OBS_TEST_CARD"), "1", StringComparison.Ordinal))
        {
            output.WriteLine("Controlled OBS test-card probe disabled. Set BETTERDEMO_OBS_TEST_CARD=1 only through the rollback harness.");
            return;
        }

        await using var adapter = new MediaFoundationVideoAdapter();
        var devices = await adapter.EnumerateDevicesAsync();
        var obs = Assert.Single(devices, device => device.Kind == VideoDeviceKind.ObsVirtualCamera);
        var bgraFormat = Assert.Single(obs.Formats, format => format.PixelFormat == VideoPixelFormat.Bgra32);
        await using var obsSource = await adapter.OpenSourceAsync(obs.Id, bgraFormat);
        await obsSource.StartAsync();

        var physical = devices.FirstOrDefault(device =>
            device.Kind == VideoDeviceKind.PhysicalCamera && device.Formats.Count > 0);
        if (physical is not null)
        {
            await using var physicalSource = await adapter.OpenSourceAsync(physical.Id, physical.Formats[0]);
            await physicalSource.StartAsync();
            using var physicalTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await using var physicalFrames = physicalSource.ReadFramesAsync(physicalTimeout.Token).GetAsyncEnumerator(physicalTimeout.Token);
            Assert.True(await physicalFrames.MoveNextAsync());
            physicalFrames.Current.Dispose();
            await physicalSource.StopAsync();
            Assert.Equal(SourceState.Running, obsSource.State);
            output.WriteLine($"PHYSICAL_INDEPENDENCE device={physical.DisplayName} stopped={physicalSource.State} obs={obsSource.State}");
        }

        var expected = new[]
        {
            new ExpectedSample("top-left-red", 0.25, 0.25, 0, 0, 255),
            new ExpectedSample("top-right-green", 0.75, 0.25, 0, 255, 0),
            new ExpectedSample("bottom-left-blue", 0.25, 0.75, 255, 0, 0),
            new ExpectedSample("bottom-right-yellow", 0.75, 0.75, 0, 255, 255)
        };
        ObservedSample[] observed = [];
        var matched = false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var frames = obsSource.ReadFramesAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        for (var attempt = 0; attempt < 120 && await frames.MoveNextAsync(); attempt++)
        {
            using var frame = frames.Current;
            observed = expected.Select(sample => Observe(frame, sample)).ToArray();
            if (observed.All(sample => sample.WithinTolerance))
            {
                matched = true;
                output.WriteLine($"OBS_TEST_CARD format={frame.Format.Width}x{frame.Format.Height}/{frame.Format.PixelFormat} stride={frame.Format.Stride} tolerance={ChannelTolerance}");
                foreach (var sample in observed)
                {
                    output.WriteLine(
                        $"SAMPLE region={sample.Expected.Name} coordinate=({sample.X},{sample.Y}) " +
                        $"expectedBGRA=({sample.Expected.Blue},{sample.Expected.Green},{sample.Expected.Red},255) " +
                        $"observedBGRA=({sample.Blue},{sample.Green},{sample.Red},{sample.Alpha}) pass={sample.WithinTolerance}");
                }
                break;
            }
        }

        await obsSource.StopAsync();
        Assert.True(matched, "No captured OBS BGRA frame matched all four controlled test-card regions within tolerance.");
    }

    private static ObservedSample Observe(VideoFrame frame, ExpectedSample expected)
    {
        Assert.Equal(VideoPixelFormat.Bgra32, frame.Format.PixelFormat);
        var x = Math.Clamp((int)(frame.Format.Width * expected.NormalizedX), 0, frame.Format.Width - 1);
        var y = Math.Clamp((int)(frame.Format.Height * expected.NormalizedY), 0, frame.Format.Height - 1);
        var offset = checked(y * frame.Format.Stride + x * 4);
        var pixels = frame.Data.Span;
        var blue = pixels[offset];
        var green = pixels[offset + 1];
        var red = pixels[offset + 2];
        var alpha = pixels[offset + 3];
        var withinTolerance =
            Math.Abs(blue - expected.Blue) <= ChannelTolerance &&
            Math.Abs(green - expected.Green) <= ChannelTolerance &&
            Math.Abs(red - expected.Red) <= ChannelTolerance &&
            alpha >= 255 - ChannelTolerance;
        return new ObservedSample(expected, x, y, blue, green, red, alpha, withinTolerance);
    }

    private sealed record ExpectedSample(
        string Name,
        double NormalizedX,
        double NormalizedY,
        byte Blue,
        byte Green,
        byte Red);

    private sealed record ObservedSample(
        ExpectedSample Expected,
        int X,
        int Y,
        byte Blue,
        byte Green,
        byte Red,
        byte Alpha,
        bool WithinTolerance);
}
