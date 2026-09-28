using System.Buffers;
using System.Collections.ObjectModel;

namespace BetterDemo.Core.Contracts;

public enum VideoDeviceKind
{
    ObsVirtualCamera,
    PhysicalCamera
}

public readonly record struct VideoDeviceId
{
    public VideoDeviceId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A stable video device ID is required.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public enum VideoPixelFormat
{
    Nv12,
    Bgra32
}

public readonly record struct VideoFrameFormat
{
    public VideoFrameFormat(int width, int height, VideoPixelFormat pixelFormat, int stride)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (stride <= 0) throw new ArgumentOutOfRangeException(nameof(stride));

        Width = width;
        Height = height;
        PixelFormat = pixelFormat;
        Stride = stride;
    }

    public int Width { get; }
    public int Height { get; }
    public VideoPixelFormat PixelFormat { get; }
    public int Stride { get; }
}

public sealed record VideoDeviceDescriptor(
    VideoDeviceId Id,
    VideoDeviceKind Kind,
    string DisplayName,
    IReadOnlyList<VideoFrameFormat> Formats);

public readonly record struct QpcTimestamp
{
    public QpcTimestamp(long ticks, long frequency)
    {
        if (ticks <= 0) throw new ArgumentOutOfRangeException(nameof(ticks));
        if (frequency <= 0) throw new ArgumentOutOfRangeException(nameof(frequency));

        Ticks = ticks;
        Frequency = frequency;
    }

    public long Ticks { get; }
    public long Frequency { get; }
}

public readonly record struct VideoFrameStamp(ulong Sequence, QpcTimestamp Timestamp);

/// <summary>
/// Describes one logical image plane inside an owned <see cref="VideoFrame"/> payload.
/// </summary>
public readonly record struct VideoFramePlane
{
    public VideoFramePlane(int offset, int length, int stride, int width, int height)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));
        if (stride <= 0) throw new ArgumentOutOfRangeException(nameof(stride));
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

        Offset = offset;
        Length = length;
        Stride = stride;
        Width = width;
        Height = height;
    }

    public int Offset { get; }
    public int Length { get; }
    public int Stride { get; }
    public int Width { get; }
    public int Height { get; }
}

public static class VideoFrameOrdering
{
    public static bool IsStrictlyNewer(VideoFrameStamp candidate, VideoFrameStamp previous) =>
        candidate.Sequence > previous.Sequence && candidate.Timestamp.Ticks > previous.Timestamp.Ticks;
}

public static class VideoFrameFreshness
{
    public static bool IsFresh(
        VideoFrameStamp stamp,
        long nowTicks,
        long nowFrequency,
        TimeSpan maximumAge)
    {
        if (nowTicks <= 0) throw new ArgumentOutOfRangeException(nameof(nowTicks));
        if (nowFrequency <= 0) throw new ArgumentOutOfRangeException(nameof(nowFrequency));
        if (maximumAge <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maximumAge));

        var ageSeconds = nowTicks / (double)nowFrequency - stamp.Timestamp.Ticks / (double)stamp.Timestamp.Frequency;
        return ageSeconds >= 0 && ageSeconds <= maximumAge.TotalSeconds;
    }
}

public sealed class VideoFrame : IDisposable
{
    private readonly object lifetimeGate = new();
    private readonly int dataLength;
    private IMemoryOwner<byte>? memoryOwner;
    private int disposed;

    public VideoFrame(VideoDeviceId deviceId, VideoFrameFormat format, VideoFrameStamp stamp)
    {
        DeviceId = deviceId;
        Format = format;
        Stamp = stamp;
        Planes = Array.Empty<VideoFramePlane>();
    }

    private VideoFrame(
        VideoDeviceId deviceId,
        VideoFrameFormat format,
        VideoFrameStamp stamp,
        IMemoryOwner<byte> memoryOwner,
        int dataLength,
        IReadOnlyList<VideoFramePlane> planes)
    {
        DeviceId = deviceId;
        Format = format;
        Stamp = stamp;
        this.memoryOwner = memoryOwner;
        this.dataLength = dataLength;
        Planes = planes;
    }

    public VideoDeviceId DeviceId { get; }
    public VideoFrameFormat Format { get; }
    public VideoFrameStamp Stamp { get; }
    public ulong Sequence => Stamp.Sequence;
    public QpcTimestamp Timestamp => Stamp.Timestamp;
    public bool HasPayload => memoryOwner is not null && !IsDisposed;
    public bool IsDisposed => Volatile.Read(ref disposed) != 0;
    public IReadOnlyList<VideoFramePlane> Planes { get; }

    /// <summary>
    /// Gets the owned image bytes. The memory is valid until this frame is disposed.
    /// </summary>
    public ReadOnlyMemory<byte> Data
    {
        get
        {
            lock (lifetimeGate)
            {
                ObjectDisposedException.ThrowIf(IsDisposed, this);
                if (memoryOwner is null)
                {
                    throw new InvalidOperationException("This frame has no pixel payload.");
                }

                return memoryOwner.Memory[..dataLength];
            }
        }
    }

    /// <summary>
    /// Copies pixel bytes into pooled memory owned by the returned frame.
    /// </summary>
    public static VideoFrame CopyFrom(
        VideoDeviceId deviceId,
        VideoFrameFormat format,
        VideoFrameStamp stamp,
        ReadOnlySpan<byte> pixels)
    {
        var requiredLength = GetRequiredBufferLength(format);
        if (pixels.Length < requiredLength)
        {
            throw new ArgumentException(
                $"The pixel buffer contains {pixels.Length} bytes; {requiredLength} are required.",
                nameof(pixels));
        }

        var owner = MemoryPool<byte>.Shared.Rent(requiredLength);
        try
        {
            pixels[..requiredLength].CopyTo(owner.Memory.Span);
            return new VideoFrame(
                deviceId,
                format,
                stamp,
                owner,
                requiredLength,
                CreatePlanes(format));
        }
        catch
        {
            owner.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Returns the exact number of bytes required by the packed CPU frame layout.
    /// </summary>
    public static int GetRequiredBufferLength(VideoFrameFormat format) => format.PixelFormat switch
    {
        VideoPixelFormat.Bgra32 => checked(format.Stride * format.Height),
        VideoPixelFormat.Nv12 => checked(format.Stride * format.Height + format.Stride * ((format.Height + 1) / 2)),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format.PixelFormat, "Unsupported pixel format.")
    };

    /// <summary>
    /// Gets one plane's bytes. The memory is valid until this frame is disposed.
    /// </summary>
    public ReadOnlyMemory<byte> GetPlane(int index)
    {
        var plane = Planes[index];
        return Data.Slice(plane.Offset, plane.Length);
    }

    public void Dispose()
    {
        lock (lifetimeGate)
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            memoryOwner?.Dispose();
            memoryOwner = null;
        }
    }

    private static IReadOnlyList<VideoFramePlane> CreatePlanes(VideoFrameFormat format)
    {
        if (format.PixelFormat == VideoPixelFormat.Bgra32)
        {
            return new ReadOnlyCollection<VideoFramePlane>(
                [new VideoFramePlane(0, checked(format.Stride * format.Height), format.Stride, format.Width, format.Height)]);
        }

        var yLength = checked(format.Stride * format.Height);
        var chromaHeight = (format.Height + 1) / 2;
        return new ReadOnlyCollection<VideoFramePlane>(
        [
            new VideoFramePlane(0, yLength, format.Stride, format.Width, format.Height),
            new VideoFramePlane(yLength, checked(format.Stride * chromaHeight), format.Stride, (format.Width + 1) / 2, chromaHeight)
        ]);
    }
}
