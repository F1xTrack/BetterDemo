using System.Text.Json;
using System.Text.Json.Serialization;
using BetterDemo.Core.Contracts;
using BetterDemo.Core.Scene;
using BetterDemo.Remote.Protocol;

namespace BetterDemo.App.Scenes;

public sealed record UserSceneFileData(
    SceneMode Mode,
    CameraCornerState CameraCorner,
    double Zoom,
    PanState Pan,
    BlurState Blur,
    SceneDocument Document,
    IReadOnlyDictionary<string, string> LayerDisplayNames);

public sealed class UserSceneFileSerializationException : FormatException
{
    public UserSceneFileSerializationException(string message) : base(message) { }

    public UserSceneFileSerializationException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>Persists a scene document together with the desktop controls that affect its live output.</summary>
public static class UserSceneFileSerializer
{
    private const int CurrentFileVersion = 1;
    private const int MaximumSceneJsonCharacters = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new()
    {
        AllowTrailingCommas = false,
        NumberHandling = JsonNumberHandling.Strict,
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    public static string Serialize(
        RemoteSceneSnapshot state,
        SceneDocument document,
        IReadOnlyDictionary<string, string>? layerDisplayNames = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(document);
        return JsonSerializer.Serialize(new UserSceneFileWire
        {
            FileVersion = CurrentFileVersion,
            Mode = SceneModeWireNames.ToWireName(state.Mode),
            CameraCorner = state.CameraCorner,
            Zoom = state.Zoom,
            Pan = state.Pan,
            Blur = state.Blur,
            LayerDisplayNames = layerDisplayNames is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : layerDisplayNames.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            SceneDocument = SceneSerializer.Serialize(document)
        }, Options);
    }

    public static UserSceneFileData Deserialize(
        string json,
        ISceneAssetResolver? assetResolver = null,
        IDiagnosticsSink? diagnostics = null)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new UserSceneFileSerializationException("Scene file content is required.");
        if (json.Length > MaximumSceneJsonCharacters)
            throw new UserSceneFileSerializationException($"Scene files must not exceed {MaximumSceneJsonCharacters / 1024 / 1024} MiB.");

        UserSceneFileWire? wire;
        try
        {
            wire = JsonSerializer.Deserialize<UserSceneFileWire>(json, Options);
        }
        catch (JsonException exception)
        {
            throw new UserSceneFileSerializationException("Scene file JSON is malformed or contains unsupported fields.", exception);
        }

        if (wire is null || wire.FileVersion is null || wire.Mode is null || wire.CameraCorner is null ||
            wire.Zoom is null || wire.Pan is null || wire.Blur is null || string.IsNullOrWhiteSpace(wire.SceneDocument))
        {
            throw new UserSceneFileSerializationException("Scene file is missing a required property.");
        }
        if (wire.FileVersion.Value != CurrentFileVersion)
            throw new UserSceneFileSerializationException($"Unsupported scene file version '{wire.FileVersion.Value}'.");
        if (!SceneModeWireNames.TryParse(wire.Mode, out var mode))
            throw new UserSceneFileSerializationException($"Unknown scene mode '{wire.Mode}'.");

        var layerDisplayNames = wire.LayerDisplayNames ?? new Dictionary<string, string>(StringComparer.Ordinal);
        if (layerDisplayNames.Count > 256 || layerDisplayNames.Any(pair =>
                string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value) || pair.Value.Length > 256))
        {
            throw new UserSceneFileSerializationException("Scene file contains invalid layer display names.");
        }

        try
        {
            var validator = new RemoteStateStore();
            validator.UpdateLocalMode(mode);
            validator.UpdateLocalCameraCorner(wire.CameraCorner.Value);
            validator.UpdateLocalTransform(wire.Zoom.Value, wire.Pan.Value.X, wire.Pan.Value.Y);
            validator.UpdateLocalBlur(wire.Blur.Value);
            var document = SceneSerializer.Deserialize(wire.SceneDocument, assetResolver, diagnostics);
            if (document.Layers.Count > 18)
                throw new UserSceneFileSerializationException("Scene files may contain at most 18 built-in and custom layers.");
            if (layerDisplayNames.Keys.Any(key => !document.Layers.Any(layer => layer.Id.Value == key)))
                throw new UserSceneFileSerializationException("Scene file contains a display name for an unknown layer.");
            return new UserSceneFileData(
                mode,
                wire.CameraCorner.Value,
                wire.Zoom.Value,
                wire.Pan.Value,
                wire.Blur.Value,
                document,
                new Dictionary<string, string>(layerDisplayNames, StringComparer.Ordinal));
        }
        catch (SceneSerializationException exception)
        {
            throw new UserSceneFileSerializationException("Scene file contains an invalid scene document.", exception);
        }
        catch (ArgumentException exception)
        {
            throw new UserSceneFileSerializationException("Scene file contains invalid control settings.", exception);
        }
    }

    private sealed class UserSceneFileWire
    {
        [JsonPropertyName("fileVersion")]
        public int? FileVersion { get; set; }

        [JsonPropertyName("mode")]
        public string? Mode { get; set; }

        [JsonPropertyName("cameraCorner")]
        public CameraCornerState? CameraCorner { get; set; }

        [JsonPropertyName("zoom")]
        public double? Zoom { get; set; }

        [JsonPropertyName("pan")]
        public PanState? Pan { get; set; }

        [JsonPropertyName("blur")]
        public BlurState? Blur { get; set; }

        [JsonPropertyName("layerDisplayNames")]
        public Dictionary<string, string>? LayerDisplayNames { get; set; }

        [JsonPropertyName("sceneDocument")]
        public string? SceneDocument { get; set; }
    }
}
