using InstagramFeed.Service.Domain;
using InstagramFeed.Service.Services.Instagram;

namespace InstagramFeed.Tests.Api;

public sealed class InstagramMediaMapperTests
{
    [Fact]
    public void Carousel_children_get_their_own_file_keys()
    {
        var node = new InstagramMediaNode
        {
            Id = "100",
            MediaType = "CAROUSEL_ALBUM",
            Timestamp = "2026-10-12T08:14:00+0000",
            Children = new InstagramChildren
            {
                Data =
                {
                    new InstagramMediaNode { Id = "100a", MediaType = "IMAGE", MediaUrl = "https://scontent.cdninstagram.com/a.jpg" },
                    new InstagramMediaNode { Id = "100b", MediaType = "IMAGE", MediaUrl = "https://scontent.cdninstagram.com/b.jpg" }
                }
            }
        };

        var mapped = InstagramMediaMapper.Map(node);

        Assert.NotNull(mapped);
        Assert.Equal(MediaKind.Carousel, mapped!.Kind);
        Assert.Equal(2, mapped.Targets.Count);
        Assert.Equal("100-100a", mapped.Targets[0].FileKey);
        Assert.Equal("100-100b", mapped.Targets[1].FileKey);
    }

    [Fact]
    public void Video_without_media_url_falls_back_to_thumbnail()
    {
        var node = new InstagramMediaNode
        {
            Id = "200",
            MediaType = "VIDEO",
            ThumbnailUrl = "https://scontent.cdninstagram.com/thumb.jpg"
        };

        var mapped = InstagramMediaMapper.Map(node);

        Assert.NotNull(mapped);
        Assert.Equal(MediaKind.Video, mapped!.Kind);
        Assert.Equal("https://scontent.cdninstagram.com/thumb.jpg", Assert.Single(mapped.Targets).RemoteUrl);
    }

    [Fact]
    public void Post_without_any_media_still_exists_as_a_card()
    {
        // Reels mit urheberrechtlich geflaggtem Audio liefern weder media_url noch thumbnail_url.
        var node = new InstagramMediaNode { Id = "300", MediaType = "VIDEO", Permalink = "https://www.instagram.com/reel/XYZ/" };

        var mapped = InstagramMediaMapper.Map(node);

        Assert.NotNull(mapped);
        Assert.Empty(mapped!.Targets);
        Assert.Equal("XYZ", mapped.Shortcode);
    }

    [Fact]
    public void Timestamp_with_offset_without_colon_is_parsed_as_utc()
    {
        var parsed = InstagramMediaMapper.ParseTimestamp("2026-10-12T08:14:00+0000");

        Assert.Equal(new DateTimeOffset(2026, 10, 12, 8, 14, 0, TimeSpan.Zero), parsed);
    }

    [Theory]
    [InlineData("https://www.instagram.com/p/CxYzAbCdEfg/", "CxYzAbCdEfg")]
    [InlineData("https://www.instagram.com/reel/CxYzAbCdEfg/", "CxYzAbCdEfg")]
    [InlineData("https://www.instagram.com/tv/CxYzAbCdEfg/", "CxYzAbCdEfg")]
    [InlineData(null, null)]
    public void Shortcode_is_extracted_from_permalink(string? permalink, string? expected)
    {
        Assert.Equal(expected, InstagramMediaMapper.ExtractShortcode(permalink));
    }
}
