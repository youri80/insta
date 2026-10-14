using InstagramFeed.Service.Services.Sync;

namespace InstagramFeed.Service.Workers;

/// <summary>
/// Haelt den Feed aktuell. Eine Exception im Lauf darf den Dienst nie beenden - sonst steht die
/// Website irgendwann mit einem eingefrorenen Raster da und niemand merkt es.
/// </summary>
public sealed class InstagramSyncWorker : BackgroundService
{
    private readonly InstagramSyncService _sync;
    private readonly TimeSpan _interval;
    private readonly ILogger<InstagramSyncWorker> _log;

    public InstagramSyncWorker(
        InstagramSyncService sync,
        Microsoft.Extensions.Options.IOptions<Configuration.InstagramOptions> options,
        ILogger<InstagramSyncWorker> log)
    {
        _sync = sync;
        _interval = options.Value.MinSyncInterval;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RunSafelyAsync(stoppingToken);

        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunSafelyAsync(stoppingToken);
        }
    }

    private async Task RunSafelyAsync(CancellationToken ct)
    {
        try
        {
            await _sync.SyncAsync(force: false, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Dienst wird beendet.
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Feed-Abgleich im Hintergrunddienst fehlgeschlagen. Naechster Versuch in {Interval}.", _interval);
        }
    }
}
