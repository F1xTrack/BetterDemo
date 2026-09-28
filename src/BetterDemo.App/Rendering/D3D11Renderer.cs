using System.Diagnostics;
using System.Runtime.InteropServices;
using BetterDemo.Core.Contracts;
using BetterDemo.Core.Scene;
using BetterDemo.Interop.D3D11;

namespace BetterDemo.App.Rendering;

public sealed class D3D11Renderer : IAsyncDisposable
{
    private static readonly VideoDeviceId CompositorDeviceId = new("betterdemo://compositor");
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ID3D11RenderAdapter adapter;
    private readonly OutputWindowId outputWindowId;
    private readonly SceneCompositor compositor = new();
    private D3D11DeviceHandle device;
    private D3D11SurfaceHandle surface;
    private VideoFrameFormat outputFormat;
    private long sequence;
    private bool initialized;
    private bool disposed;

    public D3D11Renderer(ID3D11RenderAdapter adapter, OutputWindowId outputWindowId)
    {
        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        this.outputWindowId = outputWindowId;
    }

    public async ValueTask InitializeAsync(int width, int height, CancellationToken cancellationToken = default)
    {
        var format = new VideoFrameFormat(width, height, VideoPixelFormat.Bgra32, checked(width * 4));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (initialized)
            {
                if (format != outputFormat)
                {
                    throw new InvalidOperationException("An initialized renderer cannot change output dimensions.");
                }

                return;
            }

            outputFormat = format;
            await CreateDeviceResourcesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<SceneComposition> RenderAsync(
        SceneMode mode,
        VideoFrame? obsFrame,
        VideoFrame? physicalFrame,
        SceneRenderOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!initialized)
            {
                throw new InvalidOperationException("Initialize the renderer before presenting a scene.");
            }
            if (options.Width != outputFormat.Width || options.Height != outputFormat.Height)
            {
                throw new ArgumentException("The render options must match the initialized output dimensions.", nameof(options));
            }

            var composition = compositor.Render(mode, obsFrame, physicalFrame, options);
            await PresentCompositionAsync(composition, cancellationToken).ConfigureAwait(false);
            return composition;
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<SceneComposition> RenderAsync(
        SceneDocument document,
        IReadOnlyDictionary<SceneLayerId, VideoFrame>? layerFrames,
        SceneRenderOptions options,
        CancellationToken cancellationToken = default,
        SceneComposition? transitionFrom = null,
        double transitionProgress = 1)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!initialized)
            {
                throw new InvalidOperationException("Initialize the renderer before presenting a scene.");
            }
            if (options.Width != outputFormat.Width || options.Height != outputFormat.Height)
            {
                throw new ArgumentException("The render options must match the initialized output dimensions.", nameof(options));
            }

            var composition = compositor.Render(document, layerFrames, options);
            if (transitionFrom is not null) composition = SceneCompositor.Crossfade(transitionFrom, composition, transitionProgress);
            await PresentCompositionAsync(composition, cancellationToken).ConfigureAwait(false);
            return composition;
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
            if (disposed)
            {
                return;
            }

            disposed = true;
            if (initialized)
            {
                await adapter.DisposeSurfaceAsync(surface, CancellationToken.None).ConfigureAwait(false);
                await adapter.DisposeDeviceAsync(device, CancellationToken.None).ConfigureAwait(false);
                surface = default;
                device = default;
                initialized = false;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    private async ValueTask CreateDeviceResourcesAsync(CancellationToken cancellationToken)
    {
        device = await adapter.CreateDeviceAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            surface = await adapter.CreateSurfaceAsync(device, outputFormat, cancellationToken).ConfigureAwait(false);
            initialized = true;
        }
        catch
        {
            await adapter.DisposeDeviceAsync(device, CancellationToken.None).ConfigureAwait(false);
            device = default;
            throw;
        }
    }

    private async ValueTask RecreateDeviceResourcesAsync(CancellationToken cancellationToken)
    {
        await adapter.DisposeSurfaceAsync(surface, CancellationToken.None).ConfigureAwait(false);
        await adapter.DisposeDeviceAsync(device, CancellationToken.None).ConfigureAwait(false);
        surface = default;
        device = default;
        initialized = false;
        await CreateDeviceResourcesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask UploadAndPresentAsync(VideoFrame frame, CancellationToken cancellationToken)
    {
        await adapter.UploadAsync(surface, frame, cancellationToken).ConfigureAwait(false);
        await adapter.PresentAsync(surface, outputWindowId, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask PresentCompositionAsync(SceneComposition composition, CancellationToken cancellationToken)
    {
        var frameSequence = checked((ulong)Interlocked.Increment(ref sequence));
        using var frame = VideoFrame.CopyFrom(
            CompositorDeviceId,
            outputFormat,
            new VideoFrameStamp(frameSequence, new QpcTimestamp(Stopwatch.GetTimestamp(), Stopwatch.Frequency)),
            composition.Pixels.Span);
        try
        {
            await UploadAndPresentAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (D3D11DeviceLoss.IsDeviceLost(exception))
        {
            await RecreateDeviceResourcesAsync(cancellationToken).ConfigureAwait(false);
            await UploadAndPresentAsync(frame, cancellationToken).ConfigureAwait(false);
        }
    }

    private static class D3D11DeviceLoss
    {
        private static readonly HashSet<int> DeviceLossResults =
        [
            unchecked((int)0x887A0005), // DXGI_ERROR_DEVICE_REMOVED
            unchecked((int)0x887A0006), // DXGI_ERROR_DEVICE_HUNG
            unchecked((int)0x887A0007), // DXGI_ERROR_DEVICE_RESET
            unchecked((int)0x887A0020)  // DXGI_ERROR_DRIVER_INTERNAL_ERROR
        ];

        public static bool IsDeviceLost(Exception exception)
        {
            for (Exception? current = exception; current is not null; current = current.InnerException)
            {
                if (DeviceLossResults.Contains(current.HResult)) return true;
                if (current is COMException comException && DeviceLossResults.Contains(comException.ErrorCode)) return true;
            }
            return false;
        }
    }
}
