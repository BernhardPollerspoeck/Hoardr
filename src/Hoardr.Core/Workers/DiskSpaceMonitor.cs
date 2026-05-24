using Hoardr.Core.Notifications;

namespace Hoardr.Core.Workers;

/// <summary>A point-in-time view of a drive's capacity.</summary>
public readonly record struct DiskUsage(long TotalBytes, long FreeBytes)
{
    public int UsedPercent => TotalBytes <= 0 ? 0 : (int)Math.Round((TotalBytes - FreeBytes) * 100.0 / TotalBytes);
}

/// <summary>
/// Watches the data drive and pushes an ntfy alert when usage crosses the configured threshold.
/// Re-alerts at most once per cooldown while still over; resets once usage drops back below.
/// The drive probe is injected so the logic is testable without touching a real disk.
/// </summary>
public sealed class DiskSpaceMonitor(NtfyService ntfy, Func<DiskUsage> probe, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private DateTime? _lastAlert;

    public TimeSpan ReAlertCooldown { get; init; } = TimeSpan.FromHours(6);

    /// <summary>Current disk usage (for display in the admin UI).</summary>
    public DiskUsage CurrentUsage() => probe();

    /// <summary>Runs one check. Returns true if an alert was sent.</summary>
    public async Task<bool> RunOnceAsync(CancellationToken ct = default)
    {
        var config = ntfy.GetConfig();
        if (!config.Enabled)
            return false;

        var usage = probe();
        if (usage.UsedPercent < config.ThresholdPercent)
        {
            _lastAlert = null; // recovered — allow immediate alert next time it rises
            return false;
        }

        var now = _time.GetUtcNow().UtcDateTime;
        if (_lastAlert is { } last && now - last < ReAlertCooldown)
            return false;

        var sent = await ntfy.SendAsync(
            title: "Hoardr: storage running low",
            message: $"Disk usage {usage.UsedPercent}% (threshold {config.ThresholdPercent}%). " +
                     $"Free: {Human(usage.FreeBytes)} of {Human(usage.TotalBytes)}.",
            priority: "high",
            tags: "warning,floppy_disk",
            ct);

        if (sent)
            _lastAlert = now;
        return sent;
    }

    private static string Human(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }
}
