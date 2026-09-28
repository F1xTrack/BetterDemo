using System.Collections.Concurrent;
using System.Threading.Channels;
using BetterDemo.Audio;
using BetterDemo.Core.Contracts;
using BetterDemo.Interop.Wasapi;
using Xunit;

namespace BetterDemo.Integration.Tests;

public sealed class ProcessAudioRouteTests
{
    [Fact]
    public async Task Route_forwards_selected_audio_honors_mute_and_generates_bounded_tone()
    {
        var adapter = new FakeAudioAdapter();
        await using var route = new ProcessAudioRoute(adapter, () => 12345);
        await route.StartAsync(adapter.Endpoint);
        Assert.True(route.IsRunning);
        Assert.Equal(AudioCapturePath.ExcludeTargetProcessTree, adapter.Request!.CapturePath);
        Assert.Equal(12345, adapter.Request.SourceProcessId);
        Assert.True(adapter.Request.ExcludesDiscordByProcessId(12345));
        Assert.True(adapter.Request.ExcludesDiscordByExecutable("Discord.exe"));

        adapter.Capture.Push([0.25f, -0.25f]);
        await WaitUntilAsync(() => adapter.Writer.Writes.Count >= 1);
        Assert.Equal([0.25f, -0.25f], adapter.Writer.Writes.First());

        route.OutputVolume = 0.5f;
        adapter.Capture.Push([0.25f, -0.25f]);
        await WaitUntilAsync(() => adapter.Writer.Writes.Count >= 2);
        Assert.Equal([0.125f, -0.125f], adapter.Writer.Writes.Skip(1).First());
        route.OutputVolume = 1f;

        route.IsMuted = true;
        adapter.Capture.Push([0.5f, 0.5f]);
        await Task.Delay(50);
        Assert.Equal(2, adapter.Writer.Writes.Count);

        await route.TestToneAsync();
        Assert.Equal(52, adapter.Writer.Writes.Count);
        Assert.All(adapter.Writer.Writes.Skip(2), block =>
        {
            Assert.Equal(960, block.Length);
            Assert.All(block, sample => Assert.InRange(sample, -0.121f, 0.121f));
        });
        var toneSamples = adapter.Writer.Writes.Skip(2).SelectMany(block => block.Where((_, index) => index % 2 == 0)).ToArray();
        var positiveZeroCrossings = Enumerable.Range(1, toneSamples.Length - 1)
            .Count(index => toneSamples[index - 1] <= 0 && toneSamples[index] > 0);
        Assert.InRange(positiveZeroCrossings, 219, 221); // 440 Hz for half a second.

        await route.StopAsync();
        Assert.False(route.IsRunning);
        Assert.Equal(SourceState.Disposed, adapter.Capture.State);
        Assert.Equal(SourceState.Disposed, adapter.Writer.State);
    }

    [Fact]
    public async Task Route_fails_closed_when_the_discord_process_tree_changes()
    {
        var adapter = new FakeAudioAdapter();
        var discordRoot = 12345;
        await using var route = new ProcessAudioRoute(adapter, () => discordRoot);
        await route.StartAsync(adapter.Endpoint);

        discordRoot = 54321;
        adapter.Capture.Push([0.75f, 0.75f]);
        await WaitUntilAsync(() => route.LastFailure is not null);

        Assert.Contains("Discord restarted", route.LastFailure!.Message, StringComparison.Ordinal);
        Assert.Empty(adapter.Writer.Writes);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class FakeAudioAdapter : IWasapiAudioAdapter
    {
        public AudioRenderEndpoint Endpoint { get; } = new(new AudioDeviceId("test-cable"), "CABLE Input test");
        public FakeCapture Capture { get; } = new();
        public FakeWriter Writer { get; } = new();
        public AudioCaptureRequest? Request { get; private set; }

        public ValueTask<IReadOnlyList<AudioRenderEndpoint>> EnumerateActiveRenderEndpointsAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AudioRenderEndpoint>>([Endpoint]);

        public ValueTask<IProcessAwareAudioCapture> OpenProcessCaptureAsync(AudioCaptureRequest request, AudioFormat format,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return ValueTask.FromResult<IProcessAwareAudioCapture>(Capture);
        }

        public ValueTask<IAudioOutputWriter> OpenAudioOutputWriterAsync(AudioRenderEndpoint endpoint, AudioFormat format,
            CancellationToken cancellationToken = default) => ValueTask.FromResult<IAudioOutputWriter>(Writer);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeCapture : IProcessAwareAudioCapture
    {
        private readonly Channel<ReadOnlyMemory<float>> blocks = Channel.CreateUnbounded<ReadOnlyMemory<float>>();
        public AudioFormat Format => AudioFormat.Mixer48KHz;
        public SourceState State { get; private set; } = SourceState.Created;
        public ValueTask ConfigureAsync(AudioCaptureRequest request, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            State = SourceState.Running;
            return ValueTask.CompletedTask;
        }
        public void Push(float[] samples) => blocks.Writer.TryWrite(samples);
        public IAsyncEnumerable<ReadOnlyMemory<float>> ReadBlocksAsync(CancellationToken cancellationToken = default) =>
            blocks.Reader.ReadAllAsync(cancellationToken);
        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            blocks.Writer.TryComplete();
            State = SourceState.Stopped;
            return ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            State = SourceState.Disposed;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeWriter : IAudioOutputWriter
    {
        public AudioRenderEndpoint Endpoint { get; } = new(new AudioDeviceId("test-cable"), "CABLE Input test");
        public ConcurrentQueue<float[]> Writes { get; } = new();
        public SourceState State { get; private set; } = SourceState.Created;
        public ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            State = SourceState.Running;
            return ValueTask.CompletedTask;
        }
        public ValueTask WriteAsync(ReadOnlyMemory<float> samples, AudioFormat format, CancellationToken cancellationToken = default)
        {
            Assert.Equal(AudioFormat.Mixer48KHz, format);
            Writes.Enqueue(samples.ToArray());
            return ValueTask.CompletedTask;
        }
        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            State = SourceState.Stopped;
            return ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            State = SourceState.Disposed;
            return ValueTask.CompletedTask;
        }
    }
}
