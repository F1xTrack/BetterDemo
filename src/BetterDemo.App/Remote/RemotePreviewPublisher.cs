using System.Diagnostics;
using System.Threading.Channels;
using BetterDemo.Core.Scene;
using BetterDemo.Remote;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace BetterDemo.App.Remote;

/// <summary>Throttles and JPEG-encodes the same composed frames used by the desktop output.</summary>
public sealed class RemotePreviewPublisher : IAsyncDisposable
{
    private const int MaximumWidth = 640;
    private static readonly TimeSpan PublishInterval = TimeSpan.FromMilliseconds(200);

    private readonly RemotePreviewFrameStore destination;
    private readonly Channel<PreviewPixels> pending = Channel.CreateBounded<PreviewPixels>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });
    private readonly Task encoderTask;
    private long lastQueuedTicks;
    private string? lastError;

    public RemotePreviewPublisher(RemotePreviewFrameStore destination)
    {
        this.destination = destination ?? throw new ArgumentNullException(nameof(destination));
        encoderTask = Task.Run(EncodeLoopAsync);
    }

    public string? LastError => Volatile.Read(ref lastError);

    public bool TryPublish(SceneComposition composition)
    {
        ArgumentNullException.ThrowIfNull(composition);
        var now = Stopwatch.GetTimestamp();
        while (true)
        {
            var previous = Volatile.Read(ref lastQueuedTicks);
            if (previous != 0 && Stopwatch.GetElapsedTime(previous, now) < PublishInterval)
                return false;
            if (Interlocked.CompareExchange(ref lastQueuedTicks, now, previous) == previous)
                break;
        }

        return pending.Writer.TryWrite(new PreviewPixels(
            composition.Width,
            composition.Height,
            composition.Pixels.ToArray()));
    }

    public async ValueTask DisposeAsync()
    {
        pending.Writer.TryComplete();
        await encoderTask.ConfigureAwait(false);
    }

    private async Task EncodeLoopAsync()
    {
        await foreach (var frame in pending.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                var jpeg = await EncodeJpegAsync(frame).ConfigureAwait(false);
                destination.Publish(jpeg);
                Volatile.Write(ref lastError, null);
            }
            catch (Exception exception)
            {
                Volatile.Write(ref lastError, exception.Message);
            }
        }
    }

    private static async Task<byte[]> EncodeJpegAsync(PreviewPixels frame)
    {
        var width = checked((uint)frame.Width);
        var height = checked((uint)frame.Height);
        var targetWidth = Math.Min(width, (uint)MaximumWidth);
        var targetHeight = Math.Max(1u, checked((uint)((ulong)height * targetWidth / width)));

        using var pixelWriter = new DataWriter();
        pixelWriter.WriteBytes(frame.BgraPixels);
        using var source = new SoftwareBitmap(BitmapPixelFormat.Bgra8, frame.Width, frame.Height, BitmapAlphaMode.Ignore);
        source.CopyFromBuffer(pixelWriter.DetachBuffer());

        using var stream = new InMemoryRandomAccessStream();
        var encoderOptions = new BitmapPropertySet
        {
            { "ImageQuality", new BitmapTypedValue(0.65, PropertyType.Single) }
        };
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream, encoderOptions);
        encoder.SetSoftwareBitmap(source);
        encoder.BitmapTransform.ScaledWidth = targetWidth;
        encoder.BitmapTransform.ScaledHeight = targetHeight;
        await encoder.FlushAsync();

        var encoded = new byte[checked((int)stream.Size)];
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync(checked((uint)encoded.Length));
        reader.ReadBytes(encoded);
        return encoded;
    }

    private sealed record PreviewPixels(int Width, int Height, byte[] BgraPixels);
}
