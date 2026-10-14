using InstagramFeed.Service.Configuration;
using Microsoft.AspNetCore.DataProtection;
using InstagramFeed.Service.Endpoints;
using InstagramFeed.Service.Services.Demo;
using InstagramFeed.Service.Services.Export;
using InstagramFeed.Service.Services.Feed;
using InstagramFeed.Service.Services.Instagram;
using InstagramFeed.Service.Services.Media;
using InstagramFeed.Service.Services.Rendering;
using InstagramFeed.Service.Services.Sync;
using InstagramFeed.Service.Workers;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<InstagramOptions>(builder.Configuration.GetSection(InstagramOptions.SectionName));

builder.Services.AddSingleton(TimeProvider.System);

// Datenbanklos -> keine Schema-Pflege. Der Data-Protection-Schluesselbund MUSS bei Containern auf
// ein persistentes Volume zeigen (Instagram__KeyDirectory), sonst wird der gespeicherte Token nach
// jedem Neustart unlesbar.
var keyDirectory = builder.Configuration["Instagram:KeyDirectory"] ?? "keys";
if (!Path.IsPathRooted(keyDirectory))
{
    keyDirectory = Path.Combine(builder.Environment.ContentRootPath, keyDirectory);
}

Directory.CreateDirectory(keyDirectory);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keyDirectory))
    .SetApplicationName("InstagramFeedService");

// Zwei getrennte HTTP-Clients: der API-Client darf Weiterleitungen folgen, der Medien-Download
// nicht - dort wird jeder Hop einzeln gegen die Freigabeliste geprueft.
builder.Services.AddHttpClient<InstagramApiClient>(client => client.Timeout = TimeSpan.FromSeconds(30))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    });

builder.Services.AddHttpClient<LocalMediaStore>(client => client.Timeout = TimeSpan.FromSeconds(60))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    });

builder.Services.AddSingleton<IInstagramApiClient>(sp => sp.GetRequiredService<InstagramApiClient>());
builder.Services.AddSingleton<IMediaStore>(sp => sp.GetRequiredService<LocalMediaStore>());

builder.Services.AddSingleton<ITokenStore, FileTokenStore>();
builder.Services.AddSingleton<InstagramTokenService>();
builder.Services.AddSingleton<IFeedRepository, JsonFeedRepository>();
builder.Services.AddSingleton<DemoFeedFactory>();
builder.Services.AddSingleton<InstagramSyncService>();
builder.Services.AddSingleton<FeedHtmlRenderer>();
builder.Services.AddSingleton<StaticExporter>();
builder.Services.AddHostedService<InstagramSyncWorker>();

var app = builder.Build();

// Diese Header sind die technische Absicherung der Datenschutz-Zusage: ohne Freigabe fuer fremde
// Hosts kann kein Skript und kein Bild von Meta nachgeladen werden - auch nicht versehentlich.
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["Content-Security-Policy"] =
        "default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; " +
        "connect-src 'self'; frame-ancestors 'self'; base-uri 'self'; form-action 'self'";
    headers["X-Content-Type-Options"] = "nosniff";
    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    headers["X-Frame-Options"] = "SAMEORIGIN";
    await next();
});

app.MapInstagramEndpoints();

// Statik-Export fuer Webspace ohne .NET:  dotnet run -- export --to <verzeichnis>
if (args.Length > 0 && args[0].Equals("export", StringComparison.OrdinalIgnoreCase))
{
    var target = "out";
    for (var i = 1; i < args.Length - 1; i++)
    {
        if (args[i].Equals("--to", StringComparison.OrdinalIgnoreCase))
        {
            target = args[i + 1];
        }
    }

    var exporter = app.Services.GetRequiredService<StaticExporter>();
    return await exporter.RunAsync(target, CancellationToken.None);
}

app.Run();
return 0;

/// <summary>Marker fuer WebApplicationFactory in den Integrationstests.</summary>
public partial class Program;
