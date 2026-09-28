namespace BetterDemo.Remote;

public sealed record RemotePreviewFrame(long Sequence, ReadOnlyMemory<byte> JpegBytes);

/// <summary>Holds only the newest bounded JPEG frame for paired LAN preview clients.</summary>
public sealed class RemotePreviewFrameStore
{
    public const int MaximumFrameBytes = 1_048_576;

    private readonly object gate = new();
    private byte[]? jpegBytes;
    private long sequence;

    public long Publish(ReadOnlyMemory<byte> frame)
    {
        if (frame.Length is < 4 or > MaximumFrameBytes)
            throw new ArgumentOutOfRangeException(nameof(frame), $"Preview frames must contain 4 to {MaximumFrameBytes} bytes.");
        if (frame.Span[0] != 0xFF || frame.Span[1] != 0xD8 ||
            frame.Span[^2] != 0xFF || frame.Span[^1] != 0xD9)
            throw new ArgumentException("Preview frames must be complete JPEG images.", nameof(frame));

        var owned = frame.ToArray();
        lock (gate)
        {
            sequence = checked(sequence + 1);
            jpegBytes = owned;
            return sequence;
        }
    }

    public bool TryGetLatest(out RemotePreviewFrame? frame)
    {
        lock (gate)
        {
            if (jpegBytes is null)
            {
                frame = null;
                return false;
            }

            frame = new RemotePreviewFrame(sequence, jpegBytes);
            return true;
        }
    }

    public void Clear()
    {
        lock (gate) jpegBytes = null;
    }
}
