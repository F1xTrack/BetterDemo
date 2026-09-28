using BetterDemo.Core.Contracts;

namespace BetterDemo.Remote.Protocol;

public static class RemoteCommandNames
{
    public const int ProtocolVersion = RemoteProtocol.CurrentVersion;
    public const string SetMode = "setMode";
    public const string SetCameraCorner = "setCameraCorner";
    public const string SetZoom = "setZoom";
    public const string SetTransform = "setTransform";
    public const string PanBy = "panBy";
    public const string ResetTransform = "resetTransform";
    public const string SetBlur = "setBlur";
    public const string SetLayerVisibility = "setLayerVisibility";
    public const string SetOutputVisibility = "setOutputVisibility";
    public const string GetState = "getState";
    public const string Ping = "ping";

    public static IReadOnlyList<string> All { get; } = new[]
    {
        SetMode, SetCameraCorner, SetZoom, SetTransform, PanBy, ResetTransform, SetBlur,
        SetLayerVisibility, SetOutputVisibility, GetState, Ping
    };

    public static bool IsKnown(string? commandName) => commandName is
        SetMode or SetCameraCorner or SetZoom or SetTransform or PanBy or ResetTransform or SetBlur or
        SetLayerVisibility or SetOutputVisibility or GetState or Ping;
}
