using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using BetterDemo.Core.Contracts;
using BetterDemo.Interop.D3D11;
using BetterDemo.Interop.Win32;
using Xunit;

namespace BetterDemo.Integration.Tests;

[SupportedOSPlatform("windows")]
public sealed class D3D11RenderAdapterTests
{
    [Fact]
    public async Task Uploaded_frame_is_presented_to_the_stable_output_window_handle()
    {
        var hwnd = NativeMethods.CreateTestWindow();
        try
        {
            Assert.NotEqual(nint.Zero, hwnd);
            Assert.True(NativeWindowHandle.TryCreate(hwnd, out var nativeHandle));
            var outputWindowId = new OutputWindowId("test-output-window");
            await using var adapter = new D3D11RenderAdapter(new BoundWindowAdapter(
                new OutputWindowBinding(outputWindowId, nativeHandle)));
            var device = await adapter.CreateDeviceAsync();
            var format = new VideoFrameFormat(4, 4, VideoPixelFormat.Bgra32, 16);
            var surface = await adapter.CreateSurfaceAsync(device, format);
            using var frame = SolidFrame(format);

            await adapter.UploadAsync(surface, frame);
            await adapter.PresentAsync(surface, outputWindowId);
            await adapter.PresentAsync(surface, outputWindowId);

            await adapter.DisposeSurfaceAsync(surface);
            await adapter.DisposeDeviceAsync(device);
        }
        finally
        {
            NativeMethods.DestroyTestWindow(hwnd);
        }
    }

    [Fact]
    public async Task D3d11_device_surface_upload_and_independent_disposal_work()
    {
        await using var adapter = new D3D11RenderAdapter(new UnusedWindowAdapter());
        var firstDevice = await adapter.CreateDeviceAsync();
        var secondDevice = await adapter.CreateDeviceAsync();
        var format = new VideoFrameFormat(2, 2, VideoPixelFormat.Bgra32, 8);
        var firstSurface = await adapter.CreateSurfaceAsync(firstDevice, format);
        var secondSurface = await adapter.CreateSurfaceAsync(secondDevice, format);
        using var frame = VideoFrame.CopyFrom(
            new VideoDeviceId("d3d11-test"),
            format,
            new VideoFrameStamp(1, new QpcTimestamp(1, 10_000_000)),
            new byte[]
            {
                0, 0, 255, 255, 0, 255, 0, 255,
                255, 0, 0, 255, 255, 255, 255, 255
            });

        await adapter.UploadAsync(firstSurface, frame);
        await adapter.UploadAsync(secondSurface, frame);
        await adapter.DisposeSurfaceAsync(firstSurface);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await adapter.UploadAsync(firstSurface, frame));
        await adapter.UploadAsync(secondSurface, frame);
        await adapter.DisposeDeviceAsync(secondDevice);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await adapter.UploadAsync(secondSurface, frame));
        Assert.False(frame.IsDisposed);

        await adapter.DisposeDeviceAsync(firstDevice);
    }

    private sealed class UnusedWindowAdapter : IWin32WindowAdapter
    {
        public ValueTask<OutputWindowBinding> CreateOutputWindowAsync(
            OutputWindowId outputWindowId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("This resource-lifetime test does not create an HWND.");

        public ValueTask<OutputWindowBinding?> TryGetOutputWindowAsync(
            OutputWindowId outputWindowId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("This resource-lifetime test does not present a frame.");

        public ValueTask ShowAsync(OutputWindowId outputWindowId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask HideAsync(OutputWindowId outputWindowId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask CloseAsync(OutputWindowId outputWindowId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BoundWindowAdapter(OutputWindowBinding binding) : IWin32WindowAdapter
    {
        public ValueTask<OutputWindowBinding> CreateOutputWindowAsync(
            OutputWindowId outputWindowId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(binding);

        public ValueTask<OutputWindowBinding?> TryGetOutputWindowAsync(
            OutputWindowId outputWindowId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<OutputWindowBinding?>(outputWindowId == binding.OutputWindowId ? binding : null);

        public ValueTask ShowAsync(OutputWindowId outputWindowId, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask HideAsync(OutputWindowId outputWindowId, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask CloseAsync(OutputWindowId outputWindowId, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static VideoFrame SolidFrame(VideoFrameFormat format)
    {
        var pixels = new byte[VideoFrame.GetRequiredBufferLength(format)];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = 32;
            pixels[offset + 1] = 128;
            pixels[offset + 2] = 240;
            pixels[offset + 3] = 255;
        }
        return VideoFrame.CopyFrom(
            new VideoDeviceId("present-test"),
            format,
            new VideoFrameStamp(1, new QpcTimestamp(1, 10_000_000)),
            pixels);
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern nint CreateWindowEx(
            uint extendedStyle,
            string className,
            string windowName,
            uint style,
            int x,
            int y,
            int width,
            int height,
            nint parent,
            nint menu,
            nint instance,
            nint parameter);

        [DllImport("user32.dll", EntryPoint = "DestroyWindow", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyWindow(nint window);

        public static nint CreateTestWindow()
        {
            var window = CreateWindowEx(0, "STATIC", "BetterDemo D3D11 present test", 0x80000000, 0, 0, 64, 64, 0, 0, 0, 0);
            if (window == 0)
            {
                throw new InvalidOperationException($"CreateWindowExW failed with Win32 error {Marshal.GetLastWin32Error()}.");
            }
            return window;
        }

        public static void DestroyTestWindow(nint window)
        {
            if (window != 0 && !DestroyWindow(window))
            {
                throw new InvalidOperationException($"DestroyWindow failed with Win32 error {Marshal.GetLastWin32Error()}.");
            }
        }
    }
}
