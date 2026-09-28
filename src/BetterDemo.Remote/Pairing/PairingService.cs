using System.Security.Cryptography;
using System.Text;

namespace BetterDemo.Remote.Pairing;

public sealed class PairingCodeIssue
{
    public PairingCodeIssue(string code, DateTimeOffset expiresAt)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("A pairing code is required.", nameof(code));
        Code = code;
        ExpiresAt = expiresAt;
    }

    public string Code { get; }
    public DateTimeOffset ExpiresAt { get; }

    public override string ToString() => "Pairing code issued (value redacted).";
}

public sealed class PairingTokenIssue
{
    public PairingTokenIssue(string token, DateTimeOffset expiresAt)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("A pairing token is required.", nameof(token));
        Token = token;
        ExpiresAt = expiresAt;
    }

    public string Token { get; }
    public DateTimeOffset ExpiresAt { get; }

    public override string ToString() => "Pairing token issued (value redacted).";
}

public sealed class PairingService
{
    private const int PairingCodeUpperBound = 1_000_000;
    private readonly object gate = new();
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan codeLifetime;
    private readonly TimeSpan tokenLifetime;
    private readonly List<PairingSession> sessions = new();
    private PairingCodeRecord? currentCode;

    public PairingService(
        TimeProvider? timeProvider = null,
        TimeSpan? codeLifetime = null,
        TimeSpan? tokenLifetime = null)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.codeLifetime = codeLifetime ?? TimeSpan.FromMinutes(5);
        this.tokenLifetime = tokenLifetime ?? TimeSpan.FromDays(30);
        if (this.codeLifetime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(codeLifetime));
        if (this.tokenLifetime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(tokenLifetime));
    }

    public PairingCodeIssue IssueCode()
    {
        lock (gate)
        {
            var code = RandomNumberGenerator.GetInt32(PairingCodeUpperBound).ToString("D6");
            var expiresAt = timeProvider.GetUtcNow().Add(codeLifetime);
            currentCode = new PairingCodeRecord(HashSecret(code), expiresAt);
            return new PairingCodeIssue(code, expiresAt);
        }
    }

    public PairingTokenIssue? RedeemCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;

        lock (gate)
        {
            var pairingCode = currentCode;
            if (pairingCode is null)
            {
                return null;
            }

            var codeMatches = CryptographicOperations.FixedTimeEquals(
                pairingCode.SecretHash,
                HashSecret(code));
            if (pairingCode.ExpiresAt <= timeProvider.GetUtcNow())
            {
                currentCode = null;
                return null;
            }

            if (!codeMatches)
            {
                return null;
            }

            currentCode = null;
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var expiresAt = timeProvider.GetUtcNow().Add(tokenLifetime);
            sessions.Add(new PairingSession(HashSecret(token), expiresAt));
            return new PairingTokenIssue(token, expiresAt);
        }
    }

    public bool IsTokenActive(string? token) => TryGetSession(token, out _);

    public bool RevokeToken(string? token)
    {
        lock (gate)
        {
            var session = FindSessionLocked(token);
            if (session is null || !IsActiveLocked(session)) return false;
            session.Revoked = true;
            return true;
        }
    }

    internal bool TryGetSession(string? token, out PairingSession? matchingSession)
    {
        matchingSession = null;
        if (string.IsNullOrWhiteSpace(token)) return false;

        lock (gate)
        {
            matchingSession = FindSessionLocked(token);
            if (matchingSession is null || !IsActiveLocked(matchingSession))
            {
                matchingSession = null;
                return false;
            }

            return true;
        }
    }

    internal bool TryExecuteAuthenticated<T>(
        string? token,
        Action? beforeExecution,
        Func<PairingSession, T> operation,
        out T result)
    {
        result = default!;
        if (string.IsNullOrWhiteSpace(token)) return false;

        lock (gate)
        {
            var session = FindSessionLocked(token);
            if (session is null || !IsActiveLocked(session)) return false;

            beforeExecution?.Invoke();
            if (!IsActiveLocked(session)) return false;

            result = operation(session);
            return true;
        }
    }

    private PairingSession? FindSessionLocked(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        var candidateHash = HashSecret(token);
        PairingSession? matchingSession = null;
        foreach (var session in sessions)
        {
            var matches = CryptographicOperations.FixedTimeEquals(session.SecretHash, candidateHash);
            if (matches && matchingSession is null)
            {
                matchingSession = session;
            }
        }

        return matchingSession;
    }

    private bool IsActiveLocked(PairingSession session) =>
        !session.Revoked && session.ExpiresAt > timeProvider.GetUtcNow();

    private static byte[] HashSecret(string secret) => SHA256.HashData(Encoding.UTF8.GetBytes(secret));

    private sealed record PairingCodeRecord(byte[] SecretHash, DateTimeOffset ExpiresAt);
}

internal sealed class PairingSession
{
    internal PairingSession(byte[] secretHash, DateTimeOffset expiresAt)
    {
        SecretHash = secretHash;
        ExpiresAt = expiresAt;
    }

    internal byte[] SecretHash { get; }
    internal DateTimeOffset ExpiresAt { get; }
    internal bool Revoked { get; set; }
    internal ulong LastSequence { get; set; }
    internal Dictionary<string, byte[]> IdempotencyFingerprints { get; } = new(StringComparer.Ordinal);
}
