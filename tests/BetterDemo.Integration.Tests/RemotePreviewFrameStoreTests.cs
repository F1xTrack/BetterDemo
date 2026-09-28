using BetterDemo.Remote;
using Xunit;

namespace BetterDemo.Integration.Tests;

public sealed class RemotePreviewFrameStoreTests
{
    [Fact]
    public void Store_keeps_only_the_latest_owned_jpeg_and_can_be_cleared()
    {
        var store = new RemotePreviewFrameStore();
        Assert.False(store.TryGetLatest(out _));

        byte[] first = [0xFF, 0xD8, 0x01, 0xFF, 0xD9];
        Assert.Equal(1, store.Publish(first));
        first[2] = 0x55;

        byte[] second = [0xFF, 0xD8, 0x02, 0xFF, 0xD9];
        Assert.Equal(2, store.Publish(second));
        Assert.True(store.TryGetLatest(out var frame));
        Assert.NotNull(frame);
        Assert.Equal(2, frame.Sequence);
        Assert.Equal(second, frame.JpegBytes.ToArray());

        store.Clear();
        Assert.False(store.TryGetLatest(out _));
        Assert.Equal(3, store.Publish(second));
    }

    [Fact]
    public void Store_rejects_non_jpeg_and_oversized_frames()
    {
        var store = new RemotePreviewFrameStore();
        Assert.Throws<ArgumentException>(() => store.Publish(new byte[] { 0, 1, 2, 3 }));

        var oversized = new byte[RemotePreviewFrameStore.MaximumFrameBytes + 1];
        oversized[0] = 0xFF;
        oversized[1] = 0xD8;
        oversized[^2] = 0xFF;
        oversized[^1] = 0xD9;
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Publish(oversized));
    }
}
