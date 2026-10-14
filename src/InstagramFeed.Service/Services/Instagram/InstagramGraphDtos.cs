using System.Text.Json.Serialization;

namespace InstagramFeed.Service.Services.Instagram;

// DTOs der Instagram-API. Feldnamen kommen in snake_case.

public sealed class InstagramMediaResponse
{
    [JsonPropertyName("data")] public List<InstagramMediaNode> Data { get; set; } = new();
    [JsonPropertyName("paging")] public InstagramPaging? Paging { get; set; }
}

public sealed class InstagramMediaNode
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("media_type")] public string? MediaType { get; set; }
    [JsonPropertyName("media_url")] public string? MediaUrl { get; set; }
    [JsonPropertyName("thumbnail_url")] public string? ThumbnailUrl { get; set; }
    [JsonPropertyName("permalink")] public string? Permalink { get; set; }
    [JsonPropertyName("caption")] public string? Caption { get; set; }
    [JsonPropertyName("timestamp")] public string? Timestamp { get; set; }
    [JsonPropertyName("username")] public string? Username { get; set; }
    [JsonPropertyName("children")] public InstagramChildren? Children { get; set; }
}

public sealed class InstagramChildren
{
    [JsonPropertyName("data")] public List<InstagramMediaNode> Data { get; set; } = new();
}

public sealed class InstagramPaging
{
    [JsonPropertyName("cursors")] public InstagramCursors? Cursors { get; set; }
    [JsonPropertyName("next")] public string? Next { get; set; }
}

public sealed class InstagramCursors
{
    [JsonPropertyName("before")] public string? Before { get; set; }
    [JsonPropertyName("after")] public string? After { get; set; }
}

public sealed class InstagramUserResponse
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("username")] public string? Username { get; set; }
}

public sealed class InstagramTokenResponse
{
    [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
    [JsonPropertyName("token_type")] public string? TokenType { get; set; }
    [JsonPropertyName("expires_in")] public long ExpiresIn { get; set; }
}

public sealed class InstagramErrorResponse
{
    [JsonPropertyName("error")] public InstagramError? Error { get; set; }
}

public sealed class InstagramError
{
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("error_subcode")] public int ErrorSubcode { get; set; }
}
