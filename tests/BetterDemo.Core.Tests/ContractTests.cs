using BetterDemo.Core.Contracts;
using Xunit;

namespace BetterDemo.Core.Tests;

public sealed class ContractTests
{
    [Fact]
    public void Scene_mode_wire_names_are_exact_and_case_sensitive()
    {
        Assert.Equal(
            new[] { "black", "screen", "physicalCamera", "screenPlusPhysicalCameraCorner" },
            SceneModeWireNames.All);

        Assert.Equal("black", SceneModeWireNames.ToWireName(SceneMode.Black));
        Assert.Equal("screen", SceneModeWireNames.ToWireName(SceneMode.Screen));
        Assert.Equal("physicalCamera", SceneModeWireNames.ToWireName(SceneMode.PhysicalCamera));
        Assert.Equal(
            "screenPlusPhysicalCameraCorner",
            SceneModeWireNames.ToWireName(SceneMode.ScreenPlusPhysicalCameraCorner));

        Assert.Throws<FormatException>(() => SceneModeWireNames.Parse("physical camera"));
        Assert.False(SceneModeWireNames.TryParse("SCREEN", out _));
    }

    [Fact]
    public void Video_device_ids_are_stable_identity_values()
    {
        var firstEnumeration = new VideoDeviceId("mf://device/obs-camera");
        var secondEnumeration = new VideoDeviceId("mf://device/obs-camera");

        Assert.Equal(firstEnumeration, secondEnumeration);
        Assert.NotEqual(firstEnumeration, new VideoDeviceId("mf://device/physical-camera"));
        Assert.Throws<ArgumentException>(() => new VideoDeviceId(" "));
    }

    [Fact]
    public void Video_frames_use_positive_qpc_and_reject_stale_ordering()
    {
        var first = new VideoFrameStamp(10, new QpcTimestamp(100, 10_000_000));
        var second = new VideoFrameStamp(11, new QpcTimestamp(101, 10_000_000));
        var stale = new VideoFrameStamp(12, new QpcTimestamp(99, 10_000_000));

        Assert.True(VideoFrameOrdering.IsStrictlyNewer(second, first));
        Assert.False(VideoFrameOrdering.IsStrictlyNewer(stale, first));
        Assert.Throws<ArgumentOutOfRangeException>(() => new QpcTimestamp(0, 10_000_000));
    }

    [Theory]
    [InlineData(1_500_000, 10_000_000, true)]
    [InlineData(800_000, 10_000_000, false)]
    [InlineData(2_000_000, 20_000_000, true)]
    public void Frame_freshness_uses_the_qpc_frequency_and_age_limit(long frameTicks, long frameFrequency, bool expected)
    {
        var frame = new VideoFrameStamp(1, new QpcTimestamp(frameTicks, frameFrequency));
        Assert.Equal(expected, VideoFrameFreshness.IsFresh(frame, 2_000_000, 10_000_000, TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public void Frame_freshness_rejects_future_frames_and_invalid_age_limits()
    {
        var future = new VideoFrameStamp(1, new QpcTimestamp(20, 10));
        Assert.False(VideoFrameFreshness.IsFresh(future, 10, 10, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => VideoFrameFreshness.IsFresh(future, 10, 10, TimeSpan.Zero));
    }

    [Fact]
    public void Source_lifecycle_allows_only_named_transitions()
    {
        Assert.True(SourceStateTransitions.CanTransition(SourceState.Created, SourceState.Starting));
        Assert.True(SourceStateTransitions.CanTransition(SourceState.Starting, SourceState.Running));
        Assert.True(SourceStateTransitions.CanTransition(SourceState.Running, SourceState.Stopping));
        Assert.True(SourceStateTransitions.CanTransition(SourceState.Stopping, SourceState.Stopped));
        Assert.True(SourceStateTransitions.CanTransition(SourceState.Stopped, SourceState.Starting));
        Assert.True(SourceStateTransitions.CanTransition(SourceState.Faulted, SourceState.Disposed));
        Assert.False(SourceStateTransitions.CanTransition(SourceState.Created, SourceState.Running));
        Assert.False(SourceStateTransitions.CanTransition(SourceState.Disposed, SourceState.Starting));
    }

    [Fact]
    public void Frame_disposal_is_idempotent_and_independent()
    {
        var format = new VideoFrameFormat(1280, 720, VideoPixelFormat.Bgra32, 5120);
        var first = new VideoFrame(new VideoDeviceId("device-a"), format, new VideoFrameStamp(1, new QpcTimestamp(1, 10)));
        var second = new VideoFrame(new VideoDeviceId("device-b"), format, new VideoFrameStamp(1, new QpcTimestamp(1, 10)));

        first.Dispose();
        first.Dispose();

        Assert.True(first.IsDisposed);
        Assert.False(second.IsDisposed);
        second.Dispose();
    }

    [Fact]
    public void Audio_request_requires_process_and_discord_exclusion_invariants()
    {
        var request = new AudioCaptureRequest(
            100,
            new[] { 200 },
            new[] { "Discord.exe" },
            new[] { 3 },
            AudioCapturePath.ApplicationLoopback,
            null);

        Assert.True(request.ExcludesDiscordByProcessId(200));
        Assert.True(request.ExcludesDiscordByExecutable("Discord.exe"));
        Assert.True(request.ExcludesDiscordBySession(3));
        Assert.False(request.UsesEndpointFallback);

        var systemRequest = new AudioCaptureRequest(
            200,
            new[] { 200 },
            new[] { "Discord.exe" },
            new[] { 200 },
            AudioCapturePath.ExcludeTargetProcessTree,
            null);
        Assert.Equal(AudioCapturePath.ExcludeTargetProcessTree, systemRequest.CapturePath);
        Assert.True(systemRequest.ExcludesDiscordByProcessId(systemRequest.SourceProcessId));

        Assert.Throws<ArgumentException>(() => new AudioCaptureRequest(
            200, Array.Empty<int>(), new[] { "Discord.exe" }, new[] { 200 },
            AudioCapturePath.ExcludeTargetProcessTree, null));

        Assert.Throws<ArgumentException>(() => new AudioCaptureRequest(
            100, Array.Empty<int>(), new[] { "Music.exe" }, new[] { 3 },
            AudioCapturePath.ApplicationLoopback, null));
        Assert.Throws<ArgumentException>(() => new AudioCaptureRequest(
            100, new[] { 200 }, new[] { "Discord.exe" }, new[] { 3 },
            AudioCapturePath.RenderEndpointFallback, null));

        var fallback = new AudioCaptureRequest(
            100, new[] { 200 }, new[] { "Discord.exe" }, new[] { 3 },
            AudioCapturePath.RenderEndpointFallback, "Application loopback unavailable.");
        Assert.True(fallback.UsesEndpointFallback);
        Assert.Equal("Application loopback unavailable.", fallback.FallbackNotice);

        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioCaptureRequest(
            100, new[] { 200 }, new[] { "Discord.exe" }, new[] { 3 },
            (AudioCapturePath)99, null));
        Assert.Throws<ArgumentException>(() => new AudioCaptureRequest(
            100, Array.Empty<int>(), new[] { "Discord.exe" }, Array.Empty<int>(),
            AudioCapturePath.RenderEndpointFallback, "Fallback selected."));
    }

    [Fact]
    public void Virtual_cable_writer_and_48khz_audio_format_are_explicit()
    {
        var format = AudioFormat.Mixer48KHz;
        var endpoint = new AudioRenderEndpoint(new AudioDeviceId("vb-cable"), "VB-CABLE", IsVirtualCable: true);

        Assert.Equal(48_000, format.SampleRateHz);
        Assert.True(endpoint.IsVirtualCable);

        var diagnostic = new DiagnosticEvent(
            DiagnosticSeverity.Warning,
            DiagnosticCode.AudioEndpointFallback,
            "audio",
            "Application loopback unavailable.");
        Assert.Equal(DiagnosticCode.AudioEndpointFallback, diagnostic.Code);
    }

    [Fact]
    public void Output_window_identity_is_stable_and_lifecycle_is_explicit()
    {
        var id = new OutputWindowId("output-window");

        Assert.Equal(id, new OutputWindowId("output-window"));
        Assert.True(WindowLifecycleRules.CanTransition(OutputWindowState.Created, OutputWindowState.Visible));
        Assert.True(WindowLifecycleRules.CanTransition(OutputWindowState.Visible, OutputWindowState.Hidden));
        Assert.True(WindowLifecycleRules.CanTransition(OutputWindowState.Hidden, OutputWindowState.Visible));
        Assert.False(WindowLifecycleRules.CanTransition(OutputWindowState.Disposed, OutputWindowState.Visible));
    }

    [Fact]
    public void Remote_contract_is_versioned_and_sequenced()
    {
        var envelope = new RemoteCommandEnvelope(
            RemoteProtocol.CurrentVersion,
            7,
            "setMode",
            ReadOnlyMemory<byte>.Empty);
        var ack = new RemoteCommandAck(RemoteProtocol.CurrentVersion, 7, RemoteAckStatus.Accepted, 8);
        var snapshot = new RemoteStateSnapshot(RemoteProtocol.CurrentVersion, 8, SceneMode.Black, new OutputWindowId("output-window"));

        Assert.Equal(7UL, envelope.Sequence);
        Assert.Equal(RemoteAckStatus.Accepted, ack.Status);
        Assert.Equal(8UL, snapshot.StateSequence);
        Assert.Throws<ArgumentException>(() => new RemoteCommandEnvelope(1, 0, "ping", ReadOnlyMemory<byte>.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RemoteCommandAck(0, 7, RemoteAckStatus.Accepted, 8));
        Assert.Throws<ArgumentException>(() => new RemoteCommandAck(RemoteProtocol.CurrentVersion, 0, RemoteAckStatus.Accepted, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RemoteStateSnapshot(0, 8, SceneMode.Black, new OutputWindowId("output-window")));
        Assert.Throws<ArgumentException>(() => new RemoteStateSnapshot(RemoteProtocol.CurrentVersion, 0, SceneMode.Black, new OutputWindowId("output-window")));
    }
}
