using Microsoft.Extensions.Options;
using InstagramFeed.Service.Configuration;
using InstagramFeed.Service.Services.Instagram;

namespace InstagramFeed.Service.Services.Instagram;

public sealed record TokenState(ApiOutcome Outcome, string? AccessToken, DateTimeOffset? ExpiresAtUtc, string? Note)
{
    public bool IsUsable => Outcome is ApiOutcome.Success && AccessToken is not null;
}

/// <summary>
/// Haelt den Long-Lived-Token am Leben. Ein Feed, der nach 60 Tagen still stehen bleibt, ist der
/// klassische Ausfall dieses Ansatzes - deshalb wird bei jedem Sync geprueft und rechtzeitig
/// verlaengert.
/// </summary>
public sealed class InstagramTokenService
{
    private readonly ITokenStore _store;
    private readonly IInstagramApiClient _api;
    private readonly InstagramOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<InstagramTokenService> _log;

    public InstagramTokenService(
        ITokenStore store,
        IInstagramApiClient api,
        IOptions<InstagramOptions> options,
        TimeProvider clock,
        ILogger<InstagramTokenService> log)
    {
        _store = store;
        _api = api;
        _options = options.Value;
        _clock = clock;
        _log = log;
    }

    public async Task<TokenState> EnsureValidTokenAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var stored = await _store.ReadAsync(ct);

        if (stored is null)
        {
            return await BootstrapAsync(now, ct);
        }

        var remaining = stored.ExpiresAtUtc - now;

        if (remaining > _options.RefreshWhenLessThan)
        {
            return new TokenState(ApiOutcome.Success, stored.AccessToken, stored.ExpiresAtUtc, null);
        }

        // Ein Token, das juenger als 24 h ist, wird von der API beim Refresh abgelehnt. Die
        // Restlaufzeit verraet das Alter: bei voller Laufzeit ist er gerade erst entstanden.
        if (remaining > _options.AssumedTokenLifetime - TimeSpan.FromHours(24))
        {
            _log.LogInformation("Token ist noch zu frisch fuer eine Verlaengerung.");
            return new TokenState(ApiOutcome.Success, stored.AccessToken, stored.ExpiresAtUtc, "Refresh wegen Mindestalter uebersprungen");
        }

        var refreshed = await _api.RefreshLongLivedTokenAsync(stored.AccessToken, ct);

        if (refreshed is { Outcome: ApiOutcome.Success, AccessToken: not null })
        {
            var lifetime = refreshed.Lifetime ?? _options.AssumedTokenLifetime;
            var expiresAt = now + lifetime;
            await _store.WriteAsync(new StoredToken(refreshed.AccessToken, expiresAt), ct);
            _log.LogInformation("Instagram-Token verlaengert, gueltig bis {ExpiresAt:u}.", expiresAt);
            return new TokenState(ApiOutcome.Success, refreshed.AccessToken, expiresAt, null);
        }

        // Der alte Token laeuft noch - also weiterverwenden und laut warnen, statt den Feed
        // abzuschalten.
        _log.LogWarning("Token-Verlaengerung fehlgeschlagen ({Error}). Alter Token wird weiterverwendet, " +
                        "laeuft am {ExpiresAt:u} ab.", refreshed.Error, stored.ExpiresAtUtc);

        return new TokenState(ApiOutcome.Failed, stored.AccessToken, stored.ExpiresAtUtc,
            $"Verlaengerung fehlgeschlagen: {refreshed.Error}");
    }

    private async Task<TokenState> BootstrapAsync(DateTimeOffset now, CancellationToken ct)
    {
        var initial = _options.InitialAccessToken;

        if (string.IsNullOrWhiteSpace(initial))
        {
            if (_options.UseDemoData)
            {
                return new TokenState(ApiOutcome.Success, null, null, "Demo-Modus ohne Token");
            }

            return new TokenState(ApiOutcome.Failed, null, null, "Kein Token konfiguriert (Instagram__InitialAccessToken)");
        }

        var token = initial;
        var lifetime = _options.AssumedTokenLifetime;

        if (!string.IsNullOrWhiteSpace(_options.AppSecret))
        {
            var exchanged = await _api.ExchangeForLongLivedTokenAsync(initial, _options.AppSecret, ct);
            if (exchanged is { Outcome: ApiOutcome.Success, AccessToken: not null })
            {
                token = exchanged.AccessToken;
                lifetime = exchanged.Lifetime ?? lifetime;
                _log.LogInformation("Short-Lived-Token gegen Long-Lived-Token getauscht ({Days} Tage).", (int)lifetime.TotalDays);
            }
            else
            {
                _log.LogWarning("Tausch gegen Long-Lived-Token fehlgeschlagen ({Error}). " +
                                "Startwert wird als Long-Lived-Token behandelt.", exchanged.Error);
            }
        }

        var expiresAt = now + lifetime;
        await _store.WriteAsync(new StoredToken(token, expiresAt), ct);
        return new TokenState(ApiOutcome.Success, token, expiresAt, null);
    }
}
