# Architektur- und Designentscheidungen

## Server-Side-Mirror statt Einbettung

Der Datenfluss ist die Konformität, nicht die Optik. Jede Lösung, bei der der Besucher-Browser
eine Ressource von `*.cdninstagram.com` / `*.fbcdn.net` lädt, überträgt dessen IP-Adresse an Meta
und ist damit einwilligungspflichtig (§ 25 Abs. 1 TDDDG). Deshalb werden Medien heruntergeladen und
ersetzt.

## Warum kein Blazor

Die Zielhomepage läuft nicht auf .NET. Eine UI-Technologie hätte nur einen neuen Stack eingeführt,
den die Homepage nicht benutzen kann. Der Dienst liefert stattdessen **Ausgabeformate, die jede
Seite aufnehmen kann**: HTML-Fragment, JSON, optional ein eigenes Skript und einen Statik-Export für
Webspace ohne .NET. Damit bleibt der Nutzen (Konformität) und der Aufwand sinkt (keine
Interaktivitäts-, Circuit- und Reverse-Proxy-WebSocket-Fragen).

## Kein Datenbankzugriff

Ein Feed hat genau eine Quelle und maximal 80 Einträge ohne Benutzerbezug. Eine relationale
Datenbank wäre Ballast plus Update- und Schema-Risiko (z. B. `EnsureCreatedAsync` legt auf
bestehenden Datenbanken nichts an). `data/feed.json` mit atomarem Schreiben ist ausreichend; der
Vertrag `IFeedRepository` erlaubt später einen Wechsel, ohne Aufrufer zu ändern.

## Warum `media_url` nicht im Domänenmodell vorkommt

`InstagramPost` hat kein Feld für die Fremd-URL — nur `LocalMediaPath`. Eine Regel, die man
versehentlich verletzen kann, ist schwächer als eine, die man gar nicht formulieren kann. Das ist
der Grund, warum die Invarianten in der Ausgabe überhaupt belastbar prüfbar sind: die URL existiert
nur zwischen API-Antwort und Download.

## Freigabeliste statt Vertrauen

Beim Medien-Download wird jeder Weiterleitungsschritt einzeln gegen die erlaubten Hosts geprüft
(`AllowAutoRedirect = false`). Mit automatischen Weiterleitungen wäre der Zielhost unsichtbar und
eine offene Weiterleitung ein SSRF-Pfad. Zusätzlich: HTTPS-Zwang, Größenlimit, `Content-Type` muss
`image/*` sein, Dateiname wird auf `[A-Za-z0-9_-]` reduziert (kein Pfadausbruch).

## Fehler dürfen den Feed nicht leeren

Drei Regeln, die im Sync kodiert sind und jeweils einen Test haben:

1. Leere API-Antwort bei vorhandenem Alt-Feed → nichts schreiben, `degraded` melden. Ursachen sind
   meist Copyright-Flags, ein privates Konto oder ein Token-Problem — nie „die Galerie ist leer".
2. Fehlschlagender Einzel-Download → Beitrag bleibt mit Platzhalter, Lauf zählt als Erfolg.
3. Fehlschlagende Token-Verlängerung → alter Token weiterverwenden und warnen; erst wenn er wirklich
   abläuft, degradiert der Dienst sichtbar.

## Token wie ein Passwort

Long-Lived-Token = Kontozugang für 60 Tage. Verschlüsselt per ASP.NET Core Data Protection,
Dateirechte `600`, kein Klartext im Log, kein Token in einer Fehlermeldung. Wichtig bei Containern:
der Data-Protection-Schlüsselbund braucht ein persistentes Volume, sonst ist der Token nach einem
Neustart unlesbar; `FileTokenStore` loggt diesen Fall ausdrücklich als Fehler, statt ihn als „kein
Token" zu verstecken.
