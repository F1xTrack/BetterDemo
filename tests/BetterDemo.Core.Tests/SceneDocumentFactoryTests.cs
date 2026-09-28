using BetterDemo.Core.Contracts;
using BetterDemo.Core.Scene;
using Xunit;

namespace BetterDemo.Core.Tests;

public sealed class SceneDocumentFactoryTests
{
    [Fact]
    public void Built_in_modes_use_stable_video_and_color_layer_ids()
    {
        var obs = SceneDocumentFactory.CreatePreset(SceneMode.Screen);
        var physical = SceneDocumentFactory.CreatePreset(SceneMode.PhysicalCamera);
        var black = SceneDocumentFactory.CreatePreset(SceneMode.Black);

        Assert.Equal(SceneDocumentFactory.ObsVirtualCameraLayerId, Assert.Single(obs.Layers).Id);
        Assert.Equal(SceneLayerKind.Video, obs.Layers[0].Kind);
        Assert.Equal(SceneDocumentFactory.PhysicalCameraLayerId, Assert.Single(physical.Layers).Id);
        Assert.Equal(SceneLayerKind.Video, physical.Layers[0].Kind);
        Assert.Equal(SceneDocumentFactory.BlackBackgroundLayerId, Assert.Single(black.Layers).Id);
        Assert.Equal(SceneRgbaColor.Black, black.Layers[0].FillColor);
    }

    [Fact]
    public void Corner_preset_places_physical_camera_above_obs_using_the_requested_layout()
    {
        var corner = new CameraCornerLayout(scale: 0.2, margin: 0.1, rotationDegrees: 12);
        var document = SceneDocumentFactory.CreatePreset(SceneMode.ScreenPlusPhysicalCameraCorner, corner);

        Assert.Equal(new[]
        {
            SceneDocumentFactory.ObsVirtualCameraLayerId,
            SceneDocumentFactory.PhysicalCameraLayerId
        }, document.Layers.Select(layer => layer.Id));
        Assert.Equal(new[] { 0, 1 }, document.Layers.Select(layer => layer.ZIndex));
        Assert.Equal(new NormalizedTransform(0.7, 0.1, 0.2, 0.2, 12), document.Layers[1].Transform);
    }

    [Fact]
    public void Unknown_preset_mode_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SceneDocumentFactory.CreatePreset((SceneMode)99));
    }

    [Fact]
    public void Visibility_overrides_change_matching_layers_and_ignore_unknown_layers()
    {
        var document = SceneDocumentFactory.CreatePreset(SceneMode.ScreenPlusPhysicalCameraCorner);

        SceneDocumentFactory.ApplyVisibilityOverrides(document, new Dictionary<string, bool>
        {
            [SceneDocumentFactory.ObsVirtualCameraLayerId.Value] = false,
            ["preset:missing-layer"] = false
        });

        Assert.False(document.GetLayer(SceneDocumentFactory.ObsVirtualCameraLayerId).IsVisible);
        Assert.True(document.GetLayer(SceneDocumentFactory.PhysicalCameraLayerId).IsVisible);
    }
}
