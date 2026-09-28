using BetterDemo.Core.Contracts;
using BetterDemo.Remote.Pairing;
using BetterDemo.Remote.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace BetterDemo.Core.Tests;

public sealed class ProtocolManualQaTests
{
    private readonly ITestOutputHelper output;

    public ProtocolManualQaTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    public void Harness_prints_exact_ack_lines_for_auth_malformed_replay_stale_and_revoke()
    {
        var pairing = new PairingService();
        var code = pairing.IssueCode();
        var token = pairing.RedeemCode(code.Code)!.Token;
        var processor = new RemoteCommandProcessor(pairing, new RemoteStateStore());

        Print(processor.Process(Command(1, "unauthenticated", "unauthorized", RemotePayloads.PanBy(1, 1))));
        Print(processor.Process(Command(1, token, "malformed", "not-json"u8.ToArray())));

        var pan = Command(1, token, "pan-1", RemotePayloads.PanBy(1, 2));
        Print(processor.Process(pan));
        Print(processor.Process(pan));
        Print(processor.Process(Command(3, token, "gap", RemotePayloads.PanBy(10, 10))));

        Assert.True(pairing.RevokeToken(token));
        Print(processor.Process(Command(2, token, "revoked", RemotePayloads.PanBy(1, 1))));
    }

    private void Print(RemoteCommandResponse response)
    {
        var error = response.Ack.ErrorCode ?? "none";
        output.WriteLine(
            $"ACK version={response.Ack.ProtocolVersion} sequence={response.Ack.Sequence} " +
            $"status={response.Ack.Status} error={error} stateSequence={response.Ack.StateSequence} " +
            $"pan=({response.Snapshot.Pan.X},{response.Snapshot.Pan.Y})");
    }

    private static RemoteCommandEnvelope Command(
        ulong sequence,
        string token,
        string key,
        ReadOnlyMemory<byte> payload) =>
        new(RemoteProtocol.CurrentVersion, sequence, RemoteCommandNames.PanBy, payload, token, key);
}
