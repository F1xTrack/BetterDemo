using System.Runtime.Versioning;
using System.Buffers.Binary;
using BetterDemo.App.Assets;
using BetterDemo.App.Scenes;
using BetterDemo.Core.Contracts;
using BetterDemo.Core.Scene;
using BetterDemo.Remote.Protocol;
using Xunit;

namespace BetterDemo.Integration.Tests;

[SupportedOSPlatform("windows")]
public sealed class UserSceneFileSerializerTests
{
    [Fact]
    public void Round_trip_preserves_mode_controls_layer_order_and_transforms()
    {
        var state = CreateState();
        var asset = new SceneAssetReference("logo", Path.Combine(Path.GetTempPath(), "logo.png"));
        var image = new SceneLayer(
            new SceneLayerId("image:logo"),
            SceneLayerKind.Image,
            1,
            new NormalizedTransform(0.12, 0.16, 0.4, 0.5, 24, 1.1, 0.9),
            opacity: 0.85,
            isVisible: false,
            isLocked: true,
            assetReference: asset);
        var document = new SceneDocument(
        [
            SceneDocumentFactory.CreatePreset(SceneMode.Screen).Layers[0].WithVisibility(false),
            image,
            new SceneLayer(
                new SceneLayerId("background-label"),
                SceneLayerKind.Color,
                2,
                new NormalizedTransform(0, 0, 0.2, 0.2),
                fillColor: SceneRgbaColor.White)
        ]);

        var json = UserSceneFileSerializer.Serialize(
            state.Snapshot,
            document,
            new Dictionary<string, string> { [image.Id.Value] = "logo.png" });
        var restored = UserSceneFileSerializer.Deserialize(json, new DelegateSceneAssetResolver(_ => true));

        Assert.Equal(SceneMode.PhysicalCamera, restored.Mode);
        Assert.Equal(new CameraCornerState(18, 0.32, 36), restored.CameraCorner);
        Assert.Equal(2.5, restored.Zoom);
        Assert.Equal(new PanState(0.4, -0.2), restored.Pan);
        Assert.Equal(new BlurState(true, 7), restored.Blur);
        var restoredImage = restored.Document.GetLayer(image.Id);
        Assert.Equal(image.Transform, restoredImage.Transform);
        Assert.Equal(image.Opacity, restoredImage.Opacity);
        Assert.Equal(image.IsVisible, restoredImage.IsVisible);
        Assert.Equal(image.IsLocked, restoredImage.IsLocked);
        Assert.Equal(image.AssetReference, restoredImage.AssetReference);
        Assert.Equal("logo.png", restored.LayerDisplayNames[image.Id.Value]);
        Assert.False(restored.Document.GetLayer(SceneDocumentFactory.ObsVirtualCameraLayerId).IsVisible);
        Assert.Equal(document.Layers.Select(layer => layer.Id), restored.Document.Layers.Select(layer => layer.Id));
        Assert.Equal(SceneSerializer.Serialize(document), SceneSerializer.Serialize(restored.Document));
    }

    [Fact]
    public void Missing_image_asset_becomes_a_diagnostic_placeholder()
    {
        var image = new SceneLayer(
            new SceneLayerId("image:missing"),
            SceneLayerKind.Image,
            0,
            new NormalizedTransform(0, 0, 1, 1),
            assetReference: new SceneAssetReference("missing", Path.Combine(Path.GetTempPath(), "absent.png")));
        var document = new SceneDocument([image]);
        var json = UserSceneFileSerializer.Serialize(CreateState().Snapshot, document);
        var diagnostics = new List<DiagnosticEvent>();

        var restored = UserSceneFileSerializer.Deserialize(
            json,
            new DelegateSceneAssetResolver(_ => false),
            new DelegateDiagnosticsSink(diagnostics.Add));

        Assert.True(restored.Document.GetLayer(image.Id).IsDiagnosticPlaceholder);
        Assert.Contains("unavailable", Assert.Single(diagnostics).Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("\"fileVersion\": 1", "\"fileVersion\": 2")]
    [InlineData("\"mode\": \"physicalCamera\"", "\"mode\": \"unknown\"")]
    [InlineData("\"zoom\": 2.5", "\"zoom\": 9")]
    public void Rejects_unsupported_or_invalid_scene_file_state(string original, string replacement)
    {
        var document = new SceneDocument([SceneDocumentFactory.CreatePreset(SceneMode.Black).Layers[0]]);
        var json = UserSceneFileSerializer.Serialize(CreateState().Snapshot, document);
        var mutated = json.Replace(original, replacement, StringComparison.Ordinal);
        Assert.NotEqual(json, mutated);

        Assert.Throws<UserSceneFileSerializationException>(() => UserSceneFileSerializer.Deserialize(mutated));
    }

    [Fact]
    public void Rejects_scene_files_larger_than_four_mebibytes()
    {
        var json = new string(' ', 4 * 1024 * 1024 + 1);
        Assert.Throws<UserSceneFileSerializationException>(() => UserSceneFileSerializer.Deserialize(json));
    }

    [Fact]
    public void Rejects_documents_with_more_layers_than_the_editor_can_load()
    {
        var layers = Enumerable.Range(0, 19).Select(index => new SceneLayer(
            new SceneLayerId($"color:{index}"),
            SceneLayerKind.Color,
            index,
            new NormalizedTransform(0, 0, 1, 1),
            fillColor: SceneRgbaColor.Black));
        var document = new SceneDocument(layers);
        var json = UserSceneFileSerializer.Serialize(CreateState().Snapshot, document);

        Assert.Throws<UserSceneFileSerializationException>(() => UserSceneFileSerializer.Deserialize(json));
    }

    [Fact]
    public async Task Saved_scene_reimports_its_image_and_renders_the_same_pixels()
    {
        var root = Path.Combine(Path.GetTempPath(), $"BetterDemo-SceneRoundTrip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var sourcePath = Path.Combine(root, "two-colors.bmp");
        await File.WriteAllBytesAsync(sourcePath, CreateTwoColorBmp());
        var originalStore = new SceneAssetStore(Path.Combine(root, "original-assets"));
        var restoredStore = new SceneAssetStore(Path.Combine(root, "restored-assets"));

        try
        {
            var originalAsset = await originalStore.ImportImageAsync(sourcePath);
            var imageId = new SceneLayerId("image:two-colors");
            var document = new SceneDocument(
            [
                new SceneLayer(new SceneLayerId("background"), SceneLayerKind.Color, 0,
                    new NormalizedTransform(0, 0, 1, 1), fillColor: SceneRgbaColor.Black),
                new SceneLayer(imageId, SceneLayerKind.Image, 1,
                    new NormalizedTransform(0, 0, 1, 1), assetReference: originalAsset)
            ]);
            var state = new RemoteStateStore(new OutputWindowId("scene-round-trip"));
            state.UpdateLocalMode(SceneMode.Black);
            var scenePath = Path.Combine(root, "scene.json");
            await File.WriteAllTextAsync(scenePath, UserSceneFileSerializer.Serialize(
                state.Snapshot,
                document,
                new Dictionary<string, string> { [imageId.Value] = "two-colors.bmp" }));

            var restored = UserSceneFileSerializer.Deserialize(
                await File.ReadAllTextAsync(scenePath),
                new DelegateSceneAssetResolver(asset => File.Exists(asset.Location)));
            var savedImage = restored.Document.GetLayer(imageId);
            var reboundAsset = await restoredStore.ImportOrLoadImageAsync(savedImage.AssetReference!.Value);
            Assert.NotEqual(originalAsset.Location, reboundAsset.Location);
            Assert.True(restoredStore.TryGetFrame(reboundAsset, out var imageFrame));
            Assert.NotNull(imageFrame);

            var reboundLayer = new SceneLayer(
                savedImage.Id,
                savedImage.Kind,
                savedImage.ZIndex,
                savedImage.Transform,
                savedImage.Opacity,
                savedImage.IsVisible,
                savedImage.IsLocked,
                reboundAsset);
            var renderDocument = new SceneDocument([restored.Document.GetLayer(new SceneLayerId("background")), reboundLayer]);
            var composition = new SceneCompositor().Render(
                renderDocument,
                new Dictionary<SceneLayerId, VideoFrame> { [imageId] = imageFrame },
                new SceneRenderOptions(2, 1));

            Assert.Empty(composition.Diagnostics);
            Assert.Equal(new byte[] { 0, 0, 255, 255, 0, 255, 0, 255 }, composition.Pixels.ToArray());
            Assert.Equal("two-colors.bmp", restored.LayerDisplayNames[imageId.Value]);
        }
        finally
        {
            await originalStore.DisposeAsync();
            await restoredStore.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] CreateTwoColorBmp()
    {
        const int pixelOffset = 54;
        const int rowSize = 8;
        var bytes = new byte[pixelOffset + rowSize];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0, 2), 0x4D42);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(2, 4), (uint)bytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(10, 4), pixelOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(14, 4), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18, 4), 2);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22, 4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(26, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(28, 2), 24);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(34, 4), rowSize);
        bytes[pixelOffset + 2] = 255;
        bytes[pixelOffset + 4] = 255;
        bytes[pixelOffset + 5] = 0;
        return bytes;
    }

    private static RemoteStateStore CreateState()
    {
        var state = new RemoteStateStore(new OutputWindowId("scene-file-test"));
        state.UpdateLocalMode(SceneMode.PhysicalCamera);
        state.UpdateLocalCameraCorner(new CameraCornerState(18, 0.32, 36));
        state.UpdateLocalTransform(2.5, 0.4, -0.2);
        state.UpdateLocalBlur(new BlurState(true, 7));
        return state;
    }
}
