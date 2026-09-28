using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wasapi.CoreAudioApi.Interfaces;

namespace BetterDemo.Interop.Wasapi;

internal static class ProcessLoopbackActivator
{
    private const string VirtualProcessLoopbackDevice = "VAD\\Process_Loopback";
    private static readonly Guid AudioClientId = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");

    public static async Task<AudioClient> ActivateAsync(int processId, int processLoopbackMode, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
        {
            throw new PlatformNotSupportedException("Process loopback requires Windows build 20348 or newer.");
        }
        if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
        cancellationToken.ThrowIfCancellationRequested();

        if (processLoopbackMode is not (0 or 1)) throw new ArgumentOutOfRangeException(nameof(processLoopbackMode));
        var parameters = new AudioClientActivationParams(1, (uint)processId, processLoopbackMode);
        var parametersMemory = Marshal.AllocHGlobal(Marshal.SizeOf<AudioClientActivationParams>());
        var variantMemory = Marshal.AllocHGlobal(Marshal.SizeOf<BlobPropVariant>());
        IActivateAudioInterfaceAsyncOperation? operation = null;
        var completion = new CompletionHandler();
        var activationStarted = false;
        var clientTransferred = false;
        try
        {
            Marshal.StructureToPtr(parameters, parametersMemory, false);
            Marshal.StructureToPtr(
                new BlobPropVariant(65, (uint)Marshal.SizeOf<AudioClientActivationParams>(), parametersMemory),
                variantMemory,
                false);

            var interfaceId = AudioClientId;
            var result = ActivateAudioInterfaceAsync(
                VirtualProcessLoopbackDevice,
                ref interfaceId,
                variantMemory,
                completion,
                out operation);
            Marshal.ThrowExceptionForHR(result);
            activationStarted = true;

            var nativeClient = await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            var audioClient = new AudioClient(nativeClient);
            clientTransferred = true;
            return audioClient;
        }
        finally
        {
            if (activationStarted && !clientTransferred)
            {
                _ = completion.Task.ContinueWith(task =>
                {
                    if (task.IsCompletedSuccessfully && OperatingSystem.IsWindows())
                        Marshal.ReleaseComObject(task.Result);
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            if (activationStarted && !completion.Task.IsCompleted)
            {
                // A cancelled caller must not free the activation blob before Windows has used it.
                _ = completion.Task.ContinueWith(
                    _ => ReleaseActivation(operation, variantMemory, parametersMemory),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            else
            {
                ReleaseActivation(operation, variantMemory, parametersMemory);
            }
        }
    }

    private static void ReleaseActivation(
        IActivateAudioInterfaceAsyncOperation? operation,
        IntPtr variantMemory,
        IntPtr parametersMemory)
    {
        if (OperatingSystem.IsWindows() && operation is not null) Marshal.ReleaseComObject(operation);
        Marshal.FreeHGlobal(variantMemory);
        Marshal.FreeHGlobal(parametersMemory);
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [In] ref Guid interfaceId,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct AudioClientActivationParams(
        int ActivationType,
        uint TargetProcessId,
        int ProcessLoopbackMode);

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private readonly struct BlobPropVariant
    {
        public BlobPropVariant(short variantType, uint blobSize, IntPtr blobData)
        {
            VariantType = variantType;
            BlobSize = blobSize;
            BlobData = blobData;
        }

        [FieldOffset(0)] public readonly short VariantType;
        [FieldOffset(8)] public readonly uint BlobSize;
        [FieldOffset(16)] public readonly IntPtr BlobData;
    }

    [ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject { }

    private sealed class CompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        private readonly TaskCompletionSource<IAudioClient> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IAudioClient> Task => completion.Task;

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activationOperation)
        {
            try
            {
                activationOperation.GetActivateResult(out var result, out var activatedInterface);
                Marshal.ThrowExceptionForHR(result);
                if (activatedInterface is not IAudioClient audioClient)
                {
                    throw new InvalidCastException("Process loopback activation did not return IAudioClient.");
                }
                completion.TrySetResult(audioClient);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }
    }
}
