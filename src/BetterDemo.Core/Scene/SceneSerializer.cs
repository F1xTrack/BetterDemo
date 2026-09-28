using System.Text.Json;
using System.Text.Json.Serialization;
using BetterDemo.Core.Contracts;

namespace BetterDemo.Core.Scene;

public sealed class SceneSerializationException : FormatException
{
    public SceneSerializationException(string message) : base(message)
    {
    }

    public SceneSerializationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

public static class SceneSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        AllowTrailingCommas = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        NumberHandling = JsonNumberHandling.Strict,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false
    };

    public static string Serialize(SceneDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var wire = new SceneWireDocument
        {
            SchemaVersion = document.SchemaVersion,
            Layers = document.Layers.Select(ToWire).ToList()
        };

        return JsonSerializer.Serialize(wire, Options);
    }

    public static SceneDocument Deserialize(
        string json,
        ISceneAssetResolver? assetResolver = null,
        IDiagnosticsSink? diagnostics = null)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new SceneSerializationException("Scene JSON is required.");

        SceneWireDocument? wire;
        try
        {
            wire = JsonSerializer.Deserialize<SceneWireDocument>(json, Options);
        }
        catch (JsonException exception)
        {
            throw new SceneSerializationException("Scene JSON is malformed.", exception);
        }

        if (wire is null || wire.SchemaVersion is null || wire.Layers is null)
        {
            throw new SceneSerializationException("Scene JSON must contain schemaVersion and layers.");
        }

        if (wire.SchemaVersion.Value is not 1 and not SceneDocument.CurrentSchemaVersion)
        {
            throw new SceneSerializationException($"Unsupported scene schema version '{wire.SchemaVersion.Value}'.");
        }

        try
        {
            var isVersionOne = wire.SchemaVersion.Value == 1;
            var layers = wire.Layers.Select(layer => FromWire(layer, assetResolver, diagnostics, isVersionOne)).ToList();
            return new SceneDocument(layers);
        }
        catch (SceneSerializationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or ArgumentOutOfRangeException or KeyNotFoundException)
        {
            throw new SceneSerializationException("Scene JSON contains an invalid layer.", exception);
        }
    }

    private static SceneWireLayer ToWire(SceneLayer layer) => new()
    {
        Id = layer.Id.Value,
        Kind = ToWireKind(layer.Kind),
        ZIndex = layer.ZIndex,
        Transform = new SceneWireTransform
        {
            X = layer.Transform.X,
            Y = layer.Transform.Y,
            Width = layer.Transform.Width,
            Height = layer.Transform.Height,
            RotationDegrees = layer.Transform.RotationDegrees,
            ScaleX = layer.Transform.ScaleX,
            ScaleY = layer.Transform.ScaleY
        },
        Opacity = layer.Opacity,
        Visible = layer.IsVisible,
        Locked = layer.IsLocked,
        Asset = layer.AssetReference is { } asset ? new SceneWireAsset { AssetId = asset.AssetId, Location = asset.Location } : null,
        FillColor = layer.FillColor is { } fillColor ? ToWireColor(fillColor) : null,
        TextContent = layer.TextContent is { } textContent ? new SceneWireTextContent
        {
            Text = textContent.Text,
            Foreground = ToWireColor(textContent.Foreground),
            FontSize = textContent.FontSize,
            FontFamily = textContent.FontFamily
        } : null,
        DiagnosticMessage = layer.DiagnosticMessage
    };

    private static SceneLayer FromWire(
        SceneWireLayer wire,
        ISceneAssetResolver? assetResolver,
        IDiagnosticsSink? diagnostics,
        bool isVersionOne)
    {
        if (wire is null || string.IsNullOrWhiteSpace(wire.Id) || string.IsNullOrWhiteSpace(wire.Kind) || wire.ZIndex is null ||
            wire.Transform is null || wire.Opacity is null || wire.Visible is null || wire.Locked is null)
        {
            throw new SceneSerializationException("Scene layer is missing a required property.");
        }

        SceneAssetReference? asset = null;
        if (wire.Asset is not null)
        {
            if (string.IsNullOrWhiteSpace(wire.Asset.AssetId) || string.IsNullOrWhiteSpace(wire.Asset.Location))
            {
                throw new SceneSerializationException("Scene asset is missing a required property.");
            }

            asset = new SceneAssetReference(wire.Asset.AssetId, wire.Asset.Location);
        }

        var kind = ParseKind(wire.Kind);
        SceneRgbaColor? fillColor = wire.FillColor is null
            ? isVersionOne && kind == SceneLayerKind.Color ? SceneRgbaColor.Black : null
            : FromWireColor(wire.FillColor);
        var textContent = wire.TextContent is null ? null : new SceneTextContent(
            RequiredString(wire.TextContent.Text, "textContent.text"),
            FromWireColor(wire.TextContent.Foreground ?? throw new SceneSerializationException("Text content is missing foreground color.")),
            Required(wire.TextContent.FontSize, "textContent.fontSize"),
            RequiredString(wire.TextContent.FontFamily, "textContent.fontFamily"));
        var transform = new NormalizedTransform(
            Required(wire.Transform.X, "x"),
            Required(wire.Transform.Y, "y"),
            Required(wire.Transform.Width, "width"),
            Required(wire.Transform.Height, "height"),
            Required(wire.Transform.RotationDegrees, "rotationDegrees"),
            Required(wire.Transform.ScaleX, "scaleX"),
            Required(wire.Transform.ScaleY, "scaleY"));
        var layer = kind == SceneLayerKind.DiagnosticPlaceholder || isVersionOne && kind == SceneLayerKind.Text && textContent is null
            ? SceneLayer.CreateDiagnosticPlaceholder(
                new SceneLayerId(wire.Id),
                wire.ZIndex.Value,
                transform,
                wire.Opacity.Value,
                wire.Visible.Value,
                wire.Locked.Value,
                asset,
                wire.DiagnosticMessage ?? "Legacy text layer had no saved text content; showing a diagnostic placeholder.")
            : new SceneLayer(
                new SceneLayerId(wire.Id),
                kind,
                wire.ZIndex.Value,
                transform,
                wire.Opacity.Value,
                wire.Visible.Value,
                wire.Locked.Value,
                asset,
                fillColor,
                textContent);

        if (assetResolver is null || asset is null || assetResolver.IsAvailable(asset.Value)) return layer;

        const string component = "scene";
        var message = $"Scene asset '{asset.Value.AssetId}' is unavailable; using a diagnostic placeholder.";
        diagnostics?.Report(new DiagnosticEvent(DiagnosticSeverity.Warning, DiagnosticCode.SceneAssetMissing, component, message));
        return layer.AsDiagnosticPlaceholder(message);
    }

    private static double Required(double? value, string name) => value
        ?? throw new SceneSerializationException($"Scene transform is missing '{name}'.");

    private static string RequiredString(string? value, string name) => !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new SceneSerializationException($"Scene content is missing '{name}'.");

    private static SceneWireColor ToWireColor(SceneRgbaColor color) => new()
    {
        Red = color.Red,
        Green = color.Green,
        Blue = color.Blue,
        Alpha = color.Alpha
    };

    private static SceneRgbaColor FromWireColor(SceneWireColor wire) => new(
        Required(wire.Red, "color.red"),
        Required(wire.Green, "color.green"),
        Required(wire.Blue, "color.blue"),
        Required(wire.Alpha, "color.alpha"));

    private static byte Required(byte? value, string name) => value
        ?? throw new SceneSerializationException($"Scene content is missing '{name}'.");

    private static string ToWireKind(SceneLayerKind kind) => kind switch
    {
        SceneLayerKind.Image => "image",
        SceneLayerKind.Video => "video",
        SceneLayerKind.Text => "text",
        SceneLayerKind.Color => "color",
        SceneLayerKind.DiagnosticPlaceholder => "diagnosticPlaceholder",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown scene layer kind.")
    };

    private static SceneLayerKind ParseKind(string kind) => kind switch
    {
        "image" => SceneLayerKind.Image,
        "video" => SceneLayerKind.Video,
        "text" => SceneLayerKind.Text,
        "color" => SceneLayerKind.Color,
        "diagnosticPlaceholder" => SceneLayerKind.DiagnosticPlaceholder,
        _ => throw new SceneSerializationException($"Unknown scene layer kind '{kind}'.")
    };

    private sealed class SceneWireDocument
    {
        [JsonPropertyName("schemaVersion")]
        [JsonPropertyOrder(0)]
        public int? SchemaVersion { get; set; }

        [JsonPropertyName("layers")]
        [JsonPropertyOrder(1)]
        public List<SceneWireLayer>? Layers { get; set; }
    }

    private sealed class SceneWireLayer
    {
        [JsonPropertyName("id")]
        [JsonPropertyOrder(0)]
        public string? Id { get; set; }

        [JsonPropertyName("kind")]
        [JsonPropertyOrder(1)]
        public string? Kind { get; set; }

        [JsonPropertyName("zIndex")]
        [JsonPropertyOrder(2)]
        public int? ZIndex { get; set; }

        [JsonPropertyName("transform")]
        [JsonPropertyOrder(3)]
        public SceneWireTransform? Transform { get; set; }

        [JsonPropertyName("opacity")]
        [JsonPropertyOrder(4)]
        public double? Opacity { get; set; }

        [JsonPropertyName("visible")]
        [JsonPropertyOrder(5)]
        public bool? Visible { get; set; }

        [JsonPropertyName("locked")]
        [JsonPropertyOrder(6)]
        public bool? Locked { get; set; }

        [JsonPropertyName("asset")]
        [JsonPropertyOrder(7)]
        public SceneWireAsset? Asset { get; set; }

        [JsonPropertyName("diagnosticMessage")]
        [JsonPropertyOrder(8)]
        public string? DiagnosticMessage { get; set; }

        [JsonPropertyName("fillColor")]
        [JsonPropertyOrder(9)]
        public SceneWireColor? FillColor { get; set; }

        [JsonPropertyName("textContent")]
        [JsonPropertyOrder(10)]
        public SceneWireTextContent? TextContent { get; set; }
    }

    private sealed class SceneWireTextContent
    {
        [JsonPropertyName("text")]
        [JsonPropertyOrder(0)]
        public string? Text { get; set; }

        [JsonPropertyName("foreground")]
        [JsonPropertyOrder(1)]
        public SceneWireColor? Foreground { get; set; }

        [JsonPropertyName("fontSize")]
        [JsonPropertyOrder(2)]
        public double? FontSize { get; set; }

        [JsonPropertyName("fontFamily")]
        [JsonPropertyOrder(3)]
        public string? FontFamily { get; set; }
    }

    private sealed class SceneWireColor
    {
        [JsonPropertyName("red")]
        [JsonPropertyOrder(0)]
        public byte? Red { get; set; }

        [JsonPropertyName("green")]
        [JsonPropertyOrder(1)]
        public byte? Green { get; set; }

        [JsonPropertyName("blue")]
        [JsonPropertyOrder(2)]
        public byte? Blue { get; set; }

        [JsonPropertyName("alpha")]
        [JsonPropertyOrder(3)]
        public byte? Alpha { get; set; }
    }

    private sealed class SceneWireTransform
    {
        [JsonPropertyName("x")]
        [JsonPropertyOrder(0)]
        public double? X { get; set; }

        [JsonPropertyName("y")]
        [JsonPropertyOrder(1)]
        public double? Y { get; set; }

        [JsonPropertyName("width")]
        [JsonPropertyOrder(2)]
        public double? Width { get; set; }

        [JsonPropertyName("height")]
        [JsonPropertyOrder(3)]
        public double? Height { get; set; }

        [JsonPropertyName("rotationDegrees")]
        [JsonPropertyOrder(4)]
        public double? RotationDegrees { get; set; }

        [JsonPropertyName("scaleX")]
        [JsonPropertyOrder(5)]
        public double? ScaleX { get; set; }

        [JsonPropertyName("scaleY")]
        [JsonPropertyOrder(6)]
        public double? ScaleY { get; set; }
    }

    private sealed class SceneWireAsset
    {
        [JsonPropertyName("assetId")]
        [JsonPropertyOrder(0)]
        public string? AssetId { get; set; }

        [JsonPropertyName("location")]
        [JsonPropertyOrder(1)]
        public string? Location { get; set; }
    }
}
