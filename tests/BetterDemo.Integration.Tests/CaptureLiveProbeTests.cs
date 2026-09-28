using BetterDemo.Core.Contracts;
using BetterDemo.Interop.MediaFoundation;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Xunit;
using Xunit.Abstractions;

namespace BetterDemo.Integration.Tests;

[Collection("Live capture")]
public sealed class CaptureLiveProbeTests
{
    private readonly ITestOutputHelper output;

    public CaptureLiveProbeTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task Enumerate_and_sample_live_cameras_when_explicitly_enabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("BETTERDEMO_LIVE_CAPTURE"), "1", StringComparison.Ordinal))
        {
            output.WriteLine("Live camera probe disabled. Set BETTERDEMO_LIVE_CAPTURE=1 to run it.");
            return;
        }

        await using var adapter = new MediaFoundationVideoAdapter();
        var devices = await adapter.EnumerateDevicesAsync();
        var repeatedDevices = await adapter.EnumerateDevicesAsync();
        Assert.Equal(
            devices.Select(device => device.Id).OrderBy(id => id.Value, StringComparer.Ordinal),
            repeatedDevices.Select(device => device.Id).OrderBy(id => id.Value, StringComparer.Ordinal));
        output.WriteLine($"LIVE_DEVICE_COUNT={devices.Count}");
        output.WriteLine("STABLE_ID_REPEAT_ENUMERATION=PASS");
        foreach (var device in devices)
        {
            output.WriteLine($"DEVICE kind={device.Kind} id={device.Id} name={device.DisplayName} formats={device.Formats.Count}");
            foreach (var format in device.Formats.Take(8))
            {
                output.WriteLine($"  FORMAT {format.Width}x{format.Height} {format.PixelFormat} stride={format.Stride}");
            }
        }

        var obs = devices.FirstOrDefault(device => device.Kind == VideoDeviceKind.ObsVirtualCamera);
        Assert.NotNull(obs);
        Assert.NotEmpty(obs.Formats);

        await using var obsSource = await adapter.OpenSourceAsync(obs.Id, obs.Formats[0]);
        await obsSource.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using (var frames = obsSource.ReadFramesAsync(timeout.Token).GetAsyncEnumerator(timeout.Token))
        {
            Assert.True(await frames.MoveNextAsync());
            using var frame = frames.Current;
            output.WriteLine(
                $"OBS_FRAME id={frame.DeviceId} format={frame.Format.Width}x{frame.Format.Height}/{frame.Format.PixelFormat} " +
                $"sequence={frame.Sequence} qpc={frame.Timestamp.Ticks}/{frame.Timestamp.Frequency} {DescribePayload(frame)}");
        }

        var physical = devices.FirstOrDefault(device =>
            device.Kind == VideoDeviceKind.PhysicalCamera && device.Formats.Count > 0);
        if (physical is not null)
        {
            await using var physicalSource = await adapter.OpenSourceAsync(physical.Id, physical.Formats[0]);
            await physicalSource.StartAsync();
            await using var frames = physicalSource.ReadFramesAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
            Assert.True(await frames.MoveNextAsync());
            using var frame = frames.Current;
            output.WriteLine(
                $"PHYSICAL_FRAME id={frame.DeviceId} format={frame.Format.Width}x{frame.Format.Height}/{frame.Format.PixelFormat} " +
                $"sequence={frame.Sequence} qpc={frame.Timestamp.Ticks}/{frame.Timestamp.Frequency} {DescribePayload(frame)}");
            await physicalSource.StopAsync();
            output.WriteLine($"PHYSICAL_STOP state={physicalSource.State}; OBS_STATE={obsSource.State}");

            var physicalBgraFormats = physical.Formats
                .Where(format => format.PixelFormat == VideoPixelFormat.Bgra32)
                .OrderByDescending(format => (long)format.Width * format.Height)
                .ToArray();
            if (physicalBgraFormats.Length > 0)
            {
                var physicalBgra = physicalBgraFormats[0];
                await using var physicalBgraSource = await adapter.OpenSourceAsync(physical.Id, physicalBgra);
                await physicalBgraSource.StartAsync();
                using var physicalBgraTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                await using var physicalBgraFrames = physicalBgraSource.ReadFramesAsync(physicalBgraTimeout.Token).GetAsyncEnumerator(physicalBgraTimeout.Token);
                Assert.True(await physicalBgraFrames.MoveNextAsync());
                using var bgraFrame = physicalBgraFrames.Current;
                output.WriteLine(
                    $"PHYSICAL_BGRA_FRAME id={bgraFrame.DeviceId} format={bgraFrame.Format.Width}x{bgraFrame.Format.Height}/{bgraFrame.Format.PixelFormat} " +
                    $"sequence={bgraFrame.Sequence} qpc={bgraFrame.Timestamp.Ticks}/{bgraFrame.Timestamp.Frequency} {DescribePayload(bgraFrame)}");
                await physicalBgraSource.StopAsync();
                output.WriteLine($"PHYSICAL_BGRA_STOP state={physicalBgraSource.State}; OBS_STATE={obsSource.State}");
            }
        }
        else
        {
            output.WriteLine("PHYSICAL_CAMERA=unavailable");
        }

        await obsSource.StopAsync();
        output.WriteLine($"OBS_STOP state={obsSource.State}");

        var obsBgra = obs.Formats.First(format => format.PixelFormat == VideoPixelFormat.Bgra32);
        await using var bgraSource = await adapter.OpenSourceAsync(obs.Id, obsBgra);
        await bgraSource.StartAsync();
        using var bgraTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using (var frames = bgraSource.ReadFramesAsync(bgraTimeout.Token).GetAsyncEnumerator(bgraTimeout.Token))
        {
            Assert.True(await frames.MoveNextAsync());
            using var frame = frames.Current;
            output.WriteLine(
                $"OBS_BGRA_FRAME id={frame.DeviceId} format={frame.Format.Width}x{frame.Format.Height}/{frame.Format.PixelFormat} " +
                $"sequence={frame.Sequence} qpc={frame.Timestamp.Ticks}/{frame.Timestamp.Frequency} {DescribePayload(frame)}");
        }
        await bgraSource.StopAsync();
    }

    private static string DescribePayload(VideoFrame frame)
    {
        Assert.True(frame.HasPayload);
        Assert.Equal(VideoFrame.GetRequiredBufferLength(frame.Format), frame.Data.Length);
        Assert.True(frame.Data.Span.IndexOfAnyExcept(frame.Data.Span[0]) >= 0, "Live frame payload must contain varying pixel bytes.");
        var checksum = Convert.ToHexString(SHA256.HashData(frame.Data.Span));
        var planes = string.Join(",", frame.Planes.Select(plane => $"{plane.Offset}:{plane.Length}:{plane.Stride}"));
        return $"bytes={frame.Data.Length} planes={planes} sha256={checksum}";
    }
}
