using Hoardr.Core.Registry;

namespace Hoardr.Core.Workers;

/// <summary>
/// Reaps abandoned uploads: stale active sessions lose their temp file and are marked
/// abandoned; long-dead sessions are dropped entirely. (hoard-spec.md → Cleanup Worker)
/// </summary>
public sealed class UploadCleaner(BlobStore blobs, UploadSessionService sessions, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan PurgeAfter { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Runs one cleanup pass. Returns the number of sessions acted upon.</summary>
    public int RunOnce()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var affected = 0;

        foreach (var session in sessions.ListStaleActive(now - StaleAfter))
        {
            blobs.AbortUpload(session.Uuid);
            sessions.SetStatus(session.Uuid, UploadStatus.Abandoned);
            affected++;
        }

        foreach (var session in sessions.ListStartedBefore(now - PurgeAfter))
        {
            blobs.AbortUpload(session.Uuid); // ensure no temp lingers
            sessions.Delete(session.Uuid);
            affected++;
        }

        return affected;
    }
}
