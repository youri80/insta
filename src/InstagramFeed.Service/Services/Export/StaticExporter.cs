using InstagramFeed.Service.Domain;
using InstagramFeed.Service.Services.Feed;
using InstagramFeed.Service.Services.Media;
using InstagramFeed.Service.Services.Rendering;
using InstagramFeed.Service.Services.Sync;

namespace InstagramFeed.Service.Services.Export;

/// <summary>
/// Exportiert den Feed als fertige statische Dateien fuer Webspace ohne .NET. Die Dateien liegen
/// beim Hoster, der Besucher-Browser spricht weiterhin nur mit dieser einen Domain.
///
/// Der Export bildet die URL-Struktur nach: was unter <c>/instagram/feed.json</c> erreichbar sein
/// muss, landet auch unter <c>&lt;ziel&gt;/instagram/feed.json</c>. Nur so passen die Verweise im
/// Fragment und im Skript zur Ablage - sonst zeigt die Galerie nach dem Hochladen ins Leere.
/// </summary>
public sealed class StaticExporter
{
    private static readonly string[] MediaExtensions = { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".svg" };

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

    /// <summary>Zielverzeichnis unterhalb der Export-Wurzel, das dem oeffentlichen Basispfad entspricht.</summary>
    public string RelativeBasePath => ToRelative(_renderer.BasePath);

    /// <summary>Zielverzeichnis unterhalb der Export-Wurzel, das dem Medien-URL-Praefix entspricht.</summary>
    public string RelativeMediaPath => ToRelative(_media.UrlPrefix);

    public async Task<int> RunAsync(string targetDirectory, CancellationToken ct)
    {
        var result = await _sync.SyncAsync(force: true, ct);
        var snapshot = await _repository.ReadAsync(ct);

        var target = Path.GetFullPath(targetDirectory);
        var baseTarget = Path.Combine(target, RelativeBasePath);
        var mediaTarget = Path.Combine(target, RelativeMediaPath);

        Directory.CreateDirectory(baseTarget);
        Directory.CreateDirectory(mediaTarget);

        var copied = 0;
        foreach (var file in Directory.EnumerateFiles(_media.DirectoryPath)
                     .Where(path => MediaExtensions.Contains(Path.GetExtension(path).ToLowerInvariant())))
        {
            File.Copy(file, Path.Combine(mediaTarget, Path.GetFileName(file)), overwrite: true);
            copied++;
        }

        if (File.Exists(_repository.SnapshotPath))
        {
            File.Copy(_repository.SnapshotPath, Path.Combine(baseTarget, "feed.json"), overwrite: true);
        }

        await File.WriteAllTextAsync(Path.Combine(baseTarget, "style.css"), _renderer.RenderStyle(), ct);
        await File.WriteAllTextAsync(Path.Combine(baseTarget, "snippet.html"), _renderer.RenderFragment(snapshot), ct);
        await File.WriteAllTextAsync(Path.Combine(baseTarget, "index.html"), _renderer.RenderStandalonePage(snapshot), ct);
        await File.WriteAllTextAsync(Path.Combine(baseTarget, "embed.js"), _renderer.RenderEmbedScript(), ct);
        await File.WriteAllTextAsync(Path.Combine(target, "README.txt"), BuildReadme(snapshot), ct);

        _log.LogInformation("Export nach {Target}: {Posts} Beitraege, {Files} Mediendateien.",
            target, snapshot?.Posts.Count ?? 0, copied);

        Console.WriteLine($"Export: {target}");
        Console.WriteLine($"  Beitraege    : {snapshot?.Posts.Count ?? 0}");
        Console.WriteLine($"  Mediendateien: {copied}");
        Console.WriteLine($"  Pfade        : /{RelativeBasePath}/ (Seite)  /{RelativeMediaPath}/ (Bilder)");
        Console.WriteLine($"  Status       : {result.Status}" + (result.Message is null ? "" : $" ({result.Message})"));

        return result.Ok ? 0 : 1;
    }

    private static string ToRelative(string urlPath)
    {
        var trimmed = urlPath.Trim('/');
        return trimmed.Length == 0 ? "." : trimmed;
    }

    private string BuildReadme(InstagramFeedSnapshot? snapshot)
    {
        var basePath = _renderer.BasePath;
        var mediaPath = _media.UrlPrefix;

        return $$"""
            Instagram-Feed - statischer Export
            ==================================

            Stand    : {{snapshot?.GeneratedAtUtc:u}}
            Beitraege: {{snapshot?.Posts.Count ?? 0}}

            Aufbau (entspricht der URL-Struktur der Website)
            ------------------------------------------------
            {{RelativeBasePath}}/feed.json      -> {{basePath}}/feed.json
            {{RelativeBasePath}}/style.css      -> {{basePath}}/style.css
            {{RelativeBasePath}}/snippet.html   -> {{basePath}}/snippet.html   (Fragment zum Einfuegen)
            {{RelativeBasePath}}/embed.js       -> {{basePath}}/embed.js       (optional, JS-Variante)
            {{RelativeBasePath}}/index.html     -> {{basePath}}/index.html     (Vorschau, noindex)
            {{RelativeMediaPath}}/*             -> {{mediaPath}}/*             (gespiegelte Bilder)
            README.txt                          (nur fuer Sie, nicht hochladen)

            Ausrollen
            ---------
            Inhalt in das Web-Root spiegeln - die Ordnerstruktur ist dafuer bereits passend:

                rsync -a --delete {{RelativeBasePath}}/ /var/www/html/{{RelativeBasePath}}/
                rsync -a --delete {{RelativeMediaPath}}/ /var/www/html/{{RelativeMediaPath}}/

            Wichtig: Die Zielordner NUR dann mit --delete spiegeln, wenn sie ausschliesslich diesen
            Feed enthalten. Liegt die Medienablage innerhalb des Basisordners, genuegt der erste Befehl.

            Einbau in die Seite
            -------------------
            Statische Seite (Bauen/Generieren): Inhalt von snippet.html an der gewuenschten Stelle
            einsetzen und im <head> ergaenzen:

                <link rel="stylesheet" href="{{basePath}}/style.css">

            Seite, die zur Laufzeit zusammenbaut wird (PHP/Server):
                <?php readfile(__DIR__ . '/snippet.html'); ?>

            Ohne Vorlage, nur mit JavaScript:
                <div data-igfeed-embed data-igfeed-title="Aktuelles von Instagram"></div>
                <script src="{{basePath}}/embed.js" defer></script>

            Datenschutz
            -----------
            Die Dateien laden nichts von fremden Servern nach; alle Verweise zeigen auf die eigene
            Domain. Erst der Klick auf einen Beitrag oeffnet instagram.com - das ist eine bewusste
            Entscheidung des Besuchers.
            """;
    }
}
