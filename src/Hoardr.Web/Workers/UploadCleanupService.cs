using Hoardr.Core.Workers;

namespace Hoardr.Web.Workers;

/// <summary>Runs the upload-session cleanup every few minutes.</summary>
public sealed class UploadCleanupService(UploadCleaner cleaner, ILogger<UploadCleanupService> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                var affected = cleaner.RunOnce();
                if (affected > 0)
                    logger.LogInformation("Upload cleanup handled {Count} session(s).", affected);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Upload cleanup failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
