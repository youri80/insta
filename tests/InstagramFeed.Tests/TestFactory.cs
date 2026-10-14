using InstagramFeed.Service.Configuration;
using InstagramFeed.Service.Services.Instagram;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InstagramFeed.Tests;

internal sealed class TestHostEnvironment : IHostEnvironment
{
    public TestHostEnvironment(string contentRoot) => ContentRootPath = contentRoot;

    public string ApplicationName { get; set; } = "InstagramFeed.Tests";
    public IFileProvider ContentRootFileProvider { get; set; } = null!;
    public string ContentRootPath { get; set; }
    public string EnvironmentName { get; set; } = "Test";
}

internal static class TestFactory
{
    public static IOptions<InstagramOptions> Options(Action<InstagramOptions>? configure = null)
    {
        var options = new InstagramOptions
        {
            MinSyncInterval = TimeSpan.FromMinutes(60),
            RefreshWhenLessThan = TimeSpan.FromDays(7)
        };

        configure?.Invoke(options);
        return Microsoft.Extensions.Options.Options.Create(options);
    }

    public static IHostEnvironment Environment(string contentRoot) => new TestHostEnvironment(contentRoot);

    /// <summary>Temporaeres Verzeichnis, das beim Entsorgen der Fixture wieder verschwindet.</summary>
    public static string NewTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "igfeed-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public static InstagramApiClient CreateApiClient(HttpMessageHandler handler, Action<InstagramOptions>? configure = null)
    {
        var options = Options(configure);
        return new InstagramApiClient(
            new HttpClient(handler),
            options,
            NullLogger<InstagramApiClient>.Instance);
    }
}
