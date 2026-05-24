using Hoardr.Core.Workers;

namespace Hoardr.Web.Workers;

/// <summary>Runs tag retention and then garbage collection on a slow cadence.</summary>
public sealed class RetentionGcService(
    TagRetention retention,
    GarbageCollector gc,
    ILogger<RetentionGcService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                var tagsDeleted = retention.RunOnce();          // retention first…
                var result = gc.RunOnce();                      // …then GC reclaims what was released
                if (tagsDeleted > 0 || result.ManifestsDeleted > 0 || result.BlobsDeleted > 0)
                    logger.LogInformation(
                        "Maintenance: {Tags} tag(s) retired, {Manifests} manifest(s) and {Blobs} blob(s) collected.",
                        tagsDeleted, result.ManifestsDeleted, result.BlobsDeleted);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Retention/GC run failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
