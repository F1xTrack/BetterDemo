using BetterDemo.Core.Contracts;

namespace BetterDemo.Interop.Win32;

public readonly struct NativeWindowHandle : IEquatable<NativeWindowHandle>
{
    private readonly nint value;

    private NativeWindowHandle(nint value) => this.value = value;

    public bool IsValid => value != 0;

    internal nint Value => value;

    public static bool TryCreate(nint value, out NativeWindowHandle handle)
    {
        if (value == 0)
        {
            handle = default;
            return false;
        }

        handle = new NativeWindowHandle(value);
        return true;
    }

    public bool Equals(NativeWindowHandle other) => value == other.value;
    public override bool Equals(object? obj) => obj is NativeWindowHandle other && Equals(other);
    public override int GetHashCode() => value.GetHashCode();
}

public sealed class OutputWindowBinding
{
    public OutputWindowBinding(OutputWindowId outputWindowId, NativeWindowHandle nativeWindowHandle)
    {
        if (!nativeWindowHandle.IsValid) throw new ArgumentException("A valid native window handle is required.", nameof(nativeWindowHandle));

        OutputWindowId = outputWindowId;
        NativeWindowHandle = nativeWindowHandle;
    }

    public OutputWindowId OutputWindowId { get; }
    public NativeWindowHandle NativeWindowHandle { get; }
}

public readonly record struct OutputWindowPosition(int X, int Y);

public interface IWin32WindowAdapter : IAsyncDisposable
{
    ValueTask<OutputWindowBinding> CreateOutputWindowAsync(
        OutputWindowId outputWindowId,
        CancellationToken cancellationToken = default);

    ValueTask<OutputWindowBinding?> TryGetOutputWindowAsync(
        OutputWindowId outputWindowId,
        CancellationToken cancellationToken = default);

    ValueTask ShowAsync(OutputWindowId outputWindowId, CancellationToken cancellationToken = default);

    ValueTask HideAsync(OutputWindowId outputWindowId, CancellationToken cancellationToken = default);

    ValueTask CloseAsync(OutputWindowId outputWindowId, CancellationToken cancellationToken = default);
}
