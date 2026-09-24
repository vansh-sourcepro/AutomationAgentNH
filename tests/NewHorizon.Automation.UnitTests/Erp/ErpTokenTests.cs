using FluentAssertions;
using NewHorizon.Automation.ErpClient.Authentication;

namespace NewHorizon.Automation.UnitTests.Erp;

public sealed class ErpTokenTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 27, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_fresh_token_is_usable()
    {
        var token = new ErpToken("t", Now.AddMinutes(60));

        token.IsUsableAt(Now).Should().BeTrue();
    }

    [Fact]
    public void A_token_inside_the_refresh_margin_is_already_treated_as_stale()
    {
        // Replaced before real expiry: a token that lapses while a request is in flight would
        // surface as a spurious 401 in the middle of an operation.
        var token = new ErpToken("t", Now.AddSeconds(90));

        token.IsUsableAt(Now).Should().BeFalse();
    }

    [Fact]
    public void The_margin_boundary_is_exact()
    {
        var token = new ErpToken("t", Now + ErpToken.RefreshMargin + TimeSpan.FromSeconds(1));
        token.IsUsableAt(Now).Should().BeTrue();

        var atBoundary = new ErpToken("t", Now + ErpToken.RefreshMargin);
        atBoundary.IsUsableAt(Now).Should().BeFalse();
    }

    [Fact]
    public void An_expired_token_is_not_usable()
    {
        var token = new ErpToken("t", Now.AddMinutes(-1));

        token.IsUsableAt(Now).Should().BeFalse();
    }
}
