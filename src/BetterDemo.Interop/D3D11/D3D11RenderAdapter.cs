using System.Runtime.Versioning;
using BetterDemo.Core.Contracts;
using BetterDemo.Interop.Win32;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using CoreVideoFrameFormat = BetterDemo.Core.Contracts.VideoFrameFormat;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;

namespace BetterDemo.Interop.D3D11;

[SupportedOSPlatform("windows")]
public sealed class D3D11RenderAdapter : ID3D11RenderAdapter
{
    private readonly object gate = new();
    private readonly IWin32WindowAdapter windows;
    private readonly Dictionary<nint, DeviceResources> devices = [];
    private readonly Dictionary<nint, SurfaceResources> surfaces = [];
    private long nextHandle;
    private bool disposed;

    public D3D11RenderAdapter(IWin32WindowAdapter windows)
    {
        this.windows = windows ?? throw new ArgumentNullException(nameof(windows));
    }

    public ValueTask<D3D11DeviceHandle> CreateDeviceAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ThrowIfDisposed();
            var result = D3D11CreateDevice(
                IntPtr.Zero,
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0],
                out ID3D11Device? device,
                out _,
                out ID3D11DeviceContext? context);
            if (result.Failure)
            {
                result = D3D11CreateDevice(
                    IntPtr.Zero,
                    DriverType.Warp,
                    DeviceCreationFlags.BgraSupport,
                    [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0],
                    out device,
                    out _,
                    out context);
            }

            result.CheckError();
            var resources = new DeviceResources(device!, context!);
            var handleValue = NextHandle();
            devices.Add(handleValue, resources);
            if (!D3D11DeviceHandle.TryCreate(handleValue, out var handle))
            {
                devices.Remove(handleValue);
                resources.Dispose();
                throw new InvalidOperationException("The D3D11 device handle could not be created.");
            }

            return ValueTask.FromResult(handle);
        }
    }

    public ValueTask DisposeDeviceAsync(D3D11DeviceHandle device, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ThrowIfDisposed();
            if (!devices.Remove(device.Value, out var resources))
            {
                return ValueTask.CompletedTask;
            }

            foreach (var pair in surfaces.Where(pair => ReferenceEquals(pair.Value.Device, resources)).ToArray())
            {
                surfaces.Remove(pair.Key);
                pair.Value.Dispose();
            }

            resources.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    public ValueTask<D3D11SurfaceHandle> CreateSurfaceAsync(
        D3D11DeviceHandle device,
        CoreVideoFrameFormat format,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (format.PixelFormat != VideoPixelFormat.Bgra32)
        {
            throw new NotSupportedException("D3D11 presentation surfaces use a composed BGRA32 frame.");
        }

        if (format.Stride < checked(format.Width * 4))
        {
            throw new ArgumentException("A BGRA32 surface stride must fit every pixel row.", nameof(format));
        }

        lock (gate)
        {
            ThrowIfDisposed();
            var deviceResources = GetDevice(device);
            var description = new Texture2DDescription
            {
                Width = (uint)format.Width,
                Height = (uint)format.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None
            };
            var texture = deviceResources.Device.CreateTexture2D(description);
            var resources = new SurfaceResources(deviceResources, texture, format);
            var handleValue = NextHandle();
            surfaces.Add(handleValue, resources);
            if (!D3D11SurfaceHandle.TryCreate(handleValue, out var handle))
            {
                surfaces.Remove(handleValue);
                resources.Dispose();
                throw new InvalidOperationException("The D3D11 surface handle could not be created.");
            }

            return ValueTask.FromResult(handle);
        }
    }

    public ValueTask UploadAsync(
        D3D11SurfaceHandle surface,
        VideoFrame frame,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        cancellationToken.ThrowIfCancellationRequested();
        if (frame.IsDisposed || !frame.HasPayload)
        {
            throw new ObjectDisposedException(nameof(frame), "A live BGRA frame is required for upload.");
        }

        if (frame.Format.PixelFormat != VideoPixelFormat.Bgra32)
        {
            throw new NotSupportedException("Convert NV12 input into the composed BGRA output before upload.");
        }

        lock (gate)
        {
            ThrowIfDisposed();
            var resources = GetSurface(surface);
            if (frame.Format.Width != resources.Format.Width ||
                frame.Format.Height != resources.Format.Height ||
                frame.Format.Stride != resources.Format.Stride)
            {
                throw new ArgumentException("The frame dimensions and stride must match the D3D11 surface.", nameof(frame));
            }

            var pixels = frame.Data;
            var requiredLength = VideoFrame.GetRequiredBufferLength(frame.Format);
            if (pixels.Length < requiredLength)
            {
                throw new ArgumentException("The frame payload is shorter than its declared format.", nameof(frame));
            }

            unsafe
            {
                fixed (byte* pixelPointer = pixels.Span)
                {
                    resources.Device.Context.UpdateSubresource(
                        resources.Texture,
                        0,
                        null,
                        (IntPtr)pixelPointer,
                        (uint)frame.Format.Stride,
                        0);
                }
            }

            return ValueTask.CompletedTask;
        }
    }

    public ValueTask DisposeSurfaceAsync(
        D3D11SurfaceHandle surface,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ThrowIfDisposed();
            if (surfaces.Remove(surface.Value, out var resources))
            {
                resources.Dispose();
            }

            return ValueTask.CompletedTask;
        }
    }

    public async ValueTask PresentAsync(
        D3D11SurfaceHandle surface,
        OutputWindowId outputWindowId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OutputWindowBinding? binding = await windows.TryGetOutputWindowAsync(outputWindowId, cancellationToken).ConfigureAwait(false);
        if (binding is null || !binding.NativeWindowHandle.IsValid)
        {
            throw new InvalidOperationException($"Output window '{outputWindowId}' is unavailable.");
        }

        lock (gate)
        {
            ThrowIfDisposed();
            var source = GetSurface(surface);
            var device = source.Device;
            if (!device.Outputs.TryGetValue(outputWindowId, out var output) ||
                output.Width != source.Format.Width || output.Height != source.Format.Height ||
                output.WindowHandle != binding.NativeWindowHandle.Value)
            {
                if (output is not null)
                {
                    device.Outputs.Remove(outputWindowId);
                    output.Dispose();
                }

                output = CreateOutput(device, binding, source.Format);
                device.Outputs.Add(outputWindowId, output);
            }

            device.Context.CopyResource(output.BackBuffer, source.Texture);
            output.SwapChain.Present(1, PresentFlags.None).CheckError();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposed)
            {
                return ValueTask.CompletedTask;
            }

            disposed = true;
            foreach (var surface in surfaces.Values)
            {
                surface.Dispose();
            }
            surfaces.Clear();

            foreach (var device in devices.Values)
            {
                device.Dispose();
            }
            devices.Clear();
            return ValueTask.CompletedTask;
        }
    }

    private static OutputResources CreateOutput(
        DeviceResources device,
        OutputWindowBinding binding,
        CoreVideoFrameFormat format)
    {
        var description = new SwapChainDescription1
        {
            Width = (uint)format.Width,
            Height = (uint)format.Height,
            Format = Format.B8G8R8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Ignore,
            Flags = SwapChainFlags.None
        };

        using var factory = CreateDXGIFactory2<IDXGIFactory2>(false);
        var swapChain = factory.CreateSwapChainForHwnd(
            device.Device,
            binding.NativeWindowHandle.Value,
            description);
        var backBuffer = swapChain.GetBuffer<ID3D11Texture2D>(0);
        return new OutputResources(swapChain, backBuffer, format.Width, format.Height, binding.NativeWindowHandle.Value);
    }

    private DeviceResources GetDevice(D3D11DeviceHandle handle) =>
        handle.IsValid && devices.TryGetValue(handle.Value, out var resources)
            ? resources
            : throw new InvalidOperationException("The D3D11 device handle is invalid or has been disposed.");

    private SurfaceResources GetSurface(D3D11SurfaceHandle handle) =>
        handle.IsValid && surfaces.TryGetValue(handle.Value, out var resources)
            ? resources
            : throw new InvalidOperationException("The D3D11 surface handle is invalid or has been disposed.");

    private nint NextHandle()
    {
        var value = Interlocked.Increment(ref nextHandle);
        if (value <= 0)
        {
            throw new InvalidOperationException("The D3D11 handle sequence is exhausted.");
        }

        return (nint)value;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    private sealed class DeviceResources : IDisposable
    {
        public DeviceResources(ID3D11Device device, ID3D11DeviceContext context)
        {
            Device = device;
            Context = context;
        }

        public ID3D11Device Device { get; }
        public ID3D11DeviceContext Context { get; }
        public Dictionary<OutputWindowId, OutputResources> Outputs { get; } = [];

        public void Dispose()
        {
            foreach (var output in Outputs.Values)
            {
                output.Dispose();
            }
            Outputs.Clear();
            Context.ClearState();
            Context.Flush();
            Context.Dispose();
            Device.Dispose();
        }
    }

    private sealed class SurfaceResources : IDisposable
    {
        public SurfaceResources(DeviceResources device, ID3D11Texture2D texture, CoreVideoFrameFormat format)
        {
            Device = device;
            Texture = texture;
            Format = format;
        }

        public DeviceResources Device { get; }
        public ID3D11Texture2D Texture { get; }
        public CoreVideoFrameFormat Format { get; }

        public void Dispose() => Texture.Dispose();
    }

    private sealed class OutputResources : IDisposable
    {
        public OutputResources(
            IDXGISwapChain1 swapChain,
            ID3D11Texture2D backBuffer,
            int width,
            int height,
            nint windowHandle)
        {
            SwapChain = swapChain;
            BackBuffer = backBuffer;
            Width = width;
            Height = height;
            WindowHandle = windowHandle;
        }

        public IDXGISwapChain1 SwapChain { get; }
        public ID3D11Texture2D BackBuffer { get; }
        public int Width { get; }
        public int Height { get; }
        public nint WindowHandle { get; }

        public void Dispose()
        {
            BackBuffer.Dispose();
            SwapChain.Dispose();
        }
    }
}
