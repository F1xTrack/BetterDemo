using System.Text;
using BetterDemo.Core.Contracts;
using BetterDemo.Remote.Pairing;
using BetterDemo.Remote.Protocol;
using Xunit;

namespace BetterDemo.Core.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public void Wire_command_names_are_versioned_and_complete()
    {
        Assert.Equal(
            new[]
            {
                "setMode", "setCameraCorner", "setZoom", "setTransform", "panBy", "resetTransform",
                "setBlur", "setLayerVisibility", "setOutputVisibility", "getState", "ping"
            },
            RemoteCommandNames.All);
        Assert.Equal(RemoteProtocol.CurrentVersion, RemoteCommandNames.ProtocolVersion);
    }

    [Fact]
    public async Task Every_command_is_authenticated_ordered_and_followed_by_snapshot()
    {
        var (processor, token) = CreateProcessor();
        var commands = new[]
        {
            Command(1, token, "mode", RemotePayloads.SetMode(SceneMode.Screen), RemoteCommandNames.SetMode),
            Command(2, token, "corner", RemotePayloads.SetCameraCorner(15, 0.4, 12), RemoteCommandNames.SetCameraCorner),
            Command(3, token, "zoom", RemotePayloads.SetZoom(1.5), RemoteCommandNames.SetZoom),
            Command(4, token, "transform", RemotePayloads.SetTransform(2, 0.5, -0.25), RemoteCommandNames.SetTransform),
            Command(5, token, "pan", RemotePayloads.PanBy(0.2, -0.1), RemoteCommandNames.PanBy),
            Command(6, token, "reset", RemotePayloads.ResetTransform(), RemoteCommandNames.ResetTransform),
            Command(7, token, "blur", RemotePayloads.SetBlur(true, 4), RemoteCommandNames.SetBlur),
            Command(8, token, "layer", RemotePayloads.SetLayerVisibility("layer-1", false), RemoteCommandNames.SetLayerVisibility),
            Command(9, token, "output-visibility", RemotePayloads.SetOutputVisibility(false), RemoteCommandNames.SetOutputVisibility),
            Command(10, token, "state", RemotePayloads.Empty(), RemoteCommandNames.GetState),
            Command(11, token, "ping", RemotePayloads.Empty(), RemoteCommandNames.Ping)
        };

        foreach (var command in commands)
        {
            var response = await processor.ProcessAsync(command);
            Assert.Equal(RemoteAckStatus.Accepted, response.Ack.Status);
            Assert.Equal(command.Sequence, response.Ack.Sequence);
            Assert.Equal(RemoteProtocol.CurrentVersion, response.Snapshot.ProtocolVersion);
            Assert.NotEqual(0UL, response.Snapshot.StateSequence);
        }

        var final = (await processor.ProcessAsync(Command(12, token, "final-state", RemotePayloads.Empty(), "getState"))).Snapshot;
        Assert.Equal(SceneMode.Screen, final.Mode);
        Assert.Equal(1d, final.Zoom);
        Assert.Equal(0d, final.Pan.X);
        Assert.Equal(0d, final.Pan.Y);
        Assert.True(final.Blur.Enabled);
        Assert.Equal(4d, final.Blur.Radius);
        Assert.True(final.LayerVisibility["layer-1"] == false);
        Assert.False(final.OutputVisible);
    }

    [Fact]
    public async Task Unauthenticated_and_malformed_commands_return_errors_without_mutation()
    {
        var (processor, token) = CreateProcessor();
        var unauthenticated = Command(1, "not-a-token", "unauthorized", RemotePayloads.SetMode(SceneMode.Screen));
        var rejected = await processor.ProcessAsync(unauthenticated);
        Assert.Equal(RemoteAckStatus.Rejected, rejected.Ack.Status);
        Assert.Equal(RemoteErrorCodes.Unauthorized, rejected.Ack.ErrorCode);
        Assert.Equal(SceneMode.Black, rejected.Snapshot.Mode);

        var malformed = Command(1, token, "malformed", Encoding.UTF8.GetBytes("{\"mode\":").AsMemory());
        var malformedResponse = await processor.ProcessAsync(malformed);
        Assert.Equal(RemoteAckStatus.Rejected, malformedResponse.Ack.Status);
        Assert.Equal(RemoteErrorCodes.MalformedPayload, malformedResponse.Ack.ErrorCode);
        Assert.Equal(SceneMode.Black, malformedResponse.Snapshot.Mode);
    }

    [Fact]
    public async Task Stale_sequences_and_replayed_idempotency_keys_do_not_reapply_pan()
    {
        var (processor, token) = CreateProcessor();
        var pan = Command(1, token, "pan-1", RemotePayloads.PanBy(3, -2));
        var accepted = await processor.ProcessAsync(pan);
        Assert.Equal(RemoteAckStatus.Accepted, accepted.Ack.Status);
        Assert.Equal(3d, accepted.Snapshot.Pan.X);
        Assert.Equal(-2d, accepted.Snapshot.Pan.Y);

        var reconnectProcessor = CreateProcessor(processor.Pairing, processor.State).Processor;
        var duplicate = await reconnectProcessor.ProcessAsync(pan);
        Assert.Equal(RemoteAckStatus.Duplicate, duplicate.Ack.Status);
        Assert.Equal(3d, duplicate.Snapshot.Pan.X);
        Assert.Equal(-2d, duplicate.Snapshot.Pan.Y);

        var stale = await reconnectProcessor.ProcessAsync(
            Command(3, token, "pan-gap", RemotePayloads.PanBy(10, 10)));
        Assert.Equal(RemoteAckStatus.Stale, stale.Ack.Status);
        Assert.Equal(RemoteErrorCodes.StaleSequence, stale.Ack.ErrorCode);
        Assert.Equal(3d, stale.Snapshot.Pan.X);
        Assert.Equal(-2d, stale.Snapshot.Pan.Y);

        var duplicateKey = await reconnectProcessor.ProcessAsync(
            Command(2, token, "pan-1", RemotePayloads.PanBy(3, -2)));
        Assert.Equal(RemoteAckStatus.Duplicate, duplicateKey.Ack.Status);
        Assert.Equal(3d, duplicateKey.Snapshot.Pan.X);
        Assert.Equal(-2d, duplicateKey.Snapshot.Pan.Y);
    }

    [Fact]
    public void Codec_rejects_missing_authentication_and_unknown_wire_shape()
    {
        Assert.False(RemoteEnvelopeCodec.TryDecode(
            Encoding.UTF8.GetBytes("{\"protocolVersion\":1,\"sequence\":1,\"command\":\"ping\"}"),
            out _, out var missingFieldError));
        Assert.Equal(RemoteErrorCodes.MalformedEnvelope, missingFieldError);

        Assert.False(RemoteEnvelopeCodec.TryDecode(
            Encoding.UTF8.GetBytes("not-json"), out _, out var invalidJsonError));
        Assert.Equal(RemoteErrorCodes.MalformedEnvelope, invalidJsonError);

        var validPrefix = "{\"protocolVersion\":1,\"sequence\":1,\"command\":\"ping\",\"token\":\"token\",\"idempotencyKey\":\"key\",\"payload\":{},";
        Assert.False(RemoteEnvelopeCodec.TryDecode(
            Encoding.UTF8.GetBytes(validPrefix + "\"unknown\":true}"),
            out _, out var unknownFieldError));
        Assert.Equal(RemoteErrorCodes.MalformedEnvelope, unknownFieldError);

        Assert.False(RemoteEnvelopeCodec.TryDecode(
            Encoding.UTF8.GetBytes("{\"protocolVersion\":1,\"sequence\":1,\"command\":\"ping\",\"token\":\"token\",\"idempotencyKey\":\"key\",\"payload\":{},\"payload\":{}}"),
            out _, out var duplicateFieldError));
        Assert.Equal(RemoteErrorCodes.MalformedEnvelope, duplicateFieldError);
    }

    [Fact]
    public async Task Revocation_completed_before_inflight_authentication_cannot_mutate_or_consume_sequence()
    {
        var (pairing, token) = CreatePairing();
        var state = new RemoteStateStore();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processor = new RemoteCommandProcessor(
            pairing,
            state,
            beforeAuthentication: () =>
            {
                entered.SetResult();
                release.Task.GetAwaiter().GetResult();
            },
            beforeExecution: null);

        var commandTask = Task.Run(() => processor.Process(Command(1, token, "revoke-race", RemotePayloads.PanBy(4, 5))));
        await entered.Task;
        Assert.True(pairing.RevokeToken(token));
        release.SetResult();

        var response = await commandTask;
        Assert.Equal(RemoteAckStatus.Rejected, response.Ack.Status);
        Assert.Equal(RemoteErrorCodes.Unauthorized, response.Ack.ErrorCode);
        Assert.Equal(1UL, response.Ack.Sequence);
        Assert.Equal(1UL, response.Snapshot.StateSequence);
        Assert.Equal(0d, response.Snapshot.Pan.X);
        Assert.Equal(0d, response.Snapshot.Pan.Y);
    }

    [Fact]
    public async Task Expiry_observed_during_inflight_authentication_cannot_mutate_or_consume_sequence()
    {
        var clock = new BlockingTimeProvider(DateTimeOffset.UtcNow);
        var pairing = new PairingService(clock, tokenLifetime: TimeSpan.FromSeconds(10));
        var code = pairing.IssueCode();
        var token = pairing.RedeemCode(code.Code)!.Token;
        var state = new RemoteStateStore();
        var processor = new RemoteCommandProcessor(pairing, state);
        clock.BlockNextRead();

        var commandTask = Task.Run(() => processor.Process(Command(1, token, "expiry-race", RemotePayloads.PanBy(6, 7))));
        await clock.ReadBlocked.Task;
        clock.Advance(TimeSpan.FromSeconds(11));
        clock.ReleaseRead();

        var response = await commandTask;
        Assert.Equal(RemoteAckStatus.Rejected, response.Ack.Status);
        Assert.Equal(RemoteErrorCodes.Unauthorized, response.Ack.ErrorCode);
        Assert.Equal(1UL, response.Ack.Sequence);
        Assert.Equal(1UL, response.Snapshot.StateSequence);
        Assert.Equal(0d, response.Snapshot.Pan.X);
        Assert.Equal(0d, response.Snapshot.Pan.Y);
    }

    [Fact]
    public async Task Idempotency_ledger_is_bounded_without_consuming_the_rejected_sequence()
    {
        var (processor, token) = CreateProcessor();
        for (var sequence = 1UL; sequence <= 4096; sequence++)
        {
            var response = await processor.ProcessAsync(
                Command(sequence, token, $"ledger-{sequence}", RemotePayloads.Empty(), RemoteCommandNames.Ping));
            Assert.Equal(RemoteAckStatus.Accepted, response.Ack.Status);
        }

        var full = await processor.ProcessAsync(
            Command(4097, token, "ledger-full", RemotePayloads.Empty(), RemoteCommandNames.Ping));
        Assert.Equal(RemoteAckStatus.Rejected, full.Ack.Status);
        Assert.Equal(RemoteErrorCodes.IdempotencyLedgerFull, full.Ack.ErrorCode);
        Assert.Equal(1UL, full.Snapshot.StateSequence);
    }

    private static (RemoteCommandProcessor Processor, string Token) CreateProcessor(
        PairingService? pairing = null,
        RemoteStateStore? state = null)
    {
        pairing ??= new PairingService();
        var code = pairing.IssueCode();
        var token = pairing.RedeemCode(code.Code)!.Token;
        state ??= new RemoteStateStore();
        return (new RemoteCommandProcessor(pairing, state), token);
    }

    private static (PairingService Pairing, string Token) CreatePairing()
    {
        var pairing = new PairingService();
        var code = pairing.IssueCode();
        return (pairing, pairing.RedeemCode(code.Code)!.Token);
    }

    private static RemoteCommandEnvelope Command(
        ulong sequence,
        string token,
        string idempotencyKey,
        ReadOnlyMemory<byte> payload,
        string commandName = "panBy") =>
        new(
            RemoteProtocol.CurrentVersion,
            sequence,
            commandName,
            payload,
            token,
            idempotencyKey);

    private sealed class BlockingTimeProvider : TimeProvider
    {
        private DateTimeOffset utcNow;
        private int blockNextRead;
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BlockingTimeProvider(DateTimeOffset utcNow) => this.utcNow = utcNow;

        public TaskCompletionSource ReadBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override DateTimeOffset GetUtcNow()
        {
            if (Interlocked.Exchange(ref blockNextRead, 0) == 1)
            {
                ReadBlocked.SetResult();
                release.Task.GetAwaiter().GetResult();
            }

            return utcNow;
        }

        public void BlockNextRead() => Volatile.Write(ref blockNextRead, 1);

        public void Advance(TimeSpan duration) => utcNow += duration;

        public void ReleaseRead() => release.SetResult();
    }
}
