using System.Text;
using Microsoft.Extensions.Options;
using InstagramFeed.Service.Configuration;
using InstagramFeed.Service.Domain;
using InstagramFeed.Service.Services.Media;

namespace InstagramFeed.Service.Services.Demo;

/// <summary>
/// Erzeugt Beispielbeitraege mit lokal erzeugten Bildern. Zweck: der Dienst laesst sich starten,
/// ausliefern und pruefen, OHNE dass ein Instagram-Token existiert - und der Token ist genau das,
/// was man am Anfang nicht hat.
/// </summary>
public sealed class DemoFeedFactory
{
    private static readonly string[] Captions =
    {
        "Erste Runde auf dem Conger - Wind aus West, drei Knoten zu wenig.",
        "Regatta-Vorbereitung am Steg. Die Jolle ist klar zum Gefecht.",
        "Sonnenuntergang ueber dem See, der letzte Schlag nach Hause.",
        "Saitenwechsel am Wochenende: die Bespannung hat es hinter sich.",
        "Notiz zum Training: Vorwindkurs mit ausgebaumter Fock uebt sich nicht von allein.",
        "Kleine Werkzeugkiste an Bord - was wirklich mitmuss.",
        "Wetterfenster genutzt, bevor die Front durchzieht.",
        "Ergebnisliste vom Vereinsabend.",
        "Anlegen bei auffrischendem Wind, dritte Anlauf war sauber."
    };

    private readonly IMediaStore _media;
    private readonly InstagramOptions _options;
    private readonly TimeProvider _clock;

    public DemoFeedFactory(IMediaStore media, IOptions<InstagramOptions> options, TimeProvider clock)
    {
        _media = media;
        _options = options.Value;
        _clock = clock;
    }

    public async Task<IReadOnlyList<InstagramPost>> BuildPostsAsync(CancellationToken ct)
    {
        var posts = new List<InstagramPost>();
        var now = _clock.GetUtcNow();

        for (var index = 1; index <= Math.Max(1, _options.DemoPostCount); index++)
        {
            var key = $"demo-{index:00}";
            var kind = index % 3 == 0 ? MediaKind.Carousel : MediaKind.Image;

            var localPath = await _media.StoreAsync(key, BuildSvg(index), "image/svg+xml", ct);
            var children = new List<string>();

            if (kind == MediaKind.Carousel)
            {
                var childPath = await _media.StoreAsync($"{key}-child1", BuildSvg(index, "zweites Bild"), "image/svg+xml", ct);
                children.Add(childPath);
            }

            posts.Add(new InstagramPost
            {
                Id = key,
                Shortcode = null,
                MediaKind = kind,
                Caption = Captions[(index - 1) % Captions.Length],
                Permalink = null,
                TimestampUtc = now.AddDays(-index * 3),
                LocalMediaPath = localPath,
                LocalChildPaths = children,
                AltText = $"Beispielbild {index} zur Demonstration der Galerie"
            });
        }

        return posts;
    }

    private static byte[] BuildSvg(int index, string? subtitle = null)
    {
        var hue = (index * 47) % 360;
        var label = subtitle is null ? $"Beispielbeitrag {index}" : $"Beispielbeitrag {index} - {subtitle}";

        var svg =
            $"""
             <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 900 900" width="900" height="900">
               <defs>
                 <linearGradient id="g" x1="0" y1="0" x2="1" y2="1">
                   <stop offset="0%" stop-color="hsl({hue}, 55%, 32%)"/>
                   <stop offset="100%" stop-color="hsl({(hue + 40) % 360}, 60%, 18%)"/>
                 </linearGradient>
               </defs>
               <rect width="900" height="900" fill="url(#g)"/>
               <circle cx="450" cy="380" r="150" fill="none" stroke="rgba(255,255,255,0.55)" stroke-width="6"/>
               <path d="M450 230 L450 530 M320 380 L580 380" stroke="rgba(255,255,255,0.35)" stroke-width="4"/>
               <text x="450" y="660" text-anchor="middle" font-family="system-ui, sans-serif"
                     font-size="46" fill="rgba(255,255,255,0.92)">{label}</text>
               <text x="450" y="720" text-anchor="middle" font-family="system-ui, sans-serif"
                     font-size="26" fill="rgba(255,255,255,0.6)">Demo-Daten, nicht von Instagram</text>
             </svg>
             """;

        return Encoding.UTF8.GetBytes(svg);
    }
}
