using InstagramFeed.Service.Domain;
using InstagramFeed.Service.Services.Feed;
using InstagramFeed.Service.Services.Media;
using InstagramFeed.Service.Services.Rendering;
using InstagramFeed.Service.Services.Sync;

namespace InstagramFeed.Service.Services.Export;

/// <summary>
/// Exportiert den Feed als fertige statische Dateien. Damit laeuft der Fall "Webspace ohne .NET"
/// ohne Abschlag bei der DSGVO-Konformitaet: die Dateien liegen beim Hoster, der Besucher-Browser
/// spricht weiterhin nur mit dieser einen Domain.
/// </summary>
public sealed class StaticExporter
{
    private readonly InstagramSyncService _sync;
    private readonly IFeedRepository _repository;
    private readonly IMediaStore _media;
    private readonly FeedHtmlRenderer _renderer;
    private readonly ILogger<StaticExporter> _log;

    public StaticExporter(
        InstagramSyncService sync,
        IFeedRepository repository,
        IMediaStore media,
        FeedHtmlRenderer renderer,
        ILogger<StaticExporter> log)
    {
        _sync = sync;
        _repository = repository;
        _media = media;
        _renderer = renderer;
        _log = log;
    }

    public async Task<int> RunAsync(string targetDirectory, CancellationToken ct)
    {
        var result = await _sync.SyncAsync(force: true, ct);
        var snapshot = await _repository.ReadAsync(ct);

        var target = Path.GetFullPath(targetDirectory);
        Directory.CreateDirectory(target);

        var mediaTarget = Path.Combine(target, "media", "instagram");
        Directory.CreateDirectory(mediaTarget);

        var copied = 0;
        foreach (var file in Directory.EnumerateFiles(_media.DirectoryPath))
        {
            File.Copy(file, Path.Combine(mediaTarget, Path.GetFileName(file)), overwrite: true);
            copied++;
        }

        if (File.Exists(_repository.SnapshotPath))
        {
            File.Copy(_repository.SnapshotPath, Path.Combine(target, "feed.json"), overwrite: true);
        }

        await File.WriteAllTextAsync(Path.Combine(target, "style.css"), _renderer.RenderStyle(), ct);
        await File.WriteAllTextAsync(Path.Combine(target, "snippet.html"), _renderer.RenderFragment(snapshot), ct);
        await File.WriteAllTextAsync(Path.Combine(target, "index.html"), _renderer.RenderStandalonePage(snapshot), ct);
        await File.WriteAllTextAsync(Path.Combine(target, "embed.js"), _renderer.RenderEmbedScript(), ct);
        await File.WriteAllTextAsync(Path.Combine(target, "README.txt"), BuildReadme(snapshot), ct);

        _log.LogInformation("Export nach {Target}: {Posts} Beitraege, {Files} Mediendateien.",
            target, snapshot?.Posts.Count ?? 0, copied);

        Console.WriteLine($"Export: {target}");
        Console.WriteLine($"  Beitraege   : {snapshot?.Posts.Count ?? 0}");
        Console.WriteLine($"  Mediendateien: {copied}");
        Console.WriteLine($"  Status      : {result.Status}" + (result.Message is null ? "" : $" ({result.Message})"));

        return result.Ok ? 0 : 1;
    }

    private string BuildReadme(InstagramFeedSnapshot? snapshot)
    {
        var prefix = _media.UrlPrefix;

        return $$"""
            Instagram-Feed - statischer Export
            ==================================

            Stand    : {{snapshot?.GeneratedAtUtc:u}}
            Beitraege: {{snapshot?.Posts.Count ?? 0}}

            Inhalt
            ------
            snippet.html   HTML-Fragment, das in die bestehende Seite eingefuegt wird
            index.html     eigenstaendige Vorschauseite (noindex)
            style.css      Stylesheet der Galerie
            feed.json      Rohdaten (falls die eigene Seite selbst rendert)
            embed.js       optionales Skript fuer Seiten ohne serverseitiges Einbinden
            media/instagram/  gespiegelte Bilder

            Einbau (serverseitig, empfohlen)
            --------------------------------
            1. Den Ordner media/ und die Datei style.css in das Web-Root kopieren.
            2. snippet.html in die Vorlage einfuegen, z. B. PHP:
                 <?php include __DIR__ . '/snippet.html'; ?>
               oder beim Bauen der Seite (SSG) den Inhalt von snippet.html einsetzen.

            Einbau (per Skript, ohne serverseitige Vorlage)
            ----------------------------------------------
            1. media/, feed.json, style.css und embed.js ins Web-Root kopieren.
            2. An der gewuenschten Stelle einfuegen:
                 <div data-igfeed-embed data-igfeed-title="Aktuelles von Instagram"></div>
                 <script src="/embed.js" defer></script>

            Wichtig
            -------
            Es sind keine Anpassungen noetig, solange snippet.html und die Bilder unter denselben
            Pfaden erreichbar sind (Bilder: {{prefix}}/...). Beim Anklicken eines Beitrags wird
            instagram.com geoeffnet - das ist ein bewusster Klick des Besuchers. Diese Dateien
            selbst laden nichts nach; alle Verweise zeigen auf die eigene Domain.
            """;
    }
}
