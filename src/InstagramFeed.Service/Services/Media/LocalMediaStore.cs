using Microsoft.Extensions.Options;
using InstagramFeed.Service.Configuration;

namespace InstagramFeed.Service.Services.Media;

public sealed record MediaFetchOutcome(string? LocalPath, bool Downloaded);

/// <summary>
/// Spiegelt Instagram-Medien in den eigenen Speicher. Der Besucher-Browser darf NIE eine
/// CDN-URL von Meta laden - deshalb ist dies der einzige Ort, an dem eine solche URL vorkommt,
/// und sie verlaesst den Prozess nie.
/// </summary>
public interface IMediaStore
{
    string UrlPrefix { get; }

    /// <summary>Absoluter Pfad des Medienverzeichnisses (fuer den Statik-Export).</summary>
    string DirectoryPath { get; }

    /// <summary>
    /// Stellt sicher, dass die Datei lokal liegt. Existiert sie schon, wird nicht erneut geladen.
    /// Gibt bei unzulaessiger Quelle null zurueck (kein Fehler, nur keine Datei).
    /// </summary>
    Task<MediaFetchOutcome> EnsureLocalAsync(string fileKey, string? remoteUrl, CancellationToken ct);

    /// <summary>Schreibt vorgegebene Bytes (Demo-Modus, spaetere Bildverarbeitung) und gibt den URL-Pfad zurueck.</summary>
    Task<string> StoreAsync(string fileKey, byte[] content, string contentType, CancellationToken ct);

    /// <summary>Prueft, ob bereits eine Datei zu diesem Schluessel existiert, ohne etwas zu tun.</summary>
    string? FindExisting(string fileKey);
}

public sealed class LocalMediaStore : IMediaStore
{
    private static readonly string[] AllowedHostSuffixes = { "cdninstagram.com", "fbcdn.net" };
    private static readonly string[] KnownExtensions = { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".svg" };

    private readonly HttpClient _http;
    private readonly InstagramOptions _options;
    private readonly string _directory;
    private readonly ILogger<LocalMediaStore> _log;

    public LocalMediaStore(
        HttpClient http,
        IOptions<InstagramOptions> options,
        IHostEnvironment environment,
        ILogger<LocalMediaStore> log)
    {
        _http = http;
        _options = options.Value;
        _log = log;

        _directory = Path.IsPathRooted(_options.MediaDirectory)
            ? _options.MediaDirectory
            : Path.Combine(environment.ContentRootPath, _options.MediaDirectory);

        Directory.CreateDirectory(_directory);
    }

    public string UrlPrefix => _options.MediaUrlPrefix.TrimEnd('/');

    public string DirectoryPath => _directory;

    public async Task<MediaFetchOutcome> EnsureLocalAsync(string fileKey, string? remoteUrl, CancellationToken ct)
    {
        var existing = FindExisting(fileKey);
        if (existing is not null)
        {
            return new MediaFetchOutcome(existing, false);
        }

        if (string.IsNullOrWhiteSpace(remoteUrl) || !Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri))
        {
            return new MediaFetchOutcome(null, false);
        }

        // Weiterleitungen selbst verfolgen, damit JEDER Hop einzeln geprueft wird. Mit
        // AllowAutoRedirect waere der Zielhost unsichtbar.
        for (var hop = 0; hop < 5; hop++)
        {
            if (!IsAllowedHost(uri))
            {
                _log.LogWarning("Medien-Download abgelehnt: Host nicht in der Freigabeliste ({Host}, {Scheme}).",
                    uri.Host, uri.Scheme);
                return new MediaFetchOutcome(null, false);
            }

            HttpResponseMessage response;
            try
            {
                response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                _log.LogWarning(ex, "Medien-Download fehlgeschlagen.");
                return new MediaFetchOutcome(null, false);
            }

            if (IsRedirect(response.StatusCode))
            {
                var location = response.Headers.Location;
                response.Dispose();

                if (location is null)
                {
                    return new MediaFetchOutcome(null, false);
                }

                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                _log.LogWarning("Medien-Download HTTP {Status}.", (int)response.StatusCode);
                response.Dispose();
                return new MediaFetchOutcome(null, false);
            }

            if (response.Content.Headers.ContentLength > _options.MaxMediaBytes)
            {
                _log.LogWarning("Medien-Datei ueberschreitet die Groessengrenze ({Bytes} Bytes).",
                    response.Content.Headers.ContentLength);
                response.Dispose();
                return new MediaFetchOutcome(null, false);
            }

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                _log.LogWarning("Medien-Antwort ist kein Bild ({ContentType}).", contentType);
                response.Dispose();
                return new MediaFetchOutcome(null, false);
            }

            byte[] payload;
            try
            {
                payload = await ReadWithLimitAsync(response, ct);
            }
            finally
            {
                response.Dispose();
            }

            var path = await StoreAsync(fileKey, payload, contentType, ct);
            return new MediaFetchOutcome(path, true);
        }

        _log.LogWarning("Medien-Download: zu viele Weiterleitungen.");
        return new MediaFetchOutcome(null, false);
    }

    public async Task<string> StoreAsync(string fileKey, byte[] content, string contentType, CancellationToken ct)
    {
        var safeKey = SanitizeKey(fileKey);
        var fileName = safeKey + ExtensionFor(contentType);
        var fullPath = Path.Combine(_directory, fileName);

        var temp = fullPath + ".tmp";
        await File.WriteAllBytesAsync(temp, content, ct);
        File.Move(temp, fullPath, overwrite: true);

        return $"{UrlPrefix}/{fileName}";
    }

    public string? FindExisting(string fileKey)
    {
        var safeKey = SanitizeKey(fileKey);

        foreach (var extension in KnownExtensions)
        {
            var candidate = Path.Combine(_directory, safeKey + extension);
            if (File.Exists(candidate))
            {
                return $"{UrlPrefix}/{safeKey}{extension}";
            }
        }

        return null;
    }

    private async Task<byte[]> ReadWithLimitAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        long total = 0;
        int read;

        while ((read = await source.ReadAsync(chunk, ct)) > 0)
        {
            total += read;
            if (total > _options.MaxMediaBytes)
            {
                throw new InvalidOperationException("Mediendatei ueberschreitet die Groessengrenze.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static bool IsAllowedHost(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps &&
        AllowedHostSuffixes.Any(suffix =>
            uri.Host.Equals(suffix, StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase));

    private static bool IsRedirect(System.Net.HttpStatusCode status) =>
        status is System.Net.HttpStatusCode.MovedPermanently
            or System.Net.HttpStatusCode.Found
            or System.Net.HttpStatusCode.SeeOther
            or System.Net.HttpStatusCode.TemporaryRedirect
            or System.Net.HttpStatusCode.PermanentRedirect;

    /// <summary>Nur harmlose Zeichen im Dateinamen - verhindert Pfad-Ausbrueche.</summary>
    public static string SanitizeKey(string fileKey)
    {
        var safe = new string(fileKey.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        return safe.Length == 0 ? "media" : safe;
    }

    public static string ExtensionFor(string contentType) => contentType.ToLowerInvariant() switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        "image/svg+xml" => ".svg",
        _ => ".bin"
    };
}
