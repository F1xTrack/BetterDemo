using System.Collections.Concurrent;
using System.Diagnostics;
using BetterDemo.Core.Contracts;
using BetterDemo.Core.Scene;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace BetterDemo.App.Assets;

/// <summary>Imports local still images and keeps decoded BGRA frames alive for scene rendering.</summary>
public sealed class SceneAssetStore : IAsyncDisposable
{
    private const long MaximumFileBytes = 128 * 1024 * 1024;
    private const long MaximumDecodedPixels = 64 * 1024 * 1024;
    private const long MaximumTotalDecodedPixels = 64 * 1024 * 1024;
    private const int MaximumDimension = 8192;
    private static long nextSequence;
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff"
    };

    private readonly string assetFolder;
    private readonly ConcurrentDictionary<string, Lazy<Task<VideoFrame>>> decodedFrames = new(StringComparer.OrdinalIgnoreCase);
    private long reservedDecodedPixels;
    private int disposed;

    public SceneAssetStore(string? assetFolder = null)
    {
        this.assetFolder = Path.GetFullPath(assetFolder ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BetterDemo",
            "assets"));
    }

    public async Task<SceneAssetReference> ImportImageAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(sourcePath)) throw new ArgumentException("An image file path is required.", nameof(sourcePath));

        var fullSourcePath = Path.GetFullPath(sourcePath);
        var extension = Path.GetExtension(fullSourcePath);
        if (!SupportedExtensions.Contains(extension))
        {
            throw new NotSupportedException($"Image format '{extension}' is not supported.");
        }
        if (!File.Exists(fullSourcePath)) throw new FileNotFoundException("The selected image file does not exist.", fullSourcePath);
        if (new FileInfo(fullSourcePath).Length > MaximumFileBytes)
        {
            throw new InvalidDataException($"Image files must be no larger than {MaximumFileBytes / 1024 / 1024} MiB.");
        }

        Directory.CreateDirectory(assetFolder);
        var assetId = Guid.NewGuid().ToString("N");
        var storedPath = Path.Combine(assetFolder, $"{assetId}{extension.ToLowerInvariant()}");
        try
        {
            await using (var source = new FileStream(fullSourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(storedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            }

            var asset = new SceneAssetReference(assetId, storedPath);
            await LoadFrameAsync(asset, cancellationToken).ConfigureAwait(false);
            return asset;
        }
        catch
        {
            if (File.Exists(storedPath)) File.Delete(storedPath);
            throw;
        }
    }

    public async Task<VideoFrame> LoadFrameAsync(SceneAssetReference asset, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var key = CreateCacheKey(asset);
        var loader = decodedFrames.GetOrAdd(key, _ => new Lazy<Task<VideoFrame>>(
            () => DecodeAsync(asset), LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return await loader.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if ((!loader.IsValueCreated || loader.Value.IsCompleted) &&
                decodedFrames.TryGetValue(key, out var current) && ReferenceEquals(current, loader))
                decodedFrames.TryRemove(key, out _);
            throw;
        }
    }

    public async Task<SceneAssetReference> ImportOrLoadImageAsync(
        SceneAssetReference asset,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(asset.AssetId) || string.IsNullOrWhiteSpace(asset.Location))
            throw new ArgumentException("A scene image asset reference is required.", nameof(asset));
        var path = Path.GetFullPath(asset.Location);
        if (!File.Exists(path)) throw new FileNotFoundException($"Scene image asset '{asset.AssetId}' was not found.", path);

        var relativePath = Path.GetRelativePath(assetFolder, path);
        var isStoredAsset = !Path.IsPathRooted(relativePath) &&
            !string.Equals(relativePath, "..", StringComparison.Ordinal) &&
            !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
            !relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
        if (isStoredAsset)
        {
            var storedAsset = new SceneAssetReference(asset.AssetId, path);
            await LoadFrameAsync(storedAsset, cancellationToken).ConfigureAwait(false);
            return storedAsset;
        }

        return await ImportImageAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public bool TryGetFrame(SceneAssetReference asset, out VideoFrame? frame)
    {
        frame = null;
        if (Volatile.Read(ref disposed) != 0 || !decodedFrames.TryGetValue(CreateCacheKey(asset), out var loader) || !loader.IsValueCreated)
            return false;
        if (!loader.Value.IsCompletedSuccessfully) return false;
        frame = loader.Value.Result;
        return frame.HasPayload;
    }

    public bool TryGetFrame(SceneLayer layer, out VideoFrame? frame)
    {
        ArgumentNullException.ThrowIfNull(layer);
        if (layer.AssetReference is { } asset) return TryGetFrame(asset, out frame);
        frame = null;
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        foreach (var loader in decodedFrames.Values)
        {
            if (!loader.IsValueCreated) continue;
            try { (await loader.Value.ConfigureAwait(false)).Dispose(); }
            catch { }
        }
        decodedFrames.Clear();
        Interlocked.Exchange(ref reservedDecodedPixels, 0);
    }

    private async Task<VideoFrame> DecodeAsync(SceneAssetReference asset)
    {
        var path = Path.GetFullPath(asset.Location);
        if (!File.Exists(path)) throw new FileNotFoundException($"Scene image asset '{asset.AssetId}' was not found.", path);

        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenAsync(FileAccessMode.Read);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var width = checked((int)decoder.OrientedPixelWidth);
        var height = checked((int)decoder.OrientedPixelHeight);
        if (width <= 0 || height <= 0 || width > MaximumDimension || height > MaximumDimension || (long)width * height > MaximumDecodedPixels)
        {
            throw new InvalidDataException($"Image '{asset.AssetId}' dimensions {width}×{height} exceed the supported decode limits.");
        }

        var pixelCount = checked((long)width * height);
        ReserveDecodedPixels(pixelCount, asset.AssetId);
        try
        {
            var pixels = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Straight,
                new BitmapTransform(),
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb);
            var bytes = pixels.DetachPixelData();
            var format = new VideoFrameFormat(width, height, VideoPixelFormat.Bgra32, checked(width * 4));
            var requiredLength = VideoFrame.GetRequiredBufferLength(format);
            if (bytes.Length != requiredLength)
            {
                throw new InvalidDataException($"Image '{asset.AssetId}' decoded to {bytes.Length} bytes; {requiredLength} were expected.");
            }

            var sequence = checked((ulong)Interlocked.Increment(ref nextSequence));
            return VideoFrame.CopyFrom(
                new VideoDeviceId($"asset://{asset.AssetId}"),
                format,
                new VideoFrameStamp(sequence, new QpcTimestamp(Stopwatch.GetTimestamp(), Stopwatch.Frequency)),
                bytes);
        }
        catch
        {
            Interlocked.Add(ref reservedDecodedPixels, -pixelCount);
            throw;
        }
    }

    private void ReserveDecodedPixels(long pixelCount, string assetId)
    {
        while (true)
        {
            var current = Interlocked.Read(ref reservedDecodedPixels);
            var updated = checked(current + pixelCount);
            if (updated > MaximumTotalDecodedPixels)
            {
                throw new InvalidDataException(
                    $"Importing image '{assetId}' would exceed the {MaximumTotalDecodedPixels:N0}-pixel scene image memory limit.");
            }
            if (Interlocked.CompareExchange(ref reservedDecodedPixels, updated, current) == current) return;
        }
    }

    private static string CreateCacheKey(SceneAssetReference asset) =>
        $"{asset.AssetId}|{Path.GetFullPath(asset.Location)}";

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
}
