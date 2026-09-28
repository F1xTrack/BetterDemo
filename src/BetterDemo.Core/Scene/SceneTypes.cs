using System.Collections.ObjectModel;
using BetterDemo.Core.Contracts;

namespace BetterDemo.Core.Scene;

public readonly record struct SceneLayerId
{
    public SceneLayerId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A stable scene layer ID is required.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct SceneAssetReference
{
    public SceneAssetReference(string assetId, string location)
    {
        if (string.IsNullOrWhiteSpace(assetId))
        {
            throw new ArgumentException("A stable asset ID is required.", nameof(assetId));
        }

        if (string.IsNullOrWhiteSpace(location))
        {
            throw new ArgumentException("An asset location is required.", nameof(location));
        }

        AssetId = assetId;
        Location = location;
    }

    public string AssetId { get; }
    public string Location { get; }
}

public enum SceneLayerKind
{
    Image,
    Video,
    Text,
    Color,
    DiagnosticPlaceholder
}

public readonly record struct SceneRgbaColor(byte Red, byte Green, byte Blue, byte Alpha = byte.MaxValue)
{
    public static SceneRgbaColor Black => new(0, 0, 0);
    public static SceneRgbaColor White => new(byte.MaxValue, byte.MaxValue, byte.MaxValue);
}

public sealed record SceneTextContent
{
    public SceneTextContent(
        string text,
        SceneRgbaColor? foreground = null,
        double fontSize = 32,
        string fontFamily = "Segoe UI")
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 4096)
        {
            throw new ArgumentException("Text content must contain between one and 4096 characters.", nameof(text));
        }
        if (!double.IsFinite(fontSize) || fontSize < 4 || fontSize > 512)
        {
            throw new ArgumentOutOfRangeException(nameof(fontSize), "Font size must be between four and 512 points.");
        }
        if (string.IsNullOrWhiteSpace(fontFamily) || fontFamily.Length > 128)
        {
            throw new ArgumentException("A font family of at most 128 characters is required.", nameof(fontFamily));
        }

        Text = text;
        Foreground = foreground ?? SceneRgbaColor.White;
        FontSize = fontSize;
        FontFamily = fontFamily;
    }

    public string Text { get; }
    public SceneRgbaColor Foreground { get; }
    public double FontSize { get; }
    public string FontFamily { get; }
}

public readonly record struct NormalizedTransform
{
    public NormalizedTransform(
        double x,
        double y,
        double width,
        double height,
        double rotationDegrees = 0,
        double scaleX = 1,
        double scaleY = 1)
    {
        ValidateBounded(x, nameof(x), 0, 1);
        ValidateBounded(y, nameof(y), 0, 1);
        ValidateBounded(width, nameof(width), double.Epsilon, 1);
        ValidateBounded(height, nameof(height), double.Epsilon, 1);
        ValidateBounded(rotationDegrees, nameof(rotationDegrees), -360, 360);
        ValidateBounded(scaleX, nameof(scaleX), 0.01, 100);
        ValidateBounded(scaleY, nameof(scaleY), 0.01, 100);

        X = x;
        Y = y;
        Width = width;
        Height = height;
        RotationDegrees = rotationDegrees;
        ScaleX = scaleX;
        ScaleY = scaleY;
    }

    public double X { get; }
    public double Y { get; }
    public double Width { get; }
    public double Height { get; }
    public double RotationDegrees { get; }
    public double ScaleX { get; }
    public double ScaleY { get; }

    private static void ValidateBounded(double value, string parameterName, double minimum, double maximum)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Transform values must be finite and bounded.");
        }
    }
}

public sealed class SceneLayer
{
    public SceneLayer(
        SceneLayerId id,
        SceneLayerKind kind,
        int zIndex,
        NormalizedTransform transform,
        double opacity = 1,
        bool isVisible = true,
        bool isLocked = false,
        SceneAssetReference? assetReference = null,
        SceneRgbaColor? fillColor = null,
        SceneTextContent? textContent = null)
        : this(id, kind, zIndex, transform, opacity, isVisible, isLocked, assetReference, fillColor, textContent, null)
    {
    }

    private SceneLayer(
        SceneLayerId id,
        SceneLayerKind kind,
        int zIndex,
        NormalizedTransform transform,
        double opacity,
        bool isVisible,
        bool isLocked,
        SceneAssetReference? assetReference,
        SceneRgbaColor? fillColor,
        SceneTextContent? textContent,
        string? diagnosticMessage)
    {
        if (string.IsNullOrWhiteSpace(id.Value)) throw new ArgumentException("A layer ID is required.", nameof(id));
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (zIndex < 0) throw new ArgumentOutOfRangeException(nameof(zIndex));
        if (!double.IsFinite(opacity) || opacity < 0 || opacity > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(opacity), opacity, "Opacity must be finite and between zero and one.");
        }

        if ((kind is SceneLayerKind.Image or SceneLayerKind.Video) && assetReference is null)
        {
            throw new ArgumentException("Image and video layers require an asset reference.", nameof(assetReference));
        }
        if (kind == SceneLayerKind.Color && fillColor is null)
        {
            throw new ArgumentException("Color layers require a fill color.", nameof(fillColor));
        }
        if (kind == SceneLayerKind.Text && textContent is null)
        {
            throw new ArgumentException("Text layers require text content.", nameof(textContent));
        }
        if (kind != SceneLayerKind.Color && fillColor is not null)
        {
            throw new ArgumentException("Only color layers may define a fill color.", nameof(fillColor));
        }
        if (kind != SceneLayerKind.Text && textContent is not null)
        {
            throw new ArgumentException("Only text layers may define text content.", nameof(textContent));
        }

        Id = id;
        Kind = kind;
        ZIndex = zIndex;
        Transform = transform;
        Opacity = opacity;
        IsVisible = isVisible;
        IsLocked = isLocked;
        AssetReference = assetReference;
        FillColor = fillColor;
        TextContent = textContent;
        DiagnosticMessage = diagnosticMessage;
    }

    public SceneLayerId Id { get; }
    public SceneLayerKind Kind { get; }
    public int ZIndex { get; }
    public NormalizedTransform Transform { get; }
    public double Opacity { get; }
    public bool IsVisible { get; }
    public bool IsLocked { get; }
    public SceneAssetReference? AssetReference { get; }
    public SceneRgbaColor? FillColor { get; }
    public SceneTextContent? TextContent { get; }
    public string? DiagnosticMessage { get; }
    public bool IsDiagnosticPlaceholder => Kind == SceneLayerKind.DiagnosticPlaceholder;

    internal SceneLayer WithZIndex(int zIndex) => new(
        Id, Kind, zIndex, Transform, Opacity, IsVisible, IsLocked, AssetReference, FillColor, TextContent, DiagnosticMessage);

    public SceneLayer WithTransform(NormalizedTransform transform) => new(
        Id, Kind, ZIndex, transform, Opacity, IsVisible, IsLocked, AssetReference, FillColor, TextContent, DiagnosticMessage);

    public SceneLayer WithVisibility(bool isVisible) => new(
        Id, Kind, ZIndex, Transform, Opacity, isVisible, IsLocked, AssetReference, FillColor, TextContent, DiagnosticMessage);

    public SceneLayer WithLock(bool isLocked) => new(
        Id, Kind, ZIndex, Transform, Opacity, IsVisible, isLocked, AssetReference, FillColor, TextContent, DiagnosticMessage);

    public SceneLayer AsDiagnosticPlaceholder(string message) => new(
        Id,
        SceneLayerKind.DiagnosticPlaceholder,
        ZIndex,
        Transform,
        Opacity,
        IsVisible,
        IsLocked,
        AssetReference,
        null,
        null,
        message);

    public static SceneLayer CreateDiagnosticPlaceholder(
        SceneLayerId id,
        int zIndex,
        NormalizedTransform transform,
        double opacity,
        bool isVisible,
        bool isLocked,
        SceneAssetReference? assetReference,
        string? diagnosticMessage) => new(
        id,
        SceneLayerKind.DiagnosticPlaceholder,
        zIndex,
        transform,
        opacity,
        isVisible,
        isLocked,
        assetReference,
        null,
        null,
        diagnosticMessage);
}

public sealed class SceneDocument
{
    public const int CurrentSchemaVersion = 2;
    private readonly List<SceneLayer> layers;

    public SceneDocument(IEnumerable<SceneLayer>? layers = null)
    {
        this.layers = layers?.OrderBy(layer => layer.ZIndex).ToList() ?? new List<SceneLayer>();
        ValidateLayers(this.layers);
    }

    public int SchemaVersion => CurrentSchemaVersion;
    public IReadOnlyList<SceneLayer> Layers => new ReadOnlyCollection<SceneLayer>(layers);

    public SceneLayer GetLayer(SceneLayerId id) => layers.FirstOrDefault(layer => layer.Id == id)
        ?? throw new KeyNotFoundException($"Scene layer '{id}' was not found.");

    public void AddLayer(SceneLayer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        if (layers.Any(existing => existing.Id == layer.Id)) throw new ArgumentException("Layer IDs must be unique.", nameof(layer));
        if (layers.Any(existing => existing.ZIndex == layer.ZIndex)) throw new ArgumentException("Layer z-indices must be unique.", nameof(layer));
        layers.Add(layer);
        layers.Sort((left, right) => left.ZIndex.CompareTo(right.ZIndex));
    }

    public bool RemoveLayer(SceneLayerId id)
    {
        var index = layers.FindIndex(layer => layer.Id == id);
        if (index < 0) return false;
        layers.RemoveAt(index);
        return true;
    }

    public void SetLayerTransform(SceneLayerId id, NormalizedTransform transform) => ReplaceLayer(GetLayer(id).WithTransform(transform));

    public void SetLayerVisibility(SceneLayerId id, bool isVisible) => ReplaceLayer(GetLayer(id).WithVisibility(isVisible));

    public void MoveLayer(SceneLayerId id, int targetIndex)
    {
        if (targetIndex < 0 || targetIndex >= layers.Count) throw new ArgumentOutOfRangeException(nameof(targetIndex));
        var currentIndex = layers.FindIndex(layer => layer.Id == id);
        if (currentIndex < 0) throw new KeyNotFoundException($"Scene layer '{id}' was not found.");
        var layer = layers[currentIndex];
        layers.RemoveAt(currentIndex);
        layers.Insert(targetIndex, layer);
        for (var index = 0; index < layers.Count; index++) layers[index] = layers[index].WithZIndex(index);
    }

    public SceneDocument Snapshot() => new(layers);

    internal void Restore(SceneDocument snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        layers.Clear();
        layers.AddRange(snapshot.layers);
    }

    internal void ReplaceLayer(SceneLayer replacement)
    {
        var index = layers.FindIndex(layer => layer.Id == replacement.Id);
        if (index < 0) throw new KeyNotFoundException($"Scene layer '{replacement.Id}' was not found.");
        layers[index] = replacement;
    }

    private static void ValidateLayers(IReadOnlyList<SceneLayer> candidateLayers)
    {
        if (candidateLayers.Select(layer => layer.Id).Distinct().Count() != candidateLayers.Count)
        {
            throw new ArgumentException("Layer IDs must be unique.", nameof(candidateLayers));
        }

        if (candidateLayers.Select(layer => layer.ZIndex).Distinct().Count() != candidateLayers.Count)
        {
            throw new ArgumentException("Layer z-indices must be unique.", nameof(candidateLayers));
        }
    }
}

public interface ISceneAssetResolver
{
    bool IsAvailable(SceneAssetReference assetReference);
}

public sealed class DelegateSceneAssetResolver : ISceneAssetResolver
{
    private readonly Func<SceneAssetReference, bool> resolver;

    public DelegateSceneAssetResolver(Func<SceneAssetReference, bool> resolver)
    {
        this.resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    public bool IsAvailable(SceneAssetReference assetReference) => resolver(assetReference);
}

public sealed class DelegateDiagnosticsSink : IDiagnosticsSink
{
    private readonly Action<DiagnosticEvent> report;

    public DelegateDiagnosticsSink(Action<DiagnosticEvent> report)
    {
        this.report = report ?? throw new ArgumentNullException(nameof(report));
    }

    public void Report(DiagnosticEvent diagnostic) => report(diagnostic);
}
