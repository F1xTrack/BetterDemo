using System.Collections.ObjectModel;

namespace BetterDemo.Core.Contracts;

public enum SceneMode
{
    Black = 0,
    Screen = 1,
    PhysicalCamera = 2,
    ScreenPlusPhysicalCameraCorner = 3
}

public static class SceneModeWireNames
{
    private static readonly IReadOnlyList<string> wireNames = new ReadOnlyCollection<string>(new[]
    {
        "black",
        "screen",
        "physicalCamera",
        "screenPlusPhysicalCameraCorner"
    });

    public static IReadOnlyList<string> All => wireNames;

    public static string ToWireName(SceneMode mode) => mode switch
    {
        SceneMode.Black => "black",
        SceneMode.Screen => "screen",
        SceneMode.PhysicalCamera => "physicalCamera",
        SceneMode.ScreenPlusPhysicalCameraCorner => "screenPlusPhysicalCameraCorner",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown scene mode.")
    };

    public static bool TryParse(string? wireName, out SceneMode mode)
    {
        mode = wireName switch
        {
            "black" => SceneMode.Black,
            "screen" => SceneMode.Screen,
            "physicalCamera" => SceneMode.PhysicalCamera,
            "screenPlusPhysicalCameraCorner" => SceneMode.ScreenPlusPhysicalCameraCorner,
            _ => default
        };

        return wireName is "black" or "screen" or "physicalCamera" or "screenPlusPhysicalCameraCorner";
    }

    public static SceneMode Parse(string? wireName)
    {
        if (!TryParse(wireName, out var mode))
        {
            throw new FormatException($"Unknown scene mode wire name '{wireName}'.");
        }

        return mode;
    }
}

public interface ISceneModeController
{
    SceneMode CurrentMode { get; }

    ValueTask SetModeAsync(SceneMode mode, CancellationToken cancellationToken = default);
}
