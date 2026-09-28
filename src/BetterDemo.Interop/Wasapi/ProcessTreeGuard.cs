using System.ComponentModel;
using System.Runtime.InteropServices;
using BetterDemo.Core.Contracts;
using Microsoft.Win32.SafeHandles;

namespace BetterDemo.Interop.Wasapi;

public static class ProcessTreeGuard
{
    public static void Validate(AudioCaptureRequest request)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Process audio capture requires Windows.");

        var processes = EnumerateProcesses();
        var target = processes.FirstOrDefault(process => process.Id == request.SourceProcessId);
        if (target.Id == 0)
            throw new InvalidOperationException("The requested audio process is no longer running.");

        if (request.CapturePath == AudioCapturePath.ExcludeTargetProcessTree)
        {
            if (!target.ExecutableName.Equals("Discord.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("System audio capture must exclude a Discord process tree.");

            var discordRoot = FindDiscordRoot(processes);
            if (discordRoot is null)
                throw new InvalidOperationException("Discord is not running. Start Discord before routing system audio.");
            if (discordRoot.Value != request.SourceProcessId)
                throw new InvalidOperationException("Discord has multiple process trees or its root changed. Restart the system audio route.");
            return;
        }

        if (request.CapturePath != AudioCapturePath.ApplicationLoopback)
            throw new NotSupportedException("Render-endpoint fallback cannot enforce process isolation.");

        ValidateApplicationTree(request, processes);
    }

    public static int? FindDiscordRootProcessId()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Discord process discovery requires Windows.");
        return FindDiscordRoot(EnumerateProcesses());
    }

    private static void ValidateApplicationTree(AudioCaptureRequest request, IReadOnlyList<ProcessEntry> processes)
    {
        if (!processes.Any(process => process.Id == request.SourceProcessId))
            throw new InvalidOperationException("The selected audio source process is no longer running.");

        var descendants = new HashSet<int> { request.SourceProcessId };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var process in processes)
            {
                if (descendants.Contains(process.ParentId) && descendants.Add(process.Id)) changed = true;
            }
        }

        foreach (var process in processes.Where(process => descendants.Contains(process.Id)))
        {
            if (request.ExcludedProcessIds.Contains(process.Id) ||
                request.ExcludedExecutableNames.Contains(process.ExecutableName) ||
                process.ExecutableName.Equals("Discord.exe", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"The selected process tree contains excluded audio process {process.ExecutableName} ({process.Id}).");
            }
        }
    }

    private static int? FindDiscordRoot(IReadOnlyList<ProcessEntry> processes)
    {
        var discord = processes
            .Where(process => process.ExecutableName.Equals("Discord.exe", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (discord.Length == 0) return null;

        var discordIds = discord.Select(process => process.Id).ToHashSet();
        var roots = discord
            .Where(process => !discordIds.Contains(process.ParentId))
            .Select(process => process.Id)
            .Distinct()
            .ToArray();
        return roots.Length switch
        {
            0 => throw new InvalidOperationException("The Discord process tree could not be identified."),
            1 => roots[0],
            _ => throw new InvalidOperationException("Multiple independent Discord process trees are running; audio routing was stopped to avoid capturing Discord.")
        };
    }

    private static List<ProcessEntry> EnumerateProcesses()
    {
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());

        var processes = new List<ProcessEntry>();
        var entry = new NativeProcessEntry { Size = (uint)Marshal.SizeOf<NativeProcessEntry>() };
        if (!Process32FirstW(snapshot, ref entry)) throw new Win32Exception(Marshal.GetLastWin32Error());
        do
        {
            processes.Add(new ProcessEntry((int)entry.ProcessId, (int)entry.ParentProcessId, entry.ExecutableName));
            entry.Size = (uint)Marshal.SizeOf<NativeProcessEntry>();
        }
        while (Process32NextW(snapshot, ref entry));
        return processes;
    }

    private readonly record struct ProcessEntry(int Id, int ParentId, string ExecutableName);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExecutableName;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(SafeFileHandle snapshot, ref NativeProcessEntry entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(SafeFileHandle snapshot, ref NativeProcessEntry entry);
}
