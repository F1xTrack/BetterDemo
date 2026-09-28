namespace BetterDemo.Core.Contracts;

public static class RemoteProtocol
{
    public const int CurrentVersion = 1;

    public static void ValidateVersion(int protocolVersion)
    {
        if (protocolVersion != CurrentVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(protocolVersion), protocolVersion, "Unsupported remote protocol version.");
        }
    }
}

public sealed class RemoteCommandEnvelope
{
    public RemoteCommandEnvelope(
        int protocolVersion,
        ulong sequence,
        string commandName,
        ReadOnlyMemory<byte> payload,
        string? authenticationToken = null,
        string? idempotencyKey = null)
    {
        RemoteProtocol.ValidateVersion(protocolVersion);
        if (sequence == 0) throw new ArgumentException("Remote command sequences start at one.", nameof(sequence));
        if (string.IsNullOrWhiteSpace(commandName)) throw new ArgumentException("A command name is required.", nameof(commandName));

        ProtocolVersion = protocolVersion;
        Sequence = sequence;
        CommandName = commandName;
        Payload = payload;
        AuthenticationToken = authenticationToken ?? string.Empty;
        IdempotencyKey = idempotencyKey ?? string.Empty;
    }

    public int ProtocolVersion { get; }
    public ulong Sequence { get; }
    public string CommandName { get; }
    public ReadOnlyMemory<byte> Payload { get; }
    public string AuthenticationToken { get; }
    public string IdempotencyKey { get; }
}

public enum RemoteAckStatus
{
    Accepted,
    Duplicate,
    Rejected,
    Stale,
    Failed
}

public sealed class RemoteCommandAck
{
    public RemoteCommandAck(
        int protocolVersion,
        ulong sequence,
        RemoteAckStatus status,
        ulong stateSequence,
        string? errorCode = null,
        string? errorMessage = null)
    {
        RemoteProtocol.ValidateVersion(protocolVersion);
        if (sequence == 0) throw new ArgumentException("Remote acknowledgement sequences start at one.", nameof(sequence));
        if (stateSequence == 0) throw new ArgumentException("Remote state sequences start at one.", nameof(stateSequence));

        ProtocolVersion = protocolVersion;
        Sequence = sequence;
        Status = status;
        StateSequence = stateSequence;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
    }

    public int ProtocolVersion { get; }
    public ulong Sequence { get; }
    public RemoteAckStatus Status { get; }
    public ulong StateSequence { get; }
    public string? ErrorCode { get; }
    public string? ErrorMessage { get; }
}

public sealed class RemoteStateSnapshot
{
    public RemoteStateSnapshot(int protocolVersion, ulong stateSequence, SceneMode sceneMode, OutputWindowId outputWindowId)
    {
        RemoteProtocol.ValidateVersion(protocolVersion);
        if (stateSequence == 0) throw new ArgumentException("Remote state sequences start at one.", nameof(stateSequence));

        ProtocolVersion = protocolVersion;
        StateSequence = stateSequence;
        SceneMode = sceneMode;
        OutputWindowId = outputWindowId;
    }

    public int ProtocolVersion { get; }
    public ulong StateSequence { get; }
    public SceneMode SceneMode { get; }
    public OutputWindowId OutputWindowId { get; }
}

public interface IRemoteCommandEndpoint
{
    ValueTask<RemoteCommandAck> SubmitAsync(RemoteCommandEnvelope command, CancellationToken cancellationToken = default);
}

public interface IRemoteStateSnapshotSource
{
    ValueTask<RemoteStateSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
}
