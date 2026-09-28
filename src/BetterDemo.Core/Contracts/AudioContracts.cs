namespace BetterDemo.Core.Contracts;

public readonly record struct AudioDeviceId
{
    public AudioDeviceId(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A stable audio device ID is required.", nameof(value));
        Value = value;
    }

    public string Value { get; }
    public override string ToString() => Value;
}

public readonly record struct AudioFormat
{
    public AudioFormat(int sampleRateHz, int channels)
    {
        if (sampleRateHz <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        SampleRateHz = sampleRateHz;
        Channels = channels;
    }

    public int SampleRateHz { get; }
    public int Channels { get; }
    public static AudioFormat Mixer48KHz => new(48_000, 2);
}

public enum AudioCapturePath
{
    ApplicationLoopback,
    ExcludeTargetProcessTree,
    RenderEndpointFallback
}

public sealed class AudioCaptureRequest
{
    public AudioCaptureRequest(
        int sourceProcessId,
        IEnumerable<int> excludedProcessIds,
        IEnumerable<string> excludedExecutableNames,
        IEnumerable<int> excludedSessionIds,
        AudioCapturePath capturePath,
        string? fallbackNotice)
    {
        if (sourceProcessId <= 0) throw new ArgumentOutOfRangeException(nameof(sourceProcessId));
        if (!Enum.IsDefined(capturePath)) throw new ArgumentOutOfRangeException(nameof(capturePath));

        SourceProcessId = sourceProcessId;
        ExcludedProcessIds = new HashSet<int>(excludedProcessIds ?? throw new ArgumentNullException(nameof(excludedProcessIds)));
        ExcludedExecutableNames = new HashSet<string>(excludedExecutableNames ?? throw new ArgumentNullException(nameof(excludedExecutableNames)), StringComparer.OrdinalIgnoreCase);
        ExcludedSessionIds = new HashSet<int>(excludedSessionIds ?? throw new ArgumentNullException(nameof(excludedSessionIds)));
        CapturePath = capturePath;
        FallbackNotice = fallbackNotice;

        if (!ExcludesDiscordByExecutable("Discord.exe"))
        {
            throw new ArgumentException("Discord.exe must be excluded by executable identity.", nameof(excludedExecutableNames));
        }

        if (capturePath == AudioCapturePath.ExcludeTargetProcessTree &&
            (!ExcludedProcessIds.Contains(sourceProcessId) || !ExcludesDiscordByExecutable("Discord.exe")))
        {
            throw new ArgumentException("System loopback must exclude the Discord process tree.", nameof(excludedProcessIds));
        }

        if (capturePath == AudioCapturePath.ApplicationLoopback && ExcludedProcessIds.Contains(sourceProcessId))
        {
            throw new ArgumentException("An application cannot be both the captured source and an excluded process.", nameof(excludedProcessIds));
        }

        if (capturePath == AudioCapturePath.RenderEndpointFallback && string.IsNullOrWhiteSpace(fallbackNotice))
        {
            throw new ArgumentException("Endpoint fallback requires an explicit diagnostic notice.", nameof(fallbackNotice));
        }

        if (capturePath == AudioCapturePath.RenderEndpointFallback &&
            (ExcludedProcessIds.Count == 0 || ExcludedSessionIds.Count == 0))
        {
            throw new ArgumentException("Endpoint fallback requires Discord process and session exclusions.");
        }
    }

    public int SourceProcessId { get; }
    public IReadOnlySet<int> ExcludedProcessIds { get; }
    public IReadOnlySet<string> ExcludedExecutableNames { get; }
    public IReadOnlySet<int> ExcludedSessionIds { get; }
    public AudioCapturePath CapturePath { get; }
    public string? FallbackNotice { get; }
    public bool UsesEndpointFallback => CapturePath == AudioCapturePath.RenderEndpointFallback;

    public bool ExcludesDiscordByProcessId(int processId) => ExcludedProcessIds.Contains(processId);

    public bool ExcludesDiscordByExecutable(string executableName) => ExcludedExecutableNames.Contains(executableName);

    public bool ExcludesDiscordBySession(int sessionId) => ExcludedSessionIds.Contains(sessionId);
}

public sealed record AudioRenderEndpoint(AudioDeviceId Id, string DisplayName, bool IsVirtualCable = false);

public interface IProcessAwareAudioCapture : ISourceLifecycle
{
    AudioFormat Format { get; }

    ValueTask ConfigureAsync(AudioCaptureRequest request, CancellationToken cancellationToken = default);

    IAsyncEnumerable<ReadOnlyMemory<float>> ReadBlocksAsync(CancellationToken cancellationToken = default);
}

public interface IAudioOutputWriter : ISourceLifecycle
{
    AudioRenderEndpoint Endpoint { get; }

    ValueTask WriteAsync(ReadOnlyMemory<float> samples, AudioFormat format, CancellationToken cancellationToken = default);
}
