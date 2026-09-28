namespace BetterDemo.Core.Contracts;

public readonly record struct OutputWindowId
{
    public OutputWindowId(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A stable output window ID is required.", nameof(value));
        Value = value;
    }

    public string Value { get; }
    public override string ToString() => Value;
}

public enum OutputWindowState
{
    Created,
    Visible,
    Hidden,
    Minimized,
    Closed,
    Disposed
}

public static class WindowLifecycleRules
{
    public static bool CanTransition(OutputWindowState from, OutputWindowState to) => (from, to) switch
    {
        (OutputWindowState.Created, OutputWindowState.Visible or OutputWindowState.Hidden or OutputWindowState.Closed or OutputWindowState.Disposed) => true,
        (OutputWindowState.Visible, OutputWindowState.Hidden or OutputWindowState.Minimized or OutputWindowState.Closed or OutputWindowState.Disposed) => true,
        (OutputWindowState.Hidden, OutputWindowState.Visible or OutputWindowState.Closed or OutputWindowState.Disposed) => true,
        (OutputWindowState.Minimized, OutputWindowState.Visible or OutputWindowState.Closed or OutputWindowState.Disposed) => true,
        (OutputWindowState.Closed, OutputWindowState.Disposed) => true,
        _ => false
    };
}

public interface IOutputWindow : IAsyncDisposable
{
    OutputWindowId Id { get; }
    OutputWindowState State { get; }

    ValueTask ShowAsync(CancellationToken cancellationToken = default);

    ValueTask HideAsync(CancellationToken cancellationToken = default);

    ValueTask CloseAsync(CancellationToken cancellationToken = default);
}
