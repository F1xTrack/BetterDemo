using System.Text.Json;

namespace BetterDemo.Interop.Win32;

public sealed class OutputPlacementStore
{
    private readonly string path;

    public OutputPlacementStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A placement file path is required.", nameof(path));
        this.path = path;
    }

    public OutputWindowPosition? Load()
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("x", out var x) || x.ValueKind != JsonValueKind.Number || !x.TryGetInt32(out var left) ||
                !root.TryGetProperty("y", out var y) || y.ValueKind != JsonValueKind.Number || !y.TryGetInt32(out var top) ||
                Math.Abs((long)left) > 100_000 || Math.Abs((long)top) > 100_000)
                return null;
            return new OutputWindowPosition(left, top);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Save(OutputWindowPosition position)
    {
        if (Math.Abs((long)position.X) > 100_000 || Math.Abs((long)position.Y) > 100_000)
            throw new ArgumentOutOfRangeException(nameof(position));
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".output-position-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { x = position.X, y = position.Y }));
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
