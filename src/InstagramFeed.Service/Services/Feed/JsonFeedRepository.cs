using System.Text.Json;
using Microsoft.Extensions.Options;
using InstagramFeed.Service.Configuration;
using InstagramFeed.Service.Domain;

namespace InstagramFeed.Service.Services.Feed;

public interface IFeedRepository
{
    string SnapshotPath { get; }

    Task<InstagramFeedSnapshot?> ReadAsync(CancellationToken ct);

    Task WriteAsync(InstagramFeedSnapshot snapshot, CancellationToken ct);
}

/// <summary>
/// Cache als JSON-Datei. Ein Feed hat maximal 80 Eintraege und genau eine Quelle - eine relationale
/// Datenbank waere reiner Ballast.
/// Der Schreibvorgang ist atomar: bricht der Sync ab, bleibt der bisherige Feed vollstaendig stehen
/// und das Raster zeigt keine halben Daten.
/// </summary>
public sealed class JsonFeedRepository : IFeedRepository
{
    private readonly ILogger<JsonFeedRepository> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonFeedRepository(
        IOptions<InstagramOptions> options,
        IHostEnvironment environment,
        ILogger<JsonFeedRepository> log)
    {
        _log = log;

        var dataDirectory = Path.IsPathRooted(options.Value.DataDirectory)
            ? options.Value.DataDirectory
            : Path.Combine(environment.ContentRootPath, options.Value.DataDirectory);

        Directory.CreateDirectory(dataDirectory);
        SnapshotPath = Path.Combine(dataDirectory, "feed.json");
    }

    public string SnapshotPath { get; }

    public async Task<InstagramFeedSnapshot?> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(SnapshotPath))
        {
            return null;
        }

        await _gate.WaitAsync(ct);
        try
        {
            var json = await File.ReadAllTextAsync(SnapshotPath, ct);
            return JsonSerializer.Deserialize<InstagramFeedSnapshot>(json, FeedJson.Options);
        }
        catch (JsonException ex)
        {
            _log.LogError(ex, "Cache-Datei {Path} ist beschaedigt und wird ignoriert.", SnapshotPath);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task WriteAsync(InstagramFeedSnapshot snapshot, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(snapshot, FeedJson.Options);
        var temp = SnapshotPath + ".tmp";

        await _gate.WaitAsync(ct);
        try
        {
            await File.WriteAllTextAsync(temp, json, ct);
            File.Move(temp, SnapshotPath, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }
}
