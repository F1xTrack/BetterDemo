using BetterDemo.Core.Contracts;
using BetterDemo.Core.Scene;
using Xunit;

namespace BetterDemo.Core.Tests;

public sealed class SceneSerializationTests
{
    [Fact]
    public void Exactly_twenty_image_layers_round_trip_with_deterministic_ids_order_and_transforms()
    {
        var document = new SceneDocument(Enumerable.Range(0, 20).Select(index => new SceneLayer(
            new SceneLayerId($"image-layer-{index:00}"),
            SceneLayerKind.Image,
            index,
            new NormalizedTransform(index / 20d, (19 - index) / 20d, 0.04, 0.05, index - 10, 1 + index / 100d, 1 - index / 200d),
            opacity: 0.5 + index / 40d,
            isVisible: index % 2 == 0,
            isLocked: index % 3 == 0,
            assetReference: new SceneAssetReference($"image-asset-{index:00}", $"assets/image-{index:00}.png"))));

        var json = SceneSerializer.Serialize(document);
        var restored = SceneSerializer.Deserialize(json);

        Assert.Equal(20, restored.Layers.Count);
        Assert.Equal(json, SceneSerializer.Serialize(restored));
        for (var index = 0; index < 20; index++)
        {
            var expected = document.Layers[index];
            var actual = restored.Layers[index];
            Assert.Equal(SceneLayerKind.Image, actual.Kind);
            Assert.Equal(expected.Id, actual.Id);
            Assert.Equal(index, actual.ZIndex);
            Assert.Equal(expected.Transform, actual.Transform);
            Assert.Equal(expected.Opacity, actual.Opacity);
            Assert.Equal(expected.IsVisible, actual.IsVisible);
            Assert.Equal(expected.IsLocked, actual.IsLocked);
            Assert.Equal(expected.AssetReference, actual.AssetReference);
        }
    }

    [Fact]
    public void Scene_round_trip_is_deterministic_and_restores_order_and_transform()
    {
        var document = new SceneDocument(new[]
        {
            new SceneLayer(
                new SceneLayerId("front"),
                SceneLayerKind.Image,
                20,
                new NormalizedTransform(0.61, 0.12, 0.2, 0.3, -22.5, 1.2, 0.8),
                opacity: 0.72,
                isVisible: false,
                isLocked: true,
                assetReference: new SceneAssetReference("asset-front", "assets/front.png")),
            new SceneLayer(
                new SceneLayerId("back"),
                SceneLayerKind.Color,
                10,
                new NormalizedTransform(0, 0, 1, 1),
                opacity: 1,
                isVisible: true,
                isLocked: false,
                fillColor: SceneRgbaColor.Black)
        });

        var json = SceneSerializer.Serialize(document);
        var restored = SceneSerializer.Deserialize(json);

        Assert.Equal(SceneDocument.CurrentSchemaVersion, restored.SchemaVersion);
        Assert.Equal(new[] { "back", "front" }, restored.Layers.Select(layer => layer.Id.Value));
        Assert.Equal(document.Layers[1].Transform, restored.Layers[1].Transform);
        Assert.Equal(document.Layers[1].Opacity, restored.Layers[1].Opacity);
        Assert.Equal(document.Layers[1].IsVisible, restored.Layers[1].IsVisible);
        Assert.Equal(document.Layers[1].IsLocked, restored.Layers[1].IsLocked);
        Assert.Equal(document.Layers[1].FillColor, restored.Layers[1].FillColor);
        Assert.Equal(json, SceneSerializer.Serialize(restored));
    }

    [Fact]
    public void Text_and_color_content_round_trip_with_the_current_schema()
    {
        var fill = new SceneRgbaColor(12, 34, 56, 200);
        var foreground = new SceneRgbaColor(240, 230, 220, 255);
        var document = new SceneDocument(new[]
        {
            new SceneLayer(
                new SceneLayerId("background"),
                SceneLayerKind.Color,
                0,
                new NormalizedTransform(0, 0, 1, 1),
                fillColor: fill),
            new SceneLayer(
                new SceneLayerId("title"),
                SceneLayerKind.Text,
                1,
                new NormalizedTransform(0.1, 0.2, 0.8, 0.2, 3, 1.1, 0.9),
                opacity: 0.8,
                textContent: new SceneTextContent("Live title", foreground, 48, "Arial"))
        });

        var json = SceneSerializer.Serialize(document);
        var restored = SceneSerializer.Deserialize(json);

        Assert.Equal(2, restored.SchemaVersion);
        Assert.Equal(fill, restored.Layers[0].FillColor);
        Assert.Equal("Live title", restored.Layers[1].TextContent?.Text);
        Assert.Equal(foreground, restored.Layers[1].TextContent?.Foreground);
        Assert.Equal(48, restored.Layers[1].TextContent?.FontSize);
        Assert.Equal("Arial", restored.Layers[1].TextContent?.FontFamily);
        Assert.Equal(json, SceneSerializer.Serialize(restored));
    }

    [Fact]
    public void Version_one_color_is_migrated_and_text_without_content_becomes_a_placeholder()
    {
        var oldScene = """
            {
              "schemaVersion": 1,
              "layers": [
                { "id": "background", "kind": "color", "zIndex": 0, "transform": { "x": 0, "y": 0, "width": 1, "height": 1, "rotationDegrees": 0, "scaleX": 1, "scaleY": 1 }, "opacity": 1, "visible": true, "locked": false },
                { "id": "title", "kind": "text", "zIndex": 1, "transform": { "x": 0, "y": 0, "width": 1, "height": 0.2, "rotationDegrees": 0, "scaleX": 1, "scaleY": 1 }, "opacity": 1, "visible": true, "locked": false }
              ]
            }
            """;

        var restored = SceneSerializer.Deserialize(oldScene);

        Assert.Equal(SceneRgbaColor.Black, restored.Layers[0].FillColor);
        Assert.True(restored.Layers[1].IsDiagnosticPlaceholder);
        Assert.Contains("no saved text content", restored.Layers[1].DiagnosticMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, restored.SchemaVersion);
    }

    [Fact]
    public void Unknown_schema_version_fails_safely()
    {
        var exception = Assert.Throws<SceneSerializationException>(() => SceneSerializer.Deserialize(
            "{\"schemaVersion\":99,\"layers\":[]}"));

        Assert.Contains("schema version", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Malformed_json_and_duplicate_layer_ids_are_rejected()
    {
        Assert.Throws<SceneSerializationException>(() => SceneSerializer.Deserialize(
            "{\"schemaVersion\":1,\"layers\":["));

        var duplicate = """
            {
              "schemaVersion": 1,
              "layers": [
                { "id": "same", "kind": "color", "zIndex": 0, "transform": { "x": 0, "y": 0, "width": 1, "height": 1, "rotationDegrees": 0, "scaleX": 1, "scaleY": 1 }, "opacity": 1, "visible": true, "locked": false },
                { "id": "same", "kind": "color", "zIndex": 1, "transform": { "x": 0, "y": 0, "width": 1, "height": 1, "rotationDegrees": 0, "scaleX": 1, "scaleY": 1 }, "opacity": 1, "visible": true, "locked": false }
              ]
            }
            """;

        Assert.Throws<SceneSerializationException>(() => SceneSerializer.Deserialize(duplicate));
    }

    [Fact]
    public void Invalid_transform_json_is_rejected()
    {
        var json = """
            {
              "schemaVersion": 1,
              "layers": [
                { "id": "layer", "kind": "color", "zIndex": 0, "transform": { "x": 1.5, "y": 0, "width": 1, "height": 1, "rotationDegrees": 0, "scaleX": 1, "scaleY": 1 }, "opacity": 1, "visible": true, "locked": false }
              ]
            }
            """;

        Assert.Throws<SceneSerializationException>(() => SceneSerializer.Deserialize(json));
    }

    [Fact]
    public void Missing_assets_become_diagnostic_placeholders()
    {
        var json = SceneSerializer.Serialize(new SceneDocument(new[]
        {
            new SceneLayer(
                new SceneLayerId("missing-layer"),
                SceneLayerKind.Image,
                0,
                new NormalizedTransform(0, 0, 0.5, 0.5),
                assetReference: new SceneAssetReference("missing-asset", "assets/missing.png"))
        }));
        var diagnostics = new List<DiagnosticEvent>();
        var restored = SceneSerializer.Deserialize(
            json,
            new DelegateSceneAssetResolver(asset => asset.AssetId == "present-asset"),
            new DelegateDiagnosticsSink(diagnostics.Add));

        var layer = Assert.Single(restored.Layers);
        Assert.True(layer.IsDiagnosticPlaceholder);
        Assert.Equal(new SceneAssetReference("missing-asset", "assets/missing.png"), layer.AssetReference);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticCode.SceneAssetMissing, diagnostic.Code);
        Assert.Contains("missing-asset", diagnostic.Message, StringComparison.Ordinal);
    }
}
