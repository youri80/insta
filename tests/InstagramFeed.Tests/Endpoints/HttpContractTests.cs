using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using InstagramFeed.Tests.Rendering;

namespace InstagramFeed.Tests.Endpoints;

/// <summary>
/// Startet die echte Anwendung (Demo-Daten, temporaere Verzeichnisse) und prueft die Auslieferung
/// so, wie ein Besucher sie sieht: Statuscodes, Header, Cookies, Inhalt.
/// </summary>
public sealed class FeedWebFactory : WebApplicationFactory<Program>
{
    private readonly string _root = TestFactory.NewTempDirectory();

    public string Root => _root;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseContentRoot(FindRepositoryRoot());
        builder.UseEnvironment("Production");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Instagram:UseDemoData"] = "true",
                ["Instagram:DemoPostCount"] = "3",
                ["Instagram:DataDirectory"] = Path.Combine(_root, "data"),
                ["Instagram:KeyDirectory"] = Path.Combine(_root, "keys"),
                ["Instagram:MediaDirectory"] = Path.Combine(_root, "media"),
                ["Instagram:SyncKey"] = "test-key",
                ["Instagram:ProfileUrl"] = "https://www.instagram.com/beispiel/"
            });
        });
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null &&
               !Directory.Exists(Path.Combine(directory.FullName, "src", "InstagramFeed.Service")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? AppContext.BaseDirectory;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing && Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

public sealed class HttpContractTests : IClassFixture<FeedWebFactory>
{
    private readonly FeedWebFactory _factory;

    public HttpContractTests(FeedWebFactory factory)
    {
        _factory = factory;
    }

    private async Task<HttpClient> ReadyClientAsync()
    {
        var client = _factory.CreateClient();

        var sync = new HttpRequestMessage(HttpMethod.Post, "/instagram/sync");
        sync.Headers.Add("X-Sync-Key", "test-key");
        var response = await client.SendAsync(sync);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        return client;
    }

    [Fact]
    public async Task Fragment_is_served_without_cookies_and_without_third_party_hosts()
    {
        var client = await ReadyClientAsync();

        var response = await client.GetAsync("/instagram/fragment");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Empty(FeedHtmlRendererTests.Violations(html));
        Assert.Contains("igfeed__card", html);
    }

    [Fact]
    public async Task Content_security_policy_forbids_external_sources()
    {
        var client = await ReadyClientAsync();

        var response = await client.GetAsync("/instagram/preview");
        var csp = string.Join("; ", response.Headers.GetValues("Content-Security-Policy"));

        Assert.Contains("default-src 'self'", csp);
        Assert.Contains("img-src 'self' data:", csp);
        Assert.DoesNotContain("unsafe-", csp);
        Assert.DoesNotContain("cdninstagram", csp);
        Assert.DoesNotContain("facebook", csp);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Mirrored_media_is_served_from_the_own_domain()
    {
        var client = await ReadyClientAsync();

        var response = await client.GetAsync("/media/instagram/demo-01.svg");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/svg+xml", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Path_traversal_in_media_url_is_refused()
    {
        var client = await ReadyClientAsync();

        var response = await client.GetAsync("/media/instagram/..%2F..%2Ffeed.json");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Sync_endpoint_stays_invisible_without_the_key()
    {
        var client = _factory.CreateClient();

        var withoutHeader = await client.PostAsync("/instagram/sync", null);
        var wrongKey = new HttpRequestMessage(HttpMethod.Post, "/instagram/sync");
        wrongKey.Headers.Add("X-Sync-Key", "falsch");
        var wrong = await client.SendAsync(wrongKey);

        Assert.Equal(System.Net.HttpStatusCode.NotFound, withoutHeader.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, wrong.StatusCode);
    }

    [Fact]
    public async Task Health_endpoint_reports_a_successful_sync_and_no_token_in_demo_mode()
    {
        var client = await ReadyClientAsync();

        var body = await client.GetStringAsync("/healthz");

        Assert.Contains("\"status\":\"ok\"", body);
        Assert.DoesNotContain("test-key", body);
    }

    [Fact]
    public async Task Feed_json_exposes_only_own_media_paths()
    {
        var client = await ReadyClientAsync();

        var json = await client.GetStringAsync("/instagram/feed.json");

        // Nicht auf Zeichenketten vertrauen, sondern die Struktur pruefen.
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var posts = document.RootElement.GetProperty("posts").EnumerateArray().ToList();
        Assert.Equal(3, posts.Count);

        foreach (var post in posts)
        {
            var path = post.GetProperty("localMediaPath").GetString();
            Assert.NotNull(path);
            Assert.StartsWith("/media/instagram/", path);
            Assert.DoesNotContain(".cdninstagram.com", path);
        }

        Assert.DoesNotContain("cdninstagram", json);
        Assert.DoesNotContain("fbcdn", json);
        Assert.DoesNotContain("http://", json);
    }
}
