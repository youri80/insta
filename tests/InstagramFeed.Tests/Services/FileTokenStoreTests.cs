using InstagramFeed.Service.Services.Instagram;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace InstagramFeed.Tests.Services;

public sealed class FileTokenStoreTests : IDisposable
{
    private readonly string _root = TestFactory.NewTempDirectory();

    private FileTokenStore CreateStore(string accessToken)
    {
        var options = TestFactory.Options(o => o.DataDirectory = Path.Combine(_root, "data"));
        var provider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_root, "keys")));

        return new FileTokenStore(provider, options, TestFactory.Environment(_root), NullLogger<FileTokenStore>.Instance);
    }

    [Fact]
    public async Task Token_is_read_back_unchanged()
    {
        var store = CreateStore("geheim");
        var expires = DateTimeOffset.UtcNow.AddDays(60);

        await store.WriteAsync(new StoredToken("geheim", expires), CancellationToken.None);
        var loaded = await store.ReadAsync(CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal("geheim", loaded!.AccessToken);
        Assert.Equal(expires, loaded.ExpiresAtUtc);
    }

    [Fact]
    public async Task Token_file_does_not_contain_the_plaintext_token()
    {
        const string secret = "IGQVJYb1ZAZA1234567890_SECRET";
        var provider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_root, "keys")));
        var options = TestFactory.Options(o => o.DataDirectory = Path.Combine(_root, "data"));
        var store = new FileTokenStore(provider, options, TestFactory.Environment(_root), NullLogger<FileTokenStore>.Instance);

        await store.WriteAsync(new StoredToken(secret, DateTimeOffset.UtcNow.AddDays(60)), CancellationToken.None);

        // Gegenprobe ueber den Datei-Inhalt, nicht ueber den Service - der Service koennte den
        // Token auch nur im Speicher halten und die Pruefung damit wertlos machen.
        var path = Path.Combine(_root, "data", "token.dat");
        Assert.True(File.Exists(path));
        Assert.False(store.PlaintextLeak(secret));
    }

    [Fact]
    public async Task Missing_file_returns_null_instead_of_throwing()
    {
        var store = CreateStore("egal");

        Assert.Null(await store.ReadAsync(CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
