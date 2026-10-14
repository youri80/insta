# Bedienungsanleitung

Für den Betrieb des Dienstes auf einem Linux-Server. Die Zielwebsite ist **statisch** — deshalb ist
Betriebsart A (Cron + Export) der Normalfall: sie braucht keinen dauerhaft laufenden, erreichbaren
Prozess. Betriebsart B (Dauerbetrieb) steht am Ende und ist nur nötig, wenn die Galerie ohne
Deployment- Schritt aktuell bleiben soll.

Alle Befehle sind für einen normalen Server mit installiertem .NET 10 geschrieben. Läuft man sie in
einem Container mit eigenem SDK (z. B. der Hermes-Umgebung), statt `dotnet` den Wrapper
`/opt/data/bin/dotnet` verwenden.

---

## 1. Überblick

```
   Instagram ──(serverseitig, nur der Server)──► Dienst ──► feed.json + gespiegelte Bilder
                                                              │
                        ┌─────────────────────────────────────┘
                        ▼
        Webspace/statische Seite ◄── nur eigene Dateien ◄── Besucher-Browser
```

Der Dienst holt die Beiträge selbst, lädt die Bilder herunter und legt sie so ab, **wie die
Website sie braucht**. Der Besucher lädt ausschließlich Dateien der eigenen Domain.

| Betriebsart | Wann | Was läuft dauerhaft? |
|---|---|---|
| **A: Cron + Export** (Normalfall bei statischer Seite) | Seite wird bei jedem Deploy neu gebaut/hochgeladen | nur ein Cron-Job |
| **B: Dauerbetrieb** | Galerie soll zwischen Deployments aktuell sein | ein kleiner Dienst + Reverse-Proxy-Block |

---

## 2. Dateien und Verzeichnisse

```
/opt/instagram-feed/
├── app/                 Programmdateien (Publish-Ergebnis)
│   ├── InstagramFeed.Service.dll
│   ├── appsettings.json
│   └── .env             Ihre Zugangsdaten (Dateirechte 600!)
└── work/                Arbeitsverzeichnis (aus dem Publish aufgerufen)
    ├── data/feed.json   Zwischenspeicher des Feeds
    ├── data/token.dat   verschlüsselter Zugangstoken
    ├── keys/            Schlüsselbund für die Verschlüsselung (sichern!)
    └── media/           gespiegelte Bilder
```

> **`work/keys/` und `work/data/` gehören ins Backup.** Geht `keys/` verloren, ist `token.dat`
> unlesbar — dann muss der Token neu erzeugt werden (Abschnitt 7).

---

## 3. Einmalige Einrichtung

### 3.1 Voraussetzungen prüfen

Nötig ist ein **Professional Account** (Business oder Creator) auf Instagram — die API liefert für
private Konten keine Daten mehr. Ebenso nötig: eine Meta-App mit „Instagram API with Instagram
Login" und der Berechtigung `instagram_business_basic`.

Vor jeder Installation prüfen, dass der Abruf grundsätzlich funktioniert:

```bash
APP_SECRET=...      # App-Secret aus dem Meta-Dashboard
SHORT=...           # Token aus dem Meta-Dashboard (kurz gültig)

# 1) Kurz- gegen Langzeit-Token tauschen (60 Tage)
curl -s "https://graph.instagram.com/access_token?grant_type=ig_exchange_token&client_secret=$APP_SECRET&access_token=$SHORT"
# -> {"access_token":"IGQV...","token_type":"bearer","expires_in":5184000}

LONG=IGQV...        # Wert aus der Antwort

# 2) Beiträge abrufen
curl -s "https://graph.instagram.com/me/media?fields=id,media_type,media_url,permalink,caption,timestamp,username&access_token=$LONG" | head -c 400
```

Kommt hier kein `"data": [...]`, ist die weitere Einrichtung zwecklos: dann ist das Konto kein
Professional Account oder der App fehlt die Berechtigung.

### 3.2 Programm auf den Server bringen

```bash
sudo apt-get update && sudo apt-get install -y dotnet-runtime-10.0   # oder: dotnet-sdk-10.0 zum Bauen
# Alternative ohne apt:
#   curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --runtime aspnetcore

sudo mkdir -p /opt/instagram-feed/app /opt/instagram-feed/work
sudo chown -R "$USER" /opt/instagram-feed
git clone https://github.com/youri80/insta.git ~/insta
cd ~/insta && dotnet publish src/InstagramFeed.Service/InstagramFeed.Service.csproj -c Release -o /opt/instagram-feed/app
```

### 3.3 Zugangsdaten und Einstellungen

```bash
cat > /opt/instagram-feed/app/.env <<'EOF'
Instagram__InitialAccessToken=IGQV...hier-der-langzeit-token
Instagram__ProfileUrl=https://www.instagram.com/IHRPROFIL/
Instagram__Title=Aktuelles von Instagram
Instagram__MaxPosts=12
Instagram__SyncKey=hier-ein-zufaelliger-schluessel
Instagram__DataDirectory=/opt/instagram-feed/work/data
Instagram__MediaDirectory=/opt/instagram-feed/work/media
Instagram__KeyDirectory=/opt/instagram-feed/work/keys
EOF
chmod 600 /opt/instagram-feed/app/.env
```

Den `SyncKey` erzeugen mit `openssl rand -hex 24`. Er ist nur für Betriebsart B nötig, schadet aber
nicht. `Instagram__AppSecret` wird **nur** gebraucht, wenn der Startwert noch ein Kurzzeit-Token ist.

### 3.4 Erst mal ohne Instagram ausprobieren

```bash
cd /opt/instagram-feed/work
/opt/instagram-feed/app/InstagramFeed.Service.dll   # nur als Test - so nicht dauerhaft starten
```

Besser als Testlauf ohne Server:

```bash
cd /opt/instagram-feed/work
set -a; . /opt/instagram-feed/app/.env; set +a
Instagram__UseDemoData=true dotnet /opt/instagram-feed/app/InstagramFeed.Service.dll export --to ./demo-export
# -> demo-export/instagram/snippet.html usw. mit Beispielbildern
```

### 3.5 Erster echter Abruf

```bash
cd /opt/instagram-feed/work
set -a; . /opt/instagram-feed/app/.env; set +a
dotnet /opt/instagram-feed/app/InstagramFeed.Service.dll export --to ./test-export
```

Erwartete Ausgabe:

```
Export: /opt/instagram-feed/work/test-export
  Beitraege    : 12
  Mediendateien: 14
  Pfade        : /instagram/ (Seite)  /media/instagram/ (Bilder)
  Status       : Aktualisiert
```

Steht dort `Degradiert` mit einer Meldung, siehe Abschnitt 7.

---

## 4. Betriebsart A — Cron und Statik-Export (Normalfall)

### 4.1 Export-Skript anlegen

```bash
sudo mkdir -p /opt/instagram-feed/bin
cat > /opt/instagram-feed/bin/export.sh <<'EOF'
#!/usr/bin/env bash
# Holt den Feed ab und legt eine fertige, hochladbare Fassung an.
set -euo pipefail

APP=/opt/instagram-feed/app
WORK=/opt/instagram-feed/work
STAGE="$WORK/stage"

cd "$WORK"
set -a
. "$APP/.env"
set +a

# Frische Fassung in ein Staging-Verzeichnis (der bisherige Stand bleibt bis zuletzt stehen)
rm -rf "$STAGE"
dotnet "$APP/InstagramFeed.Service.dll" export --to "$STAGE"
EOF
chmod +x /opt/instagram-feed/bin/export.sh
```

### 4.2 Cron-Job einrichten

```bash
sudo mkdir -p /var/log/instagram-feed
( crontab -l 2>/dev/null; echo "17 * * * * /opt/instagram-feed/bin/export.sh >> /var/log/instagram-feed/export.log 2>&1" ) | crontab -
```

Stündlich zur Minute 17. Häufiger als einmal pro Stunde bringt nichts — Instagram hält die
Medienseite selbst etwa eine Stunde im Cache.

Der Job erneuert auch den Token: die Verlängerung passiert bei jedem Abruf automatisch, sobald
weniger als sieben Tage Restlaufzeit übrig sind. **Solange der Cron wenigstens alle paar Wochen
läuft, muss der Token nie von Hand erneuert werden.** Läuft der Server länger als 60 Tage ohne
Cron, läuft der Token ab und der Feed bleibt stehen.

### 4.3 Auf den Webspace ausrollen

Die Ablage im Export entspricht den Adressen auf der Website — deshalb genügt reines Spiegeln:

```bash
# Beispiel: Webspace als lokales Verzeichnis (oder Mount) erreichbar
rsync -a --delete /opt/instagram-feed/work/stage/instagram/      /var/www/html/instagram/
rsync -a --delete /opt/instagram-feed/work/stage/media/instagram/ /var/www/html/media/instagram/
```

**Nur dann mit `--delete` spiegeln, wenn der Zielordner ausschließlich diesen Feed enthält.** Sonst
ohne `--delete` arbeiten — sonst löscht der Job fremde Dateien im Zielordner.

Bei externem Hoster per FTP/SFTP: denselben Ordnerinhalt hochladen (`instagram/` und `media/instagram/`).

### 4.4 In die statische Seite einbauen

Einmalig beim Bauen der Seite (oder einmalig von Hand im HTML):

```html
<!-- im <head> -->
<link rel="stylesheet" href="/instagram/style.css">

<!-- an der Stelle, an der die Galerie erscheinen soll -> Inhalt von stage/instagram/snippet.html -->
<section class="igfeed">…</section>
```

Das Fragment enthält kein `script`, kein `link` und kein `iframe` — es fasst die Seite nicht an. Bei
einem Generator (Hugo, Eleventy, Jekyll …) den Inhalt von `snippet.html` als Partial einsetzen.

Alternative ohne Neubau der Seite, mit JavaScript:

```html
<div data-igfeed-embed data-igfeed-title="Aktuelles von Instagram"></div>
<script src="/instagram/embed.js" defer></script>
```

### 4.5 Kontrolle nach dem Ausrollen

```bash
# Steht die Galerie in der Seite?
curl -s https://IHRE-DOMAIN/ | grep -c 'igfeed__card'

# Existiert ein Bild unter der Adresse, die das Fragment nennt?
curl -sI https://IHRE-DOMAIN/media/instagram/ | head -1
```

---

## 5. Betriebsart B — Dienst im Dauerbetrieb

Nur nötig, wenn die Galerie ohne Deployment aktuell sein soll.

### 5.1 systemd-Unit

```bash
sudo useradd --system --home /opt/instagram-feed instagram
sudo chown -R instagram:instagram /opt/instagram-feed

sudo tee /etc/systemd/system/instagram-feed.service > /dev/null <<'EOF'
[Unit]
Description=Instagram-Feed-Dienst
After=network-online.target
Wants=network-online.target

[Service]
User=instagram
WorkingDirectory=/opt/instagram-feed/work
ExecStart=/usr/bin/dotnet /opt/instagram-feed/app/InstagramFeed.Service.dll
EnvironmentFile=/opt/instagram-feed/app/.env
Environment=ASPNETCORE_URLS=http://127.0.0.1:8080
Environment=ASPNETCORE_ENVIRONMENT=Production
Restart=always
RestartSec=10

[Install]
WantedBy=multi-user.target
EOF

sudo systemctl daemon-reload
sudo systemctl enable --now instagram-feed
systemctl status instagram-feed --no-pager
```

Werte in `.env` **nicht in Anführungszeichen** setzen — systemd liest die Datei anders als eine
Shell.

### 5.2 Reverse Proxy und Einbau

Den Block aus `deploy/nginx-location.conf` in die vorhandene `server { … }`-Konfiguration der Website
einfügen, dann `sudo nginx -t && sudo systemctl reload nginx`. Danach in der Seite einbinden:

```html
<link rel="stylesheet" href="/instagram/style.css">
<div data-igfeed-embed data-igfeed-title="Aktuelles von Instagram"></div>
<script src="/instagram/embed.js" defer></script>
```

### 5.3 Sofort aktualisieren (nach einem neuen Beitrag)

```bash
set -a; . /opt/instagram-feed/app/.env; set +a
curl -s -X POST -H "X-Sync-Key: $Instagram__SyncKey" https://IHRE-DOMAIN/instagram/sync
```

---

## 6. Regelmäßige Aufgaben

| Wann | Was | Befehl |
|---|---|---|
| nach jedem neuen Beitrag | sofort aktualisieren (sonst bis zum nächsten Cron warten) | `POST /instagram/sync` (5.3) bzw. `/opt/instagram-feed/bin/export.sh` |
| wöchentlich | Log auf Fehler prüfen | `grep -Ei 'warn|error' /var/log/instagram-feed/export.log` |
| monatlich | Zustand und Token-Restlaufzeit prüfen | `curl -s https://IHRE-DOMAIN/healthz` |
| bei Änderungen | `work/keys/` und `work/data/` ins Backup | `tar czf backup-$(date +%F).tgz -C /opt/instagram-feed work/keys work/data` |
| gelegentlich | Platzverbrauch | `du -sh /opt/instagram-feed/work/media` |

---

## 7. Fehlerbehebung

| Symptom | Prüfung | Ursache / Abhilfe |
|---|---|---|
| `Status: Degradiert (Kein Token konfiguriert)` | `grep Token /opt/instagram-feed/app/.env` | `Instagram__InitialAccessToken` nicht gesetzt oder `.env` nicht geladen (`set -a; . .env; set +a` fehlte) |
| `Status: Degradiert (… Code 190 …)` | `curl` auf `/me/media` mit dem Token | Token abgelaufen. Neuen Kurzzeit-Token im Meta-Dashboard erzeugen, gegen Langzeit-Token tauschen (3.1), in `.env` eintragen und `data/token.dat` löschen |
| `Status: Degradiert (API lieferte keine Beitraege…)` | direkt `curl /me/media` | Konto privat/gesperrt, Berechtigung `instagram_business_basic` fehlt, oder alle Beiträge sind geflaggt. **Der alte Feed bleibt stehen** — die Seite zeigt weiter den letzten Stand |
| `Status: Degradiert (Netzwerkfehler)` | `curl -sI https://graph.instagram.com` vom Server | Firewall/DNS des Servers blockiert ausgehenden Verkehr |
| Galerie zeigt Platzhalter statt Bilder | Log und `ls work/media` | Bilder konnten nicht geladen werden (Großengrenze, Copyright-Flag). Betroffene Kacheln bleiben bewusst sichtbar statt zu fehlen |
| `Could not find a valid ICU package` | `dotnet --info` | .NET-Laufzeit ohne ICU (alpine/chiseled). Vollständiges `dotnet-runtime-10.0` installieren |
| Log: `Gespeicherter Token konnte nicht entschluesselt werden` | `ls work/keys` | Schlüsselbund verloren (neuer Container/Volume). Token neu setzen (wie oben) — die Meldung ist absichtlich ein Fehler, kein stilles „kein Token" |
| Nach dem Hochladen sind alle Kacheln leer | `curl -sI https://IHRE-DOMAIN/media/instagram/<datei>` | Bilder nicht mit hochgeladen oder unter anderem Pfad. Ablage muss den Pfaden im Fragment entsprechen (`/instagram/` und `/media/instagram/`) |
| Warnung `XmlKeyManager[35]` | — | Kein Fehler: der Schlüsselbund liegt unverschlüsselt auf Platte (Vorgabe von Data Protection). Für Verschlüsselung at rest das Volume verschlüsseln |

**Erste Prüfung immer am selben Ort:**

```bash
cd /opt/instagram-feed/work
set -a; . /opt/instagram-feed/app/.env; set +a
dotnet /opt/instagram-feed/app/InstagramFeed.Service.dll export --to ./test-export
```

Die Statuszeile nennt das Problem im Klartext.

---

## 8. Datenschutz-Nachweis im laufenden Betrieb

Das ist die Prüfung, die einem Datenschutzbeauftragten standhält — und sie lässt sich jederzeit
wiederholen:

1. Seite im Browser öffnen, Entwicklerwerkzeuge → Netzwerk, neu laden.
2. In der Liste der Anfragen darf **kein** Host außer der eigenen Domain stehen. Erscheinen
   `instagram.com`, `cdninstagram.com` oder `fbcdn.net`, ist der Einbau fehlerhaft (z. B. das
   offizielle Embed oder ein Widget eingeschleust).
3. In der Konsole prüfen: `document.cookie` muss leer bleiben.
4. Prüfen, dass die Seite keinen Fremd-Inhalt lädt: `curl -s https://IHRE-DOMAIN/ | grep -Eio 'cdninstagram|fbcdn|<iframe'` → keine Treffer.

Die zugehörigen Texte für die Datenschutzerklärung stehen in `docs/datenschutz-textbaustein.md`.

---

## 9. Update des Dienstes

```bash
cd ~/insta && git pull
dotnet publish src/InstagramFeed.Service/InstagramFeed.Service.csproj -c Release -o /opt/instagram-feed/app
```

Bei Betriebsart A ist damit nichts weiter zu tun (der nächste Cron-Lauf nutzt die neue Fassung).
Bei Betriebsart B zusätzlich:

```bash
sudo systemctl restart instagram-feed && curl -s http://127.0.0.1:8080/healthz
```

---

## 10. Befehlsübersicht

```bash
# Umgebung laden (vor jedem manuellen Aufruf)
cd /opt/instagram-feed/work && set -a; . /opt/instagram-feed/app/.env; set +a

# Feed abrufen und Fassung für den Webspace bauen
/opt/instagram-feed/bin/export.sh

# Einmalig testweise exportieren, ohne Cron
dotnet /opt/instagram-feed/app/InstagramFeed.Service.dll export --to ./test-export

# Zustand (Dauerbetrieb)
curl -s http://127.0.0.1:8080/healthz

# Sofort aktualisieren (Dauerbetrieb)
curl -s -X POST -H "X-Sync-Key: $Instagram__SyncKey" http://127.0.0.1:8080/instagram/sync

# Vorschau der Galerie ansehen (Dauerbetrieb)
curl -s http://127.0.0.1:8080/instagram/preview | less

# Export ohne Instagram testen (Beispielinhalte)
Instagram__UseDemoData=true dotnet /opt/instagram-feed/app/InstagramFeed.Service.dll export --to ./demo-export
```
