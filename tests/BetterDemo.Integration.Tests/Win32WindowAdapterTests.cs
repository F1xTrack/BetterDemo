using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using BetterDemo.Core.Contracts;
using BetterDemo.Interop.Win32;
using Xunit;

namespace BetterDemo.Integration.Tests;

[SupportedOSPlatform("windows")]
public sealed class Win32WindowAdapterTests
{
    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    public async Task Output_window_client_area_matches_selected_video_resolution(int width, int height)
    {
        using var dispatcher = new WindowThread();
        await using var adapter = new Win32WindowAdapter(dispatcher.Enqueue, "BetterDemo resolution test", width, height);
        var binding = await adapter.CreateOutputWindowAsync(new OutputWindowId("resolution-test"));

        Assert.True(GetClientRect(binding.NativeWindowHandle.Value, out var clientRect));
        Assert.Equal(width, clientRect.Right - clientRect.Left);
        Assert.Equal(height, clientRect.Bottom - clientRect.Top);
        await adapter.ShowAsync(binding.OutputWindowId);
        await adapter.HideAsync(binding.OutputWindowId);
        Assert.Equal(binding, await adapter.TryGetOutputWindowAsync(binding.OutputWindowId));
        Assert.True(GetClientRect(binding.NativeWindowHandle.Value, out clientRect));
        Assert.Equal(width, clientRect.Right - clientRect.Left);
        Assert.Equal(height, clientRect.Bottom - clientRect.Top);
    }

    [Fact]
    public async Task Output_window_position_can_be_restored_without_changing_its_handle_or_size()
    {
        using var dispatcher = new WindowThread();
        await using var adapter = new Win32WindowAdapter(dispatcher.Enqueue, "BetterDemo placement test", 320, 180);
        var id = new OutputWindowId("placement-test");
        var binding = await adapter.CreateOutputWindowAsync(id);

        Assert.True(await adapter.TryRestorePositionAsync(id, new OutputWindowPosition(200, 150)));
        Assert.Equal(new OutputWindowPosition(200, 150), await adapter.GetPositionAsync(id));
        Assert.False(await adapter.TryRestorePositionAsync(id, new OutputWindowPosition(100_000, 100_000)));
        Assert.Equal(new OutputWindowPosition(200, 150), await adapter.GetPositionAsync(id));
        Assert.Equal(binding, await adapter.TryGetOutputWindowAsync(id));
        Assert.True(GetClientRect(binding.NativeWindowHandle.Value, out var clientRect));
        Assert.Equal(320, clientRect.Right - clientRect.Left);
        Assert.Equal(180, clientRect.Bottom - clientRect.Top);
    }

    [Fact]
    public async Task Output_window_position_survives_adapter_restart()
    {
        var path = Path.GetTempFileName();
        try
        {
            var store = new OutputPlacementStore(path);
            using var dispatcher = new WindowThread();
            var id = new OutputWindowId("restart-placement-test");
            await using (var first = new Win32WindowAdapter(dispatcher.Enqueue, "BetterDemo first placement", 320, 180))
            {
                await first.CreateOutputWindowAsync(id);
                Assert.True(await first.TryRestorePositionAsync(id, new OutputWindowPosition(220, 160)));
                var position = await first.GetPositionAsync(id);
                Assert.NotNull(position);
                store.Save(position.Value);
            }

            await using (var second = new Win32WindowAdapter(dispatcher.Enqueue, "BetterDemo second placement", 320, 180))
            {
                await second.CreateOutputWindowAsync(id);
                var saved = store.Load();
                Assert.NotNull(saved);
                Assert.True(await second.TryRestorePositionAsync(id, saved.Value));
                Assert.Equal(new OutputWindowPosition(220, 160), await second.GetPositionAsync(id));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(2)] // right edge
    [InlineData(6)] // bottom edge
    [InlineData(4)] // top-left corner
    [InlineData(8)] // bottom-right corner
    public async Task User_resize_keeps_the_output_client_area_at_sixteen_by_nine(int edge)
    {
        using var dispatcher = new WindowThread();
        await using var adapter = new Win32WindowAdapter(dispatcher.Enqueue, "BetterDemo aspect test", 320, 180);
        var binding = await adapter.CreateOutputWindowAsync(new OutputWindowId("aspect-test"));
        Assert.True(GetWindowRect(binding.NativeWindowHandle.Value, out var currentOuter));
        Assert.True(GetClientRect(binding.NativeWindowHandle.Value, out var currentClient));
        var nonClientWidth = currentOuter.Right - currentOuter.Left - (currentClient.Right - currentClient.Left);
        var nonClientHeight = currentOuter.Bottom - currentOuter.Top - (currentClient.Bottom - currentClient.Top);
        var proposed = new WindowRect { Left = 100, Top = 100, Right = 820, Bottom = 700 };

        var adjusted = await dispatcher.Run(() => SendSizingMessage(binding.NativeWindowHandle.Value, edge, proposed));

        var clientWidth = adjusted.Right - adjusted.Left - nonClientWidth;
        var clientHeight = adjusted.Bottom - adjusted.Top - nonClientHeight;
        Assert.InRange(Math.Abs(clientWidth / (double)clientHeight - 16.0 / 9.0), 0, 0.01);
    }

    [Fact]
    public async Task Output_window_handle_remains_stable_until_close_and_can_be_recreated()
    {
        using var dispatcher = new WindowThread();
        await using var adapter = new Win32WindowAdapter(dispatcher.Enqueue, "BetterDemo hidden HWND test", 320, 180);
        var id = new OutputWindowId("stable-output");

        var first = await adapter.CreateOutputWindowAsync(id);
        Assert.True(GetClientRect(first.NativeWindowHandle.Value, out var clientRect));
        Assert.Equal(320, clientRect.Right - clientRect.Left);
        Assert.Equal(180, clientRect.Bottom - clientRect.Top);
        var lookedUp = await adapter.TryGetOutputWindowAsync(id);
        Assert.NotNull(lookedUp);
        Assert.Equal(first, lookedUp);
        await adapter.ShowAsync(id);
        Assert.Equal(first, await adapter.TryGetOutputWindowAsync(id));
        await adapter.HideAsync(id);
        Assert.Equal(first, await adapter.TryGetOutputWindowAsync(id));
        await adapter.CloseAsync(id);
        Assert.Null(await adapter.TryGetOutputWindowAsync(id));

        var recreated = await adapter.CreateOutputWindowAsync(id);
        Assert.NotEqual(first.NativeWindowHandle, recreated.NativeWindowHandle);
        Assert.Equal(id, recreated.OutputWindowId);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint window, out WindowRect rectangle);

    [DllImport("user32.dll", EntryPoint = "GetWindowRect", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out WindowRect rectangle);

    [DllImport("user32.dll", EntryPoint = "SendMessageW", SetLastError = true)]
    private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);

    private static WindowRect SendSizingMessage(nint window, int edge, WindowRect proposed)
    {
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<WindowRect>());
        try
        {
            Marshal.StructureToPtr(proposed, pointer, false);
            Assert.Equal((nint)1, SendMessage(window, 0x0214, edge, pointer));
            return Marshal.PtrToStructure<WindowRect>(pointer);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private sealed class WindowThread : IDisposable
    {
        private readonly BlockingCollection<Action> actions = new();
        private readonly Thread thread;
        private bool disposed;

        public WindowThread()
        {
            thread = new Thread(() =>
            {
                foreach (var action in actions.GetConsumingEnumerable())
                {
                    action();
                }
            })
            {
                IsBackground = true,
                Name = "BetterDemo test HWND thread"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        public bool Enqueue(Action action)
        {
            if (disposed || actions.IsAddingCompleted) return false;
            actions.Add(action);
            return true;
        }

        public Task<T> Run<T>(Func<T> operation)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!Enqueue(() =>
                {
                    try { completion.TrySetResult(operation()); }
                    catch (Exception exception) { completion.TrySetException(exception); }
                }))
                throw new InvalidOperationException("The test UI thread is unavailable.");
            return completion.Task;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            actions.CompleteAdding();
            if (!thread.Join(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("The test UI thread did not stop.");
            }
            actions.Dispose();
        }
    }
}
