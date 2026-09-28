namespace BetterDemo.Core.Contracts;

public enum SourceState
{
    Created,
    Starting,
    Running,
    Stopping,
    Stopped,
    Unavailable,
    Faulted,
    Disposed
}

public static class SourceStateTransitions
{
    public static bool CanTransition(SourceState from, SourceState to) => (from, to) switch
    {
        (SourceState.Created, SourceState.Starting or SourceState.Unavailable or SourceState.Faulted or SourceState.Disposed) => true,
        (SourceState.Starting, SourceState.Running or SourceState.Stopping or SourceState.Unavailable or SourceState.Faulted or SourceState.Disposed) => true,
        (SourceState.Running, SourceState.Stopping or SourceState.Unavailable or SourceState.Faulted or SourceState.Disposed) => true,
        (SourceState.Stopping, SourceState.Stopped or SourceState.Faulted or SourceState.Disposed) => true,
        (SourceState.Stopped, SourceState.Starting or SourceState.Disposed) => true,
        (SourceState.Unavailable, SourceState.Starting or SourceState.Disposed) => true,
        (SourceState.Faulted, SourceState.Starting or SourceState.Disposed) => true,
        _ => false
    };
}

public interface ISourceLifecycle : IAsyncDisposable
{
    SourceState State { get; }

    ValueTask StartAsync(CancellationToken cancellationToken = default);

    ValueTask StopAsync(CancellationToken cancellationToken = default);
}

public interface IVideoSource : ISourceLifecycle
{
    VideoDeviceDescriptor Device { get; }

    IAsyncEnumerable<VideoFrame> ReadFramesAsync(CancellationToken cancellationToken = default);
}
