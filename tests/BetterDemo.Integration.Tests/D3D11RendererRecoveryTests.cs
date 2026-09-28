using System.Runtime.InteropServices;
using BetterDemo.App.Rendering;
using BetterDemo.Core.Contracts;
using BetterDemo.Core.Scene;
using BetterDemo.Interop.D3D11;
using Xunit;

namespace BetterDemo.Integration.Tests;

public sealed class D3D11RendererRecoveryTests
{
    [Fact]
    public async Task Device_removed_during_present_recreates_resources_and_retries_current_frame()
    {
        await using var adapter = new FakeRenderAdapter(unchecked((int)0x887A0005));
        await using var renderer = new D3D11Renderer(adapter, new OutputWindowId("recovery-test"));
        await renderer.InitializeAsync(8, 4);

        var frame = await renderer.RenderAsync(SceneMode.Black, null, null, new SceneRenderOptions(8, 4));

        Assert.Equal(8, frame.Width);
        Assert.Equal(2, adapter.DeviceCreates);
        Assert.Equal(2, adapter.SurfaceCreates);
        Assert.Equal(2, adapter.Uploads);
        Assert.Equal(2, adapter.PresentAttempts);
        Assert.Equal(1, adapter.SurfaceDisposals);
        Assert.Equal(1, adapter.DeviceDisposals);

        await renderer.RenderAsync(SceneMode.Black, null, null, new SceneRenderOptions(8, 4));
        Assert.Equal(3, adapter.Uploads);
        Assert.Equal(3, adapter.PresentAttempts);
    }

    [Fact]
    public async Task Non_device_present_failure_is_propagated_without_recreating_resources()
    {
        await using var adapter = new FakeRenderAdapter(unchecked((int)0x80004005));
        await using var renderer = new D3D11Renderer(adapter, new OutputWindowId("non-device-error-test"));
        await renderer.InitializeAsync(8, 4);

        var failure = await Assert.ThrowsAsync<COMException>(async () =>
            await renderer.RenderAsync(SceneMode.Black, null, null, new SceneRenderOptions(8, 4)));

        Assert.Equal(unchecked((int)0x80004005), failure.HResult);
        Assert.Equal(1, adapter.DeviceCreates);
        Assert.Equal(1, adapter.SurfaceCreates);
        Assert.Equal(1, adapter.PresentAttempts);
    }

    [Fact]
    public async Task Scene_document_layers_are_composed_and_uploaded_to_the_output_surface()
    {
        await using var adapter = new FakeRenderAdapter();
        await using var renderer = new D3D11Renderer(adapter, new OutputWindowId("scene-document-test"));
        await renderer.InitializeAsync(2, 1);
        var document = new SceneDocument(new[]
        {
            new SceneLayer(
                new SceneLayerId("background"),
                SceneLayerKind.Color,
                0,
                new NormalizedTransform(0, 0, 1, 1),
                fillColor: new SceneRgbaColor(12, 34, 56))
        });

        var composition = await renderer.RenderAsync(document, null, new SceneRenderOptions(2, 1));

        var expected = new byte[] { 56, 34, 12, 255, 56, 34, 12, 255 };
        Assert.Equal(expected, composition.Pixels.ToArray());
        Assert.Equal(expected, adapter.LastUploadedPixels);
        Assert.Equal(1, adapter.PresentAttempts);
    }

    private sealed class FakeRenderAdapter(int? firstPresentFailure = null) : ID3D11RenderAdapter
    {
        private int nextHandle;
        private bool firstFailurePending = true;

        public int DeviceCreates { get; private set; }
        public int SurfaceCreates { get; private set; }
        public int Uploads { get; private set; }
        public int PresentAttempts { get; private set; }
        public int DeviceDisposals { get; private set; }
        public int SurfaceDisposals { get; private set; }
        public byte[]? LastUploadedPixels { get; private set; }

        public ValueTask<D3D11DeviceHandle> CreateDeviceAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeviceCreates++;
            return ValueTask.FromResult(D3D11DeviceHandle.TryCreate(++nextHandle, out var device)
                ? device
                : throw new InvalidOperationException("Could not create fake device handle."));
        }

        public ValueTask DisposeDeviceAsync(D3D11DeviceHandle device, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeviceDisposals++;
            return ValueTask.CompletedTask;
        }

        public ValueTask<D3D11SurfaceHandle> CreateSurfaceAsync(D3D11DeviceHandle device, VideoFrameFormat format,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SurfaceCreates++;
            return ValueTask.FromResult(D3D11SurfaceHandle.TryCreate(++nextHandle, out var surface)
                ? surface
                : throw new InvalidOperationException("Could not create fake surface handle."));
        }

        public ValueTask UploadAsync(D3D11SurfaceHandle surface, VideoFrame frame, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Uploads++;
            LastUploadedPixels = frame.Data.ToArray();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeSurfaceAsync(D3D11SurfaceHandle surface, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SurfaceDisposals++;
            return ValueTask.CompletedTask;
        }

        public ValueTask PresentAsync(D3D11SurfaceHandle surface, OutputWindowId outputWindowId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PresentAttempts++;
            if (firstFailurePending && firstPresentFailure is { } failure)
            {
                firstFailurePending = false;
                throw new COMException("Simulated Direct3D presentation failure.", failure);
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
