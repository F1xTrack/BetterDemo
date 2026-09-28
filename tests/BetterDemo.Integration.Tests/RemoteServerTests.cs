using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using BetterDemo.Core.Contracts;
using BetterDemo.Remote;
using BetterDemo.Remote.Pairing;
using BetterDemo.Remote.Protocol;
using Xunit;

namespace BetterDemo.Integration.Tests;

public sealed class RemoteServerTests
{
    [Fact]
    public async Task Preview_requests_require_an_active_pair_and_return_the_latest_jpeg()
    {
        var pairing = new PairingService();
        var previews = new RemotePreviewFrameStore();
        var processor = new RemoteCommandProcessor(pairing, new RemoteStateStore());
        await using var server = new RemoteServer(IPAddress.Loopback, 0, pairing, processor, previews);
        await server.StartAsync();

        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
                certificate is not null && certificate.GetCertHashString(HashAlgorithmName.SHA256) == server.CertificateSha256 &&
                (errors & System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch) == 0
        };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(server.BaseAddress) };
        var pairResponse = await PostJsonAsync(client, "pair", new { code = server.PairingCode.Code });
        using var pairDocument = JsonDocument.Parse(await pairResponse.Content.ReadAsByteArrayAsync());
        var token = pairDocument.RootElement.GetProperty("token").GetString();

        var unavailable = await PostJsonAsync(client, "preview", new { token });
        Assert.Equal(HttpStatusCode.NoContent, unavailable.StatusCode);

        var unauthorized = await PostJsonAsync(client, "preview", new { token = "not-a-valid-pairing-token" });
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        byte[] jpeg = [0xFF, 0xD8, 0x10, 0x20, 0xFF, 0xD9];
        Assert.Equal(1, previews.Publish(jpeg));
        var previewResponse = await PostJsonAsync(client, "preview", new { token });
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        Assert.Equal("image/jpeg", previewResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal("1", Assert.Single(previewResponse.Headers.GetValues("X-Preview-Sequence")));
        Assert.Equal(jpeg, await previewResponse.Content.ReadAsByteArrayAsync());

        var revoked = await PostJsonAsync(client, "revoke", new { token });
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        var afterRevoke = await PostJsonAsync(client, "preview", new { token });
        Assert.Equal(HttpStatusCode.Unauthorized, afterRevoke.StatusCode);
    }

    [Fact]
    public async Task Tls_server_pairs_once_runs_authenticated_commands_and_revokes_tokens()
    {
        var pairing = new PairingService();
        var processor = new RemoteCommandProcessor(pairing, new RemoteStateStore(new OutputWindowId("remote-test-output")));
        await using var server = new RemoteServer(IPAddress.Loopback, 0, pairing, processor);
        await server.StartAsync();
        string? presentedFingerprint = null;

        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
            {
                if (certificate is null) return false;
                presentedFingerprint = certificate.GetCertHashString(HashAlgorithmName.SHA256);
                return presentedFingerprint == server.CertificateSha256 &&
                       (errors & System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch) == 0;
            }
        };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(server.BaseAddress) };

        using var chunkedPairRequest = await client.PostAsync("pair", JsonContent.Create(new { code = server.PairingCode.Code }));
        Assert.Equal(HttpStatusCode.BadRequest, chunkedPairRequest.StatusCode);

        var pairResponse = await PostJsonAsync(client, "pair", new { code = server.PairingCode.Code });
        Assert.Equal(server.CertificateSha256, presentedFingerprint);
        Assert.Equal(HttpStatusCode.OK, pairResponse.StatusCode);
        using var tokenDocument = JsonDocument.Parse(await pairResponse.Content.ReadAsByteArrayAsync());
        var token = tokenDocument.RootElement.GetProperty("token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));

        var replayedPairResponse = await PostJsonAsync(client, "pair", new { code = server.PairingCode.Code });
        Assert.Equal(HttpStatusCode.Unauthorized, replayedPairResponse.StatusCode);

        var command = new
        {
            protocolVersion = RemoteProtocol.CurrentVersion,
            sequence = 1,
            command = RemoteCommandNames.SetMode,
            token,
            idempotencyKey = "remote-server-test-1",
            payload = new { mode = "screenPlusPhysicalCameraCorner" }
        };
        var commandResponse = await PostJsonAsync(client, "command", command);
        Assert.Equal(HttpStatusCode.OK, commandResponse.StatusCode);
        using var result = JsonDocument.Parse(await commandResponse.Content.ReadAsByteArrayAsync());
        Assert.Equal("Accepted", result.RootElement.GetProperty("ack").GetProperty("status").GetString());
        Assert.Equal(
            "screenPlusPhysicalCameraCorner",
            result.RootElement.GetProperty("snapshot").GetProperty("mode").GetString());
        Assert.Equal(
            "remote-test-output",
            result.RootElement.GetProperty("snapshot").GetProperty("outputWindowId").GetString());

        var repeatedCommandResponse = await PostJsonAsync(client, "command", command);
        using (var repeatedResult = JsonDocument.Parse(await repeatedCommandResponse.Content.ReadAsByteArrayAsync()))
        {
            Assert.Equal("Duplicate", repeatedResult.RootElement.GetProperty("ack").GetProperty("status").GetString());
            Assert.Equal(
                result.RootElement.GetProperty("snapshot").GetProperty("stateSequence").GetUInt64(),
                repeatedResult.RootElement.GetProperty("snapshot").GetProperty("stateSequence").GetUInt64());
        }

        var transformCommand = new
        {
            protocolVersion = RemoteProtocol.CurrentVersion,
            sequence = 2,
            command = RemoteCommandNames.SetTransform,
            token,
            idempotencyKey = "remote-server-test-transform",
            payload = new { zoom = 2.5, panX = 0.75, panY = -0.25 }
        };
        var transformResponse = await PostJsonAsync(client, "command", transformCommand);
        Assert.Equal(HttpStatusCode.OK, transformResponse.StatusCode);
        using (var transformResult = JsonDocument.Parse(await transformResponse.Content.ReadAsByteArrayAsync()))
        {
            var transformSnapshot = transformResult.RootElement.GetProperty("snapshot");
            Assert.Equal("Accepted", transformResult.RootElement.GetProperty("ack").GetProperty("status").GetString());
            Assert.Equal(2.5, transformSnapshot.GetProperty("zoom").GetDouble());
            Assert.Equal(0.75, transformSnapshot.GetProperty("pan").GetProperty("x").GetDouble());
            Assert.Equal(-0.25, transformSnapshot.GetProperty("pan").GetProperty("y").GetDouble());
        }

        var revokeResponse = await PostJsonAsync(client, "revoke", new { token });
        Assert.Equal(HttpStatusCode.OK, revokeResponse.StatusCode);
        var deniedCommandResponse = await PostJsonAsync(client, "command", new
        {
            protocolVersion = RemoteProtocol.CurrentVersion,
            sequence = 3,
            command = RemoteCommandNames.SetMode,
            token,
            idempotencyKey = "remote-server-test-2",
            payload = new { mode = "black" }
        });
        using var deniedResult = JsonDocument.Parse(await deniedCommandResponse.Content.ReadAsByteArrayAsync());
        Assert.Equal("Rejected", deniedResult.RootElement.GetProperty("ack").GetProperty("status").GetString());
        Assert.Equal("unauthorized", deniedResult.RootElement.GetProperty("ack").GetProperty("errorCode").GetString());
    }

    [Fact]
    public void Server_rejects_addresses_outside_private_lan_ranges()
    {
        var pairing = new PairingService();
        var processor = new RemoteCommandProcessor(pairing, new RemoteStateStore());
        Assert.Throws<ArgumentException>(() => new RemoteServer(IPAddress.Parse("8.8.8.8"), 47829, pairing, processor));
    }

    private static Task<HttpResponseMessage> PostJsonAsync<T>(HttpClient client, string relativePath, T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return client.PostAsync(relativePath, content);
    }
}
