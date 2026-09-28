using BetterDemo.Core.Contracts;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.Versioning;

namespace BetterDemo.Interop.MediaFoundation;

[SupportedOSPlatform("windows")]
public sealed class MediaFoundationVideoAdapter : IMediaFoundationVideoAdapter
{
    private bool disposed;

    public ValueTask<IReadOnlyList<VideoDeviceDescriptor>> EnumerateDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var nativeDevices = EnumerateNativeDevices(includeFormats: true, cancellationToken);
        return ValueTask.FromResult<IReadOnlyList<VideoDeviceDescriptor>>(
            nativeDevices.Select(device => device.Descriptor).ToArray());
    }

    public ValueTask<IVideoSource> OpenSourceAsync(
        VideoDeviceId deviceId,
        VideoFrameFormat format,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var nativeDevice = EnumerateNativeDevices(includeFormats: true, cancellationToken)
            .SingleOrDefault(device => device.Descriptor.Id == deviceId);
        if (nativeDevice is null)
        {
            throw new InvalidOperationException($"Video device '{deviceId}' is unavailable.");
        }

        if (!nativeDevice.Descriptor.Formats.Contains(format))
        {
            throw new NotSupportedException($"Video device '{deviceId}' does not expose the requested format.");
        }

        return ValueTask.FromResult<IVideoSource>(
            new MediaFoundationVideoSource(nativeDevice.Descriptor, nativeDevice.SymbolicLink, format));
    }

    public ValueTask DisposeAsync()
    {
        disposed = true;
        return ValueTask.CompletedTask;
    }

    internal static IReadOnlyList<NativeVideoDevice> EnumerateNativeDevices(
        bool includeFormats,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Media Foundation camera capture requires Windows.");
        }

        var comInitialized = MediaFoundationNative.CoInitializeEx(0, 0) >= 0;
        MediaFoundationNative.ThrowIfFailed(
            MediaFoundationNative.MFStartup(MediaFoundationNative.MfVersion, MediaFoundationNative.MfStartupFull),
            "MFStartup");
        IMFAttributes? attributes = null;
        nint activateArray = 0;
        uint count = 0;
        try
        {
            MediaFoundationNative.ThrowIfFailed(MediaFoundationNative.MFCreateAttributes(out attributes, 1), "MFCreateAttributes");
            MediaFoundationNative.ThrowIfFailed(
                attributes.SetGUID(
                    MediaFoundationNative.MfDevSourceAttributeSourceType,
                    MediaFoundationNative.MfDevSourceAttributeSourceTypeVideoCaptureGuid),
                "IMFAttributes.SetGUID");
            MediaFoundationNative.ThrowIfFailed(
                MediaFoundationNative.MFEnumDeviceSources(attributes, out activateArray, out count),
                "MFEnumDeviceSources");

            var devices = new List<NativeVideoDevice>((int)count);
            for (var index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var unknown = Marshal.ReadIntPtr(activateArray, checked((int)(index * (uint)nint.Size)));
                IMFActivate? activate = null;
                try
                {
                    activate = (IMFActivate)Marshal.GetObjectForIUnknown(unknown);
                    var displayName = MediaFoundationNative.GetAllocatedString(
                        activate,
                        MediaFoundationNative.MfDevSourceAttributeFriendlyName);
                    var symbolicLink = MediaFoundationNative.GetAllocatedString(
                        activate,
                        MediaFoundationNative.MfDevSourceAttributeSourceTypeVidcapSymbolicLink);
                    var formats = includeFormats ? ReadFormats(activate) : Array.Empty<VideoFrameFormat>();
                    var kind = MediaFoundationVideoDeviceClassifier.Classify(displayName);
                    if (kind is null)
                    {
                        continue;
                    }

                    devices.Add(new NativeVideoDevice(
                        new VideoDeviceDescriptor(CreateStableId(symbolicLink), kind.Value, displayName, formats),
                        symbolicLink));
                }
                finally
                {
                    MediaFoundationNative.FinalRelease(activate);
                    Marshal.Release(unknown);
                    Marshal.WriteIntPtr(activateArray, checked((int)(index * (uint)nint.Size)), 0);
                }
            }

            return devices;
        }
        finally
        {
            if (activateArray != 0)
            {
                ReleaseActivationPointers(activateArray, count);
                Marshal.FreeCoTaskMem(activateArray);
            }

            MediaFoundationNative.FinalRelease(attributes);
            MediaFoundationNative.MFShutdown();
            if (comInitialized)
            {
                MediaFoundationNative.CoUninitialize();
            }
        }
    }

    private static IReadOnlyList<VideoFrameFormat> ReadFormats(IMFActivate activate)
    {
        IMFMediaSource? source = null;
        IMFSourceReader? reader = null;
        try
        {
            MediaFoundationNative.ThrowIfFailed(
                activate.ActivateObject(MediaFoundationNative.ImfMediaSourceGuid, out var sourceObject),
                "IMFActivate.ActivateObject");
            source = (IMFMediaSource)sourceObject;
            MediaFoundationNative.ThrowIfFailed(
                MediaFoundationNative.MFCreateSourceReaderFromMediaSource(source, null, out reader),
                "MFCreateSourceReaderFromMediaSource");

            var sizes = new HashSet<(int Width, int Height)>();
            for (uint mediaTypeIndex = 0; ; mediaTypeIndex++)
            {
                var result = reader.GetNativeMediaType(
                    MediaFoundationNative.SourceReaderFirstVideoStream,
                    mediaTypeIndex,
                    out var mediaType);
                if (result == MediaFoundationNative.MfENoMoreTypes)
                {
                    break;
                }

                MediaFoundationNative.ThrowIfFailed(result, "IMFSourceReader.GetNativeMediaType");
                try
                {
                    if (mediaType.GetUINT64(MediaFoundationNative.MfMtFrameSize, out var packedSize) >= 0)
                    {
                        var width = checked((int)(packedSize >> 32));
                        var height = checked((int)(packedSize & uint.MaxValue));
                        if (width > 0 && height > 0)
                        {
                            sizes.Add((width, height));
                        }
                    }
                }
                finally
                {
                    MediaFoundationNative.FinalRelease(mediaType);
                }
            }

            return sizes
                .SelectMany(size => new[]
                {
                    new VideoFrameFormat(size.Width, size.Height, VideoPixelFormat.Nv12, size.Width),
                    new VideoFrameFormat(size.Width, size.Height, VideoPixelFormat.Bgra32, checked(size.Width * 4))
                })
                .OrderBy(format => format.Width)
                .ThenBy(format => format.Height)
                .ThenBy(format => format.PixelFormat)
                .ToArray();
        }
        catch (COMException)
        {
            return Array.Empty<VideoFrameFormat>();
        }
        finally
        {
            source?.Shutdown();
            MediaFoundationNative.FinalRelease(reader);
            MediaFoundationNative.FinalRelease(source);
        }
    }

    private static VideoDeviceId CreateStableId(string symbolicLink)
    {
        var hash = SHA256.HashData(Encoding.Unicode.GetBytes(symbolicLink));
        return new VideoDeviceId($"mf://video/{Convert.ToHexString(hash)}");
    }

    private static void ReleaseActivationPointers(nint activateArray, uint count)
    {
        for (var index = 0; index < count; index++)
        {
            var unknown = Marshal.ReadIntPtr(activateArray, checked((int)(index * (uint)nint.Size)));
            if (unknown != 0)
            {
                Marshal.Release(unknown);
            }
        }
    }

    internal sealed record NativeVideoDevice(VideoDeviceDescriptor Descriptor, string SymbolicLink);
}

internal static class MediaFoundationVideoDeviceClassifier
{
    internal static VideoDeviceKind? Classify(string displayName)
    {
        ArgumentNullException.ThrowIfNull(displayName);

        if (displayName.Contains("OBS Virtual Camera", StringComparison.OrdinalIgnoreCase))
        {
            return VideoDeviceKind.ObsVirtualCamera;
        }

        var identifiesCamera = displayName.Contains("camera", StringComparison.OrdinalIgnoreCase) ||
                               displayName.Contains("камера", StringComparison.OrdinalIgnoreCase);
        var identifiesVirtualDevice = displayName.Contains("virtual", StringComparison.OrdinalIgnoreCase) ||
                                      displayName.Contains("виртуал", StringComparison.OrdinalIgnoreCase);
        return identifiesCamera && identifiesVirtualDevice
            ? null
            : VideoDeviceKind.PhysicalCamera;
    }
}
