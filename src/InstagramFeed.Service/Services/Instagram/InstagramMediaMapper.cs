using System.Globalization;
using System.Text.RegularExpressions;
using InstagramFeed.Service.Domain;

namespace InstagramFeed.Service.Services.Instagram;

/// <summary>Eine Mediendatei, die zu einem Beitrag gehoert (Titelbild oder Karussell-Kind).</summary>
public sealed record MediaTarget(string FileKey, string RemoteUrl);

/// <summary>Beitrag nach dem Mapping, noch ohne lokale Datei.</summary>
public sealed record MappedPost(
    string Id,
    string? Shortcode,
    MediaKind Kind,
    string? Caption,
    string? Permalink,
    DateTimeOffset TimestampUtc,
    IReadOnlyList<MediaTarget> Targets);

/// <summary>
/// Reine Abbildung API-Knoten -> Beitrag. Enthaelt bewusst keinen Datei- oder Netzwerkzugriff,
/// damit sie ohne Infrastruktur testbar bleibt.
/// </summary>
public static class InstagramMediaMapper
{
    public const int MaxCarouselChildren = 10;

    private static readonly Regex OffsetWithoutColon = new(@"([+-])(\d{2})(\d{2})$", RegexOptions.Compiled);
    private static readonly Regex ShortcodePattern = new(@"instagram\.com/(?:p|reel|tv)/([A-Za-z0-9_-]+)", RegexOptions.Compiled);

    public static MappedPost? Map(InstagramMediaNode node)
    {
        if (string.IsNullOrWhiteSpace(node.Id))
        {
            return null;
        }

        var kind = node.MediaType switch
        {
            "IMAGE" => MediaKind.Image,
            "VIDEO" => MediaKind.Video,
            "CAROUSEL_ALBUM" => MediaKind.Carousel,
            _ => MediaKind.Unknown
        };

        var targets = new List<MediaTarget>();

        if (kind == MediaKind.Carousel && node.Children?.Data.Count > 0)
        {
            var index = 0;
            foreach (var child in node.Children.Data.Take(MaxCarouselChildren))
            {
                index++;
                // Bei Videos liefert die API oft kein media_url (z. B. urheberrechtlich geflaggtes
                // Audio) - dann bleibt nur das Vorschaubild.
                var url = child.MediaUrl ?? child.ThumbnailUrl;
                if (!string.IsNullOrWhiteSpace(url))
                {
                    var childKey = string.IsNullOrWhiteSpace(child.Id) ? $"{node.Id}-{index}" : $"{node.Id}-{child.Id}";
                    targets.Add(new MediaTarget(childKey, url));
                }
            }
        }
        else
        {
            var url = node.MediaUrl ?? node.ThumbnailUrl;
            if (!string.IsNullOrWhiteSpace(url))
            {
                targets.Add(new MediaTarget(node.Id, url));
            }
        }

        return new MappedPost(
            node.Id,
            ExtractShortcode(node.Permalink),
            kind,
            string.IsNullOrWhiteSpace(node.Caption) ? null : node.Caption.Trim(),
            string.IsNullOrWhiteSpace(node.Permalink) ? null : node.Permalink,
            ParseTimestamp(node.Timestamp),
            targets);
    }

    public static string? ExtractShortcode(string? permalink)
    {
        if (string.IsNullOrWhiteSpace(permalink))
        {
            return null;
        }

        var match = ShortcodePattern.Match(permalink);
        return match.Success ? match.Groups[1].Value : null;
    }

    public static DateTimeOffset ParseTimestamp(string? timestamp)
    {
        if (string.IsNullOrWhiteSpace(timestamp))
        {
            return DateTimeOffset.UnixEpoch;
        }

        // Die API liefert "2026-10-12T08:14:00+0000" - der Offset ohne Doppelpunkt ist fuer
        // DateTimeOffset.TryParse nicht zuverlaessig, deshalb normalisieren.
        var normalized = OffsetWithoutColon.Replace(timestamp, "$1$2:$3");

        return DateTimeOffset.TryParse(normalized, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : DateTimeOffset.UnixEpoch;
    }
}
