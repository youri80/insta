using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using InstagramFeed.Service.Configuration;
using InstagramFeed.Service.Domain;

namespace InstagramFeed.Service.Services.Instagram;

public sealed record StoredToken(string AccessToken, DateTimeOffset ExpiresAtUtc);

public interface ITokenStore
{
    Task<StoredToken?> ReadAsync(CancellationToken ct);

    Task WriteAsync(StoredToken token, CancellationToken ct);
}

/// <summary>
/// Legt den Long-Lived-Token verschluesselt auf Platte (ASP.NET Core Data Protection).
/// Der Token ist ein Kontozugang und wird wie ein Passwort behandelt: nie im Klartext, nie im Log.
/// </summary>
public sealed class FileTokenStore : ITokenStore
{
    private const string ProtectorPurpose = "instagram.token.v1";

    private readonly IDataProtector _protector;
    private readonly string _path;
    private readonly ILogger<FileTokenStore> _log;

    public FileTokenStore(
        IDataProtectionProvider provider,
        IOptions<InstagramOptions> options,
        IHostEnvironment environment,
        ILogger<FileTokenStore> log)
    {
        _protector = provider.CreateProtector(ProtectorPurpose);
        _log = log;

        var dataDirectory = Path.IsPathRooted(options.Value.DataDirectory)
            ? options.Value.DataDirectory
            : Path.Combine(environment.ContentRootPath, options.Value.DataDirectory);

        Directory.CreateDirectory(dataDirectory);
        _path = Path.Combine(dataDirectory, "token.dat");
    }

    public async Task<StoredToken?> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            var protectedBytes = await File.ReadAllBytesAsync(_path, ct);
            var json = _protector.Unprotect(protectedBytes);
            return JsonSerializer.Deserialize<StoredToken>(json, FeedJson.Options);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            // Haeufigste Ursache: der Data-Protection-Schluesselbund hat sich geaendert (z. B. neuer
            // Container ohne persistentes Volume). Dann ist der Token nicht mehr lesbar - das ist ein
            // Konfigurationsfehler und muss sichtbar sein, nicht still zu "kein Token" werden.
            _log.LogError(ex, "Gespeicherter Token konnte nicht entschluesselt werden. " +
                              "Data-Protection-Schluessel persistent halten und Token neu setzen.");
            return null;
        }
    }

    public async Task WriteAsync(StoredToken token, CancellationToken ct)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(token, FeedJson.Options);
        var protectedBytes = _protector.Protect(json);

        var temp = _path + ".tmp";
        await File.WriteAllBytesAsync(temp, protectedBytes, ct);

        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning(ex, "Dateirechte des Token-Speichers konnten nicht gesetzt werden.");
            }
        }

        File.Move(temp, _path, overwrite: true);
    }

    /// <summary>Nur fuer Diagnose: enthaelt die Datei den Klartext-Token?</summary>
    public bool PlaintextLeak(string accessToken)
    {
        if (!File.Exists(_path) || string.IsNullOrEmpty(accessToken))
        {
            return false;
        }

        var bytes = File.ReadAllBytes(_path);
        return Encoding.UTF8.GetString(bytes).Contains(accessToken, StringComparison.Ordinal);
    }
}
