using BetterDemo.Core.Contracts;
using BetterDemo.Interop.Wasapi;

namespace BetterDemo.Audio;

public sealed class ProcessAudioRoute : IAsyncDisposable
{
    private readonly IWasapiAudioAdapter adapter;
    private readonly Func<int?> discordRootProcessIdProvider;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private IProcessAwareAudioCapture? capture;
    private IAudioOutputWriter? writer;
    private CancellationTokenSource? pumpCancellation;
    private Task? pump;
    private bool muted;
    private float outputVolume = 1f;
    private bool disposed;

    public ProcessAudioRoute(IWasapiAudioAdapter adapter, Func<int?>? discordRootProcessIdProvider = null)
    {
        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        this.discordRootProcessIdProvider = discordRootProcessIdProvider ?? ProcessTreeGuard.FindDiscordRootProcessId;
    }

    public bool IsRunning => capture?.State == SourceState.Running &&
        writer?.State == SourceState.Running && pump is { IsCompleted: false };
    public bool IsMuted
    {
        get => Volatile.Read(ref muted);
        set => Volatile.Write(ref muted, value);
    }

    public float OutputVolume
    {
        get => Volatile.Read(ref outputVolume);
        set
        {
            if (!float.IsFinite(value) || value is < 0 or > 1)
                throw new ArgumentOutOfRangeException(nameof(value), "Volume must be between 0 and 1.");
            Volatile.Write(ref outputVolume, value);
        }
    }

    public Exception? LastFailure { get; private set; }

    public async ValueTask StartAsync(AudioRenderEndpoint endpoint,
        CancellationToken cancellationToken = default)
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (capture is not null || writer is not null) throw new InvalidOperationException("Stop the current audio route first.");

            var discordRootProcessId = discordRootProcessIdProvider()
                ?? throw new InvalidOperationException("Start Discord before routing system audio. Discord is always excluded from the mix.");
            var request = new AudioCaptureRequest(
                discordRootProcessId,
                [discordRootProcessId],
                ["Discord.exe"],
                [discordRootProcessId],
                AudioCapturePath.ExcludeTargetProcessTree,
                null);
            var openedWriter = await adapter.OpenAudioOutputWriterAsync(endpoint, AudioFormat.Mixer48KHz, cancellationToken)
                .ConfigureAwait(false);
            IProcessAwareAudioCapture? openedCapture = null;
            try
            {
                await openedWriter.StartAsync(cancellationToken).ConfigureAwait(false);
                openedCapture = await adapter.OpenProcessCaptureAsync(request, AudioFormat.Mixer48KHz, cancellationToken)
                    .ConfigureAwait(false);
                await openedCapture.StartAsync(cancellationToken).ConfigureAwait(false);
                var cancellation = new CancellationTokenSource();
                writer = openedWriter;
                capture = openedCapture;
                pumpCancellation = cancellation;
                LastFailure = null;
                pump = Task.Run(() => PumpAsync(openedCapture, openedWriter, discordRootProcessId, cancellation.Token));
            }
            catch
            {
                if (openedCapture is not null) await openedCapture.DisposeAsync().ConfigureAwait(false);
                await openedWriter.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async ValueTask TestToneAsync(CancellationToken cancellationToken = default)
    {
        var activeWriter = writer ?? throw new InvalidOperationException("Start an audio route first.");
        const int sampleRate = 48_000;
        const int framesPerBlock = 480;
        const int blocks = 50;
        for (var block = 0; block < blocks; block++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var samples = new float[framesPerBlock * 2];
            for (var frame = 0; frame < framesPerBlock; frame++)
            {
                var frameIndex = block * framesPerBlock + frame;
                var envelope = Math.Min(1.0, Math.Min(frameIndex, sampleRate / 2 - frameIndex - 1) / 240.0);
                var value = (float)(0.12 * envelope * Math.Sin(2 * Math.PI * 440 * frameIndex / sampleRate));
                samples[frame * 2] = value;
                samples[frame * 2 + 1] = value;
            }
            var volume = OutputVolume;
            if (volume > 0)
            {
                for (var index = 0; index < samples.Length; index++) samples[index] *= volume;
                await activeWriter.WriteAsync(samples, AudioFormat.Mixer48KHz, cancellationToken).ConfigureAwait(false);
            }
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;
            try
            {
                await StopCoreAsync().ConfigureAwait(false);
            }
            finally
            {
                await adapter.DisposeAsync().ConfigureAwait(false);
                disposed = true;
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private async Task PumpAsync(IProcessAwareAudioCapture source, IAudioOutputWriter output, int excludedDiscordRootProcessId,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var block in source.ReadBlocksAsync(cancellationToken).ConfigureAwait(false))
            {
                if (discordRootProcessIdProvider() != excludedDiscordRootProcessId)
                {
                    throw new InvalidOperationException("Discord restarted or its process tree changed. Audio forwarding stopped to keep Discord excluded; restart the route.");
                }

                var volume = OutputVolume;
                if (!IsMuted && volume > 0)
                {
                    if (volume >= 0.999f)
                        await output.WriteAsync(block, source.Format, cancellationToken).ConfigureAwait(false);
                    else
                    {
                        var scaled = new float[block.Length];
                        for (var index = 0; index < block.Length; index++) scaled[index] = block.Span[index] * volume;
                        await output.WriteAsync(scaled, source.Format, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            LastFailure = exception;
        }
    }

    private async Task StopCoreAsync()
    {
        var activeCapture = capture;
        var activeWriter = writer;
        var activePump = pump;
        var cancellation = pumpCancellation;
        capture = null;
        writer = null;
        pump = null;
        pumpCancellation = null;

        Exception? failure = null;
        try { cancellation?.Cancel(); }
        catch (Exception exception) { failure = exception; }
        try { if (activeCapture is not null) await activeCapture.StopAsync().ConfigureAwait(false); }
        catch (Exception exception) { failure ??= exception; }
        try { if (activePump is not null) await activePump.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (Exception exception) { failure ??= exception; }
        try { if (activeCapture is not null) await activeCapture.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) { failure ??= exception; }
        try { if (activeWriter is not null) await activeWriter.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) { failure ??= exception; }
        cancellation?.Dispose();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
