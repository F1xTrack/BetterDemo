using System.Runtime.InteropServices;
using BetterDemo.Core.Contracts;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace BetterDemo.Interop.Wasapi;

public sealed class WasapiAudioOutputWriter : IAudioOutputWriter
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly AudioFormat format;
    private MMDevice? device;
    private WasapiOut? output;
    private BufferedWaveProvider? buffer;
    private SourceState state = SourceState.Created;
    private long droppedSamples;

    public WasapiAudioOutputWriter(AudioRenderEndpoint endpoint, AudioFormat format)
    {
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        if (format.Channels != 2 || format.SampleRateHz != 48_000)
            throw new ArgumentException("Audio output requires stereo 48 kHz float audio.", nameof(format));
        this.format = format;
    }

    public AudioRenderEndpoint Endpoint { get; }
    public SourceState State => state;
    public long DroppedSampleCount => Interlocked.Read(ref droppedSamples);

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(state == SourceState.Disposed, this);
            if (state == SourceState.Running) return;
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("WASAPI requires Windows.");
            state = SourceState.Starting;
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var selected = enumerator.GetDevice(Endpoint.Id.Value);
                WasapiOut? player = null;
                try
                {
                    if (selected.State != DeviceState.Active || selected.DataFlow != DataFlow.Render)
                        throw new InvalidOperationException("Selected audio output is no longer active.");

                    var waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRateHz, format.Channels);
                    var provider = new BufferedWaveProvider(waveFormat, TimeSpan.FromMilliseconds(300))
                    {
                        ReadFully = true,
                        DiscardOnBufferOverflow = false
                    };
                    player = new WasapiOut(selected, AudioClientShareMode.Shared, true, 80);
                    player.Init(provider);
                    player.Play();
                    device = selected;
                    output = player;
                    buffer = provider;
                    state = SourceState.Running;
                }
                catch
                {
                    player?.Dispose();
                    selected.Dispose();
                    throw;
                }
            }
            catch
            {
                state = SourceState.Unavailable;
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<float> samples, AudioFormat sourceFormat,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (state != SourceState.Running || output is null || buffer is null)
                throw new InvalidOperationException("Start the audio output before writing audio.");
            if (sourceFormat != format) throw new ArgumentException("Audio must be 48 kHz stereo float.", nameof(sourceFormat));
            if (samples.Length % format.Channels != 0) throw new ArgumentException("Incomplete interleaved audio frame.", nameof(samples));
            if (output.PlaybackState != PlaybackState.Playing)
            {
                state = SourceState.Unavailable;
                throw new InvalidOperationException("The audio output stopped or disconnected.");
            }

            var payload = MemoryMarshal.AsBytes(samples.Span).ToArray();
            if (payload.Length > buffer.BufferLength)
            {
                var discarded = payload.Length - buffer.BufferLength;
                discarded += discarded % (sizeof(float) * format.Channels);
                Interlocked.Add(ref droppedSamples, discarded / sizeof(float));
                payload = payload[discarded..];
            }
            if (buffer.BufferedBytes + payload.Length > buffer.BufferLength)
            {
                Interlocked.Add(ref droppedSamples, buffer.BufferedBytes / sizeof(float));
                buffer.ClearBuffer();
            }
            buffer.AddSamples(payload, 0, payload.Length);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (state == SourceState.Disposed) return;
            StopCore();
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (state == SourceState.Disposed) return;
            StopCore();
            state = SourceState.Disposed;
        }
        finally
        {
            gate.Release();
        }
    }

    private void StopCore()
    {
        state = SourceState.Stopping;
        Exception? failure = null;
        try { output?.Stop(); }
        catch (Exception exception) { failure = exception; }
        try { output?.Dispose(); }
        catch (Exception exception) { failure ??= exception; }
        try { device?.Dispose(); }
        catch (Exception exception) { failure ??= exception; }
        output = null;
        device = null;
        buffer = null;
        state = SourceState.Stopped;
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
