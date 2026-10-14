using Microsoft.Extensions.Options;
using InstagramFeed.Service.Configuration;
using InstagramFeed.Service.Domain;
using InstagramFeed.Service.Services.Demo;
using InstagramFeed.Service.Services.Feed;
using InstagramFeed.Service.Services.Instagram;
using InstagramFeed.Service.Services.Media;

namespace InstagramFeed.Service.Services.Sync;

public sealed record SyncResult(
    bool Ok,
    string Status,
    int PostCount,
    int NewMediaFiles,
    TimeSpan Duration,
    string? Message,
    DateTimeOffset FinishedAtUtc);

/// <summary>
/// Der eigentliche Abgleich. Alle Regeln, die verhindern, dass der Feed auf der Website kaputt
/// aussieht oder still leer wird, sitzen hier - nicht in der Oberflaeche.
/// </summary>
public sealed class InstagramSyncService
{
    private readonly IInstagramApiClient _api;
    private readonly InstagramTokenService _tokens;
    private readonly IMediaStore _media;
    private readonly IFeedRepository _repository;
    private readonly DemoFeedFactory _demo;
    private readonly InstagramOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<InstagramSyncService> _log;

    private int _running;

    public InstagramSyncService(
        IInstagramApiClient api,
        InstagramTokenService tokens,
        IMediaStore media,
        IFeedRepository repository,
        DemoFeedFactory demo,
        IOptions<InstagramOptions> options,
        TimeProvider clock,
        ILogger<InstagramSyncService> log)
    {
        _api = api;
        _tokens = tokens;
        _media = media;
        _repository = repository;
        _demo = demo;
        _options = options.Value;
        _clock = clock;
        _log = log;
    }

    public SyncResult? LastResult { get; private set; }

    public TokenState? LastToken { get; private set; }

    public async Task<SyncResult> SyncAsync(bool force, CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return new SyncResult(true, "Laeuft bereits", 0, 0, TimeSpan.Zero, "Ein Abgleich ist noch aktiv.", _clock.GetUtcNow());
        }

        var started = _clock.GetUtcNow();
        try
        {
            var previous = await _repository.ReadAsync(ct);

            if (!force && previous is not null && started - previous.GeneratedAtUtc < _options.MinSyncInterval)
            {
                var skipped = new SyncResult(true, "Uebersprungen", previous.Posts.Count, 0, TimeSpan.Zero,
                    $"Letzter Abgleich {previous.GeneratedAtUtc:u}, Mindestabstand {_options.MinSyncInterval}.",
                    _clock.GetUtcNow());
                LastResult = skipped;
                return skipped;
            }

            if (_options.UseDemoData)
            {
                var demoPosts = await _demo.BuildPostsAsync(ct);
                var demoSnapshot = new InstagramFeedSnapshot
                {
                    GeneratedAtUtc = _clock.GetUtcNow(),
                    Username = "demo",
                    ProfileUrl = _options.ProfileUrl,
                    IsDemo = true,
                    Posts = demoPosts
                };

                await _repository.WriteAsync(demoSnapshot, ct);

                var demoResult = new SyncResult(true, "Demo", demoPosts.Count, demoPosts.Count, _clock.GetUtcNow() - started,
                    "Demo-Daten: es wurde kein Instagram-Zugriff ausgefuehrt.", _clock.GetUtcNow());
                LastResult = demoResult;
                return demoResult;
            }

            var token = await _tokens.EnsureValidTokenAsync(ct);
            LastToken = token;

            if (!token.IsUsable)
            {
                return Failed(started, previous, token.Note ?? "Kein gueltiges Token.");
            }

            var fetch = await _api.GetLatestMediaAsync(token.AccessToken!, _options.MaxPosts, ct);

            if (fetch.Outcome == ApiOutcome.TokenExpired)
            {
                return Failed(started, previous, "Instagram-Token abgelaufen (Code 190). Neuen Token hinterlegen.");
            }

            if (fetch.Outcome != ApiOutcome.Success)
            {
                return Failed(started, previous, $"Abruf fehlgeschlagen: {fetch.Error}");
            }

            // Eine leere Liste bei einem Feed, der vorher Beitraege hatte, ist KEIN gueltiger Zustand:
            // Ursache sind meist Copyright-Flags, ein privates Konto oder ein Token-Problem. Der alte
            // Stand bleibt stehen, statt das Raster auf der Website zu leeren.
            if (fetch.Items.Count == 0 && previous is { Posts.Count: > 0 })
            {
                return Failed(started, previous, "API lieferte keine Beitraege; bisheriger Feed bleibt erhalten.");
            }

            var posts = new List<InstagramPost>();
            var newFiles = 0;

            foreach (var node in fetch.Items)
            {
                var mapped = InstagramMediaMapper.Map(node);
                if (mapped is null)
                {
                    continue;
                }

                string? localMain = null;
                var children = new List<string>();

                if (mapped.Targets.Count > 0)
                {
                    var main = await DownloadAsync(mapped.Targets[0], ct);
                    localMain = main.LocalPath;
                    newFiles += main.Downloaded ? 1 : 0;
                }

                foreach (var child in mapped.Targets.Skip(1))
                {
                    var outcome = await DownloadAsync(child, ct);
                    newFiles += outcome.Downloaded ? 1 : 0;

                    if (outcome.LocalPath is not null)
                    {
                        children.Add(outcome.LocalPath);
                    }
                }

                posts.Add(new InstagramPost
                {
                    Id = mapped.Id,
                    Shortcode = mapped.Shortcode,
                    MediaKind = mapped.Kind,
                    Caption = mapped.Caption,
                    Permalink = mapped.Permalink,
                    TimestampUtc = mapped.TimestampUtc,
                    LocalMediaPath = localMain,
                    LocalChildPaths = children,
                    AltText = BuildAltText(mapped)
                });
            }

            var username = fetch.Items.Select(item => item.Username).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
                           ?? previous?.Username;

            if (username is null)
            {
                var user = await _api.GetUsernameAsync(token.AccessToken!, ct);
                username = user.Username;
            }

            var snapshot = new InstagramFeedSnapshot
            {
                GeneratedAtUtc = _clock.GetUtcNow(),
                Username = username,
                ProfileUrl = _options.ProfileUrl,
                IsDemo = false,
                Posts = posts
            };

            await _repository.WriteAsync(snapshot, ct);

            var result = new SyncResult(true, "Aktualisiert", posts.Count, newFiles, _clock.GetUtcNow() - started, null, _clock.GetUtcNow());
            LastResult = result;
            _log.LogInformation("Feed aktualisiert: {Count} Beitraege, {New} neue Mediendateien.", posts.Count, newFiles);
            return result;
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private SyncResult Failed(DateTimeOffset started, InstagramFeedSnapshot? previous, string message)
    {
        _log.LogError("Feed-Abgleich fehlgeschlagen: {Message}", message);

        var result = new SyncResult(false, "Degradiert", previous?.Posts.Count ?? 0, 0,
            _clock.GetUtcNow() - started, message, _clock.GetUtcNow());

        LastResult = result;
        return result;
    }

    private async Task<MediaFetchOutcome> DownloadAsync(MediaTarget target, CancellationToken ct)
    {
        try
        {
            return await _media.EnsureLocalAsync(target.FileKey, target.RemoteUrl, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            // Ein einzelnes fehlendes Bild darf den ganzen Lauf nicht abbrechen.
            _log.LogWarning(ex, "Medium {FileKey} konnte nicht gesichert werden.", target.FileKey);
            return new MediaFetchOutcome(null, false);
        }
    }

    private static string BuildAltText(MappedPost mapped)
    {
        if (string.IsNullOrWhiteSpace(mapped.Caption))
        {
            return mapped.Kind == MediaKind.Carousel ? "Instagram-Karussell" : "Instagram-Beitrag";
        }

        var firstLine = mapped.Caption.Split('\n')[0].Trim();
        return firstLine.Length <= 160 ? firstLine : firstLine[..160];
    }
}
