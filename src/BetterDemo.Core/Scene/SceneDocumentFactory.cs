using BetterDemo.Core.Contracts;

namespace BetterDemo.Core.Scene;

/// <summary>Builds the built-in scenes from the same layer model used by saved user scenes.</summary>
public static class SceneDocumentFactory
{
    public static readonly SceneLayerId ObsVirtualCameraLayerId = new("preset:obs-virtual-camera");
    public static readonly SceneLayerId PhysicalCameraLayerId = new("preset:physical-camera");
    public static readonly SceneLayerId BlackBackgroundLayerId = new("preset:black-background");

    public static SceneDocument CreatePreset(SceneMode mode, CameraCornerLayout? cameraCorner = null)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));

        SceneLayer[] layers = mode switch
        {
            SceneMode.Screen => [CreateVideoLayer(ObsVirtualCameraLayerId, "obs-virtual-camera", 0, FullFrame())],
            SceneMode.PhysicalCamera => [CreateVideoLayer(PhysicalCameraLayerId, "physical-camera", 0, FullFrame())],
            SceneMode.ScreenPlusPhysicalCameraCorner => CreateCornerLayers(cameraCorner ?? CameraCornerLayout.Default),
            SceneMode.Black => [new SceneLayer(
                BlackBackgroundLayerId,
                SceneLayerKind.Color,
                0,
                FullFrame(),
                fillColor: SceneRgbaColor.Black)],
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };

        return new SceneDocument(layers);
    }

    public static SceneDocument ApplyVisibilityOverrides(
        SceneDocument document,
        IReadOnlyDictionary<string, bool> visibilityOverrides)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(visibilityOverrides);

        foreach (var layer in document.Layers.ToArray())
        {
            if (visibilityOverrides.TryGetValue(layer.Id.Value, out var isVisible) && layer.IsVisible != isVisible)
                document.SetLayerVisibility(layer.Id, isVisible);
        }

        return document;
    }

    private static SceneLayer[] CreateCornerLayers(CameraCornerLayout layout)
    {
        var transform = new NormalizedTransform(
            1 - layout.Margin - layout.Scale,
            layout.Margin,
            layout.Scale,
            layout.Scale,
            layout.RotationDegrees);
        return
        [
            CreateVideoLayer(ObsVirtualCameraLayerId, "obs-virtual-camera", 0, FullFrame()),
            CreateVideoLayer(PhysicalCameraLayerId, "physical-camera", 1, transform)
        ];
    }

    private static SceneLayer CreateVideoLayer(SceneLayerId id, string assetId, int zIndex, NormalizedTransform transform) => new(
        id,
        SceneLayerKind.Video,
        zIndex,
        transform,
        assetReference: new SceneAssetReference(assetId, $"source://{assetId}"));

    private static NormalizedTransform FullFrame() => new(0, 0, 1, 1);
}
