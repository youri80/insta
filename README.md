# Instagram-Feed für die eigene Homepage — DSGVO-konform

Ein eigenständiger .NET-Dienst, der die eigenen Instagram-Beiträge serverseitig abruft, die Bilder
im eigenen Speicher spiegelt und sie unter der eigenen Domain ausliefert. Der Browser des Besuchers
spricht dabei **nie** mit Servern von Meta — kein Fremd-Skript, kein iframe, keine Fremd-CDN, keine
Cookies. Deshalb braucht die Galerie **keinen Consent-Banner**.

* Kein Blazor, kein CMS-Zwang: die bestehende Homepage bindet entweder ein HTML-Fragment
  serverseitig ein, nutzt `feed.json`, oder — auf Webspace ohne .NET — einen **Statik-Export**.
* Getestet: 54 automatisierte Tests, darunter die Konformitäts-Invarianten (kein Fremdhost in der
  Auslieferung, kein `Set-Cookie`, CSP ohne Fremdfreigaben) und ein Integrationstest über die echte
  HTTP-Schicht.
* **Bedienungsanleitung für den Serverbetrieb: `docs/bedienung.md`** — Installation, Token,
  Cron-Export, Ausrollen auf die statische Seite, Fehlerbehebung, Datenschutz-Nachweis.

---

## 1. Warum der übliche Weg nicht konform ist

| Weg | Problem |
|---|---|
| Offizielles Embed (`embed.js` + Blockquote) | Lädt Skript und Bilder von `instagram.com` / `*.cdninstagram.com`; IP, User-Agent und Referrer der Besucher gehen an Meta (USA), Meta kann `datr`/`ig_did` setzen → **§ 25 Abs. 1 TDDDG**: einwilligungspflichtig, praktisch abmahnfähig |
| Widget-Dienste | gleicher Datenfluss plus zusätzlicher Auftragsverarbeiter |
| „API-basierte" Plugins ohne Bild-Cache | holen die Metadaten serverseitig, schreiben aber die **signierten CDN-URLs** ins HTML — die Bilder lädt dann doch der Browser des Besuchers von Meta |

Dieser Dienst schließt die letzte Lücke: `media_url` ist eine flüchtige, signierte URL und existiert
**nur** serverseitig. Im Datenmodell gibt es kein Feld für sie — was nicht im Modell steht, kann
nicht ins Markup geraten.

### Rechtliches in einem Absatz

Für die Anzeige werden keine personenbezogenen Daten der Besucher verarbeitet: keine
Fremdverbindung, kein Cookie, kein Skript, kein LocalStorage. § 25 TDDDG greift damit nicht, eine
Einwilligung ist nicht erforderlich. Der serverseitige Abruf der eigenen Inhalte stützt sich auf
**Art. 6 Abs. 1 lit. f DSGVO** und ist in der Datenschutzerklärung zu erwähnen
(`docs/datenschutz-textbaustein.md`). Der Klick auf einen Beitrag führt bewusst zu `instagram.com` —
ab da gelten die Datenschutzhinweise von Meta.

**Voraussetzung:** Instagram-**Professional Account** (Business/Creator). Die Basic Display API ist
seit Dezember 2024 abgeschaltet; die API liefert nur noch Daten professioneller Konten. Ein privates
Profil kann über die API nicht angebunden werden — dann bleibt nur eine manuell gepflegte Galerie.

---

## 2. Aufbau

```
InstagramSyncWorker (Hintergrunddienst, Intervall)
   └─ InstagramSyncService
        ├─ InstagramTokenService  → FileTokenStore (Data Protection, verschlüsselt)
        ├─ InstagramApiClient     → nur graph.instagram.com
        ├─ LocalMediaStore        → Download, Host-Freigabeliste, eigener Speicher
        └─ JsonFeedRepository     → data/feed.json (atomar geschrieben)

Auslieferung (nur eigene Domain):
   GET /instagram/fragment    HTML-Fragment zum serverseitigen Einbinden   ← empfohlen
   GET /instagram/feed.json   Rohdaten
   GET /instagram/embed.js    optionales Skript (eigene Domain, textContent)
   GET /instagram/style.css   Stylesheet
   GET /instagram/preview     Vorschauseite (noindex)
   GET /media/instagram/<id>  gespiegelte Bilder
   POST /instagram/sync       manueller Anstoß, nur mit X-Sync-Key
   GET /healthz               Zustand inkl. Token-Restlaufzeit
```

Entscheidungen mit Begründung: `docs/entscheidungen.md`.

---

## 3. Inbetriebnahme

### 3.1 Meta-App und Token

1. Instagram-Konto auf **Business oder Creator** umstellen (App → Einstellungen → Kontotyp).
2. Auf <https://developers.facebook.com/apps> eine App anlegen, Produkt **„Instagram"** mit
   **„Instagram API with Instagram Login"** hinzufügen (Host `graph.instagram.com`,
   Permission `instagram_business_basic`).
3. Im App-Dashboard einen **Access Token** für das eigene Konto erzeugen. Dabei entsteht ein
   **Short-Lived-Token (1 Stunde)** — den gegen einen Long-Lived-Token (60 Tage) tauschen:

   ```bash
   APP_ID=...            # App-ID
   APP_SECRET=...        # App-Secret (nie ins Repo!)
   SHORT=...             # Token aus dem Dashboard

   curl -s "https://graph.instagram.com/access_token?grant_type=ig_exchange_token&client_secret=$APP_SECRET&access_token=$SHORT"
   # -> { "access_token": "IGQV...", "token_type": "bearer", "expires_in": 5184000 }
   ```

4. **Vor jeder weiteren Arbeit prüfen, dass der Abruf läuft:**

   ```bash
   LONG=IGQV...
   curl -s "https://graph.instagram.com/me/media?fields=id,media_type,media_url,permalink,caption,timestamp,username&access_token=$LONG" \
     | head -c 400
   ```

   Kommt hier kein `data`-Array, ist die restliche Einrichtung zwecklos — dann ist das Konto
   vermutlich kein Professional Account oder die App hat die Berechtigung nicht.

### 3.2 Konfiguration

Alles unter dem Abschnitt `Instagram` (appsettings.json) — Secrets **ausschließlich** per
Umgebungsvariable:

```bash
export Instagram__InitialAccessToken="IGQV..."     # Long-Lived-Token
export Instagram__ProfileUrl="https://www.instagram.com/<profil>/"
export Instagram__SyncKey="$(openssl rand -hex 24)"
export Instagram__MaxPosts=12
export Instagram__Title="Aktuelles von Instagram"
```

| Schlüssel | Vorgabe | Bedeutung |
|---|---|---|
| `UserId` | `me` | Bei Instagram Login genügt `me` |
| `Host` / `ApiVersion` | `https://graph.instagram.com` / `v25.0` | Für „Facebook Login" stattdessen `https://graph.facebook.com` |
| `InitialAccessToken` | – | Startwert; danach liegt der Token verschlüsselt in `data/token.dat` |
| `AppSecret` | – | Nur nötig, wenn der Startwert noch ein Short-Lived-Token ist |
| `MaxPosts` | 12 | 1..80 |
| `MinSyncInterval` | 01:00:00 | Häufiger als stündlich bringt nichts, Instagram cacht selbst |
| `RefreshWhenLessThan` | 7 Tage | Ab dieser Restlaufzeit wird verlängert |
| `DataDirectory` / `MediaDirectory` | `data` / `wwwroot/media/instagram` | Bei Containern auf Volumes legen |
| `KeyDirectory` | `keys` | Data-Protection-Schlüsselbund — bei Containern auf ein persistentes Volume |
| `PublicBasePath` / `MediaUrlPrefix` | `/instagram` / `/media/instagram` | Müssen zum Reverse Proxy passen |
| `SyncKey` | – | Leer = `/instagram/sync` existiert nicht (404) |
| `UseDemoData` | false | Beispielinhalte ohne Token — nur zum Ausprobieren |

### 3.3 Starten

```bash
/opt/data/bin/dotnet run --project src/InstagramFeed.Service        # Entwicklung
ASPNETCORE_URLS=http://127.0.0.1:8080 dotnet InstagramFeed.Service.dll   # Release/Deploy
```

Ohne Token einmal ausprobieren:

```bash
Instagram__UseDemoData=true dotnet run --project src/InstagramFeed.Service
# -> http://localhost:5000/instagram/preview
```

---

## 4. Einbau in die bestehende Homepage

Die Homepage läuft **nicht** auf .NET — das ist kein Problem, es gibt drei Wege:

### A) Serverseitiges Fragment (empfohlen)

```php
<!-- in die Vorlage, an der Stelle der Galerie -->
<link rel="stylesheet" href="/instagram/style.css">
<?php readfile('/var/www/instagram-cache/snippet.html'); ?>
```

Kein JavaScript, keine Fremdverbindung, für Suchmaschinen sichtbar. Das Fragment enthält kein
`<head>`, kein `<script>`, kein `<link>` — es fasst die fremde Seite nicht an.

### B) `feed.json` selbst rendern

Für Templates, die selbst Schleifen bauen:

```php
$feed = json_decode(file_get_contents('https://eigene-domain.tld/instagram/feed.json'), true);
foreach ($feed['posts'] as $post) {
    printf('<a href="%s" target="_blank" rel="noopener noreferrer nofollow"><img src="%s" alt="%s"></a>',
        htmlspecialchars($post['permalink']),
        htmlspecialchars($post['localMediaPath']),
        htmlspecialchars($post['altText'] ?? ''));
}
```

### C) `embed.js` für Baukasten-/statische Seiten

```html
<div data-igfeed-embed data-igfeed-title="Aktuelles von Instagram"></div>
<script src="/instagram/embed.js" defer></script>
```

Das Skript lädt ausschließlich `/instagram/feed.json` und `/instagram/style.css` von der eigenen
Domain und baut die Kacheln mit `textContent` (kein `innerHTML`).

### D) Statische Website (Normalfall hier): Statik-Export

```bash
dotnet run --project src/InstagramFeed.Service -- export --to ./stage
# stage/ spiegelt die URL-Struktur der Website:
#   stage/instagram/       feed.json, style.css, snippet.html, index.html, embed.js
#   stage/media/instagram/ gespiegelte Bilder
```

Den Ordnerinhalt ausrollen (Ablage = Adresse, deshalb genügt Spiegeln):

```bash
rsync -a --delete ./stage/instagram/       /var/www/html/instagram/
rsync -a --delete ./stage/media/instagram/ /var/www/html/media/instagram/
```

Dann `stage/instagram/snippet.html` beim Bauen der Seite einsetzen und
`<link rel="stylesheet" href="/instagram/style.css">` in den `<head>` — fertig. Kein .NET beim
Hoster nötig; die DSGVO-Eigenschaften sind identisch. Den Export per Cron laufen lassen
(z. B. stündlich).

**Vollständige Schritt-für-Schritt-Anleitung für den Serverbetrieb: `docs/bedienung.md`.**

> Achtung: Der Export bildet die konfigurierten Pfade ab. Wer `PublicBasePath` oder
> `MediaUrlPrefix` umstellt, muss die Zielordner beim Ausrollen entsprechend anpassen — die
> Verweise im Fragment folgen der Konfiguration.

---

## 5. Betrieb

```bash
# Manuell aktualisieren (z. B. direkt nach einem neuen Beitrag)
curl -X POST -H "X-Sync-Key: $INSTAGRAM__SYNC_KEY" https://eigene-domain.tld/instagram/sync

# Zustand
curl -s https://eigene-domain.tld/healthz
# {"status":"ok","lastSyncStatus":"Aktualisiert","posts":12,"tokenValidUntilUtc":"2026-12-13T..."}
```

* **Token-Pflege ist automatisch:** Verlängert wird, sobald weniger als `RefreshWhenLessThan`
  verbleiben (Achtung: die API lehnt Tokens ab, die jünger als 24 h sind — der Dienst berücksichtigt
  das). Fällt die Verlängerung aus, läuft der alte Token weiter und `/healthz` meldet `degraded`.
* **Schlägt der Abruf fehl, bleibt der alte Feed stehen.** Eine leere API-Antwort löscht das Raster
  nie — das ist der Unterschied zwischen „Instagram hat ein Problem" und „unsere Website ist leer".
* **Logs** enthalten keine URLs und keine Token.
* **Eine Warnung bleibt bewusst stehen:** `XmlKeyManager[35]` — „der Schlüsselbund liegt
  unverschlüsselt auf Platte". Das ist der Vorgabezustand von Data Protection; geschützt wird der
  Token durch die Dateirechte (`600`) und dadurch, dass der Bund das Gerät nicht verlässt. Wer
  zusätzlich Verschlüsselung at rest braucht, verschlüsselt das Volume oder hinterlegt ein
  Zertifikat für `ProtectKeysWithCertificate`.
* **Data-Protection-Schlüssel persistent halten** (`/app/keys`), sonst ist `data/token.dat` nach
  einem Neustart unlesbar und der Dienst loggt einen klaren Fehler statt still zu schweigen.

### Deployment

* `Dockerfile` / `docker-compose.yml` — Container-Betrieb auf dem VPS (Volumes für `data` und
  `keys`).
* `deploy/nginx-location.conf` — Reverse-Proxy-Block; die Pfade `/instagram/` und
  `/media/instagram/` müssen frei sein bzw. angepasst werden.

> Hinweis: Das Dockerfile ist hier **nicht gebaut** worden (in dieser Umgebung gibt es keinen
> Docker-Daemon). Verifiziert ist der Release-Publish und dessen Start in `Production`
> (`dotnet publish -c Release` → Routen antworten, Bilder werden ausgeliefert).

---

## 6. Tests

```bash
dotnet build
dotnet test tests/InstagramFeed.Tests/InstagramFeed.Tests.csproj
```

Abgedeckt: Paging und Fehlercodes der API (`TokenExpired` bei Code 190), Karussell-/Video-Mapping,
Token-Verschlüsselung (Datei enthält keinen Klartext), Host-Freigabeliste und SSRF-Schutz beim
Medien-Download inklusive Zwischenweiterleitung, Sync-Verhalten (leere Antwort löscht nichts,
fehlendes Einzelbild bricht nichts ab, Mindestabstand), Rendering-Invarianten und die echte
HTTP-Auslieferung.

Der wichtigste Test ist die **Gegenprobe**: `Detector_actually_catches_a_cdn_url_as_image_source`
belegt, dass die Invariantenprüfung anschlägt, wenn eine Meta-CDN-URL im Markup landet — ohne ihn
hieße „keine Verstöße" womöglich nur „der Test prüft nichts".

---

## 7. Grenzen

* Ohne Professional Account kein API-Weg (siehe oben).
* Reels mit urheberrechtlich geflaggtem Audio liefern kein `media_url` — die Kachel zeigt dann nur
  das Vorschaubild oder den Platzhalter.
* Das lokale Zwischenspeichern eigener Bilder ist der übliche und datenschutzfreundlichste Weg,
  bewegt sich bei fremden Profilen aber außerhalb der Instagram Platform Terms: für fremde Inhalte
  zusätzlich eine Zustimmung der Urheber einholen.
* Dieses Dokument ist keine Rechtsberatung.
