using BetterDemo.Core.Contracts;

namespace BetterDemo.Interop.Wasapi;

public interface IWasapiAudioAdapter : IAsyncDisposable
{
    ValueTask<IReadOnlyList<AudioRenderEndpoint>> EnumerateActiveRenderEndpointsAsync(CancellationToken cancellationToken = default);

    ValueTask<IProcessAwareAudioCapture> OpenProcessCaptureAsync(
        AudioCaptureRequest request,
        AudioFormat format,
        CancellationToken cancellationToken = default);

    ValueTask<IAudioOutputWriter> OpenAudioOutputWriterAsync(
        AudioRenderEndpoint endpoint,
        AudioFormat format,
        CancellationToken cancellationToken = default);
}
