using BetterDemo.Core.Contracts;

namespace BetterDemo.Interop.D3D11;

public readonly struct D3D11DeviceHandle : IEquatable<D3D11DeviceHandle>
{
    private readonly nint value;

    private D3D11DeviceHandle(nint value) => this.value = value;

    public bool IsValid => value != 0;

    internal nint Value => value;

    public static bool TryCreate(nint value, out D3D11DeviceHandle handle)
    {
        if (value == 0)
        {
            handle = default;
            return false;
        }

        handle = new D3D11DeviceHandle(value);
        return true;
    }

    public bool Equals(D3D11DeviceHandle other) => value == other.value;
    public override bool Equals(object? obj) => obj is D3D11DeviceHandle other && Equals(other);
    public override int GetHashCode() => value.GetHashCode();
}

public readonly struct D3D11SurfaceHandle : IEquatable<D3D11SurfaceHandle>
{
    private readonly nint value;

    private D3D11SurfaceHandle(nint value) => this.value = value;

    public bool IsValid => value != 0;

    internal nint Value => value;

    public static bool TryCreate(nint value, out D3D11SurfaceHandle handle)
    {
        if (value == 0)
        {
            handle = default;
            return false;
        }

        handle = new D3D11SurfaceHandle(value);
        return true;
    }

    public bool Equals(D3D11SurfaceHandle other) => value == other.value;
    public override bool Equals(object? obj) => obj is D3D11SurfaceHandle other && Equals(other);
    public override int GetHashCode() => value.GetHashCode();
}

public interface ID3D11RenderAdapter : IAsyncDisposable
{
    ValueTask<D3D11DeviceHandle> CreateDeviceAsync(CancellationToken cancellationToken = default);

    ValueTask DisposeDeviceAsync(D3D11DeviceHandle device, CancellationToken cancellationToken = default);

    ValueTask<D3D11SurfaceHandle> CreateSurfaceAsync(
        D3D11DeviceHandle device,
        VideoFrameFormat format,
        CancellationToken cancellationToken = default);

    ValueTask UploadAsync(
        D3D11SurfaceHandle surface,
        VideoFrame frame,
        CancellationToken cancellationToken = default);

    ValueTask DisposeSurfaceAsync(D3D11SurfaceHandle surface, CancellationToken cancellationToken = default);

    ValueTask PresentAsync(
        D3D11SurfaceHandle surface,
        OutputWindowId outputWindowId,
        CancellationToken cancellationToken = default);
}
