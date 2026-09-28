using BetterDemo.Core.Contracts;

namespace BetterDemo.Interop.MediaFoundation;

public interface IMediaFoundationVideoAdapter : IAsyncDisposable
{
    ValueTask<IReadOnlyList<VideoDeviceDescriptor>> EnumerateDevicesAsync(CancellationToken cancellationToken = default);

    ValueTask<IVideoSource> OpenSourceAsync(
        VideoDeviceId deviceId,
        VideoFrameFormat format,
        CancellationToken cancellationToken = default);
}
