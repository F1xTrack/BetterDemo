using System.Text.Json;
using BetterDemo.Core.Contracts;

namespace BetterDemo.Remote.Protocol;

public static class RemoteEnvelopeCodec
{
    public static bool TryDecode(
        ReadOnlyMemory<byte> wireMessage,
        out RemoteCommandEnvelope? envelope,
        out string errorCode)
    {
        envelope = null;
        errorCode = RemoteErrorCodes.MalformedEnvelope;
        try
        {
            using var document = JsonDocument.Parse(wireMessage);
            var root = document.RootElement;
            var fieldNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.ValueKind == JsonValueKind.Object
                         ? root.EnumerateObject()
                         : Enumerable.Empty<JsonProperty>())
            {
                if (!fieldNames.Add(property.Name) || property.Name is not
                    ("protocolVersion" or "sequence" or "command" or "token" or "idempotencyKey" or "payload"))
                {
                    return false;
                }
            }

            if (root.ValueKind != JsonValueKind.Object ||
                fieldNames.Count != 6 ||
                !root.TryGetProperty("protocolVersion", out var version) ||
                !root.TryGetProperty("sequence", out var sequence) ||
                !root.TryGetProperty("command", out var command) ||
                !root.TryGetProperty("token", out var token) ||
                !root.TryGetProperty("idempotencyKey", out var idempotencyKey) ||
                !root.TryGetProperty("payload", out var payload) ||
                version.ValueKind != JsonValueKind.Number ||
                sequence.ValueKind != JsonValueKind.Number ||
                command.ValueKind != JsonValueKind.String ||
                token.ValueKind != JsonValueKind.String ||
                idempotencyKey.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var protocolVersion = version.GetInt32();
            if (protocolVersion != RemoteProtocol.CurrentVersion) return false;
            var commandSequence = sequence.GetUInt64();
            if (commandSequence == 0) return false;
            var commandName = command.GetString()!;
            if (!RemoteCommandNames.IsKnown(commandName)) return false;
            var tokenValue = token.GetString()!;
            var keyValue = idempotencyKey.GetString()!;
            if (string.IsNullOrWhiteSpace(tokenValue) || string.IsNullOrWhiteSpace(keyValue)) return false;

            envelope = new RemoteCommandEnvelope(
                protocolVersion,
                commandSequence,
                commandName,
                JsonSerializer.SerializeToUtf8Bytes(payload),
                tokenValue,
                keyValue);
            errorCode = string.Empty;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
    }
}
