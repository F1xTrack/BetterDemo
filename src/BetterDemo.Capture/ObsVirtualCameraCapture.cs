using BetterDemo.Core.Contracts;
using BetterDemo.Interop.MediaFoundation;

namespace BetterDemo.Capture;

public sealed class ObsVirtualCameraCapture : CameraCaptureBase
{
    private static readonly VideoDeviceDescriptor InitialDevice = new(
        new VideoDeviceId("mf://obs-virtual-camera/unavailable"),
        VideoDeviceKind.ObsVirtualCamera,
        "OBS Virtual Camera",
        []);

    public ObsVirtualCameraCapture(
        IMediaFoundationVideoAdapter adapter,
        VideoFrameFormat requestedFormat,
        IDiagnosticsSink? diagnostics = null,
        int queueCapacity = 3,
        TimeSpan? stopTimeout = null)
        : base(adapter, InitialDevice, requestedFormat, diagnostics, queueCapacity, stopTimeout)
    {
    }

    protected override string ComponentName => nameof(ObsVirtualCameraCapture);

    protected override VideoDeviceDescriptor? SelectDevice(IReadOnlyList<VideoDeviceDescriptor> devices) =>
        devices.FirstOrDefault(device => device.Kind == VideoDeviceKind.ObsVirtualCamera);
}
