using BetterDemo.Core.Contracts;
using NAudio.CoreAudioApi;

namespace BetterDemo.Interop.Wasapi;

public sealed class WasapiAudioAdapter : IWasapiAudioAdapter
{
    public ValueTask<IReadOnlyList<AudioRenderEndpoint>> EnumerateActiveRenderEndpointsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("WASAPI requires Windows.");

        using var enumerator = new MMDeviceEnumerator();
        var endpoints = new List<AudioRenderEndpoint>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                cancellationToken.ThrowIfCancellationRequested();
                endpoints.Add(new AudioRenderEndpoint(
                    new AudioDeviceId(device.ID),
                    device.FriendlyName,
                    VirtualCableEndpointPolicy.IsVirtualCableRenderEndpoint(device.FriendlyName, device.DeviceFriendlyName)));
            }
        }
        return ValueTask.FromResult<IReadOnlyList<AudioRenderEndpoint>>(endpoints);
    }

    public async ValueTask<IProcessAwareAudioCapture> OpenProcessCaptureAsync(
        AudioCaptureRequest request,
        AudioFormat format,
        CancellationToken cancellationToken = default)
    {
        var capture = new WasapiProcessLoopbackCapture(format);
        await capture.ConfigureAsync(request, cancellationToken).ConfigureAwait(false);
        return capture;
    }

    public ValueTask<IAudioOutputWriter> OpenAudioOutputWriterAsync(
        AudioRenderEndpoint endpoint,
        AudioFormat format,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(endpoint);
        return ValueTask.FromResult<IAudioOutputWriter>(new WasapiAudioOutputWriter(endpoint, format));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
