using System.Net;
using InstagramFeed.Service.Domain;
using InstagramFeed.Service.Services.Demo;
using InstagramFeed.Service.Services.Feed;
using InstagramFeed.Service.Services.Instagram;
using InstagramFeed.Service.Services.Media;
using InstagramFeed.Service.Services.Sync;
using InstagramFeed.Tests.Fakes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace InstagramFeed.Tests.Services;

public sealed class InstagramSyncServiceTests : IDisposable
{
    private readonly string _root = TestFactory.NewTempDirectory();

    private sealed record Harness(
        InstagramSyncService Sync,
        IFeedRepository Repository,
        IMediaStore Media,
        StubHttpMessageHandler ApiHandler,
        StubHttpMessageHandler MediaHandler);

    private Harness Build(
        Func<HttpRequestMessage, HttpResponseMessage> apiResponder,
        Func<HttpRequestMessage, HttpResponseMessage>? mediaResponder = null,
        Action<InstagramFeed.Service.Configuration.InstagramOptions>? configure = null)
    {
        var options = TestFactory.Options(o =>
        {
            o.DataDirectory = Path.Combine(_root, "data");
            o.MediaDirectory = Path.Combine(_root, "media");
            o.InitialAccessToken = "start-token";
            o.ProfileUrl = "https://www.instagram.com/beispiel/";
            configure?.Invoke(o);
        });

        var environment = TestFactory.Environment(_root);
        var apiHandler = new StubHttpMessageHandler(apiResponder);
        var api = new InstagramApiClient(new HttpClient(apiHandler), options, NullLogger<InstagramApiClient>.Instance);

        var tokenStore = new FileTokenStore(
            DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_root, "keys"))),
            options, environment, NullLogger<FileTokenStore>.Instance);

        var tokens = new InstagramTokenService(tokenStore, api, options, TimeProvider.System, NullLogger<InstagramTokenService>.Instance);

        var mediaHandler = new StubHttpMessageHandler(mediaResponder ?? (_ => StubHttpMessageHandler.Image()));
        var media = new LocalMediaStore(new HttpClient(mediaHandler), options, environment, NullLogger<LocalMediaStore>.Instance);

        var repository = new JsonFeedRepository(options, environment, NullLogger<JsonFeedRepository>.Instance);
        var demo = new DemoFeedFactory(media, options, TimeProvider.System);

        var sync = new InstagramSyncService(api, tokens, media, repository, demo, options, TimeProvider.System,
            NullLogger<InstagramSyncService>.Instance);

        return new Harness(sync, repository, media, apiHandler, mediaHandler);
    }

    private const string TwoPosts =
        """
        {
          "data": [
            { "id": "111", "media_type": "IMAGE", "media_url": "https://scontent.cdninstagram.com/111.jpg",
              "permalink": "https://www.instagram.com/p/AAA/", "caption": "Erster Beitrag", "username": "beispiel",
              "timestamp": "2026-10-10T09:00:00+0000" },
            { "id": "222", "media_type": "CAROUSEL_ALBUM",
              "children": { "data": [
                { "id": "222a", "media_type": "IMAGE", "media_url": "https://scontent.cdninstagram.com/222a.jpg" },
                { "id": "222b", "media_type": "IMAGE", "media_url": "https://scontent.cdninstagram.com/222b.jpg" } ] },
              "permalink": "https://www.instagram.com/p/BBB/", "caption": "Karussell", "timestamp": "2026-10-11T09:00:00+0000" }
          ]
        }
        """;

    [Fact]
    public async Task Successful_run_writes_feed_and_mirrors_media_locally()
    {
        var harness = Build(_ => StubHttpMessageHandler.Json(TwoPosts));

        var result = await harness.Sync.SyncAsync(force: true, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(2, result.PostCount);
        Assert.Equal(3, result.NewMediaFiles);

        var snapshot = await harness.Repository.ReadAsync(CancellationToken.None);
        Assert.NotNull(snapshot);
        Assert.Equal("beispiel", snapshot!.Username);

        var first = snapshot.Posts.Single(p => p.Id == "111");
        Assert.Equal("/media/instagram/111.jpg", first.LocalMediaPath);

        var carousel = snapshot.Posts.Single(p => p.Id == "222");
        Assert.Equal("/media/instagram/222-222a.jpg", carousel.LocalMediaPath);
        Assert.Equal(new[] { "/media/instagram/222-222b.jpg" }, carousel.LocalChildPaths);
    }

    [Fact]
    public async Task Empty_api_response_does_not_wipe_the_existing_feed()
    {
        var harness = Build(_ => StubHttpMessageHandler.Json("""{ "data": [] }"""));

        await harness.Repository.WriteAsync(new InstagramFeedSnapshot
        {
            GeneratedAtUtc = DateTimeOffset.UtcNow.AddHours(-3),
            Username = "beispiel",
            Posts = new[]
            {
                new InstagramPost { Id = "alt", TimestampUtc = DateTimeOffset.UtcNow.AddDays(-5), LocalMediaPath = "/media/instagram/alt.jpg" }
            }
        }, CancellationToken.None);

        var result = await harness.Sync.SyncAsync(force: true, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("Degradiert", result.Status);

        var snapshot = await harness.Repository.ReadAsync(CancellationToken.None);
        Assert.Single(snapshot!.Posts);
        Assert.Equal("alt", snapshot.Posts[0].Id);
    }

    [Fact]
    public async Task Second_run_inside_the_minimum_interval_does_not_call_the_api()
    {
        var harness = Build(_ => StubHttpMessageHandler.Json(TwoPosts));

        await harness.Sync.SyncAsync(force: true, CancellationToken.None);
        var callsAfterFirst = harness.ApiHandler.CallCount;

        var second = await harness.Sync.SyncAsync(force: false, CancellationToken.None);

        Assert.Equal("Uebersprungen", second.Status);
        Assert.Equal(callsAfterFirst, harness.ApiHandler.CallCount);
    }

    [Fact]
    public async Task Failed_image_download_still_produces_a_card()
    {
        var harness = Build(
            _ => StubHttpMessageHandler.Json(TwoPosts),
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await harness.Sync.SyncAsync(force: true, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(2, result.PostCount);
        Assert.Equal(0, result.NewMediaFiles);

        var snapshot = await harness.Repository.ReadAsync(CancellationToken.None);
        Assert.All(snapshot!.Posts, post => Assert.Null(post.LocalMediaPath));
        Assert.Contains(snapshot.Posts, post => post.Caption == "Erster Beitrag");
    }

    [Fact]
    public async Task Expired_token_is_reported_and_the_old_feed_survives()
    {
        var harness = Build(_ => StubHttpMessageHandler.Json(
            """{ "error": { "code": 190, "message": "expired" } }""", HttpStatusCode.BadRequest));

        await harness.Repository.WriteAsync(new InstagramFeedSnapshot
        {
            GeneratedAtUtc = DateTimeOffset.UtcNow.AddHours(-3),
            Posts = new[] { new InstagramPost { Id = "alt", TimestampUtc = DateTimeOffset.UtcNow.AddDays(-1) } }
        }, CancellationToken.None);

        var result = await harness.Sync.SyncAsync(force: true, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("190", result.Message);
        Assert.Single((await harness.Repository.ReadAsync(CancellationToken.None))!.Posts);
    }

    [Fact]
    public async Task Demo_mode_produces_a_complete_feed_without_any_api_call()
    {
        var harness = Build(
            _ => throw new InvalidOperationException("Die API darf im Demo-Modus nicht aufgerufen werden."),
            configure: o =>
            {
                o.UseDemoData = true;
                o.InitialAccessToken = null;
                o.DemoPostCount = 4;
            });

        var result = await harness.Sync.SyncAsync(force: true, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(4, result.PostCount);
        Assert.Equal(0, harness.ApiHandler.CallCount);

        var snapshot = await harness.Repository.ReadAsync(CancellationToken.None);
        Assert.True(snapshot!.IsDemo);
        Assert.All(snapshot.Posts, post => Assert.StartsWith("/media/instagram/", post.LocalMediaPath));
        Assert.All(snapshot.Posts, post => Assert.True(File.Exists(Path.Combine(_root, "media", Path.GetFileName(post.LocalMediaPath!)))));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
