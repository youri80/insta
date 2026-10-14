namespace InstagramFeed.Service.Configuration;

/// <summary>
/// Konfiguration des Instagram-Feed-Dienstes. Wird aus dem Abschnitt "Instagram" gebunden.
/// Secrets (Token, AppSecret) gehoeren NICHT in appsettings.json, sondern in Umgebungsvariablen
/// (Instagram__InitialAccessToken) oder in die Datenschutz-sichere Secret-Ablage des Hosters.
/// </summary>
public sealed class InstagramOptions
{
    public const string SectionName = "Instagram";

    /// <summary>Instagram-User-ID. Bei "Instagram API with Instagram Login" genuegt "me".</summary>
    public string UserId { get; set; } = "me";

    /// <summary>Long-Lived Instagram User Access Token (60 Tage). Wird beim ersten Start in den TokenStore uebernommen.</summary>
    public string? InitialAccessToken { get; set; }

    /// <summary>App Secret. Nur noetig, wenn der Startwert noch ein Short-Lived-Token (1 h) ist.</summary>
    public string? AppSecret { get; set; }

    /// <summary>Graph-Host. "https://graph.instagram.com" (Instagram Login) oder "https://graph.facebook.com" (Facebook Login).</summary>
    public string Host { get; set; } = "https://graph.instagram.com";

    /// <summary>API-Version der Meta-App.</summary>
    public string ApiVersion { get; set; } = "v25.0";

    /// <summary>Mindestabstand zwischen zwei Abrufen. Instagram cacht Medienseiten selbst ~1 h.</summary>
    public TimeSpan MinSyncInterval { get; set; } = TimeSpan.FromMinutes(60);

    /// <summary>Token erneuern, sobald weniger als diese Restlaufzeit verbleibt.</summary>
    public TimeSpan RefreshWhenLessThan { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Vorgabe-Laufzeit eines Long-Lived-Tokens (60 Tage), wenn die API keine Angabe liefert.</summary>
    public TimeSpan AssumedTokenLifetime { get; set; } = TimeSpan.FromDays(60);

    /// <summary>Anzahl der Beitraege im Feed (1..80).</summary>
    public int MaxPosts { get; set; } = 12;

    /// <summary>Verzeichnis fuer Cache und Token (relativ zum Arbeitsverzeichnis oder absolut).</summary>
    public string DataDirectory { get; set; } = "data";

    /// <summary>
    /// Verzeichnis fuer den Data-Protection-Schluesselbund. MUSS bei Containern auf ein
    /// persistentes Volume zeigen, sonst ist der gespeicherte Token nach einem Neustart unlesbar.
    /// </summary>
    public string KeyDirectory { get; set; } = "keys";

    /// <summary>Verzeichnis der gespiegelten Bilder.</summary>
    public string MediaDirectory { get; set; } = "wwwroot/media/instagram";

    /// <summary>URL-Praefix, unter dem die Bilder ausgeliefert werden.</summary>
    public string MediaUrlPrefix { get; set; } = "/media/instagram";

    /// <summary>Oeffentlicher Pfad, unter dem der Dienst seine Endpunkte anbietet.</summary>
    public string PublicBasePath { get; set; } = "/instagram";

    /// <summary>Oeffentliche Profil-URL fuer den "Alle Beitraege"-Link.</summary>
    public string? ProfileUrl { get; set; }

    /// <summary>Anzeigename/Betreiber fuer die Ueberschrift der Galerie.</summary>
    public string? Title { get; set; } = "Aktuelles von Instagram";

    /// <summary>Key fuer POST /instagram/sync. Leer = Endpunkt abgeschaltet (404).</summary>
    public string? SyncKey { get; set; }

    /// <summary>Nur fuer Entwicklung/Tests: erzeugt Beispielbeitraege mit lokalen SVG-Bildern, kein API-Zugriff.</summary>
    public bool UseDemoData { get; set; }

    /// <summary>Anzahl der Demo-Beitraege.</summary>
    public int DemoPostCount { get; set; } = 6;

    /// <summary>Maximale Groesse einer einzelnen Mediendatei in Bytes.</summary>
    public long MaxMediaBytes { get; set; } = 15 * 1024 * 1024;
}
