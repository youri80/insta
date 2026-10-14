using System.Text.Json;
using System.Text.Json.Serialization;

namespace InstagramFeed.Service.Domain;

public enum MediaKind
{
    Image,
    Video,
    Carousel,
    Unknown
}

/// <summary>
/// Ein Beitrag, wie er ausgeliefert wird. Es gibt hier bewusst KEIN Feld fuer die Instagram-CDN-URL:
/// was nicht im Modell steht, kann nicht versehentlich ins HTML geraten und damit Besucherdaten an
/// Meta uebertragen.
/// </summary>
public sealed record InstagramPost
{
    public required string Id { get; init; }

    public string? Shortcode { get; init; }

    public MediaKind MediaKind { get; init; } = MediaKind.Unknown;

    public string? Caption { get; init; }

    /// <summary>Permalink auf instagram.com. Wird nur als Link verwendet, nie automatisch geladen.</summary>
    public string? Permalink { get; init; }

    public DateTimeOffset TimestampUtc { get; init; }

    /// <summary>Immer ein eigener, relativer Pfad (z. B. /media/instagram/123.jpg) - nie eine Fremd-URL.</summary>
    public string? LocalMediaPath { get; init; }

    /// <summary>Weitere Bilder eines Karussells (ebenfalls nur eigene Pfade).</summary>
    public IReadOnlyList<string> LocalChildPaths { get; init; } = Array.Empty<string>();

    public string? AltText { get; init; }
}

/// <summary>Zustand des gesamten Feeds. Wird als feed.json zwischengespeichert und ausgeliefert.</summary>
public sealed record InstagramFeedSnapshot
{
    public DateTimeOffset GeneratedAtUtc { get; init; }

    public string? Username { get; init; }

    public string? ProfileUrl { get; init; }

    /// <summary>True, wenn der Inhalt aus dem Demo-Generator stammt (nie produktiv ausliefern).</summary>
    public bool IsDemo { get; init; }

    public IReadOnlyList<InstagramPost> Posts { get; init; } = Array.Empty<InstagramPost>();
}

/// <summary>Zentrale JSON-Einstellungen fuer Cache und Auslieferung.</summary>
public static class FeedJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };
}
