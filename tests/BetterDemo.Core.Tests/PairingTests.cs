using BetterDemo.Remote.Pairing;
using Xunit;

namespace BetterDemo.Core.Tests;

public sealed class PairingTests
{
    [Fact]
    public void Pairing_code_is_six_digits_one_time_and_issues_random_token()
    {
        var service = new PairingService();
        var firstCode = service.IssueCode();
        var secondCode = service.IssueCode();

        Assert.Equal(6, firstCode.Code.Length);
        Assert.All(firstCode.Code, character => Assert.InRange(character, '0', '9'));
        Assert.Null(service.RedeemCode(firstCode.Code));

        var firstToken = service.RedeemCode(secondCode.Code);
        Assert.NotNull(firstToken);
        Assert.NotEmpty(firstToken.Token);
        Assert.Null(service.RedeemCode(secondCode.Code));

        var anotherCode = service.IssueCode();
        var anotherToken = service.RedeemCode(anotherCode.Code);
        Assert.NotNull(anotherToken);
        Assert.Equal(43, anotherToken.Token.Length);
    }

    [Fact]
    public void Pairing_code_and_token_expiry_and_revoke_are_enforced()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var service = new PairingService(
            clock,
            codeLifetime: TimeSpan.FromSeconds(10),
            tokenLifetime: TimeSpan.FromSeconds(20));

        var expiredCode = service.IssueCode();
        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.Null(service.RedeemCode(expiredCode.Code));

        var validCode = service.IssueCode();
        var token = service.RedeemCode(validCode.Code);
        Assert.NotNull(token);
        Assert.True(service.IsTokenActive(token.Token));

        Assert.True(service.RevokeToken(token.Token));
        Assert.False(service.IsTokenActive(token.Token));
        Assert.False(service.RevokeToken(token.Token));

        var secondCode = service.IssueCode();
        var expiringToken = service.RedeemCode(secondCode.Code);
        Assert.NotNull(expiringToken);
        clock.Advance(TimeSpan.FromSeconds(21));
        Assert.False(service.IsTokenActive(expiringToken.Token));
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset utcNow;

        public ManualTimeProvider(DateTimeOffset utcNow) => this.utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan duration) => utcNow += duration;
    }
}
