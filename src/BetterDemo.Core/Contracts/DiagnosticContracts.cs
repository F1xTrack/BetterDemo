namespace BetterDemo.Core.Contracts;

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error
}

public enum DiagnosticCode
{
    SourceUnavailable,
    DeviceLost,
    FrameDropped,
    AudioEndpointUnavailable,
    AudioEndpointFallback,
    AudioUnderrun,
    DiscordExcluded,
    VirtualCableUnavailable,
    OutputWindowUnavailable,
    ProtocolRejected,
    ProtocolStale,
    SceneAssetMissing
}

public sealed record DiagnosticEvent
{
    public DiagnosticEvent(
        DiagnosticSeverity severity,
        DiagnosticCode code,
        string component,
        string message)
    {
        if (string.IsNullOrWhiteSpace(component)) throw new ArgumentException("A diagnostic component is required.", nameof(component));
        if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("A diagnostic message is required.", nameof(message));

        Severity = severity;
        Code = code;
        Component = component;
        Message = message;
    }

    public DiagnosticSeverity Severity { get; }
    public DiagnosticCode Code { get; }
    public string Component { get; }
    public string Message { get; }
}

public interface IDiagnosticsSink
{
    void Report(DiagnosticEvent diagnostic);
}

public interface IDiagnosticsSnapshot
{
    IReadOnlyList<DiagnosticEvent> Current { get; }
}
