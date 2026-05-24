namespace Hoardr.Web.Ui;

public static class Humanize
{
    public static string Bytes(long bytes)
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

    public static string Ago(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        if (span < TimeSpan.FromMinutes(1)) return "gerade eben";
        if (span < TimeSpan.FromHours(1)) return $"vor {(int)span.TotalMinutes} Min";
        if (span < TimeSpan.FromDays(1)) return $"vor {(int)span.TotalHours} Std";
        if (span < TimeSpan.FromDays(30)) return $"vor {(int)span.TotalDays} Tg";
        return utc.ToLocalTime().ToString("yyyy-MM-dd");
    }
}
