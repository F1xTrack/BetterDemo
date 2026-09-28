using System.Text.Json;
using BetterDemo.Core.Contracts;

namespace BetterDemo.Remote.Protocol;

public static class RemotePayloads
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static ReadOnlyMemory<byte> Empty() => "{}"u8.ToArray();

    public static ReadOnlyMemory<byte> SetMode(SceneMode mode) => Serialize(new { mode = SceneModeWireNames.ToWireName(mode) });

    public static ReadOnlyMemory<byte> SetCameraCorner(double angleDegrees, double scale, double margin) =>
        Serialize(new { angleDegrees, scale, margin });

    public static ReadOnlyMemory<byte> SetZoom(double zoom) => Serialize(new { zoom });

    public static ReadOnlyMemory<byte> SetTransform(double zoom, double panX, double panY) => Serialize(new { zoom, panX, panY });

    public static ReadOnlyMemory<byte> PanBy(double x, double y) => Serialize(new { x, y });

    public static ReadOnlyMemory<byte> ResetTransform() => Empty();

    public static ReadOnlyMemory<byte> SetBlur(bool enabled, double radius) => Serialize(new { enabled, radius });

    public static ReadOnlyMemory<byte> SetLayerVisibility(string layerId, bool visible) =>
        Serialize(new { layerId, visible });

    public static ReadOnlyMemory<byte> SetOutputVisibility(bool visible) => Serialize(new { visible });

    private static ReadOnlyMemory<byte> Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);
}
