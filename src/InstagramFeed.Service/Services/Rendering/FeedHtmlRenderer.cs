using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using InstagramFeed.Service.Configuration;
using InstagramFeed.Service.Domain;

namespace InstagramFeed.Service.Services.Rendering;

/// <summary>
/// Erzeugt das HTML-Fragment, das Stylesheet und das optionale Einbindungs-Skript.
/// Reine Funktionen ohne Infrastruktur - damit sind die DSGVO-Eigenschaften der Ausgabe
/// automatisiert pruefbar (Tests in FeedHtmlRendererTests).
/// </summary>
public sealed class FeedHtmlRenderer
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    private readonly InstagramOptions _options;

    public FeedHtmlRenderer(IOptions<InstagramOptions> options) => _options = options.Value;

    public string BasePath => _options.PublicBasePath.TrimEnd('/');

    public string RenderFragment(InstagramFeedSnapshot? snapshot)
    {
        var posts = snapshot?.Posts ?? Array.Empty<InstagramPost>();
        var html = new StringBuilder();

        html.Append("<section class=\"igfeed\" data-igfeed>");
        html.Append("<h2 class=\"igfeed__title\">").Append(Encode(_options.Title ?? "Aktuelles von Instagram")).Append("</h2>");

        if (posts.Count == 0)
        {
            html.Append("<p class=\"igfeed__empty\">Zurzeit sind keine Beitraege verfuegbar.</p>");
        }
        else
        {
            html.Append("<div class=\"igfeed__grid\">");
            foreach (var post in posts)
            {
                AppendCard(html, post);
            }

            html.Append("</div>");
        }

        html.Append("<p class=\"igfeed__note\">Diese Galerie laedt keine Inhalte von Instagram nach. ")
            .Append("Erst mit dem Klick auf einen Beitrag verlassen Sie unsere Website.</p>");

        if (!string.IsNullOrWhiteSpace(_options.ProfileUrl))
        {
            html.Append("<p class=\"igfeed__more\"><a href=\"").Append(Encode(_options.ProfileUrl))
                .Append("\" target=\"_blank\" rel=\"noopener noreferrer nofollow\">Alle Beitraege auf Instagram</a></p>");
        }

        html.Append("</section>");
        return html.ToString();
    }

    public string RenderStandalonePage(InstagramFeedSnapshot? snapshot)
    {
        var html = new StringBuilder();
        html.Append("<!doctype html>\n<html lang=\"de\">\n<head>\n<meta charset=\"utf-8\">\n")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
            .Append("<meta name=\"robots\" content=\"noindex, nofollow\">\n")
            .Append("<title>").Append(Encode(_options.Title ?? "Instagram-Feed")).Append("</title>\n")
            .Append("<link rel=\"stylesheet\" href=\"").Append(Encode(BasePath)).Append("/style.css\">\n")
            .Append("</head>\n<body>\n")
            .Append(RenderFragment(snapshot))
            .Append("\n</body>\n</html>\n");
        return html.ToString();
    }

    private static void AppendCard(StringBuilder html, InstagramPost post)
    {
        html.Append("<article class=\"igfeed__card\">");

        var hasImage = !string.IsNullOrWhiteSpace(post.LocalMediaPath);
        var alt = Encode(post.AltText ?? "Instagram-Beitrag");

        if (hasImage)
        {
            var imageTag = $"<img class=\"igfeed__img\" src=\"{Encode(post.LocalMediaPath!)}\" alt=\"{alt}\" " +
                           "loading=\"lazy\" decoding=\"async\">";

            if (!string.IsNullOrWhiteSpace(post.Permalink))
            {
                html.Append("<a class=\"igfeed__media\" href=\"").Append(Encode(post.Permalink))
                    .Append("\" target=\"_blank\" rel=\"noopener noreferrer nofollow\">")
                    .Append(imageTag).Append("</a>");
            }
            else
            {
                html.Append("<span class=\"igfeed__media\">").Append(imageTag).Append("</span>");
            }
        }
        else
        {
            html.Append("<span class=\"igfeed__media igfeed__media--empty\">Beitrag nicht verfuegbar</span>");
        }

        html.Append("<div class=\"igfeed__text\">");

        if (!string.IsNullOrWhiteSpace(post.Caption))
        {
            html.Append("<p class=\"igfeed__caption\">").Append(Encode(post.Caption!)).Append("</p>");
        }

        if (post.TimestampUtc != DateTimeOffset.UnixEpoch)
        {
            html.Append("<time class=\"igfeed__date\" datetime=\"")
                .Append(post.TimestampUtc.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .Append("\">")
                .Append(Encode(post.TimestampUtc.ToLocalTime().ToString("d. MMMM yyyy", German)))
                .Append("</time>");
        }

        html.Append("</div></article>");
    }

    public string RenderStyle() => """
        .igfeed { font-family: inherit; color: inherit; margin: 0; }
        .igfeed__title { font-size: 1.35rem; font-weight: 600; margin: 0 0 1rem; }
        .igfeed__grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(230px, 1fr)); gap: 1rem; }
        .igfeed__card { border: 1px solid rgba(128, 128, 128, 0.28); border-radius: 10px; overflow: hidden;
            display: flex; flex-direction: column; background: transparent; }
        .igfeed__media { display: block; aspect-ratio: 1 / 1; background: rgba(128, 128, 128, 0.10);
            line-height: 0; }
        .igfeed__media--empty { display: flex; align-items: center; justify-content: center;
            font-size: 0.85rem; opacity: 0.65; line-height: 1.4; text-align: center; padding: 1rem; }
        .igfeed__img { width: 100%; height: 100%; object-fit: cover; display: block; }
        .igfeed__text { padding: 0.65rem 0.75rem 0.85rem; }
        .igfeed__caption { margin: 0 0 0.4rem; font-size: 0.92rem; line-height: 1.35;
            display: -webkit-box; -webkit-line-clamp: 4; -webkit-box-orient: vertical; overflow: hidden; }
        .igfeed__date { font-size: 0.78rem; opacity: 0.65; }
        .igfeed__note { margin: 1rem 0 0.25rem; font-size: 0.78rem; opacity: 0.65; }
        .igfeed__more { margin: 0.25rem 0 0; font-size: 0.9rem; }
        """;

    /// <summary>
    /// Optionales Einbindungs-Skript fuer Seiten, die serverseitiges Einbinden nicht koennen.
    /// Es holt ausschliesslich Dateien von der eigenen Domain und baut die Kacheln mit textContent,
    /// damit keine Inhalte als HTML interpretiert werden.
    /// </summary>
    public string RenderEmbedScript()
    {
        var basePath = BasePath;

        return $$"""
            (function () {
              "use strict";
              var BASE = "{{basePath}}";

              function el(tag, className, text) {
                var node = document.createElement(tag);
                if (className) { node.className = className; }
                if (text !== undefined && text !== null) { node.textContent = text; }
                return node;
              }

              function ensureStyle() {
                if (document.querySelector("link[data-igfeed-style]")) { return; }
                var link = document.createElement("link");
                link.rel = "stylesheet";
                link.href = BASE + "/style.css";
                link.setAttribute("data-igfeed-style", "1");
                document.head.appendChild(link);
              }

              function card(post) {
                var article = el("article", "igfeed__card");
                var media = el(post.permalink ? "a" : "span", "igfeed__media");

                if (post.localMediaPath) {
                  var img = el("img", "igfeed__img");
                  img.src = post.localMediaPath;
                  img.alt = post.altText || "Instagram-Beitrag";
                  img.loading = "lazy";
                  img.decoding = "async";
                  media.appendChild(img);
                } else {
                  media.className += " igfeed__media--empty";
                  media.textContent = "Beitrag nicht verfuegbar";
                }

                if (post.permalink) {
                  media.href = post.permalink;
                  media.target = "_blank";
                  media.rel = "noopener noreferrer nofollow";
                }

                article.appendChild(media);

                var text = el("div", "igfeed__text");
                if (post.caption) { text.appendChild(el("p", "igfeed__caption", post.caption)); }
                if (post.timestampUtc && post.timestampUtc !== "1970-01-01T00:00:00+00:00") {
                  var time = el("time", "igfeed__date");
                  time.dateTime = post.timestampUtc;
                  time.textContent = new Date(post.timestampUtc).toLocaleDateString("de-DE",
                    { year: "numeric", month: "long", day: "numeric" });
                  text.appendChild(time);
                }

                article.appendChild(text);
                return article;
              }

              function render(host, data) {
                ensureStyle();
                var section = el("section", "igfeed");
                section.appendChild(el("h2", "igfeed__title", host.getAttribute("data-igfeed-title") || "Aktuelles von Instagram"));

                var posts = (data && data.posts) || [];
                if (posts.length === 0) {
                  section.appendChild(el("p", "igfeed__empty", "Zurzeit sind keine Beitraege verfuegbar."));
                } else {
                  var grid = el("div", "igfeed__grid");
                  posts.forEach(function (post) { grid.appendChild(card(post)); });
                  section.appendChild(grid);
                }

                section.appendChild(el("p", "igfeed__note",
                  "Diese Galerie laedt keine Inhalte von Instagram nach. Erst mit dem Klick auf einen Beitrag verlassen Sie unsere Website."));

                var profile = host.getAttribute("data-igfeed-profile");
                if (profile) {
                  var more = el("p", "igfeed__more");
                  var link = el("a", null, "Alle Beitraege auf Instagram");
                  link.href = profile;
                  link.target = "_blank";
                  link.rel = "noopener noreferrer nofollow";
                  more.appendChild(link);
                  section.appendChild(more);
                }

                host.replaceChildren(section);
              }

              var hosts = document.querySelectorAll("[data-igfeed-embed]");
              Array.prototype.forEach.call(hosts, function (host) {
                fetch(BASE + "/feed.json", { credentials: "omit" })
                  .then(function (response) { return response.json(); })
                  .then(function (data) { render(host, data); })
                  .catch(function () {
                    host.replaceChildren(el("p", "igfeed__empty", "Beitraege konnten nicht geladen werden."));
                  });
              });
            })();
            """;
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
