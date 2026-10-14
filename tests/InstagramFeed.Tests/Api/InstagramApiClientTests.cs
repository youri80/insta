using System.Net;
using InstagramFeed.Service.Services.Instagram;
using InstagramFeed.Tests.Fakes;

namespace InstagramFeed.Tests.Api;

public sealed class InstagramApiClientTests
{
    private const string TwoPageFirst =
        """
        {
          "data": [
            { "id": "1", "media_type": "IMAGE", "media_url": "https://scontent.cdninstagram.com/1.jpg", "permalink": "https://www.instagram.com/p/AAA/", "timestamp": "2026-10-01T08:00:00+0000" },
            { "id": "2", "media_type": "IMAGE", "media_url": "https://scontent.cdninstagram.com/2.jpg", "permalink": "https://www.instagram.com/p/BBB/", "timestamp": "2026-10-02T08:00:00+0000" },
            { "id": "3", "media_type": "VIDEO", "thumbnail_url": "https://scontent.cdninstagram.com/3.jpg", "permalink": "https://www.instagram.com/p/CCC/", "timestamp": "2026-10-03T08:00:00+0000" },
            { "id": "4", "media_type": "IMAGE", "media_url": "https://scontent.cdninstagram.com/4.jpg", "timestamp": "2026-10-04T08:00:00+0000" },
            { "id": "5", "media_type": "CAROUSEL_ALBUM", "children": { "data": [ { "id": "5a", "media_type": "IMAGE", "media_url": "https://scontent.cdninstagram.com/5a.jpg" } ] }, "timestamp": "2026-10-05T08:00:00+0000" }
          ],
          "paging": { "next": "https://graph.instagram.com/v25.0/me/media?after=CURSOR&access_token=x" }
        }
        """;

    private const string TwoPageSecond =
        """
        {
          "data": [
            { "id": "6", "media_type": "IMAGE", "media_url": "https://scontent.cdninstagram.com/6.jpg", "timestamp": "2026-10-06T08:00:00+0000" },
            { "id": "7", "media_type": "IMAGE", "media_url": "https://scontent.cdninstagram.com/7.jpg", "timestamp": "2026-10-07T08:00:00+0000" }
          ]
        }
        """;

    [Fact]
    public async Task Paging_follows_next_until_maxPosts_is_reached()
    {
        var handler = new StubHttpMessageHandler(request =>
            request.RequestUri!.ToString().Contains("after=CURSOR", StringComparison.Ordinal)
                ? StubHttpMessageHandler.Json(TwoPageSecond)
                : StubHttpMessageHandler.Json(TwoPageFirst));

        var client = TestFactory.CreateApiClient(handler);

        var result = await client.GetLatestMediaAsync("token", maxPosts: 7, CancellationToken.None);

        Assert.Equal(ApiOutcome.Success, result.Outcome);
        Assert.Equal(7, result.Items.Count);
        Assert.Equal(2, handler.CallCount);
        Assert.Contains("limit=7", handler.Requests[0]);
    }

    [Fact]
    public async Task Next_url_with_foreign_host_is_ignored()
    {
        var body = TwoPageFirst.Replace(
            "https://graph.instagram.com/v25.0/me/media?after=CURSOR&access_token=x",
            "https://evil.example.com/steal?after=CURSOR",
            StringComparison.Ordinal);

        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(body));
        var client = TestFactory.CreateApiClient(handler);

        var result = await client.GetLatestMediaAsync("token", maxPosts: 50, CancellationToken.None);

        Assert.Equal(ApiOutcome.Success, result.Outcome);
        Assert.Equal(5, result.Items.Count);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Error_code_190_is_reported_as_expired_token()
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(
            """{ "error": { "message": "Session has expired", "type": "OAuthException", "code": 190 } }""",
            HttpStatusCode.BadRequest));

        var client = TestFactory.CreateApiClient(handler);

        var result = await client.GetLatestMediaAsync("token", maxPosts: 12, CancellationToken.None);

        Assert.Equal(ApiOutcome.TokenExpired, result.Outcome);
    }

    [Fact]
    public async Task Broken_response_body_is_a_failure_and_not_an_exception()
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json("{ das ist kein json"));
        var client = TestFactory.CreateApiClient(handler);

        var result = await client.GetLatestMediaAsync("token", maxPosts: 12, CancellationToken.None);

        Assert.Equal(ApiOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task Token_refresh_returns_new_token_and_lifetime()
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(
            """{ "access_token": "fresh", "token_type": "bearer", "expires_in": 5184000 }"""));

        var client = TestFactory.CreateApiClient(handler);

        var result = await client.RefreshLongLivedTokenAsync("old", CancellationToken.None);

        Assert.Equal(ApiOutcome.Success, result.Outcome);
        Assert.Equal("fresh", result.AccessToken);
        Assert.Equal(TimeSpan.FromDays(60), result.Lifetime);
    }
}
