using BetterDemo.Audio;
using BetterDemo.Capture;
using BetterDemo.Core;
using BetterDemo.Interop;
using BetterDemo.Remote;
using Xunit;

namespace BetterDemo.Integration.Tests;

public sealed class BootstrapCompositionTests
{
    [Fact]
    public void Bootstrap_boundaries_load_without_feature_dependencies()
    {
        Assert.True(BootstrapStatus.IsReady);
        Assert.Equal("BetterDemo.Audio", typeof(Audio.AssemblyMarker).Assembly.GetName().Name);
        Assert.Equal("BetterDemo.Capture", typeof(Capture.AssemblyMarker).Assembly.GetName().Name);
        Assert.Equal("BetterDemo.Interop", typeof(Interop.AssemblyMarker).Assembly.GetName().Name);
        Assert.Equal("BetterDemo.Remote", typeof(Remote.AssemblyMarker).Assembly.GetName().Name);
    }
}
