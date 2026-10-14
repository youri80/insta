using System.Text.RegularExpressions;
using InstagramFeed.Service.Domain;
using InstagramFeed.Service.Services.Demo;
using InstagramFeed.Service.Services.Export;
using InstagramFeed.Service.Services.Feed;
using InstagramFeed.Service.Services.Instagram;
using InstagramFeed.Service.Services.Media;
using InstagramFeed.Service.Services.Rendering;
using InstagramFeed.Service.Services.Sync;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace InstagramFeed.Tests.Export;

/// <summary>
/// Prueft den Statik-Export. Kernfrage: findet ein Besucher nach dem Hochladen wirklich jede Datei
/// unter der Adresse, die das Fragment und das Skript nennen? Ein Export, der die Dateien anders
/// ablegt als er sie verlinkt, sieht auf dem Server korrekt aus und zeigt im Browser leere Kacheln.
/// </summary>
public sealed class StaticExporterTests : IDisposable
{
    private readonly string _root = TestFactory.NewTempDirectory();

    private StaticExporter Build(string mediaUrlPrefix = "/media/instagram", string publicBasePath = "/instagram")
    {
        var options = TestFactory.Options(o =>
        {
            o.DataDirectory = Path.Combine(_root, "data");
            o.MediaDirectory = Path.Combine(_root, "media");
            o.KeyDirectory = Path.Combine(_root, "keys");
            o.MediaUrlPrefix = mediaUrlPrefix;
            o.PublicBasePath = publicBasePath;
            o.ProfileUrl = "https://www.instagram.com/beispiel/";
            o.UseDemoData = true;
            o.DemoPostCount = 3;
            o.InitialAccessToken = null;
        });

        var environment = TestFactory.Environment(_root);
        var api = new InstagramApiClient(
            new HttpClient(new Fakes.StubHttpMessageHandler(_ => Fakes.StubHttpMessageHandler.Json("{ \"data\": [] }"))),
            options, NullLogger<InstagramApiClient>.Instance);

        var tokenStore = new FileTokenStore(
            DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_root, "keys-provider"))),
            options, environment, NullLogger<FileTokenStore>.Instance);

        var tokens = new InstagramTokenService(tokenStore, api, options, TimeProvider.System, NullLogger<InstagramTokenService>.Instance);
        var media = new LocalMediaStore(new HttpClient(), options, environment, NullLogger<LocalMediaStore>.Instance);
        var repository = new JsonFeedRepository(options, environment, NullLogger<JsonFeedRepository>.Instance);
        var demo = new DemoFeedFactory(media, options, TimeProvider.System);
        var sync = new InstagramSyncService(api, tokens, media, repository, demo, options, TimeProvider.System,
            NullLogger<InstagramSyncService>.Instance);

        return new StaticExporter(sync, repository, media, new FeedHtmlRenderer(options), NullLogger<StaticExporter>.Instance);
    }

    /// <summary>Sammelt alle relativen Verweise aus den ausgelieferten Dateien.</summary>
    private static IReadOnlyList<string> RelativeReferences(string directory, string basePath = "instagram")
    {
        var references = new List<string>();

        foreach (var name in new[] { "snippet.html", "index.html" })
        {
            var html = File.ReadAllText(Path.Combine(directory, basePath, name));
            references.AddRange(Regex.Matches(html, @"(?:src|href)=""(/[^""]*)""")
                .Select(match => match.Groups[1].Value));
        }

        return references.Distinct().ToList();
    }

    [Fact]
    public async Task Every_referenced_path_exists_in_the_export()
    {
        var exporter = Build();
        var target = Path.Combine(_root, "stage");

        var exitCode = await exporter.RunAsync(target, CancellationToken.None);

        Assert.Equal(0, exitCode);

        var references = RelativeReferences(target);
        Assert.NotEmpty(references);

        foreach (var reference in references)
        {
            var onDisk = Path.Combine(target, reference.TrimStart('/'));
            Assert.True(File.Exists(onDisk), $"Verweis {reference} zeigt ins Leere (erwartet: {onDisk})");
        }
    }

    [Fact]
    public async Task Snippet_references_images_under_the_configured_media_prefix()
    {
        var exporter = Build();
        var target = Path.Combine(_root, "stage");

        await exporter.RunAsync(target, CancellationToken.None);

        var snippet = File.ReadAllText(Path.Combine(target, "instagram", "snippet.html"));

        Assert.Contains("src=\"/media/instagram/demo-01.svg\"", snippet);
        Assert.True(File.Exists(Path.Combine(target, "media", "instagram", "demo-01.svg")));
    }

    [Fact]
    public async Task Layout_follows_a_changed_prefix()
    {
        // Wer die Pfade umstellt (z. B. damit alles unter /instagram/ liegt), darf sich darauf
        // verlassen, dass der Export mitzieht - sonst zeigt die Galerie nach dem Upload nichts.
        var exporter = Build(mediaUrlPrefix: "/instagram/media", publicBasePath: "/instagram");
        var target = Path.Combine(_root, "stage");

        await exporter.RunAsync(target, CancellationToken.None);

        var snippet = File.ReadAllText(Path.Combine(target, "instagram", "snippet.html"));
        Assert.Contains("src=\"/instagram/media/demo-01.svg\"", snippet);
        Assert.True(File.Exists(Path.Combine(target, "instagram", "media", "demo-01.svg")));
        Assert.True(File.Exists(Path.Combine(target, "instagram", "feed.json")));
        Assert.True(File.Exists(Path.Combine(target, "instagram", "style.css")));

        foreach (var reference in RelativeReferences(target))
        {
            Assert.True(File.Exists(Path.Combine(target, reference.TrimStart('/'))), $"Verweis {reference} zeigt ins Leere");
        }
    }

    [Fact]
    public async Task Export_contains_only_images_from_the_media_directory()
    {
        var exporter = Build();
        var target = Path.Combine(_root, "stage");

        // Stoerdatei, wie sie beim Betrieb entstehen kann: darf nicht mit ausgeliefert werden.
        await File.WriteAllTextAsync(Path.Combine(_root, "media", "zwischendatei.tmp"), "x", CancellationToken.None);
        Directory.CreateDirectory(Path.Combine(_root, "media", "unterordner"));

        await exporter.RunAsync(target, CancellationToken.None);

        var exported = Directory.EnumerateFiles(Path.Combine(target, "media", "instagram")).Select(Path.GetFileName).ToList();
        Assert.DoesNotContain("zwischendatei.tmp", exported);
        Assert.All(exported, name => Assert.EndsWith(".svg", name));
    }

    [Fact]
    public async Task Exported_feed_json_contains_no_foreign_urls()
    {
        var exporter = Build();
        var target = Path.Combine(_root, "stage");

        await exporter.RunAsync(target, CancellationToken.None);

        var json = await File.ReadAllTextAsync(Path.Combine(target, "instagram", "feed.json"));
        Assert.DoesNotContain("cdninstagram", json);
        Assert.DoesNotContain("fbcdn", json);
        Assert.DoesNotContain("http://", json);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
