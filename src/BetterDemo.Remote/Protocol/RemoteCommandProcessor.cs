using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BetterDemo.Core.Contracts;
using BetterDemo.Remote.Pairing;

namespace BetterDemo.Remote.Protocol;

public static class RemoteErrorCodes
{
    public const string IdempotencyLedgerFull = "idempotencyLedgerFull";
    public const string Unauthorized = "unauthorized";
    public const string MalformedEnvelope = "malformedEnvelope";
    public const string MalformedPayload = "malformedPayload";
    public const string UnknownCommand = "unknownCommand";
    public const string StaleSequence = "staleSequence";
    public const string IdempotencyConflict = "idempotencyConflict";
}

public sealed class RemoteCommandResponse
{
    public RemoteCommandResponse(RemoteCommandAck ack, RemoteSceneSnapshot snapshot)
    {
        Ack = ack;
        Snapshot = snapshot;
    }

    public RemoteCommandAck Ack { get; }
    public RemoteSceneSnapshot Snapshot { get; }
}

public sealed class RemoteCommandProcessor
{
    private const int MaxIdempotencyEntries = 4096;
    private readonly PairingService pairing;
    private readonly RemoteStateStore state;
    private readonly Action? beforeAuthentication;
    private readonly Action? beforeExecution;

    public RemoteCommandProcessor(PairingService pairing, RemoteStateStore state)
        : this(pairing, state, null, null)
    {
    }

    internal RemoteCommandProcessor(
        PairingService pairing,
        RemoteStateStore state,
        Action? beforeAuthentication,
        Action? beforeExecution)
    {
        this.pairing = pairing ?? throw new ArgumentNullException(nameof(pairing));
        this.state = state ?? throw new ArgumentNullException(nameof(state));
        this.beforeAuthentication = beforeAuthentication;
        this.beforeExecution = beforeExecution;
    }

    public PairingService Pairing => pairing;
    public RemoteStateStore State => state;

    public ValueTask<RemoteCommandResponse> ProcessAsync(
        RemoteCommandEnvelope command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Process(command));
    }

    public RemoteCommandResponse Process(RemoteCommandEnvelope command)
    {
        beforeAuthentication?.Invoke();
        if (!pairing.TryExecuteAuthenticated(
                command.AuthenticationToken,
                beforeExecution,
                session => ProcessAuthorized(command, session),
                out RemoteCommandResponse? response) || response is null)
        {
            return Response(command, RemoteAckStatus.Rejected, RemoteErrorCodes.Unauthorized, "Authentication failed.");
        }

        return response;
    }

    private RemoteCommandResponse ProcessAuthorized(RemoteCommandEnvelope command, PairingSession session)
    {
        if (string.IsNullOrWhiteSpace(command.IdempotencyKey))
        {
            return Response(command, RemoteAckStatus.Rejected, RemoteErrorCodes.MalformedEnvelope, "An idempotency key is required.");
        }

        if (!RemoteCommandNames.IsKnown(command.CommandName))
        {
            return Response(command, RemoteAckStatus.Rejected, RemoteErrorCodes.UnknownCommand, "The command is not supported.");
        }

        var fingerprint = Fingerprint(command);
        if (session.IdempotencyFingerprints.TryGetValue(command.IdempotencyKey, out var priorFingerprint))
        {
            if (!CryptographicOperations.FixedTimeEquals(priorFingerprint, fingerprint))
            {
                return Response(command, RemoteAckStatus.Rejected, RemoteErrorCodes.IdempotencyConflict, "The idempotency key was reused.");
            }

            return Response(command, RemoteAckStatus.Duplicate, null, null);
        }

        if (session.LastSequence == ulong.MaxValue || command.Sequence != session.LastSequence + 1)
        {
            return Response(command, RemoteAckStatus.Stale, RemoteErrorCodes.StaleSequence, "The command sequence is not the next expected value.");
        }

        if (session.IdempotencyFingerprints.Count >= MaxIdempotencyEntries)
        {
            return Response(command, RemoteAckStatus.Rejected, RemoteErrorCodes.IdempotencyLedgerFull, "The idempotency ledger is full; reconnect pairing is required.");
        }

        try
        {
            var snapshot = Apply(command);
            session.LastSequence = command.Sequence;
            session.IdempotencyFingerprints.Add(command.IdempotencyKey, fingerprint);
            return new RemoteCommandResponse(
                new RemoteCommandAck(RemoteProtocol.CurrentVersion, command.Sequence, RemoteAckStatus.Accepted, snapshot.StateSequence),
                snapshot);
        }
        catch (JsonException)
        {
            return Response(command, RemoteAckStatus.Rejected, RemoteErrorCodes.MalformedPayload, "The command payload is malformed.");
        }
        catch (FormatException)
        {
            return Response(command, RemoteAckStatus.Rejected, RemoteErrorCodes.MalformedPayload, "The command payload is malformed.");
        }
        catch (ArgumentException)
        {
            return Response(command, RemoteAckStatus.Rejected, RemoteErrorCodes.MalformedPayload, "The command payload is invalid.");
        }
    }

    private RemoteSceneSnapshot Apply(RemoteCommandEnvelope command)
    {
        using var document = JsonDocument.Parse(command.Payload.IsEmpty ? "{}" : Encoding.UTF8.GetString(command.Payload.Span));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException();

        return command.CommandName switch
        {
            RemoteCommandNames.SetMode => SetMode(root),
            RemoteCommandNames.SetCameraCorner => SetCameraCorner(root),
            RemoteCommandNames.SetZoom => SetZoom(root),
            RemoteCommandNames.SetTransform => SetTransform(root),
            RemoteCommandNames.PanBy => PanBy(root),
            RemoteCommandNames.ResetTransform => ResetTransform(root),
            RemoteCommandNames.SetBlur => SetBlur(root),
            RemoteCommandNames.SetLayerVisibility => SetLayerVisibility(root),
            RemoteCommandNames.SetOutputVisibility => SetOutputVisibility(root),
            RemoteCommandNames.GetState or RemoteCommandNames.Ping => GetState(root),
            _ => throw new FormatException("Unknown remote command.")
        };
    }

    private RemoteSceneSnapshot SetMode(JsonElement root)
    {
        EnsureProperties(root, "mode");
        return state.SetMode(SceneModeWireNames.Parse(RequiredString(root, "mode")));
    }

    private RemoteSceneSnapshot SetCameraCorner(JsonElement root)
    {
        EnsureProperties(root, "angleDegrees", "scale", "margin");
        return state.SetCameraCorner(new CameraCornerState(
                RequiredFiniteNumber(root, "angleDegrees"),
                RequiredPositiveNumber(root, "scale"),
                RequiredNonNegativeNumber(root, "margin")));
    }

    private RemoteSceneSnapshot SetZoom(JsonElement root)
    {
        EnsureProperties(root, "zoom");
        return state.SetZoom(RequiredPositiveNumber(root, "zoom"));
    }

    private RemoteSceneSnapshot SetTransform(JsonElement root)
    {
        EnsureProperties(root, "zoom", "panX", "panY");
        return state.SetTransform(
            RequiredPositiveNumber(root, "zoom"),
            RequiredFiniteNumber(root, "panX"),
            RequiredFiniteNumber(root, "panY"));
    }

    private RemoteSceneSnapshot PanBy(JsonElement root)
    {
        EnsureProperties(root, "x", "y");
        return state.PanBy(new PanState(RequiredFiniteNumber(root, "x"), RequiredFiniteNumber(root, "y")));
    }

    private RemoteSceneSnapshot ResetTransform(JsonElement root)
    {
        EnsureProperties(root);
        return state.ResetTransform();
    }

    private RemoteSceneSnapshot SetBlur(JsonElement root)
    {
        EnsureProperties(root, "enabled", "radius");
        return state.SetBlur(new BlurState(
            RequiredBoolean(root, "enabled"), RequiredNonNegativeNumber(root, "radius")));
    }

    private RemoteSceneSnapshot SetLayerVisibility(JsonElement root)
    {
        EnsureProperties(root, "layerId", "visible");
        return state.SetLayerVisibility(RequiredString(root, "layerId"), RequiredBoolean(root, "visible"));
    }

    private RemoteSceneSnapshot SetOutputVisibility(JsonElement root)
    {
        EnsureProperties(root, "visible");
        return state.SetOutputVisibility(RequiredBoolean(root, "visible"));
    }

    private RemoteSceneSnapshot GetState(JsonElement root)
    {
        EnsureProperties(root);
        return state.Snapshot;
    }

    private RemoteCommandResponse Response(
        RemoteCommandEnvelope command,
        RemoteAckStatus status,
        string? errorCode,
        string? errorMessage)
    {
        var snapshot = state.Snapshot;
        return new RemoteCommandResponse(
            new RemoteCommandAck(RemoteProtocol.CurrentVersion, command.Sequence, status, snapshot.StateSequence, errorCode, errorMessage),
            snapshot);
    }

    private static byte[] Fingerprint(RemoteCommandEnvelope command)
    {
        var commandBytes = Encoding.UTF8.GetBytes(command.CommandName);
        var bytes = new byte[commandBytes.Length + command.Payload.Length];
        commandBytes.CopyTo(bytes, 0);
        command.Payload.Span.CopyTo(bytes.AsSpan(commandBytes.Length));
        return SHA256.HashData(bytes);
    }

    private static string RequiredString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new JsonException();
        }

        return value.GetString()!;
    }

    private static bool RequiredBoolean(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new JsonException();
        }

        return value.GetBoolean();
    }

    private static double RequiredFiniteNumber(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            throw new JsonException();
        }

        var number = value.GetDouble();
        if (!double.IsFinite(number)) throw new ArgumentOutOfRangeException(name);
        return number;
    }

    private static double RequiredPositiveNumber(JsonElement root, string name)
    {
        var number = RequiredFiniteNumber(root, name);
        if (number <= 0) throw new ArgumentOutOfRangeException(name);
        return number;
    }

    private static double RequiredNonNegativeNumber(JsonElement root, string name)
    {
        var number = RequiredFiniteNumber(root, name);
        if (number < 0) throw new ArgumentOutOfRangeException(name);
        return number;
    }

    private static void EnsureProperties(JsonElement root, params string[] allowedNames)
    {
        var allowed = new HashSet<string>(allowedNames, StringComparer.Ordinal);
        if (root.EnumerateObject().Any(property => !allowed.Contains(property.Name)))
        {
            throw new JsonException();
        }
    }
}
