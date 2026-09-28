using BetterDemo.App.Remote;
using BetterDemo.Core.Contracts;
using BetterDemo.Core.Scene;
using BetterDemo.Remote;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Xunit;

namespace BetterDemo.Integration.Tests;

public sealed class RemotePreviewPublisherTests
{
    [Fact]
    public async Task Publisher_encodes_the_desktop_composition_as_a_downscaled_jpeg()
    {
        using var input = VideoFrame.CopyFrom(
            new VideoDeviceId("preview-test-camera"),
            new VideoFrameFormat(2, 2, VideoPixelFormat.Bgra32, 8),
            new VideoFrameStamp(1, new QpcTimestamp(1, 10_000_000)),
            [0x10, 0x20, 0xE0, 0xFF, 0x10, 0xD0, 0x20, 0xFF,
             0xE0, 0x20, 0x10, 0xFF, 0x20, 0xD0, 0xE0, 0xFF]);
        var composition = new SceneCompositor().Render(
            SceneMode.Screen,
            input,
            physicalFrame: null,
            new SceneRenderOptions(1280, 720));
        var store = new RemotePreviewFrameStore();
        var publisher = new RemotePreviewPublisher(store);

        Assert.True(publisher.TryPublish(composition));
        await publisher.DisposeAsync();

        Assert.Null(publisher.LastError);
        Assert.True(store.TryGetLatest(out var frame));
        Assert.NotNull(frame);
        Assert.InRange(frame.JpegBytes.Length, 16, RemotePreviewFrameStore.MaximumFrameBytes);

        using var stream = new InMemoryRandomAccessStream();
        using var writer = new DataWriter();
        writer.WriteBytes(frame.JpegBytes.ToArray());
        await stream.WriteAsync(writer.DetachBuffer());
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        Assert.Equal(640u, decoder.PixelWidth);
        Assert.Equal(360u, decoder.PixelHeight);
        var pixels = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);
        var bgra = pixels.DetachPixelData();

        AssertColor(bgra, 80, 80, red: true, green: false, blue: false);
        AssertColor(bgra, 560, 80, red: false, green: true, blue: false);
        AssertColor(bgra, 80, 300, red: false, green: false, blue: true);
        AssertColor(bgra, 560, 300, red: true, green: true, blue: false);
    }

    private static void AssertColor(byte[] bgra, int x, int y, bool red, bool green, bool blue)
    {
        var offset = checked((y * 640 + x) * 4);
        Assert.Equal(red, bgra[offset + 2] > 100);
        Assert.Equal(green, bgra[offset + 1] > 100);
        Assert.Equal(blue, bgra[offset] > 100);
    }
}
