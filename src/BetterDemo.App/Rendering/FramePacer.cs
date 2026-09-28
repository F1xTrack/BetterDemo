using System.Diagnostics;

namespace BetterDemo.App.Rendering;

public static class FramePacer
{
    public static readonly TimeSpan TargetInterval = TimeSpan.FromSeconds(1.0 / 30);

    public static async ValueTask WaitUntilNextFrameAsync(long frameStartTimestamp, CancellationToken cancellationToken = default)
    {
        var remaining = TargetInterval - Stopwatch.GetElapsedTime(frameStartTimestamp);
        if (remaining > TimeSpan.Zero)
            await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
    }
}
