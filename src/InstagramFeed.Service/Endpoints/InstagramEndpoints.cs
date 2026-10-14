using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using InstagramFeed.Service.Configuration;
using InstagramFeed.Service.Domain;
using InstagramFeed.Service.Services.Feed;
using InstagramFeed.Service.Services.Media;
using InstagramFeed.Service.Services.Rendering;
using InstagramFeed.Service.Services.Sync;

namespace InstagramFeed.Service.Endpoints;

public static class InstagramEndpoints
{
    private static readonly Regex FileNamePattern = new(@"^[A-Za-z0-9_.\-]{1,120}$", RegexOptions.Compiled);

    public static void MapInstagramEndpoints(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<InstagramOptions>>().Value;
        var basePath = options.PublicBasePath.TrimEnd('/');
        var mediaPath = options.MediaUrlPrefix.TrimEnd('/');

        // Das Fragment ist der empfohlene Weg: die bestehende Seite bindet es serverseitig ein,
        // der Besucher-Browser sieht nur eigenes HTML.
        app.MapGet($"{basePath}/fragment", async (HttpContext context, IFeedRepository repository,
            FeedHtmlRenderer renderer, CancellationToken ct) =>
        {
            var snapshot = await repository.ReadAsync(ct);
            context.Response.Headers.CacheControl = "public, max-age=300";
            return Results.Text(renderer.RenderFragment(snapshot), "text/html; charset=utf-8");
        });

        app.MapGet($"{basePath}/preview", async (IFeedRepository repository, FeedHtmlRenderer renderer,
            CancellationToken ct) =>
        {
            var snapshot = await repository.ReadAsync(ct);
            return Results.Text(renderer.RenderStandalonePage(snapshot), "text/html; charset=utf-8");
        });

        app.MapGet($"{basePath}/feed.json", async (HttpContext context, IFeedRepository repository,
            CancellationToken ct) =>
        {
            var snapshot = await repository.ReadAsync(ct) ?? new InstagramFeedSnapshot();
            context.Response.Headers.CacheControl = "public, max-age=300";
            return Results.Json(snapshot, FeedJson.Options);
        });

        app.MapGet($"{basePath}/style.css", (HttpContext context, FeedHtmlRenderer renderer) =>
        {
            context.Response.Headers.CacheControl = "public, max-age=3600";
            return Results.Text(renderer.RenderStyle(), "text/css; charset=utf-8");
        });

        app.MapGet($"{basePath}/embed.js", (HttpContext context, FeedHtmlRenderer renderer) =>
        {
            context.Response.Headers.CacheControl = "public, max-age=3600";
            return Results.Text(renderer.RenderEmbedScript(), "application/javascript; charset=utf-8");
        });

        app.MapGet($"{mediaPath}/{{fileName}}", (string fileName, HttpContext context, IMediaStore media) =>
        {
            if (!FileNamePattern.IsMatch(fileName) || fileName.Contains("..", StringComparison.Ordinal))
            {
                return Results.NotFound();
            }

            var fullPath = Path.Combine(media.DirectoryPath, fileName);
            if (!File.Exists(fullPath))
            {
                return Results.NotFound();
            }

            context.Response.Headers.CacheControl = "public, max-age=86400";
            return Results.File(fullPath, ContentTypeFor(fileName));
        });

        // Manueller Anstoss. Ohne konfigurierten Key existiert der Endpunkt nicht (404 statt 401 -
        // ein 401 verraet, dass es hier etwas zu holen gibt).
        app.MapPost($"{basePath}/sync", async (HttpContext context, InstagramSyncService sync,
            IOptions<InstagramOptions> configured, CancellationToken ct) =>
        {
            if (!SecretEquals(configured.Value.SyncKey, context.Request.Headers["X-Sync-Key"].ToString()))
            {
                return Results.NotFound();
            }

            var force = !string.Equals(context.Request.Query["force"], "false", StringComparison.OrdinalIgnoreCase);
            var result = await sync.SyncAsync(force, ct);
            return Results.Json(result, FeedJson.Options, statusCode: result.Ok ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
        });

        app.MapGet("/healthz", (InstagramSyncService sync) =>
        {
            var last = sync.LastResult;
            var status = last is null ? "starting" : last.Ok ? "ok" : "degraded";

            return Results.Json(new
            {
                status,
                lastSyncUtc = last?.FinishedAtUtc,
                lastSyncStatus = last?.Status,
                posts = last?.PostCount ?? 0,
                message = last?.Message,
                tokenValidUntilUtc = sync.LastToken?.ExpiresAtUtc,
                tokenNote = sync.LastToken?.Note
            });
        });
    }

    internal static string ContentTypeFor(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".svg" => "image/svg+xml",
        _ => "application/octet-stream"
    };

    internal static bool SecretEquals(string? expected, string? provided)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(provided))
        {
            return false;
        }

        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(provided);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
