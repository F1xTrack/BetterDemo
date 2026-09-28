using BetterDemo.Core.Contracts;
using BetterDemo.Core.Scene;
using BetterDemo.Remote.Pairing;
using BetterDemo.Remote.Protocol;
using Xunit;

namespace BetterDemo.Integration.Tests;

public sealed class RemoteSceneCompositionTests
{
    [Fact]
    public void Authenticated_phone_scene_command_changes_the_source_used_by_the_compositor()
    {
        var pairing = new PairingService();
        var token = pairing.RedeemCode(pairing.IssueCode().Code)!.Token;
        var state = new RemoteStateStore(new OutputWindowId("phone-composition-test"));
        state.UpdateLocalMode(SceneMode.Screen);
        var processor = new RemoteCommandProcessor(pairing, state);
        var command = new RemoteCommandEnvelope(
            RemoteProtocol.CurrentVersion,
            sequence: 1,
            RemoteCommandNames.SetMode,
            RemotePayloads.SetMode(SceneMode.PhysicalCamera),
            token,
            idempotencyKey: "phone-camera-scene");

        var response = processor.Process(command);

        Assert.Equal(RemoteAckStatus.Accepted, response.Ack.Status);
        Assert.Equal(SceneMode.PhysicalCamera, state.Snapshot.Mode);

        using var physicalFrame = SolidBgraFrame(0, 0, 255, 255);
        var output = new SceneCompositor().Render(
            state.Snapshot.Mode,
            obsFrame: null,
            physicalFrame,
            new SceneRenderOptions(1, 1));

        Assert.Equal(new byte[] { 0, 0, 255, 255 }, output.Pixels.ToArray());
        Assert.Empty(output.Diagnostics);
    }

    [Fact]
    public void Authenticated_phone_source_visibility_command_hides_the_camera_from_the_composition()
    {
        var pairing = new PairingService();
        var token = pairing.RedeemCode(pairing.IssueCode().Code)!.Token;
        var state = new RemoteStateStore(new OutputWindowId("phone-source-test"));
        state.UpdateLocalMode(SceneMode.PhysicalCamera);
        var processor = new RemoteCommandProcessor(pairing, state);
        var command = new RemoteCommandEnvelope(
            RemoteProtocol.CurrentVersion,
            sequence: 1,
            RemoteCommandNames.SetLayerVisibility,
            RemotePayloads.SetLayerVisibility(SceneDocumentFactory.PhysicalCameraLayerId.Value, visible: false),
            token,
            idempotencyKey: "phone-hide-camera");

        var response = processor.Process(command);

        Assert.Equal(RemoteAckStatus.Accepted, response.Ack.Status);
        var document = SceneDocumentFactory.ApplyVisibilityOverrides(
            SceneDocumentFactory.CreatePreset(state.Snapshot.Mode),
            state.Snapshot.LayerVisibility);
        using var physicalFrame = SolidBgraFrame(0, 0, 255, 255);
        var output = new SceneCompositor().Render(document, new Dictionary<SceneLayerId, VideoFrame>
        {
            [SceneDocumentFactory.PhysicalCameraLayerId] = physicalFrame
        }, new SceneRenderOptions(1, 1));

        Assert.Equal(new byte[] { 0, 0, 0, 255 }, output.Pixels.ToArray());
        Assert.Empty(output.Diagnostics);
    }

    private static VideoFrame SolidBgraFrame(byte blue, byte green, byte red, byte alpha)
    {
        var format = new VideoFrameFormat(1, 1, VideoPixelFormat.Bgra32, 4);
        var stamp = new VideoFrameStamp(1, new QpcTimestamp(1, 10_000_000));
        return VideoFrame.CopyFrom(
            new VideoDeviceId("mf://phone-composition-test/physical"),
            format,
            stamp,
            [blue, green, red, alpha]);
    }
}
