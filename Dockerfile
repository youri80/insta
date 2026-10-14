# --- Build ---------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore zuerst, damit die Paketebene gecacht bleibt
COPY src/InstagramFeed.Service/InstagramFeed.Service.csproj src/InstagramFeed.Service/
RUN dotnet restore src/InstagramFeed.Service/InstagramFeed.Service.csproj

COPY . .

# Publish bewusst OHNE --no-restore: Static-Web-Assets werden erst gegen den vollstaendigen
# Quellbaum aufgeloest, ein Publish mit unvollstaendigem Manifest liefert ein Image ohne Assets.
RUN dotnet publish src/InstagramFeed.Service/InstagramFeed.Service.csproj -c Release -o /app/publish

# --- Laufzeit ------------------------------------------------------------
# noble statt alpine/chiseled: die App formatiert Datumsangaben in de-DE, chiseled laeuft im
# Invariant-Globalization-Modus und liefert dann still falsche Formate.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

COPY --from=build /app/publish .

# Verzeichnisse VOR dem USER anlegen; gruppenschreibbar (g=u) statt chown <uid>, damit auch eine
# beliebig vergebene UID (z. B. unter OpenShift) schreiben darf.
RUN mkdir -p /app/data /app/keys /app/media \
 && chgrp -R 0 /app/data /app/keys /app/media \
 && chmod -R g=u /app/data /app/keys /app/media

ENV ASPNETCORE_URLS=http://+:8080 \
    Instagram__DataDirectory=/app/data \
    Instagram__MediaDirectory=/app/media \
    Instagram__KeyDirectory=/app/keys \
    DOTNET_EnableDiagnostics=0
# ASPNETCORE_ENVIRONMENT wird absichtlich NICHT gesetzt (Production ist die Vorgabe).

EXPOSE 8080

# Im Image gibt es kein curl/wget - Healthcheck mit bash-Bordmitteln.
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
  CMD bash -c 'exec 3<>/dev/tcp/127.0.0.1/8080 && printf "GET /healthz HTTP/1.0\r\n\r\n" >&3 && head -1 <&3 | grep -q 200' || exit 1

USER 1000
