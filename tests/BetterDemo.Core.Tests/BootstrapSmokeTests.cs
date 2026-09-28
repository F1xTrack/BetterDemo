using BetterDemo.Core;
using Xunit;

namespace BetterDemo.Core.Tests;

public sealed class BootstrapSmokeTests
{
    [Fact]
    public void Bootstrap_shell_reports_ready()
    {
        Assert.True(BootstrapStatus.IsReady);
        Assert.Equal("W0.1", BootstrapStatus.Version);
    }
}
