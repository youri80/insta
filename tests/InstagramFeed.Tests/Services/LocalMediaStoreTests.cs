using System.Net;
using InstagramFeed.Service.Services.Media;
using InstagramFeed.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace InstagramFeed.Tests.Services;

public sealed class LocalMediaStoreTests : IDisposable
{
    private readonly string _root = TestFactory.NewTempDirectory();

    private (LocalMediaStore Store, StubHttpMessageHandler Handler) CreateStore(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var options = TestFactory.Options(o =>
        {
            o.MediaDirectory = Path.Combine(_root, "media");
            o.MediaUrlPrefix = "/media/instagram";
        });

        var handler = new StubHttpMessageHandler(responder);
        var http = new HttpClient(handler);

        return (new LocalMediaStore(http, options, TestFactory.Environment(_root), NullLogger<LocalMediaStore>.Instance), handler);
    }

    [Fact]
    public async Task Allowed_cdn_host_is_downloaded_and_returns_an_own_relative_path()
    {
        var (store, handler) = CreateStore(_ => StubHttpMessageHandler.Image());

        var outcome = await store.EnsureLocalAsync("123", "https://scontent.cdninstagram.com/v/t51/123.jpg?_nc_ht=x", CancellationToken.None);

        Assert.True(outcome.Downloaded);
        Assert.Equal("/media/instagram/123.jpg", outcome.LocalPath);
        Assert.True(File.Exists(Path.Combine(_root, "media", "123.jpg")));
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Existing_file_is_not_downloaded_again()
    {
        var (store, handler) = CreateStore(_ => StubHttpMessageHandler.Image());

        await store.EnsureLocalAsync("123", "https://scontent.cdninstagram.com/123.jpg", CancellationToken.None);
        var second = await store.EnsureLocalAsync("123", "https://scontent.cdninstagram.com/123.jpg", CancellationToken.None);

        Assert.False(second.Downloaded);
        Assert.Equal("/media/instagram/123.jpg", second.LocalPath);
        Assert.Equal(1, handler.CallCount);
    }

    [Theory]
    [InlineData("https://evil.example.com/steal.jpg")]           // falscher Host
    [InlineData("http://scontent.cdninstagram.com/plain.jpg")]   // kein https
    [InlineData("https://cdninstagram.com.angreifer.net/x.jpg")] // Suffix-Trick
    [InlineData("file:///etc/passwd")]
    public async Task Foreign_or_insecure_source_is_refused_without_a_request(string url)
    {
        var (store, handler) = CreateStore(_ => StubHttpMessageHandler.Image());

        var outcome = await store.EnsureLocalAsync("999", url, CancellationToken.None);

        Assert.Null(outcome.LocalPath);
        Assert.False(outcome.Downloaded);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Redirect_to_a_foreign_host_is_refused_mid_flight()
    {
        var calls = new List<string>();
        var (store, _) = CreateStore(request =>
        {
            calls.Add(request.RequestUri!.Host);
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri("https://evil.example.com/x.jpg");
            return redirect;
        });

        var outcome = await store.EnsureLocalAsync("777", "https://scontent.cdninstagram.com/x.jpg", CancellationToken.None);

        Assert.Null(outcome.LocalPath);
        Assert.Single(calls);
        Assert.Equal("scontent.cdninstagram.com", calls[0]);
    }

    [Fact]
    public async Task Response_that_is_not_an_image_is_refused()
    {
        var (store, _) = CreateStore(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>kein Bild</html>", System.Text.Encoding.UTF8, "text/html")
        });

        var outcome = await store.EnsureLocalAsync("555", "https://scontent.cdninstagram.com/x.jpg", CancellationToken.None);

        Assert.Null(outcome.LocalPath);
    }

    [Theory]
    [InlineData("../../../etc/passwd", "etcpasswd")]
    [InlineData("..", "media")]
    [InlineData("a/b/c", "abc")]
    public void File_keys_are_sanitized(string input, string expected)
    {
        Assert.Equal(expected, LocalMediaStore.SanitizeKey(input));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
