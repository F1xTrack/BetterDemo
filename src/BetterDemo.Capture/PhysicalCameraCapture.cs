using BetterDemo.Core.Contracts;
using BetterDemo.Interop.MediaFoundation;

namespace BetterDemo.Capture;

public sealed class PhysicalCameraCapture : CameraCaptureBase
{
    private readonly VideoDeviceId deviceId;

    public PhysicalCameraCapture(
        IMediaFoundationVideoAdapter adapter,
        VideoDeviceId deviceId,
        VideoFrameFormat requestedFormat,
        IDiagnosticsSink? diagnostics = null,
        int queueCapacity = 3,
        TimeSpan? stopTimeout = null)
        : base(
            adapter,
            new VideoDeviceDescriptor(deviceId, VideoDeviceKind.PhysicalCamera, "Physical camera", []),
            requestedFormat,
            diagnostics,
            queueCapacity,
            stopTimeout)
    {
        this.deviceId = deviceId;
    }

    protected override string ComponentName => nameof(PhysicalCameraCapture);

    protected override VideoDeviceDescriptor? SelectDevice(IReadOnlyList<VideoDeviceDescriptor> devices) =>
        devices.FirstOrDefault(device => device.Kind == VideoDeviceKind.PhysicalCamera && device.Id == deviceId);
}
