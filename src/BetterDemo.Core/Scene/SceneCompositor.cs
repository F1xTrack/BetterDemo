using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using BetterDemo.Core.Contracts;

namespace BetterDemo.Core.Scene;

public readonly record struct NormalizedCrop
{
    public NormalizedCrop(double left, double top, double width, double height)
    {
        ValidateRange(left, nameof(left));
        ValidateRange(top, nameof(top));
        if (!double.IsFinite(width) || width <= 0 || width > 1 || left + width > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }
        if (!double.IsFinite(height) || height <= 0 || height > 1 || top + height > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }

    public double Left { get; }
    public double Top { get; }
    public double Width { get; }
    public double Height { get; }

    private static void ValidateRange(double value, string name)
    {
        if (!double.IsFinite(value) || value < 0 || value >= 1)
        {
            throw new ArgumentOutOfRangeException(name);
        }
    }
}

public readonly record struct SourceViewTransform
{
    public SourceViewTransform(
        double zoom = 1,
        double panX = 0,
        double panY = 0,
        bool mirrorHorizontally = false,
        bool flipVertically = false,
        NormalizedCrop? crop = null)
    {
        if (!double.IsFinite(zoom) || zoom < 1 || zoom > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(zoom), "Zoom must be between one and eight.");
        }
        if (!double.IsFinite(panX) || panX < -4 || panX > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(panX));
        }
        if (!double.IsFinite(panY) || panY < -4 || panY > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(panY));
        }

        Zoom = zoom;
        PanX = panX;
        PanY = panY;
        MirrorHorizontally = mirrorHorizontally;
        FlipVertically = flipVertically;
        Crop = crop ?? new NormalizedCrop(0, 0, 1, 1);
    }

    public double Zoom { get; }
    public double PanX { get; }
    public double PanY { get; }
    public bool MirrorHorizontally { get; }
    public bool FlipVertically { get; }
    public NormalizedCrop Crop { get; }

    public static SourceViewTransform Identity => new(1, 0, 0, false, false, new NormalizedCrop(0, 0, 1, 1));
}

public readonly record struct CameraCornerLayout
{
    public CameraCornerLayout(double scale = 0.24, double margin = 0.04, double rotationDegrees = 0)
    {
        if (!double.IsFinite(scale) || scale < 0.05 || scale > 0.75)
        {
            throw new ArgumentOutOfRangeException(nameof(scale));
        }
        if (!double.IsFinite(margin) || margin < 0 || margin > 0.45 || 2 * margin + scale > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(margin));
        }
        if (!double.IsFinite(rotationDegrees) || rotationDegrees < -360 || rotationDegrees > 360)
        {
            throw new ArgumentOutOfRangeException(nameof(rotationDegrees));
        }

        Scale = scale;
        Margin = margin;
        RotationDegrees = rotationDegrees;
    }

    public double Scale { get; }
    public double Margin { get; }
    public double RotationDegrees { get; }

    public static CameraCornerLayout Default => new(0.24, 0.04, 0);
}

public readonly record struct BlurRegion
{
    public BlurRegion(double x, double y, double width, double height, int radius)
    {
        if (!double.IsFinite(x) || x < 0 || x >= 1) throw new ArgumentOutOfRangeException(nameof(x));
        if (!double.IsFinite(y) || y < 0 || y >= 1) throw new ArgumentOutOfRangeException(nameof(y));
        if (!double.IsFinite(width) || width <= 0 || width > 1 || x + width > 1) throw new ArgumentOutOfRangeException(nameof(width));
        if (!double.IsFinite(height) || height <= 0 || height > 1 || y + height > 1) throw new ArgumentOutOfRangeException(nameof(height));
        if (radius < 1 || radius > 32) throw new ArgumentOutOfRangeException(nameof(radius));

        X = x;
        Y = y;
        Width = width;
        Height = height;
        Radius = radius;
    }

    public double X { get; }
    public double Y { get; }
    public double Width { get; }
    public double Height { get; }
    public int Radius { get; }
}

public sealed class SceneRenderOptions
{
    public SceneRenderOptions(
        int width,
        int height,
        SourceViewTransform? sourceTransform = null,
        CameraCornerLayout? cameraCorner = null,
        IEnumerable<BlurRegion>? blurRegions = null)
    {
        if (width <= 0 || width > 8192) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0 || height > 8192) throw new ArgumentOutOfRangeException(nameof(height));
        Width = width;
        Height = height;
        SourceTransform = sourceTransform ?? SourceViewTransform.Identity;
        CameraCorner = cameraCorner ?? CameraCornerLayout.Default;
        BlurRegions = new ReadOnlyCollection<BlurRegion>((blurRegions ?? []).ToArray());
        if (BlurRegions.Count > 16) throw new ArgumentOutOfRangeException(nameof(blurRegions), "At most sixteen blur regions are supported.");
    }

    public int Width { get; }
    public int Height { get; }
    public SourceViewTransform SourceTransform { get; }
    public CameraCornerLayout CameraCorner { get; }
    public IReadOnlyList<BlurRegion> BlurRegions { get; }
}

public sealed class SceneComposition
{
    internal SceneComposition(int width, int height, byte[] pixels, IReadOnlyList<string> diagnostics)
    {
        Width = width;
        Height = height;
        Stride = checked(width * 4);
        Pixels = pixels;
        Diagnostics = diagnostics;
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public ReadOnlyMemory<byte> Pixels { get; }
    public IReadOnlyList<string> Diagnostics { get; }
}

/// <summary>
/// Composes the OBS and physical camera feeds into an opaque BGRA frame for D3D11 presentation.
/// </summary>
public sealed class SceneCompositor
{
    private const string ObsMissing = "OBS Virtual Camera is unavailable; a diagnostic placeholder is shown.";
    private const string PhysicalMissing = "Physical camera is unavailable; a diagnostic placeholder is shown.";
    private static readonly VideoDeviceId LayerContentDeviceId = new("betterdemo://scene-layer-content");
    private static readonly VideoDeviceId DiagnosticDeviceId = new("betterdemo://scene-diagnostic-placeholder");
    private static readonly VideoFrame DiagnosticFrame = CreateDiagnosticFrame();

    public static SceneComposition Crossfade(SceneComposition from, SceneComposition to, double progress)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        if (from.Width != to.Width || from.Height != to.Height)
            throw new ArgumentException("Crossfade frames must have identical dimensions.", nameof(to));
        if (!double.IsFinite(progress)) throw new ArgumentOutOfRangeException(nameof(progress));
        progress = Math.Clamp(progress, 0, 1);
        if (progress >= 1) return to;

        var fromPixels = from.Pixels.Span;
        var toPixels = to.Pixels.Span;
        var pixels = new byte[toPixels.Length];
        for (var index = 0; index < pixels.Length; index++)
            pixels[index] = (byte)Math.Clamp((int)Math.Round(fromPixels[index] * (1 - progress) + toPixels[index] * progress), 0, 255);
        return new SceneComposition(to.Width, to.Height, pixels, to.Diagnostics);
    }

    public SceneComposition Render(
        SceneDocument document,
        IReadOnlyDictionary<SceneLayerId, VideoFrame>? layerFrames,
        SceneRenderOptions options)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);

        var output = new byte[checked(options.Width * options.Height * 4)];
        Fill(output, options.Width, options.Height, 0, 0, 0, 255);
        var diagnostics = new List<string>();
        foreach (var layer in document.Layers)
        {
            if (!layer.IsVisible || layer.Opacity <= 0) continue;

            if (layer.Kind == SceneLayerKind.Color)
            {
                using var colorFrame = CreateColorFrame(layer.FillColor!.Value);
                DrawSceneLayer(output, colorFrame, layer, options);
                continue;
            }

            if (layer.Kind == SceneLayerKind.DiagnosticPlaceholder)
            {
                DrawSceneLayer(output, DiagnosticFrame, layer, options);
                diagnostics.Add(layer.DiagnosticMessage ?? $"Scene layer '{layer.Id}' is a diagnostic placeholder.");
                continue;
            }

            if (layerFrames is not null && layerFrames.TryGetValue(layer.Id, out var sourceFrame) &&
                sourceFrame is not null && sourceFrame.HasPayload)
            {
                DrawSceneLayer(output, sourceFrame, layer, options);
                continue;
            }

            DrawSceneLayer(output, DiagnosticFrame, layer, options);
            diagnostics.Add(MissingLayerContentMessage(layer));
        }

        foreach (var region in options.BlurRegions)
        {
            ApplyBlur(output, options.Width, options.Height, region);
        }

        return new SceneComposition(options.Width, options.Height, output, new ReadOnlyCollection<string>(diagnostics));
    }

    public SceneComposition Render(
        SceneMode mode,
        VideoFrame? obsFrame,
        VideoFrame? physicalFrame,
        SceneRenderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));

        var output = new byte[checked(options.Width * options.Height * 4)];
        Fill(output, options.Width, options.Height, 0, 0, 0, 255);
        var diagnostics = new List<string>(2);
        switch (mode)
        {
            case SceneMode.Black:
                break;
            case SceneMode.Screen:
                DrawFullSource(output, obsFrame, options, diagnostics, ObsMissing);
                break;
            case SceneMode.PhysicalCamera:
                DrawFullSource(output, physicalFrame, options, diagnostics, PhysicalMissing);
                break;
            case SceneMode.ScreenPlusPhysicalCameraCorner:
                DrawFullSource(output, obsFrame, options, diagnostics, ObsMissing);
                DrawCameraCorner(output, physicalFrame, options, diagnostics);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }

        foreach (var region in options.BlurRegions)
        {
            ApplyBlur(output, options.Width, options.Height, region);
        }

        return new SceneComposition(options.Width, options.Height, output, new ReadOnlyCollection<string>(diagnostics));
    }

    private static void DrawSceneLayer(byte[] output, VideoFrame frame, SceneLayer layer, SceneRenderOptions options)
    {
        var transform = layer.Transform;
        if (layer.Kind == SceneLayerKind.Video && layer.Opacity == 1 &&
            transform.X == 0 && transform.Y == 0 && transform.Width == 1 && transform.Height == 1 &&
            transform.RotationDegrees == 0 && transform.ScaleX == 1 && transform.ScaleY == 1 &&
            TryDrawOpaqueUntransformedBgra(output, frame, options))
        {
            return;
        }

        var sourceTransform = layer.Kind == SceneLayerKind.Video &&
            transform.X == 0 && transform.Y == 0 && transform.Width == 1 && transform.Height == 1 &&
            transform.ScaleX == 1 && transform.ScaleY == 1
                ? options.SourceTransform
                : SourceViewTransform.Identity;
        DrawFrame(
            output,
            options.Width,
            options.Height,
            frame,
            transform.X * options.Width,
            transform.Y * options.Height,
            transform.Width * options.Width * transform.ScaleX,
            transform.Height * options.Height * transform.ScaleY,
            transform.RotationDegrees,
            1,
            sourceTransform,
            layer.Opacity);
    }

    private static string MissingLayerContentMessage(SceneLayer layer) => layer.Kind switch
    {
        SceneLayerKind.Image => $"Image asset '{layer.AssetReference?.AssetId}' on layer '{layer.Id}' is unavailable; a diagnostic placeholder is shown.",
        SceneLayerKind.Video => $"Video layer '{layer.Id}' has no current frame; a diagnostic placeholder is shown.",
        SceneLayerKind.Text => $"Text layer '{layer.Id}' has no rasterized frame; a diagnostic placeholder is shown.",
        _ => $"Scene layer '{layer.Id}' has no renderable content; a diagnostic placeholder is shown."
    };

    private static VideoFrame CreateColorFrame(SceneRgbaColor color) => VideoFrame.CopyFrom(
        LayerContentDeviceId,
        new VideoFrameFormat(1, 1, VideoPixelFormat.Bgra32, 4),
        new VideoFrameStamp(1, new QpcTimestamp(1, 10_000_000)),
        [color.Blue, color.Green, color.Red, color.Alpha]);

    private static VideoFrame CreateDiagnosticFrame() => VideoFrame.CopyFrom(
        DiagnosticDeviceId,
        new VideoFrameFormat(2, 2, VideoPixelFormat.Bgra32, 8),
        new VideoFrameStamp(1, new QpcTimestamp(1, 10_000_000)),
        [
            128, 40, 220, 255, 40, 24, 56, 255,
            40, 24, 56, 255, 128, 40, 220, 255
        ]);

    private static void DrawFullSource(
        byte[] output,
        VideoFrame? frame,
        SceneRenderOptions options,
        List<string> diagnostics,
        string diagnostic)
    {
        if (frame is null || !frame.HasPayload)
        {
            DrawPlaceholder(output, options.Width, options.Height, 0, 0, options.Width, options.Height);
            diagnostics.Add(diagnostic);
            return;
        }

        if (TryDrawOpaqueUntransformedBgra(output, frame, options))
        {
            return;
        }

        DrawFrame(
            output,
            options.Width,
            options.Height,
            frame,
            0,
            0,
            options.Width,
            options.Height,
            0,
            options.SourceTransform.Zoom,
            options.SourceTransform,
            1);
    }

    private static void DrawCameraCorner(
        byte[] output,
        VideoFrame? frame,
        SceneRenderOptions options,
        List<string> diagnostics)
    {
        var layout = options.CameraCorner;
        var x = (1 - layout.Margin - layout.Scale) * options.Width;
        var y = layout.Margin * options.Height;
        var width = layout.Scale * options.Width;
        var height = layout.Scale * options.Height;
        if (frame is null || !frame.HasPayload)
        {
            DrawPlaceholder(output, options.Width, options.Height, x, y, width, height);
            diagnostics.Add(PhysicalMissing);
            return;
        }

        DrawFrame(
            output,
            options.Width,
            options.Height,
            frame,
            x,
            y,
            width,
            height,
            layout.RotationDegrees,
            1,
            SourceViewTransform.Identity,
            1);
    }

    private static bool TryDrawOpaqueUntransformedBgra(byte[] output, VideoFrame frame, SceneRenderOptions options)
    {
        var format = frame.Format;
        var transform = options.SourceTransform;
        if (format.PixelFormat != VideoPixelFormat.Bgra32 ||
            transform != SourceViewTransform.Identity ||
            (long)format.Width * options.Height != (long)format.Height * options.Width)
        {
            return false;
        }

        var source = frame.Data.Span;
        var visibleRowBytes = checked(format.Width * 4);
        for (var y = 0; y < format.Height; y++)
        {
            var rowStart = y * format.Stride;
            for (var offset = rowStart + 3; offset < rowStart + visibleRowBytes; offset += 4)
            {
                if (source[offset] != byte.MaxValue)
                {
                    return false;
                }
            }
        }

        var destination = output.AsSpan();
        for (var y = 0; y < options.Height; y++)
        {
            var sourceY = (int)((long)y * format.Height / options.Height);
            var sourceRow = source.Slice(sourceY * format.Stride, visibleRowBytes);
            var destinationRow = destination.Slice(y * options.Width * 4, options.Width * 4);
            if (format.Width == options.Width)
            {
                sourceRow.CopyTo(destinationRow);
                continue;
            }

            for (var x = 0; x < options.Width; x++)
            {
                var sourceX = (int)((long)x * format.Width / options.Width);
                sourceRow.Slice(sourceX * 4, 4).CopyTo(destinationRow.Slice(x * 4, 4));
            }
        }

        return true;
    }

    private static void DrawFrame(
        byte[] output,
        int outputWidth,
        int outputHeight,
        VideoFrame frame,
        double baseX,
        double baseY,
        double baseWidth,
        double baseHeight,
        double rotationDegrees,
        double zoom,
        SourceViewTransform transform,
        double opacity)
    {
        var format = frame.Format;
        if (format.PixelFormat == VideoPixelFormat.Bgra32 && format.Stride < checked(format.Width * 4))
        {
            throw new ArgumentException("The BGRA32 frame stride is shorter than its pixel rows.", nameof(frame));
        }
        if (format.PixelFormat == VideoPixelFormat.Nv12 && (format.Stride < format.Width || (format.Width & 1) != 0 || (format.Height & 1) != 0))
        {
            throw new ArgumentException("NV12 composition requires an even frame size and a complete luma stride.", nameof(frame));
        }

        var data = frame.Data.Span;
        var expectedLength = VideoFrame.GetRequiredBufferLength(format);
        if (data.Length < expectedLength)
        {
            throw new ArgumentException("The video frame payload is shorter than its declared format.", nameof(frame));
        }

        var zoomedWidth = baseWidth * zoom;
        var zoomedHeight = baseHeight * zoom;
        var centerX = baseX + baseWidth / 2 + transform.PanX * outputWidth;
        var centerY = baseY + baseHeight / 2 + transform.PanY * outputHeight;
        var radians = rotationDegrees * (Math.PI / 180);
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        var halfDiagonal = Math.Sqrt(zoomedWidth * zoomedWidth + zoomedHeight * zoomedHeight) / 2;
        var left = Math.Max(0, (int)Math.Floor(centerX - halfDiagonal));
        var top = Math.Max(0, (int)Math.Floor(centerY - halfDiagonal));
        var right = Math.Min(outputWidth, (int)Math.Ceiling(centerX + halfDiagonal));
        var bottom = Math.Min(outputHeight, (int)Math.Ceiling(centerY + halfDiagonal));

        var crop = transform.Crop;
        var cropLeft = crop.Left;
        var cropTop = crop.Top;
        var cropWidth = crop.Width;
        var cropHeight = crop.Height;
        var zoomedCropWidth = cropWidth / zoom;
        var zoomedCropHeight = cropHeight / zoom;
        cropLeft += (cropWidth - zoomedCropWidth) / 2;
        cropTop += (cropHeight - zoomedCropHeight) / 2;
        cropWidth = zoomedCropWidth;
        cropHeight = zoomedCropHeight;
        var sourceAspect = format.Width * cropWidth / (format.Height * cropHeight);
        var destinationAspect = zoomedWidth / zoomedHeight;
        if (sourceAspect > destinationAspect)
        {
            var fittedWidth = cropHeight * destinationAspect / (format.Width / (double)format.Height);
            cropLeft += (cropWidth - fittedWidth) / 2;
            cropWidth = fittedWidth;
        }
        else
        {
            var fittedHeight = cropWidth * (format.Width / (double)format.Height) / destinationAspect;
            cropTop += (cropHeight - fittedHeight) / 2;
            cropHeight = fittedHeight;
        }

        for (var y = top; y < bottom; y++)
        {
            var dy = ((y + 0.5) - centerY) / zoomedHeight;
            var outputRow = y * outputWidth * 4;
            for (var x = left; x < right; x++)
            {
                var dx = ((x + 0.5) - centerX) / zoomedWidth;
                var u = dx * cos + dy * sin + 0.5;
                var v = -dx * sin + dy * cos + 0.5;
                if (u < 0 || u >= 1 || v < 0 || v >= 1)
                {
                    continue;
                }

                if (transform.MirrorHorizontally) u = 1 - u;
                if (transform.FlipVertically) v = 1 - v;
                var sourceX = cropLeft + Math.Clamp(u, 0, 1) * cropWidth;
                var sourceY = cropTop + Math.Clamp(v, 0, 1) * cropHeight;
                SampleBgra(frame, data, sourceX, sourceY, out var blue, out var green, out var red, out var alpha);
                Blend(output, outputRow + x * 4, blue, green, red, alpha * opacity / 255);
            }
        }
    }

    private static void SampleBgra(
        VideoFrame frame,
        ReadOnlySpan<byte> data,
        double u,
        double v,
        out byte blue,
        out byte green,
        out byte red,
        out byte alpha)
    {
        var format = frame.Format;
        var x = Math.Clamp((int)(u * format.Width), 0, format.Width - 1);
        var y = Math.Clamp((int)(v * format.Height), 0, format.Height - 1);
        if (format.PixelFormat == VideoPixelFormat.Bgra32)
        {
            var offset = checked(y * format.Stride + x * 4);
            blue = data[offset];
            green = data[offset + 1];
            red = data[offset + 2];
            alpha = data[offset + 3];
            return;
        }

        var yOffset = checked(y * format.Stride + x);
        var uvBase = checked(format.Stride * format.Height);
        var uvOffset = checked(uvBase + (y / 2) * format.Stride + (x / 2) * 2);
        var luma = data[yOffset] - 16;
        var cb = data[uvOffset] - 128;
        var cr = data[uvOffset + 1] - 128;
        red = ClampByte((298 * luma + 459 * cr + 128) >> 8);
        green = ClampByte((298 * luma - 55 * cb - 136 * cr + 128) >> 8);
        blue = ClampByte((298 * luma + 541 * cb + 128) >> 8);
        alpha = 255;
    }

    private static void DrawPlaceholder(byte[] output, int outputWidth, int outputHeight, double x, double y, double width, double height)
    {
        var left = Math.Clamp((int)Math.Floor(x), 0, outputWidth);
        var top = Math.Clamp((int)Math.Floor(y), 0, outputHeight);
        var right = Math.Clamp((int)Math.Ceiling(x + width), 0, outputWidth);
        var bottom = Math.Clamp((int)Math.Ceiling(y + height), 0, outputHeight);
        var cell = Math.Max(1, Math.Min(outputWidth, outputHeight) / 8);
        for (var row = top; row < bottom; row++)
        {
            for (var column = left; column < right; column++)
            {
                var isAccent = ((column - left) / cell + (row - top) / cell) % 2 == 0;
                var offset = (row * outputWidth + column) * 4;
                output[offset] = isAccent ? (byte)128 : (byte)40;
                output[offset + 1] = isAccent ? (byte)40 : (byte)24;
                output[offset + 2] = isAccent ? (byte)220 : (byte)56;
                output[offset + 3] = 255;
            }
        }
    }

    private static void ApplyBlur(byte[] output, int width, int height, BlurRegion region)
    {
        var left = Math.Clamp((int)Math.Floor(region.X * width), 0, width - 1);
        var top = Math.Clamp((int)Math.Floor(region.Y * height), 0, height - 1);
        var right = Math.Clamp((int)Math.Ceiling((region.X + region.Width) * width), left + 1, width);
        var bottom = Math.Clamp((int)Math.Ceiling((region.Y + region.Height) * height), top + 1, height);
        var intermediate = output.ToArray();
        BlurAxis(output, intermediate, width, left, top, right, bottom, region.Radius, horizontal: true);
        intermediate.AsSpan().CopyTo(output);
        BlurAxis(output, intermediate, width, left, top, right, bottom, region.Radius, horizontal: false);
        intermediate.AsSpan().CopyTo(output);
    }

    private static void BlurAxis(
        byte[] source,
        byte[] destination,
        int width,
        int left,
        int top,
        int right,
        int bottom,
        int radius,
        bool horizontal)
    {
        var outerStart = horizontal ? top : left;
        var outerEnd = horizontal ? bottom : right;
        var innerStart = horizontal ? left : top;
        var innerEnd = horizontal ? right : bottom;
        for (var outer = outerStart; outer < outerEnd; outer++)
        {
            for (var inner = innerStart; inner < innerEnd; inner++)
            {
                var blue = 0;
                var green = 0;
                var red = 0;
                var alpha = 0;
                var sampleStart = Math.Max(innerStart, inner - radius);
                var sampleEnd = Math.Min(innerEnd, inner + radius + 1);
                for (var sample = sampleStart; sample < sampleEnd; sample++)
                {
                    var x = horizontal ? sample : outer;
                    var y = horizontal ? outer : sample;
                    var offset = (y * width + x) * 4;
                    blue += source[offset];
                    green += source[offset + 1];
                    red += source[offset + 2];
                    alpha += source[offset + 3];
                }

                var destinationX = horizontal ? inner : outer;
                var destinationY = horizontal ? outer : inner;
                var destinationOffset = (destinationY * width + destinationX) * 4;
                var sampleCount = sampleEnd - sampleStart;
                destination[destinationOffset] = (byte)(blue / sampleCount);
                destination[destinationOffset + 1] = (byte)(green / sampleCount);
                destination[destinationOffset + 2] = (byte)(red / sampleCount);
                destination[destinationOffset + 3] = (byte)(alpha / sampleCount);
            }
        }
    }

    private static void Fill(byte[] output, int width, int height, byte blue, byte green, byte red, byte alpha)
    {
        var pixel = (uint)(blue | (green << 8) | (red << 16) | (alpha << 24));
        MemoryMarshal.Cast<byte, uint>(output.AsSpan()).Fill(pixel);
    }

    private static void Blend(byte[] output, int offset, byte blue, byte green, byte red, double opacity)
    {
        var alpha = Math.Clamp(opacity, 0, 1);
        output[offset] = (byte)Math.Round(blue * alpha + output[offset] * (1 - alpha));
        output[offset + 1] = (byte)Math.Round(green * alpha + output[offset + 1] * (1 - alpha));
        output[offset + 2] = (byte)Math.Round(red * alpha + output[offset + 2] * (1 - alpha));
        output[offset + 3] = 255;
    }

    private static byte ClampByte(int value) => (byte)Math.Clamp(value, 0, 255);
}
