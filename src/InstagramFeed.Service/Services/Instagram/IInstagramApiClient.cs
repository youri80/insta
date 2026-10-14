namespace InstagramFeed.Service.Services.Instagram;

public enum ApiOutcome
{
    Success,

    /// <summary>Token abgelaufen/ungueltig (Graph-Code 190). Kein Retry, eigener Alarm.</summary>
    TokenExpired,

    Failed
}

public sealed record MediaFetchResult(ApiOutcome Outcome, IReadOnlyList<InstagramMediaNode> Items, string? Error);

public sealed record UsernameResult(ApiOutcome Outcome, string? Username, string? Error);

public sealed record TokenRefreshResult(ApiOutcome Outcome, string? AccessToken, TimeSpan? Lifetime, string? Error);

public interface IInstagramApiClient
{
    /// <summary>Holt die neuesten Beitraege (mit Paging bis maxPosts erreicht sind).</summary>
    Task<MediaFetchResult> GetLatestMediaAsync(string accessToken, int maxPosts, CancellationToken ct);

    Task<UsernameResult> GetUsernameAsync(string accessToken, CancellationToken ct);

    /// <summary>Verlaengert einen Long-Lived-Token um weitere ~60 Tage (nur fuer Tokens, die aelter als 24 h sind).</summary>
    Task<TokenRefreshResult> RefreshLongLivedTokenAsync(string accessToken, CancellationToken ct);

    /// <summary>Tauscht ein Short-Lived-Token (1 h) gegen ein Long-Lived-Token (60 Tage).</summary>
    Task<TokenRefreshResult> ExchangeForLongLivedTokenAsync(string shortLivedToken, string appSecret, CancellationToken ct);
}
