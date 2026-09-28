using BetterDemo.Core.Contracts;
using BetterDemo.Interop.MediaFoundation;
using Xunit;

namespace BetterDemo.Integration.Tests;

public sealed class VideoDeviceClassificationTests
{
    [Theory]
    [InlineData("OBS Virtual Camera")]
    [InlineData("OBS Virtual Camera (Windows Virtual Camera)")]
    public void Obs_virtual_camera_is_classified_as_the_primary_source(string displayName)
    {
        Assert.Equal(VideoDeviceKind.ObsVirtualCamera, MediaFoundationVideoDeviceClassifier.Classify(displayName));
    }

    [Theory]
    [InlineData("HD WebCam 2MP")]
    [InlineData("Logitech HD Pro Webcam C920")]
    public void Non_virtual_camera_is_classified_as_physical(string displayName)
    {
        Assert.Equal(VideoDeviceKind.PhysicalCamera, MediaFoundationVideoDeviceClassifier.Classify(displayName));
    }

    [Theory]
    [InlineData("Meta Quest 3S (Windows Virtual Camera)")]
    [InlineData("Virtual Camera")]
    [InlineData("Камера Meta Quest (Виртуальная камера Windows)")]
    public void Other_virtual_camera_is_not_misreported_as_a_physical_webcam(string displayName)
    {
        Assert.Null(MediaFoundationVideoDeviceClassifier.Classify(displayName));
    }

    [Fact]
    public void Classification_rejects_null_device_name()
    {
        Assert.Throws<ArgumentNullException>(() => MediaFoundationVideoDeviceClassifier.Classify(null!));
    }
}
