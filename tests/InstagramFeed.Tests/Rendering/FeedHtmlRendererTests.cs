using System.Text.RegularExpressions;
using InstagramFeed.Service.Domain;
using InstagramFeed.Service.Services.Rendering;

namespace InstagramFeed.Tests.Rendering;

/// <summary>
/// Die eigentliche Konformitaetspruefung. Kein Konzeptpapier, sondern eine Zusicherung an die
/// Ausgabe: die Galerie darf nichts von fremden Servern nachladen.
/// </summary>
public sealed class FeedHtmlRendererTests
{
    private static readonly string[] ForbiddenHosts =
    {
        "cdninstagram.com", "fbcdn.net", "facebook.com", "googleapis.com", "gstatic.com", "youtube.com"
    };

    private static FeedHtmlRenderer CreateRenderer() =>
        new(TestFactory.Options(o =>
        {
            o.Title = "Aktuelles von Instagram";
            o.ProfileUrl = "https://www.instagram.com/beispiel/";
        }));

    private static InstagramFeedSnapshot Snapshot(params InstagramPost[] posts) => new()
    {
        GeneratedAtUtc = DateTimeOffset.UtcNow,
        Username = "beispiel",
        Posts = posts
    };

    private static InstagramPost Post(string id, string? localPath = "/media/instagram/111.jpg", string? permalink = "https://www.instagram.com/p/AAA/") => new()
    {
        Id = id,
        MediaKind = MediaKind.Image,
        Caption = "Erster Beitrag",
        Permalink = permalink,
        TimestampUtc = new DateTimeOffset(2026, 10, 10, 9, 0, 0, TimeSpan.Zero),
        LocalMediaPath = localPath,
        AltText = "Bild zur Demonstration",
        Shortcode = "AAA"
    };

    /// <summary>
    /// Sammelt Verstoesse gegen die Zusicherung "keine Fremdressourcen". "instagram.com" ist nur
    /// als Linkziel (href des Permalinks) erlaubt - nicht als Bild-, Skript- oder Frame-Quelle.
    /// </summary>
    internal static IReadOnlyList<string> Violations(string html)
    {
        var violations = new List<string>();

        foreach (var host in ForbiddenHosts)
        {
            if (html.Contains(host, StringComparison.OrdinalIgnoreCase))
            {
                violations.Add($"host:{host}");
            }
        }

        var withoutPermalinks = Regex.Replace(html, @"href=""https://www\.instagram\.com/[^""]*""", string.Empty);
        if (withoutPermalinks.Contains("instagram.com", StringComparison.OrdinalIgnoreCase))
        {
            violations.Add("host:instagram.com (ausserhalb eines Permalink-Links)");
        }

        if (Regex.IsMatch(html, @"\ssrc\s*=\s*""(?:https?:)?//", RegexOptions.IgnoreCase))
        {
            violations.Add("absolute-src");
        }

        if (html.Contains("<script", StringComparison.OrdinalIgnoreCase))
        {
            violations.Add("script-element");
        }

        if (html.Contains("<iframe", StringComparison.OrdinalIgnoreCase))
        {
            violations.Add("iframe-element");
        }

        // Ein <link> ist nur zulaessig, wenn er auf die eigene Domain zeigt. Ein fremder
        // Stylesheet-/Font-Verweis (Google Fonts & Co.) waere genau der klassische Verstoss.
        if (Regex.IsMatch(html, @"<link[^>]*href\s*=\s*""(?:https?:)?//", RegexOptions.IgnoreCase))
        {
            violations.Add("external-link-element");
        }

        if (Regex.IsMatch(html, @"\son(click|load|error|mouseover|focus)\s*=", RegexOptions.IgnoreCase))
        {
            violations.Add("inline-event-handler");
        }

        return violations;
    }

    [Fact]
    public void Fragment_references_no_third_party_resource()
    {
        var html = CreateRenderer().RenderFragment(Snapshot(Post("111")));

        Assert.Empty(Violations(html));
        // Das Fragment wird in eine bestehende Seite eingefuegt - es darf deren <head> nicht anfassen.
        Assert.DoesNotContain("<link", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<!doctype", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Detector_actually_catches_a_cdn_url_as_image_source()
    {
        // Gegenprobe: derselbe Pruefer muss anschlagen, wenn eine Meta-CDN-URL im Markup landet.
        // Ohne diesen Fall koennte "keine Verstoesse" auch heissen "der Test prueft nichts".
        var html = CreateRenderer().RenderFragment(
            Snapshot(Post("111", localPath: "https://scontent.cdninstagram.com/v/t51/111.jpg")));

        var violations = Violations(html);

        Assert.Contains("host:cdninstagram.com", violations);
        Assert.Contains("absolute-src", violations);
    }

    [Fact]
    public void Fragment_uses_local_paths_and_lazy_loading()
    {
        var html = CreateRenderer().RenderFragment(Snapshot(Post("111")));

        Assert.Contains("src=\"/media/instagram/111.jpg\"", html);
        Assert.Contains("loading=\"lazy\"", html);
    }

    [Fact]
    public void Permalink_is_only_a_link_with_rel_attributes()
    {
        var html = CreateRenderer().RenderFragment(Snapshot(Post("111")));

        Assert.Contains("href=\"https://www.instagram.com/p/AAA/\"", html);
        Assert.Contains("rel=\"noopener noreferrer nofollow\"", html);
        Assert.Contains("target=\"_blank\"", html);
    }

    [Fact]
    public void Caption_is_rendered_as_text_and_escaped()
    {
        var html = CreateRenderer().RenderFragment(
            Snapshot(Post("111") with { Caption = "<b>fett</b> & <script>alert(1)</script>" }));

        Assert.Contains("&lt;b&gt;fett&lt;/b&gt;", html);
        Assert.DoesNotContain("<b>fett</b>", html);
        Assert.DoesNotContain("<script>", html);
    }

    [Fact]
    public void Placeholder_is_rendered_when_media_is_missing()
    {
        var html = CreateRenderer().RenderFragment(Snapshot(Post("111", localPath: null)));

        Assert.Contains("igfeed__media--empty", html);
        Assert.Empty(Violations(html));
    }

    [Fact]
    public void Empty_snapshot_renders_a_hint_instead_of_a_broken_grid()
    {
        var html = CreateRenderer().RenderFragment(new InstagramFeedSnapshot());

        Assert.Contains("keine Beitraege", html);
        Assert.DoesNotContain("igfeed__grid", html);
    }

    [Fact]
    public void Embed_script_talks_only_to_the_own_domain()
    {
        var script = CreateRenderer().RenderEmbedScript();

        Assert.DoesNotContain("cdninstagram", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fbcdn", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("instagram.com", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("BASE = \"/instagram\"", script);
        Assert.Contains("\"/feed.json\"", script);
        Assert.Contains("credentials: \"omit\"", script);
    }

    [Fact]
    public void Standalone_page_keeps_the_invariants()
    {
        var page = CreateRenderer().RenderStandalonePage(Snapshot(Post("111")));

        Assert.Contains("<!doctype html>", page);
        Assert.Contains("noindex", page);
        Assert.Empty(Violations(page));
    }
}
