using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using BetterDemo.Core.Contracts;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace BetterDemo.Interop.Wasapi;

public sealed class WasapiProcessLoopbackCapture : IProcessAwareAudioCapture
{
    private const int QueueCapacity = 8;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly object stateGate = new();
    private readonly AudioFormat format;
    private AudioCaptureRequest? request;
    private Channel<ReadOnlyMemory<float>>? blocks;
    private AudioClient? client;
    private EventWaitHandle? frameEvent;
    private CancellationTokenSource? pumpCancellation;
    private Task? pump;
    private SourceState state = SourceState.Created;
    private long droppedBlocks;

    public WasapiProcessLoopbackCapture(AudioFormat format)
    {
        if (format.Channels != 2)
        {
            throw new ArgumentException("Process loopback currently requires stereo float samples.", nameof(format));
        }
        this.format = format;
    }

    public AudioFormat Format => format;
    public long DroppedBlockCount => Interlocked.Read(ref droppedBlocks);

    public SourceState State
    {
        get { lock (stateGate) return state; }
    }

    public async ValueTask ConfigureAsync(AudioCaptureRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (State is SourceState.Starting or SourceState.Running or SourceState.Stopping)
            {
                throw new InvalidOperationException("Stop process capture before changing its source.");
            }
            if (request.CapturePath is not (AudioCapturePath.ApplicationLoopback or AudioCapturePath.ExcludeTargetProcessTree))
            {
                throw new NotSupportedException("Render-endpoint fallback cannot enforce process isolation.");
            }
            if (request.CapturePath == AudioCapturePath.ApplicationLoopback &&
                request.ExcludesDiscordByProcessId(request.SourceProcessId))
            {
                throw new ArgumentException("The selected source process is excluded from capture.", nameof(request));
            }
            if (request.CapturePath == AudioCapturePath.ExcludeTargetProcessTree &&
                !request.ExcludesDiscordByProcessId(request.SourceProcessId))
            {
                throw new ArgumentException("System audio capture must exclude Discord's process tree.", nameof(request));
            }
            this.request = request;
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (State == SourceState.Running) return;
            if (request is null) throw new InvalidOperationException("Configure a source process first.");
            if (client is not null) await StopCoreAsync().ConfigureAwait(false);

            SetState(SourceState.Starting);
            AudioClient? openedClient = null;
            EventWaitHandle? openedEvent = null;
            try
            {
                ProcessTreeGuard.Validate(request);
                var loopbackMode = request.CapturePath == AudioCapturePath.ExcludeTargetProcessTree ? 1 : 0;
                openedClient = await ProcessLoopbackActivator.ActivateAsync(request.SourceProcessId, loopbackMode, cancellationToken)
                    .ConfigureAwait(false);
                var waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRateHz, format.Channels);
                openedClient.Initialize(
                    AudioClientShareMode.Shared,
                    AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback,
                    TimeSpan.FromMilliseconds(100).Ticks,
                    0,
                    waveFormat,
                    Guid.Empty);
                openedEvent = new EventWaitHandle(false, EventResetMode.AutoReset);
                openedClient.SetEventHandle(openedEvent.SafeWaitHandle.DangerousGetHandle());
                _ = openedClient.AudioCaptureClient;
                openedClient.Start();

                blocks = CreateQueue();
                pumpCancellation = new CancellationTokenSource();
                client = openedClient;
                frameEvent = openedEvent;
                SetState(SourceState.Running);
                pump = Task.Run(() => PumpFrames(openedClient, openedEvent, blocks, pumpCancellation.Token));
            }
            catch (OperationCanceledException)
            {
                if (openedClient is not null) ReleaseFailedStart(openedClient, openedEvent);
                SetState(SourceState.Stopped);
                throw;
            }
            catch
            {
                if (openedClient is not null) ReleaseFailedStart(openedClient, openedEvent);
                SetState(SourceState.Unavailable);
                throw;
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State == SourceState.Disposed) return;
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async IAsyncEnumerable<ReadOnlyMemory<float>> ReadBlocksAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var current = blocks ?? throw new InvalidOperationException("Start process capture before reading audio.");
        await foreach (var block in current.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return block;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State == SourceState.Disposed) return;
            await StopCoreAsync().ConfigureAwait(false);
            SetState(SourceState.Disposed);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private void PumpFrames(
        AudioClient activeClient,
        EventWaitHandle activeEvent,
        Channel<ReadOnlyMemory<float>> queue,
        CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            var capture = activeClient.AudioCaptureClient;
            while (!cancellationToken.IsCancellationRequested)
            {
                activeEvent.WaitOne(TimeSpan.FromMilliseconds(250));
                if (cancellationToken.IsCancellationRequested) break;

                while (capture.GetNextPacketSize() > 0)
                {
                    var buffer = capture.GetBuffer(out var frames, out var flags);
                    var samples = new float[checked(frames * format.Channels)];
                    try
                    {
                        if ((flags & AudioClientBufferFlags.Silent) == 0)
                        {
                            Marshal.Copy(buffer, samples, 0, samples.Length);
                        }
                    }
                    finally
                    {
                        capture.ReleaseBuffer(frames);
                    }

                    if (!queue.Writer.TryWrite(samples))
                    {
                        if (queue.Reader.TryRead(out _)) Interlocked.Increment(ref droppedBlocks);
                        if (!queue.Writer.TryWrite(samples)) Interlocked.Increment(ref droppedBlocks);
                    }
                }
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            failure = exception;
            SetState(SourceState.Unavailable);
        }
        finally
        {
            queue.Writer.TryComplete(failure);
        }
    }

    private async Task StopCoreAsync()
    {
        if (client is null)
        {
            blocks?.Writer.TryComplete();
            if (State != SourceState.Disposed) SetState(SourceState.Stopped);
            return;
        }

        SetState(SourceState.Stopping);
        var currentClient = client;
        var currentEvent = frameEvent;
        var currentCancellation = pumpCancellation;
        var currentPump = pump;
        var currentQueue = blocks;
        client = null;
        frameEvent = null;
        pumpCancellation = null;
        pump = null;

        Exception? failure = null;
        try { currentCancellation?.Cancel(); }
        catch (Exception exception) { failure = exception; }
        try { currentEvent?.Set(); }
        catch (Exception exception) { failure ??= exception; }
        try
        {
            if (currentPump is not null)
            {
                await currentPump.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            try { currentClient.Stop(); }
            catch (Exception exception) { failure ??= exception; }
            try { currentClient.Dispose(); }
            catch (Exception exception) { failure ??= exception; }
            try { currentEvent?.Dispose(); }
            catch (Exception exception) { failure ??= exception; }
            currentCancellation?.Dispose();
            if (currentQueue is not null)
            {
                while (currentQueue.Reader.TryRead(out _)) { }
                currentQueue.Writer.TryComplete();
            }
            SetState(SourceState.Stopped);
        }

        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void ReleaseFailedStart(AudioClient openedClient, EventWaitHandle? openedEvent)
    {
        try { openedClient.Stop(); }
        catch { /* The client may not have reached Start. */ }
        openedClient.Dispose();
        openedEvent?.Dispose();
    }

    private static Channel<ReadOnlyMemory<float>> CreateQueue() =>
        Channel.CreateBounded<ReadOnlyMemory<float>>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });

    private void SetState(SourceState next)
    {
        lock (stateGate) state = next;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(State == SourceState.Disposed, this);
}
