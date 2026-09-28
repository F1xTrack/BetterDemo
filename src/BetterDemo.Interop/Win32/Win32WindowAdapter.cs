using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using BetterDemo.Core.Contracts;

namespace BetterDemo.Interop.Win32;

/// <summary>
/// Owns a stable, ordinary top-level HWND for the D3D11 output surface.
/// All window operations are dispatched to the UI thread supplied by the host.
/// </summary>
public sealed partial class Win32WindowAdapter : IWin32WindowAdapter
{
    private const uint ClassStyleHorizontalRedraw = 0x0002;
    private const uint ClassStyleVerticalRedraw = 0x0001;
    private const uint WindowStyleOverlapped = 0x00CF0000;
    private const uint WindowStyleClipChildren = 0x02000000;
    private const int ShowWindow = 5;
    private const int HideWindow = 0;
    private const uint MessageGetMinMaxInfo = 0x0024;
    private const uint MessageSizing = 0x0214;
    private const uint MessageNonClientDestroy = 0x0082;
    private const uint SetWindowPositionNoMove = 0x0002;
    private const uint SetWindowPositionNoSize = 0x0001;
    private const uint SetWindowPositionNoZOrder = 0x0004;
    private const uint SetWindowPositionNoActivate = 0x0010;

    private static readonly WindowProcedure Procedure = DispatchWindowMessage;
    private static readonly nint ProcedurePointer = Marshal.GetFunctionPointerForDelegate(Procedure);
    private static readonly ConcurrentDictionary<nint, (Win32WindowAdapter Adapter, OutputWindowId Id)> WindowOwners = new();

    private readonly object gate = new();
    private readonly Func<Action, bool> enqueueOnUiThread;
    private readonly Dictionary<OutputWindowId, OutputWindowBinding> windows = [];
    private readonly string title;
    private readonly string className = $"BetterDemo.Output.{Guid.NewGuid():N}";
    private readonly int width;
    private readonly int height;
    private readonly nint instance;
    private int requestedOuterWidth;
    private int requestedOuterHeight;
    private bool classRegistered;
    private bool disposed;

    public Win32WindowAdapter(Func<Action, bool> enqueueOnUiThread, string title = "BetterDemo OutputWindow", int width = 1280, int height = 720)
    {
        this.enqueueOnUiThread = enqueueOnUiThread ?? throw new ArgumentNullException(nameof(enqueueOnUiThread));
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("An output window title is required.", nameof(title));
        if (width <= 0 || width > 8192) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0 || height > 8192) throw new ArgumentOutOfRangeException(nameof(height));
        this.title = title;
        this.width = width;
        this.height = height;
        instance = NativeMethods.GetModuleHandle(0);
        if (instance == 0)
        {
            throw new InvalidOperationException($"GetModuleHandleW failed with Win32 error {Marshal.GetLastPInvokeError()}.");
        }
    }

    public ValueTask<OutputWindowBinding> CreateOutputWindowAsync(
        OutputWindowId outputWindowId,
        CancellationToken cancellationToken = default) =>
        OnUiThreadAsync(() => CreateOutputWindow(outputWindowId), cancellationToken);

    public ValueTask<OutputWindowBinding?> TryGetOutputWindowAsync(
        OutputWindowId outputWindowId,
        CancellationToken cancellationToken = default) =>
        OnUiThreadAsync(() =>
        {
            lock (gate)
            {
                ThrowIfDisposed();
                return windows.TryGetValue(outputWindowId, out var binding) ? binding : null;
            }
        }, cancellationToken);

    public ValueTask ShowAsync(OutputWindowId outputWindowId, CancellationToken cancellationToken = default) =>
        OnUiThreadAsync(() =>
        {
            var binding = GetWindow(outputWindowId);
            NativeMethods.ShowWindow(binding.NativeWindowHandle.Value, ShowWindow);
            return true;
        }, cancellationToken).AsVoid();

    public ValueTask HideAsync(OutputWindowId outputWindowId, CancellationToken cancellationToken = default) =>
        OnUiThreadAsync(() =>
        {
            var binding = GetWindow(outputWindowId);
            NativeMethods.ShowWindow(binding.NativeWindowHandle.Value, HideWindow);
            return true;
        }, cancellationToken).AsVoid();

    public ValueTask CloseAsync(OutputWindowId outputWindowId, CancellationToken cancellationToken = default) =>
        OnUiThreadAsync(() =>
        {
            var binding = GetWindow(outputWindowId);
            if (!NativeMethods.DestroyWindow(binding.NativeWindowHandle.Value))
            {
                throw new InvalidOperationException($"DestroyWindow failed with Win32 error {Marshal.GetLastPInvokeError()}.");
            }
            RemoveWindow(outputWindowId, binding.NativeWindowHandle.Value);
            return true;
        }, cancellationToken).AsVoid();

    public ValueTask<OutputWindowPosition?> GetPositionAsync(OutputWindowId outputWindowId, CancellationToken cancellationToken = default) =>
        OnUiThreadAsync(() =>
        {
            var handle = GetWindow(outputWindowId).NativeWindowHandle.Value;
            if (NativeMethods.IsIconic(handle) || NativeMethods.IsZoomed(handle)) return (OutputWindowPosition?)null;
            if (!NativeMethods.GetWindowRect(handle, out var bounds))
                throw new InvalidOperationException($"GetWindowRect failed with Win32 error {Marshal.GetLastPInvokeError()}.");
            return new OutputWindowPosition(bounds.Left, bounds.Top);
        }, cancellationToken);

    public ValueTask<bool> TryRestorePositionAsync(OutputWindowId outputWindowId, OutputWindowPosition position, CancellationToken cancellationToken = default) =>
        OnUiThreadAsync(() =>
        {
            var handle = GetWindow(outputWindowId).NativeWindowHandle.Value;
            if (!NativeMethods.GetWindowRect(handle, out var bounds))
                throw new InvalidOperationException($"GetWindowRect failed with Win32 error {Marshal.GetLastPInvokeError()}.");
            var outerWidth = bounds.Right - bounds.Left;
            var outerHeight = bounds.Bottom - bounds.Top;
            if (Math.Abs((long)position.X) > 100_000 || Math.Abs((long)position.Y) > 100_000) return false;
            var destination = new WindowRect
            {
                Left = position.X,
                Top = position.Y,
                Right = checked(position.X + outerWidth),
                Bottom = checked(position.Y + outerHeight)
            };
            var monitor = NativeMethods.MonitorFromRect(ref destination, 0);
            if (monitor == 0) return false;
            var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!NativeMethods.GetMonitorInfo(monitor, ref monitorInfo))
                throw new InvalidOperationException($"GetMonitorInfoW failed with Win32 error {Marshal.GetLastPInvokeError()}.");
            var visibleWidth = Math.Min(destination.Right, monitorInfo.Work.Right) - Math.Max(destination.Left, monitorInfo.Work.Left);
            var visibleHeight = Math.Min(destination.Bottom, monitorInfo.Work.Bottom) - Math.Max(destination.Top, monitorInfo.Work.Top);
            if (visibleWidth < 64 || visibleHeight < 64) return false;
            if (!NativeMethods.SetWindowPos(handle, 0, position.X, position.Y, 0, 0,
                    SetWindowPositionNoSize | SetWindowPositionNoZOrder | SetWindowPositionNoActivate))
                throw new InvalidOperationException($"SetWindowPos failed with Win32 error {Marshal.GetLastPInvokeError()}.");
            return true;
        }, cancellationToken);

    public ValueTask DisposeAsync() => OnUiThreadAsync(() =>
    {
        lock (gate)
        {
            if (disposed) return true;
            foreach (var pair in windows.ToArray())
            {
                if (!NativeMethods.DestroyWindow(pair.Value.NativeWindowHandle.Value))
                {
                    throw new InvalidOperationException($"DestroyWindow failed with Win32 error {Marshal.GetLastPInvokeError()}.");
                }
                WindowOwners.TryRemove(pair.Value.NativeWindowHandle.Value, out _);
                windows.Remove(pair.Key);
            }

            if (classRegistered)
            {
                using var classNameBuffer = new Utf16Buffer(className);
                if (!NativeMethods.UnregisterClass(classNameBuffer.Pointer, instance))
                {
                    throw new InvalidOperationException($"UnregisterClassW failed with Win32 error {Marshal.GetLastPInvokeError()}.");
                }
                classRegistered = false;
            }

            disposed = true;
            return true;
        }
    }, CancellationToken.None).AsVoid();

    private OutputWindowBinding CreateOutputWindow(OutputWindowId outputWindowId)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (windows.TryGetValue(outputWindowId, out var existing))
            {
                return existing;
            }

            RegisterWindowClass();
            using var classNameBuffer = new Utf16Buffer(className);
            using var titleBuffer = new Utf16Buffer(title);
            var windowRect = new WindowRect { Left = 0, Top = 0, Right = width, Bottom = height };
            if (!NativeMethods.AdjustWindowRectEx(ref windowRect, WindowStyleOverlapped | WindowStyleClipChildren, false, 0))
            {
                throw new InvalidOperationException($"AdjustWindowRectEx failed with Win32 error {Marshal.GetLastPInvokeError()}.");
            }
            requestedOuterWidth = checked(windowRect.Right - windowRect.Left);
            requestedOuterHeight = checked(windowRect.Bottom - windowRect.Top);
            var handle = NativeMethods.CreateWindow(
                0,
                classNameBuffer.Pointer,
                titleBuffer.Pointer,
                WindowStyleOverlapped | WindowStyleClipChildren,
                100,
                100,
                requestedOuterWidth,
                requestedOuterHeight,
                0,
                0,
                instance,
                0);
            if (handle == 0)
            {
                throw new InvalidOperationException($"CreateWindowExW failed with Win32 error {Marshal.GetLastPInvokeError()}.");
            }

            if (!NativeWindowHandle.TryCreate(handle, out var nativeHandle))
            {
                NativeMethods.DestroyWindow(handle);
                throw new InvalidOperationException("CreateWindowExW returned an invalid HWND.");
            }

            var binding = new OutputWindowBinding(outputWindowId, nativeHandle);
            windows.Add(outputWindowId, binding);
            WindowOwners[handle] = (this, outputWindowId);
            if (!NativeMethods.SetWindowPos(
                    handle,
                    0,
                    0,
                    0,
                    requestedOuterWidth,
                    requestedOuterHeight,
                    SetWindowPositionNoMove | SetWindowPositionNoZOrder | SetWindowPositionNoActivate))
            {
                var error = Marshal.GetLastPInvokeError();
                NativeMethods.DestroyWindow(handle);
                throw new InvalidOperationException($"SetWindowPos failed with Win32 error {error}.");
            }
            return binding;
        }
    }

    private void RegisterWindowClass()
    {
        if (classRegistered) return;
        using var classNameBuffer = new Utf16Buffer(className);
        var windowClass = new WindowClassEx
        {
            Size = (uint)Marshal.SizeOf<WindowClassEx>(),
            Style = ClassStyleHorizontalRedraw | ClassStyleVerticalRedraw,
            WindowProcedure = ProcedurePointer,
            Instance = instance,
            ClassName = classNameBuffer.Pointer
        };
        if (NativeMethods.RegisterClass(ref windowClass) == 0)
        {
            throw new InvalidOperationException($"RegisterClassExW failed with Win32 error {Marshal.GetLastPInvokeError()}.");
        }
        classRegistered = true;
    }

    private OutputWindowBinding GetWindow(OutputWindowId outputWindowId)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            return windows.TryGetValue(outputWindowId, out var binding)
                ? binding
                : throw new InvalidOperationException($"Output window '{outputWindowId}' is unavailable.");
        }
    }

    private void RemoveWindow(OutputWindowId outputWindowId, nint handle)
    {
        lock (gate)
        {
            if (windows.TryGetValue(outputWindowId, out var binding) && binding.NativeWindowHandle.Value == handle)
            {
                windows.Remove(outputWindowId);
            }
            WindowOwners.TryRemove(handle, out _);
        }
    }

    private void OnWindowDestroyed(OutputWindowId outputWindowId, nint handle) => RemoveWindow(outputWindowId, handle);

    private bool ConstrainSizing(nint handle, int edge, nint rectanglePointer)
    {
        var proposed = Marshal.PtrToStructure<WindowRect>(rectanglePointer);
        if (!NativeMethods.GetWindowRect(handle, out var current)) return false;

        var nonClientWidth = requestedOuterWidth - width;
        var nonClientHeight = requestedOuterHeight - height;
        var clientWidth = Math.Max(1, proposed.Right - proposed.Left - nonClientWidth);
        var clientHeight = Math.Max(1, proposed.Bottom - proposed.Top - nonClientHeight);
        const int minimumClientWidth = 320;
        const int minimumClientHeight = 180;
        var aspect = width / (double)height;
        var edgeChangesWidth = edge is 1 or 4 or 7;
        var edgeChangesHeight = edge is 3 or 4 or 5 or 6 or 7 or 8;
        var adjustFromWidth = edge is 1 or 2 ||
            (edgeChangesWidth && edgeChangesHeight && Math.Abs(proposed.Right - proposed.Left - (current.Right - current.Left)) >=
                Math.Abs(proposed.Bottom - proposed.Top - (current.Bottom - current.Top)));

        if (adjustFromWidth)
        {
            clientWidth = Math.Max(minimumClientWidth, clientWidth);
            var outerHeight = checked((int)Math.Round(clientWidth / aspect) + nonClientHeight);
            if (edgeChangesHeight && edge is 4 or 5)
                proposed.Top = proposed.Bottom - outerHeight;
            else
                proposed.Bottom = proposed.Top + outerHeight;
        }
        else
        {
            clientHeight = Math.Max(minimumClientHeight, clientHeight);
            var outerWidth = checked((int)Math.Round(clientHeight * aspect) + nonClientWidth);
            if (edgeChangesWidth && edge is 4 or 7)
                proposed.Left = proposed.Right - outerWidth;
            else
                proposed.Right = proposed.Left + outerWidth;
        }

        Marshal.StructureToPtr(proposed, rectanglePointer, false);
        return true;
    }

    private static nint DispatchWindowMessage(nint handle, uint message, nint wParam, nint lParam)
    {
        if (message == MessageGetMinMaxInfo && WindowOwners.TryGetValue(handle, out var sizeOwner))
        {
            var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            limits.MaxTrackSize.X = Math.Max(limits.MaxTrackSize.X, sizeOwner.Adapter.requestedOuterWidth);
            limits.MaxTrackSize.Y = Math.Max(limits.MaxTrackSize.Y, sizeOwner.Adapter.requestedOuterHeight);
            Marshal.StructureToPtr(limits, lParam, false);
            return 0;
        }
        if (message == MessageSizing && WindowOwners.TryGetValue(handle, out var sizingOwner))
            return sizingOwner.Adapter.ConstrainSizing(handle, checked((int)wParam), lParam) ? 1 : 0;
        if (message == MessageNonClientDestroy && WindowOwners.TryGetValue(handle, out var owner))
        {
            owner.Adapter.OnWindowDestroyed(owner.Id, handle);
        }
        return NativeMethods.DefWindowProc(handle, message, wParam, lParam);
    }

    private async ValueTask<T> OnUiThreadAsync<T>(Func<T> operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var enqueued = enqueueOnUiThread(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                completion.TrySetResult(operation());
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        });
        if (!enqueued)
        {
            throw new InvalidOperationException("The host UI dispatcher rejected an output window operation.");
        }

        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        return await completion.Task.ConfigureAwait(false);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowClassEx
    {
        public uint Size;
        public uint Style;
        public nint WindowProcedure;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint BackgroundBrush;
        public nint MenuName;
        public nint ClassName;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public WindowPoint Reserved;
        public WindowPoint MaxSize;
        public WindowPoint MaxPosition;
        public WindowPoint MinTrackSize;
        public WindowPoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public WindowRect Monitor;
        public WindowRect Work;
        public uint Flags;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowProcedure(nint handle, uint message, nint wParam, nint lParam);

    private sealed class Utf16Buffer : IDisposable
    {
        public Utf16Buffer(string value) => Pointer = Marshal.StringToHGlobalUni(value);
        public nint Pointer { get; private set; }
        public void Dispose()
        {
            if (Pointer == 0) return;
            Marshal.FreeHGlobal(Pointer);
            Pointer = 0;
        }
    }

    private static partial class NativeMethods
    {
        [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", SetLastError = true)]
        internal static partial nint GetModuleHandle(nint moduleName);

        [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
        internal static partial ushort RegisterClass(ref WindowClassEx windowClass);

        [LibraryImport("user32.dll", EntryPoint = "UnregisterClassW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool UnregisterClass(nint className, nint instance);

        [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true)]
        internal static partial nint CreateWindow(
            uint extendedStyle,
            nint className,
            nint windowName,
            uint style,
            int x,
            int y,
            int width,
            int height,
            nint parent,
            nint menu,
            nint instance,
            nint parameter);

        [LibraryImport("user32.dll", EntryPoint = "AdjustWindowRectEx", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool AdjustWindowRectEx(ref WindowRect rectangle, uint style, [MarshalAs(UnmanagedType.Bool)] bool hasMenu, uint extendedStyle);

        [LibraryImport("user32.dll", EntryPoint = "SetWindowPos", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

        [LibraryImport("user32.dll", EntryPoint = "GetWindowRect", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetWindowRect(nint window, out WindowRect rectangle);

        [LibraryImport("user32.dll", EntryPoint = "MonitorFromRect")]
        internal static partial nint MonitorFromRect(ref WindowRect rectangle, uint flags);

        [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

        [LibraryImport("user32.dll", EntryPoint = "IsIconic")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool IsIconic(nint window);

        [LibraryImport("user32.dll", EntryPoint = "IsZoomed")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool IsZoomed(nint window);

        [LibraryImport("user32.dll", EntryPoint = "DestroyWindow", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool DestroyWindow(nint handle);

        [LibraryImport("user32.dll", EntryPoint = "ShowWindow", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool ShowWindow(nint handle, int command);

        [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
        internal static partial nint DefWindowProc(nint handle, uint message, nint wParam, nint lParam);
    }
}

internal static class ValueTaskExtensions
{
    internal static async ValueTask AsVoid<T>(this ValueTask<T> valueTask) => _ = await valueTask.ConfigureAwait(false);
}
