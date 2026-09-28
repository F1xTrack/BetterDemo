using BetterDemo.Core.Contracts;
using BetterDemo.Core.Scene;
using BetterDemo.Remote.Protocol;
using Xunit;

namespace BetterDemo.Core.Tests;

public sealed class RemoteStateTests
{
    [Fact]
    public void Local_source_visibility_changes_are_included_in_state_snapshots()
    {
        var state = new RemoteStateStore(new OutputWindowId("source-visibility-test"));
        var layerId = SceneDocumentFactory.PhysicalCameraLayerId.Value;

        var hidden = state.UpdateLocalLayerVisibility(layerId, isVisible: false);
        var visible = state.UpdateLocalLayerVisibility(layerId, isVisible: true);

        Assert.False(hidden.LayerVisibility[layerId]);
        Assert.True(visible.LayerVisibility[layerId]);
        Assert.True(visible.StateSequence > hidden.StateSequence);
    }
}
