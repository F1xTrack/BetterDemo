using BetterDemo.Core.Scene;
using Xunit;

namespace BetterDemo.Core.Tests;

public sealed class SceneDocumentTests
{
    [Fact]
    public void Layers_are_ordered_by_z_index_and_reject_duplicate_identity()
    {
        var back = CreateLayer("back", 10);
        var front = CreateLayer("front", 20);

        var document = new SceneDocument(new[] { front, back });

        Assert.Equal(new[] { "back", "front" }, document.Layers.Select(layer => layer.Id.Value));
        Assert.Equal(new[] { 10, 20 }, document.Layers.Select(layer => layer.ZIndex));
        Assert.Throws<ArgumentException>(() => new SceneDocument(new[] { back, CreateLayer("back", 30) }));
        Assert.Throws<ArgumentException>(() => new SceneDocument(new[] { back, CreateLayer("other", 10) }));
    }

    [Fact]
    public void Normalized_transform_rejects_nonfinite_or_out_of_bounds_values()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NormalizedTransform(
            double.NaN, 0.1, 0.25, 0.25));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NormalizedTransform(
            0.1, double.PositiveInfinity, 0.25, 0.25));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NormalizedTransform(
            1.01, 0.1, 0.25, 0.25));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NormalizedTransform(
            0.1, 0.1, 0, 0.25));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NormalizedTransform(
            0.1, 0.1, 0.25, 0.25, 361));
    }

    [Fact]
    public void Layer_properties_preserve_opacity_visibility_lock_and_asset_reference()
    {
        var asset = new SceneAssetReference("image-1", "assets/image-1.png");
        var layer = new SceneLayer(
            new SceneLayerId("layer-1"),
            SceneLayerKind.Image,
            0,
            new NormalizedTransform(0.1, 0.2, 0.3, 0.4, 15, 1.1, 0.9),
            opacity: 0.65,
            isVisible: false,
            isLocked: true,
            assetReference: asset);

        Assert.Equal(0.65, layer.Opacity);
        Assert.False(layer.IsVisible);
        Assert.True(layer.IsLocked);
        Assert.Equal(asset, layer.AssetReference);
    }

    [Fact]
    public void Layer_and_asset_ids_are_stable_value_objects()
    {
        Assert.Equal(new SceneLayerId("layer-1"), new SceneLayerId("layer-1"));
        Assert.NotEqual(new SceneLayerId("layer-1"), new SceneLayerId("layer-2"));
        Assert.Equal(
            new SceneAssetReference("asset-1", "assets/one.png"),
            new SceneAssetReference("asset-1", "assets/one.png"));
        Assert.Throws<ArgumentException>(() => new SceneLayerId(" "));
        Assert.Throws<ArgumentException>(() => new SceneAssetReference(" ", "assets/one.png"));
    }

    private static SceneLayer CreateLayer(string id, int zIndex) => new(
        new SceneLayerId(id),
        SceneLayerKind.Image,
        zIndex,
        new NormalizedTransform(0.1, 0.1, 0.25, 0.25),
        assetReference: new SceneAssetReference($"asset-{id}", $"assets/{id}.png"));
}
