using System.Buffers.Binary;
using System.Runtime.Versioning;
using BetterDemo.App.Assets;
using BetterDemo.Core.Contracts;
using BetterDemo.Core.Scene;
using Xunit;

namespace BetterDemo.Integration.Tests;

[SupportedOSPlatform("windows")]
public sealed class SceneAssetStoreTests
{
    [Fact]
    public async Task Import_copies_a_bmp_decodes_straight_bgra_and_keeps_the_frame_until_store_disposal()
    {
        var root = Path.Combine(Path.GetTempPath(), $"BetterDemo-Assets-{Guid.NewGuid():N}");
        var sourcePath = Path.Combine(root, "source.bmp");
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(sourcePath, CreateTwoColorBmp());

        var store = new SceneAssetStore(Path.Combine(root, "store"));
        try
        {
            var asset = await store.ImportImageAsync(sourcePath);

            Assert.NotEqual(sourcePath, asset.Location);
            Assert.True(File.Exists(asset.Location));
            Assert.True(store.TryGetFrame(asset, out var frame));
            Assert.NotNull(frame);
            Assert.Equal(new VideoFrameFormat(2, 1, VideoPixelFormat.Bgra32, 8), frame.Format);
            Assert.Equal(new byte[] { 0, 0, 255, 255, 0, 255, 0, 255 }, frame.Data.ToArray());

            var layer = new SceneLayer(
                new SceneLayerId("imported-image"),
                SceneLayerKind.Image,
                0,
                new NormalizedTransform(0, 0, 1, 1),
                assetReference: asset);
            Assert.True(store.TryGetFrame(layer, out var layerFrame));
            Assert.Same(frame, layerFrame);

            await store.DisposeAsync();
            Assert.True(frame.IsDisposed);
        }
        finally
        {
            await store.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Import_rejects_unsupported_extensions_before_copying()
    {
        var root = Path.Combine(Path.GetTempPath(), $"BetterDemo-Assets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var sourcePath = Path.Combine(root, "notes.txt");
        await File.WriteAllTextAsync(sourcePath, "not an image");
        var store = new SceneAssetStore(Path.Combine(root, "store"));

        try
        {
            await Assert.ThrowsAsync<NotSupportedException>(() => store.ImportImageAsync(sourcePath));
            Assert.False(Directory.Exists(Path.Combine(root, "store")));
        }
        finally
        {
            await store.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Imported_image_frame_flows_through_scene_compositor()
    {
        var root = Path.Combine(Path.GetTempPath(), $"BetterDemo-Assets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var sourcePath = Path.Combine(root, "overlay.bmp");
        await File.WriteAllBytesAsync(sourcePath, CreateTwoColorBmp());
        var store = new SceneAssetStore(Path.Combine(root, "store"));

        try
        {
            var asset = await store.ImportImageAsync(sourcePath);
            Assert.True(store.TryGetFrame(asset, out var frame));
            Assert.NotNull(frame);
            var imageId = new SceneLayerId("imported-overlay");
            var document = new SceneDocument(
            [
                new SceneLayer(new SceneLayerId("background"), SceneLayerKind.Color, 0,
                    new NormalizedTransform(0, 0, 1, 1), fillColor: SceneRgbaColor.Black),
                new SceneLayer(imageId, SceneLayerKind.Image, 1,
                    new NormalizedTransform(0, 0, 1, 1), assetReference: asset)
            ]);
            var composition = new SceneCompositor().Render(
                document,
                new Dictionary<SceneLayerId, VideoFrame> { [imageId] = frame },
                new SceneRenderOptions(2, 1));

            Assert.Empty(composition.Diagnostics);
            Assert.Equal(new byte[] { 0, 0, 255, 255, 0, 255, 0, 255 }, composition.Pixels.ToArray());
        }
        finally
        {
            await store.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Import_removes_copy_when_supported_file_cannot_be_decoded()
    {
        var root = Path.Combine(Path.GetTempPath(), $"BetterDemo-Assets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var sourcePath = Path.Combine(root, "broken.png");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4]);
        var assetFolder = Path.Combine(root, "store");
        var store = new SceneAssetStore(assetFolder);

        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => store.ImportImageAsync(sourcePath));
            Assert.True(Directory.Exists(assetFolder));
            Assert.Empty(Directory.EnumerateFiles(assetFolder));
        }
        finally
        {
            await store.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Import_or_load_reuses_store_assets_and_copies_external_scene_assets()
    {
        var root = Path.Combine(Path.GetTempPath(), $"BetterDemo-Assets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var sourcePath = Path.Combine(root, "source.bmp");
        var assetFolder = Path.Combine(root, "store");
        await File.WriteAllBytesAsync(sourcePath, CreateTwoColorBmp());
        var store = new SceneAssetStore(assetFolder);

        try
        {
            var stored = await store.ImportImageAsync(sourcePath);
            var reused = await store.ImportOrLoadImageAsync(stored);
            Assert.Equal(stored, reused);
            Assert.Single(Directory.EnumerateFiles(assetFolder));

            var externalReference = new SceneAssetReference("external-image", sourcePath);
            var copied = await store.ImportOrLoadImageAsync(externalReference);
            Assert.NotEqual(externalReference.AssetId, copied.AssetId);
            Assert.NotEqual(sourcePath, copied.Location);
            Assert.True(store.TryGetFrame(copied, out var frame));
            Assert.NotNull(frame);
            Assert.Equal(2, Directory.EnumerateFiles(assetFolder).Count());
        }
        finally
        {
            await store.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] CreateTwoColorBmp()
    {
        const int pixelOffset = 54;
        const int rowSize = 8;
        const int fileSize = pixelOffset + rowSize;
        var bytes = new byte[fileSize];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0, 2), 0x4D42);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(2, 4), fileSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(10, 4), pixelOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(14, 4), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18, 4), 2);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22, 4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(26, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(28, 2), 24);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(34, 4), rowSize);
        bytes[pixelOffset] = 0;
        bytes[pixelOffset + 1] = 0;
        bytes[pixelOffset + 2] = 255;
        bytes[pixelOffset + 3] = 0;
        bytes[pixelOffset + 4] = 255;
        bytes[pixelOffset + 5] = 0;
        return bytes;
    }
}
