namespace NewHorizon.Automation.ErpClient.Authentication;

/// <summary>
/// A cached ERP login token, the moment it stops being usable, and who the agent signed in as.
/// </summary>
/// <param name="UserId">
/// The ERP user id the login resolved to (<c>data.uid</c>). Purchase documents are stamped with it
/// as their creator, so it travels with the token rather than being configured separately — a token
/// and the identity it represents cannot be allowed to drift apart. Defaulted, because the tests
/// that exercise token lifetime have no interest in it.
/// </param>
public sealed record ErpToken(string AccessToken, DateTimeOffset ExpiresAtUtc, int UserId = 0)
{
    /// <summary>
    /// How far ahead of real expiry the token is treated as stale. A token that expires while
    /// a request is in flight would surface as a spurious 401 mid-operation, so it is replaced
    /// before it can happen.
    /// </summary>
    public static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(2);

    public bool IsUsableAt(DateTimeOffset nowUtc) => nowUtc < ExpiresAtUtc - RefreshMargin;
}
