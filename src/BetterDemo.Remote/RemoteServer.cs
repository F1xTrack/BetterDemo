using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using BetterDemo.Core.Contracts;
using BetterDemo.Remote.Pairing;
using BetterDemo.Remote.Protocol;

namespace BetterDemo.Remote;

/// <summary>
/// Small TLS-only HTTP endpoint bound to one explicitly selected private LAN address.
/// It supports manual pairing, authenticated commands, and self-revocation.
/// </summary>
public sealed class RemoteServer : IAsyncDisposable
{
    private const int MaximumHeaderBytes = 8 * 1024;
    private const int MaximumBodyBytes = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly TcpListener listener;
    private readonly X509Certificate2 certificate;
    private readonly RemoteCommandProcessor processor;
    private readonly PairingService pairing;
    private readonly RemotePreviewFrameStore? previewFrames;
    private readonly CancellationTokenSource lifetime = new();
    private Task? acceptLoop;
    private int started;

    public RemoteServer(
        IPAddress bindAddress,
        int port,
        PairingService pairing,
        RemoteCommandProcessor processor,
        RemotePreviewFrameStore? previewFrames = null)
    {
        ArgumentNullException.ThrowIfNull(bindAddress);
        this.pairing = pairing ?? throw new ArgumentNullException(nameof(pairing));
        this.processor = processor ?? throw new ArgumentNullException(nameof(processor));
        this.previewFrames = previewFrames;
        if (!IsPrivateLanAddress(bindAddress))
        {
            throw new ArgumentException("The remote server can bind only to a private LAN or loopback address.", nameof(bindAddress));
        }
        if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));

        listener = new TcpListener(bindAddress, port);
        certificate = CreateCertificate(bindAddress);
        CertificateSha256 = Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256));
        PairingCode = pairing.IssueCode();
    }

    public IPAddress BindAddress => ((IPEndPoint)listener.LocalEndpoint).Address;
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public string CertificateSha256 { get; }
    public PairingCodeIssue PairingCode { get; private set; }
    public bool IsRunning => Volatile.Read(ref started) != 0 && !lifetime.IsCancellationRequested;

    public string BaseAddress
    {
        get
        {
            if (!IsRunning) throw new InvalidOperationException("Start the remote server before reading its address.");
            return $"https://{BindAddress}:{Port}/v1/";
        }
    }

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref started, 1, 0) != 0)
        {
            throw new InvalidOperationException("The remote server has already been started.");
        }

        try
        {
            listener.Start(8);
            acceptLoop = AcceptLoopAsync(lifetime.Token);
            return ValueTask.CompletedTask;
        }
        catch
        {
            Interlocked.Exchange(ref started, 0);
            listener.Stop();
            throw;
        }
    }

    public PairingCodeIssue RenewPairingCode()
    {
        ObjectDisposedException.ThrowIf(lifetime.IsCancellationRequested, this);
        PairingCode = pairing.IssueCode();
        return PairingCode;
    }

    public async ValueTask StopAsync()
    {
        if (!lifetime.IsCancellationRequested)
        {
            lifetime.Cancel();
            listener.Stop();
        }
        if (acceptLoop is not null)
        {
            try { await acceptLoop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        Interlocked.Exchange(ref started, 0);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        certificate.Dispose();
        lifetime.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            using (client)
            {
                try { await HandleClientAsync(client, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (IOException) { }
                catch (AuthenticationException) { }
                catch (JsonException) { }
                catch (SocketException) { }
                catch (Exception) { }
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        if (client.Client.RemoteEndPoint is not IPEndPoint remote || !IsPrivateLanAddress(remote.Address))
        {
            return;
        }

        await using var stream = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
        await stream.AuthenticateAsServerAsync(
            new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            },
            cancellationToken).ConfigureAwait(false);

        HttpRequest request;
        try
        {
            request = await ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            await WriteResponseAsync(stream, JsonResponse(400, new { error = "malformedRequest" }), cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        ServerResponse response;
        try
        {
            response = await DispatchAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            response = JsonResponse(400, new { error = "malformedJson" });
        }
        await WriteResponseAsync(stream, response, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ServerResponse> DispatchAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.Method == "POST" && request.Path == "/v1/pair")
        {
            using var document = JsonDocument.Parse(request.Body);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("code", out var code) ||
                code.ValueKind != JsonValueKind.String)
            {
                return JsonResponse(400, new { error = "malformedPairingRequest" });
            }

            var tokenIssue = pairing.RedeemCode(code.GetString());
            return tokenIssue is null
                ? JsonResponse(401, new { error = "invalidOrExpiredCode" })
                : JsonResponse(200, new { token = tokenIssue.Token, expiresAt = tokenIssue.ExpiresAt });
        }

        if (request.Method == "POST" && request.Path == "/v1/command")
        {
            if (!RemoteEnvelopeCodec.TryDecode(request.Body, out var envelope, out _ ) || envelope is null)
            {
                return JsonResponse(400, new { error = RemoteErrorCodes.MalformedEnvelope });
            }

            var result = await processor.ProcessAsync(envelope, cancellationToken).ConfigureAwait(false);
            return JsonResponse(200, RemoteResponseWire.From(result));
        }

        if (request.Method == "POST" && request.Path == "/v1/preview")
        {
            using var document = JsonDocument.Parse(request.Body);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("token", out var token) ||
                token.ValueKind != JsonValueKind.String)
            {
                return JsonResponse(400, new { error = "malformedPreviewRequest" });
            }

            if (!pairing.TryExecuteAuthenticated(
                    token.GetString(),
                    beforeExecution: null,
                    operation: _ => previewFrames is not null && previewFrames.TryGetLatest(out var latest)
                        ? latest
                        : null,
                    out var frame))
                return JsonResponse(401, new { error = "unauthorized" });

            if (frame is null)
                return new ServerResponse(204, [], ContentType: null);

            return new ServerResponse(200, frame.JpegBytes.ToArray(), "image/jpeg", frame.Sequence);
        }

        if (request.Method == "POST" && request.Path == "/v1/revoke")
        {
            using var document = JsonDocument.Parse(request.Body);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("token", out var token) ||
                token.ValueKind != JsonValueKind.String)
            {
                return JsonResponse(400, new { error = "malformedRevokeRequest" });
            }

            return pairing.RevokeToken(token.GetString())
                ? JsonResponse(200, new { revoked = true })
                : JsonResponse(401, new { revoked = false });
        }

        return JsonResponse(404, new { error = "notFound" });
    }

    private static async ValueTask<HttpRequest> ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var received = new MemoryStream();
        var buffer = new byte[4096];
        var headerEnd = -1;
        while (headerEnd < 0)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) throw new IOException("The client closed before sending an HTTP request.");
            received.Write(buffer, 0, count);
            if (received.Length > MaximumHeaderBytes + MaximumBodyBytes) throw new IOException("The request exceeds the size limit.");
            headerEnd = FindHeaderEnd(received.GetBuffer().AsSpan(0, checked((int)received.Length)));
            if (headerEnd < 0 && received.Length > MaximumHeaderBytes) throw new IOException("The request headers exceed the size limit.");
        }

        var bytes = received.GetBuffer().AsSpan(0, checked((int)received.Length));
        var headerText = Encoding.ASCII.GetString(bytes[..headerEnd]);
        var lines = headerText.Split("\r\n", StringSplitOptions.None);
        var requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length != 3 || !requestLine[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
        {
            throw new IOException("The HTTP request line is invalid.");
        }

        var contentLength = 0;
        foreach (var line in lines.Skip(1))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0) continue;
            var name = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Chunked request bodies are not supported.");
            }
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
                (!int.TryParse(value, out contentLength) || contentLength < 0 || contentLength > MaximumBodyBytes))
            {
                throw new IOException("The request body exceeds the size limit.");
            }
        }

        var bodyOffset = headerEnd + 4;
        while (received.Length - bodyOffset < contentLength)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) throw new IOException("The client closed before sending the complete request body.");
            received.Write(buffer, 0, count);
            if (received.Length - bodyOffset > contentLength) throw new IOException("The HTTP request contains excess body bytes.");
        }

        var body = received.GetBuffer().AsSpan(bodyOffset, contentLength).ToArray();
        var path = requestLine[1].Split('?', 2)[0];
        return new HttpRequest(requestLine[0], path, body);
    }

    private static async ValueTask WriteResponseAsync(Stream stream, ServerResponse response, CancellationToken cancellationToken)
    {
        var reason = response.StatusCode switch
        {
            200 => "OK",
            204 => "No Content",
            400 => "Bad Request",
            401 => "Unauthorized",
            404 => "Not Found",
            _ => "Error"
        };
        var headers = new StringBuilder()
            .Append($"HTTP/1.1 {response.StatusCode} {reason}\r\n");
        if (response.ContentType is not null) headers.Append($"Content-Type: {response.ContentType}\r\n");
        headers.Append($"Content-Length: {response.Body.Length}\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("Connection: close\r\n");
        if (response.PreviewSequence is { } sequence)
            headers.Append($"X-Preview-Sequence: {sequence}\r\n");
        headers.Append("\r\n");
        var header = Encoding.ASCII.GetBytes(headers.ToString());
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (response.Body.Length > 0)
            await stream.WriteAsync(response.Body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ServerResponse JsonResponse(int statusCode, object value) =>
        new(statusCode, JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions));

    private static int FindHeaderEnd(ReadOnlySpan<byte> bytes)
    {
        for (var index = 0; index <= bytes.Length - 4; index++)
        {
            if (bytes[index] == '\r' && bytes[index + 1] == '\n' && bytes[index + 2] == '\r' && bytes[index + 3] == '\n')
            {
                return index;
            }
        }
        return -1;
    }

    private static X509Certificate2 CreateCertificate(IPAddress address)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=BetterDemo LAN Remote",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
        var subjectAlternativeNames = new SubjectAlternativeNameBuilder();
        subjectAlternativeNames.AddIpAddress(address);
        request.CertificateExtensions.Add(subjectAlternativeNames.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(365));
        var exported = generated.Export(X509ContentType.Pfx, string.Empty);
        try
        {
            return new X509Certificate2(
                exported,
                string.Empty,
                X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(exported);
        }
    }

    private static bool IsPrivateLanAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10 ||
                   (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 168);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            return (bytes[0] & 0xFE) == 0xFC || address.IsIPv6LinkLocal;
        }

        return false;
    }

    private sealed record HttpRequest(string Method, string Path, byte[] Body);
    private sealed record ServerResponse(
        int StatusCode,
        byte[] Body,
        string? ContentType = "application/json; charset=utf-8",
        long? PreviewSequence = null);

    private sealed record RemoteResponseWire(RemoteAckWire Ack, RemoteSnapshotWire Snapshot)
    {
        public static RemoteResponseWire From(RemoteCommandResponse response)
        {
            var snapshot = response.Snapshot;
            return new RemoteResponseWire(
                new RemoteAckWire(
                    response.Ack.ProtocolVersion,
                    response.Ack.Sequence,
                    response.Ack.Status.ToString(),
                    response.Ack.StateSequence,
                    response.Ack.ErrorCode,
                    response.Ack.ErrorMessage),
                new RemoteSnapshotWire(
                    snapshot.ProtocolVersion,
                    snapshot.StateSequence,
                    SceneModeWireNames.ToWireName(snapshot.Mode),
                    snapshot.OutputWindowId.Value,
                    snapshot.CameraCorner,
                    snapshot.Zoom,
                    snapshot.Pan,
                    snapshot.Blur,
                    snapshot.LayerVisibility,
                    snapshot.OutputVisible));
        }
    }

    private sealed record RemoteAckWire(
        int ProtocolVersion,
        ulong Sequence,
        string Status,
        ulong StateSequence,
        string? ErrorCode,
        string? ErrorMessage);

    private sealed record RemoteSnapshotWire(
        int ProtocolVersion,
        ulong StateSequence,
        string Mode,
        string OutputWindowId,
        CameraCornerState CameraCorner,
        double Zoom,
        PanState Pan,
        BlurState Blur,
        IReadOnlyDictionary<string, bool> LayerVisibility,
        bool OutputVisible);
}
