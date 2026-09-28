using System.Diagnostics;
using BetterDemo.Core.Contracts;
using BetterDemo.Interop.Wasapi;
using Xunit;
using Xunit.Abstractions;

namespace BetterDemo.Integration.Tests;

public sealed class WasapiProcessLoopbackTests
{
    private readonly ITestOutputHelper output;

    public WasapiProcessLoopbackTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    public void Virtual_cable_policy_rejects_physical_and_accepts_vb_audio()
    {
        Assert.False(VirtualCableEndpointPolicy.IsVirtualCableRenderEndpoint("Speakers (Realtek Audio)", "Realtek Audio"));
        Assert.False(VirtualCableEndpointPolicy.IsVirtualCableRenderEndpoint("CABLE Input", "Unknown"));
        Assert.True(VirtualCableEndpointPolicy.IsVirtualCableRenderEndpoint(
            "CABLE Input (VB-Audio Virtual Cable)", "VB-Audio Virtual Cable"));
        Assert.True(VirtualCableEndpointPolicy.IsVirtualCableRenderEndpoint(
            "VoiceMeeter Input (VB-Audio VoiceMeeter VAIO)", "VB-Audio VoiceMeeter VAIO"));
    }

    [Fact]
    public async Task Activate_process_loopback_when_explicitly_enabled()
    {
        if (Environment.GetEnvironmentVariable("BETTERDEMO_LIVE_AUDIO") != "1")
        {
            output.WriteLine("Live process loopback disabled. Set BETTERDEMO_LIVE_AUDIO=1 to run it.");
            return;
        }

        var discordRootProcessId = ProcessTreeGuard.FindDiscordRootProcessId();
        if (discordRootProcessId is null)
        {
            output.WriteLine("Live process loopback probe skipped because Discord is not running.");
            return;
        }

        var request = new AudioCaptureRequest(
            discordRootProcessId.Value,
            [discordRootProcessId.Value],
            ["Discord.exe"],
            [discordRootProcessId.Value],
            AudioCapturePath.ExcludeTargetProcessTree,
            null);
        await using var adapter = new WasapiAudioAdapter();
        var endpoints = await adapter.EnumerateActiveRenderEndpointsAsync();
        output.WriteLine($"ACTIVE_OUTPUT_COUNT={endpoints.Count}");
        await using var capture = await adapter.OpenProcessCaptureAsync(request, AudioFormat.Mixer48KHz);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await capture.StartAsync(timeout.Token);
        Assert.Equal(SourceState.Running, capture.State);
        output.WriteLine($"PROCESS_LOOPBACK_START=PASS EXCLUDED_DISCORD_ROOT={discordRootProcessId.Value}");
        await Task.Delay(300, timeout.Token);
        await capture.StopAsync(timeout.Token);
        Assert.Equal(SourceState.Stopped, capture.State);
        output.WriteLine($"PROCESS_LOOPBACK_STOP=PASS dropped={((WasapiProcessLoopbackCapture)capture).DroppedBlockCount}");
    }
}
