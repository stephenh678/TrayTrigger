using System.Net;

namespace TrayTrigger.Services;

/// <summary>What a service said about an API key the user pasted - the Check result shown under
/// the SteamGridDB and RAWG key boxes in Settings and the Welcome dialog.</summary>
public enum ApiKeyCheckResult
{
    /// <summary>The service answered the request the key was sent with.</summary>
    Valid,
    /// <summary>The service refused the key (HTTP 401 / 403): mistyped, cut short, or revoked.</summary>
    Rejected,
    /// <summary>No verdict on the key: offline, a timeout, a rate limit or an outage.</summary>
    Unreachable
}

internal static class ApiKeyCheck
{
    /// <summary>
    /// Maps the status of a one-request key check. Only 401 and 403 say the key itself is wrong;
    /// a 429 or a 5xx says nothing about it, so the key is left in place and reported unchecked.
    /// Both services were confirmed to answer a made-up key with 401.
    /// </summary>
    internal static ApiKeyCheckResult FromStatus(HttpStatusCode status) => (int)status switch
    {
        >= 200 and < 300 => ApiKeyCheckResult.Valid,
        401 or 403 => ApiKeyCheckResult.Rejected,
        _ => ApiKeyCheckResult.Unreachable
    };
}
