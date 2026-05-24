using Hoardr.Core.Workers;

namespace Hoardr.Web.Workers;

/// <summary>Periodically checks disk usage and fires ntfy alerts when over threshold.</summary>
public sealed class DiskMonitorService(DiskSpaceMonitor monitor, ILogger<DiskMonitorService> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                if (await monitor.RunOnceAsync(stoppingToken))
                    logger.LogWarning("Disk-space alert sent via ntfy.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Disk-space check failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
