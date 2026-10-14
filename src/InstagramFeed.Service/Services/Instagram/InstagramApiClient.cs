using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using InstagramFeed.Service.Configuration;
using InstagramFeed.Service.Domain;

namespace InstagramFeed.Service.Services.Instagram;

public sealed class InstagramApiClient : IInstagramApiClient
{
    private const string MediaFields =
        "id,media_type,media_url,thumbnail_url,permalink,caption,timestamp,username," +
        "children{id,media_type,media_url,thumbnail_url}";

    private const int MaxPages = 10;
    private const int ApiPageSize = 80;

    private readonly HttpClient _http;
    private readonly InstagramOptions _options;
    private readonly ILogger<InstagramApiClient> _log;

    public InstagramApiClient(HttpClient http, IOptions<InstagramOptions> options, ILogger<InstagramApiClient> log)
    {
        _http = http;
        _options = options.Value;
        _log = log;
    }

    public async Task<MediaFetchResult> GetLatestMediaAsync(string accessToken, int maxPosts, CancellationToken ct)
    {
        var limit = Math.Clamp(maxPosts, 1, ApiPageSize);
        var url = $"{_options.Host}/{_options.ApiVersion}/{Uri.EscapeDataString(_options.UserId)}/media" +
                  $"?fields={Uri.EscapeDataString(MediaFields)}&limit={limit}&access_token={Uri.EscapeDataString(accessToken)}";

        var collected = new List<InstagramMediaNode>();

        for (var page = 0; page < MaxPages; page++)
        {
            HttpResponseMessage response;
            try
            {
                response = await _http.GetAsync(url, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // Kein Token, keine URL im Log - nur der Fehlertyp.
                _log.LogWarning(ex, "Instagram-API nicht erreichbar.");
                return new MediaFetchResult(ApiOutcome.Failed, Array.Empty<InstagramMediaNode>(), "Netzwerkfehler");
            }

            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                var error = TryReadError(body);
                if (error?.Code == 190)
                {
                    _log.LogWarning("Instagram-Token abgelaufen oder ungueltig (Code 190).");
                    return new MediaFetchResult(ApiOutcome.TokenExpired, Array.Empty<InstagramMediaNode>(), error.Message);
                }

                _log.LogWarning("Instagram-API antwortete {Status} (Code {Code}).", (int)response.StatusCode, error?.Code);
                return new MediaFetchResult(ApiOutcome.Failed, Array.Empty<InstagramMediaNode>(), error?.Message ?? $"HTTP {(int)response.StatusCode}");
            }

            InstagramMediaResponse? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<InstagramMediaResponse>(body, FeedJson.Options);
            }
            catch (JsonException ex)
            {
                _log.LogWarning(ex, "Antwort der Instagram-API war kein gueltiges JSON.");
                return new MediaFetchResult(ApiOutcome.Failed, Array.Empty<InstagramMediaNode>(), "Ungueltige Antwort");
            }

            if (parsed is null)
            {
                return new MediaFetchResult(ApiOutcome.Failed, Array.Empty<InstagramMediaNode>(), "Leere Antwort");
            }

            collected.AddRange(parsed.Data.Where(item => !string.IsNullOrWhiteSpace(item.Id)));

            if (collected.Count >= maxPosts)
            {
                break;
            }

            var next = SanitizeNextUrl(parsed.Paging?.Next);
            if (next is null)
            {
                break;
            }

            url = next;
        }

        var items = collected.Take(Math.Max(maxPosts, 1)).ToList();
        return new MediaFetchResult(ApiOutcome.Success, items, null);
    }

    public async Task<UsernameResult> GetUsernameAsync(string accessToken, CancellationToken ct)
    {
        try
        {
            var url = $"{_options.Host}/{_options.ApiVersion}/me?fields=id,username&access_token={Uri.EscapeDataString(accessToken)}";
            var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                return new UsernameResult(ApiOutcome.Failed, null, $"HTTP {(int)response.StatusCode}");
            }

            var parsed = JsonSerializer.Deserialize<InstagramUserResponse>(await response.Content.ReadAsStringAsync(ct), FeedJson.Options);
            return new UsernameResult(ApiOutcome.Success, parsed?.Username, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "Benutzername konnte nicht ermittelt werden.");
            return new UsernameResult(ApiOutcome.Failed, null, "Netzwerkfehler");
        }
    }

    public Task<TokenRefreshResult> RefreshLongLivedTokenAsync(string accessToken, CancellationToken ct)
    {
        var url = $"{_options.Host}/refresh_access_token?grant_type=ig_refresh_token&access_token={Uri.EscapeDataString(accessToken)}";
        return CallTokenEndpointAsync(url, ct);
    }

    public Task<TokenRefreshResult> ExchangeForLongLivedTokenAsync(string shortLivedToken, string appSecret, CancellationToken ct)
    {
        var url = $"{_options.Host}/access_token?grant_type=ig_exchange_token" +
                  $"&client_secret={Uri.EscapeDataString(appSecret)}&access_token={Uri.EscapeDataString(shortLivedToken)}";
        return CallTokenEndpointAsync(url, ct);
    }

    private async Task<TokenRefreshResult> CallTokenEndpointAsync(string url, CancellationToken ct)
    {
        try
        {
            var response = await _http.GetAsync(url, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                var error = TryReadError(body);
                return new TokenRefreshResult(ApiOutcome.Failed, null, null, error?.Message ?? $"HTTP {(int)response.StatusCode}");
            }

            var parsed = JsonSerializer.Deserialize<InstagramTokenResponse>(body, FeedJson.Options);
            if (parsed?.AccessToken is null)
            {
                return new TokenRefreshResult(ApiOutcome.Failed, null, null, "Antwort ohne access_token");
            }

            var lifetime = parsed.ExpiresIn > 0
                ? TimeSpan.FromSeconds(parsed.ExpiresIn)
                : _options.AssumedTokenLifetime;

            return new TokenRefreshResult(ApiOutcome.Success, parsed.AccessToken, lifetime, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "Token-Endpunkt nicht erreichbar.");
            return new TokenRefreshResult(ApiOutcome.Failed, null, null, "Netzwerkfehler");
        }
    }

    /// <summary>
    /// Die next-URL kommt aus der API-Antwort und wird nicht blind aufgerufen: nur der konfigurierte
    /// Graph-Host mit https ist erlaubt.
    /// </summary>
    private string? SanitizeNextUrl(string? next)
    {
        if (string.IsNullOrWhiteSpace(next) || !Uri.TryCreate(next, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (!Uri.TryCreate(_options.Host, UriKind.Absolute, out var allowed) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, allowed.Host, StringComparison.OrdinalIgnoreCase))
        {
            _log.LogWarning("next-URL mit fremdem Host wird ignoriert.");
            return null;
        }

        return uri.ToString();
    }

    private static InstagramError? TryReadError(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<InstagramErrorResponse>(body, FeedJson.Options)?.Error;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
