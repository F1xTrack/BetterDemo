using BetterDemo.Core.Contracts;
using BetterDemo.Interop.MediaFoundation;

namespace BetterDemo.Capture;

public sealed class VideoDeviceEnumerator
{
    private readonly IMediaFoundationVideoAdapter adapter;

    public VideoDeviceEnumerator(IMediaFoundationVideoAdapter adapter)
    {
        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
    }

    public async ValueTask<IReadOnlyList<VideoDeviceDescriptor>> EnumerateAsync(
        CancellationToken cancellationToken = default)
    {
        var devices = await adapter.EnumerateDevicesAsync(cancellationToken).ConfigureAwait(false);
        return devices
            .OrderBy(device => device.Kind)
            .ThenBy(device => device.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(device => device.Id.Value, StringComparer.Ordinal)
            .ToArray();
    }

    public async ValueTask<VideoDeviceDescriptor?> FindObsVirtualCameraAsync(
        CancellationToken cancellationToken = default)
    {
        var devices = await EnumerateAsync(cancellationToken).ConfigureAwait(false);
        return devices.FirstOrDefault(device => device.Kind == VideoDeviceKind.ObsVirtualCamera);
    }

    public async ValueTask<IReadOnlyList<VideoDeviceDescriptor>> EnumeratePhysicalCamerasAsync(
        CancellationToken cancellationToken = default)
    {
        var devices = await EnumerateAsync(cancellationToken).ConfigureAwait(false);
        return devices.Where(device => device.Kind == VideoDeviceKind.PhysicalCamera).ToArray();
    }
}
