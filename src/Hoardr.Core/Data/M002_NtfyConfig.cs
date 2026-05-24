using SproutDB.Core;

namespace Hoardr.Core.Data;

/// <summary>Single-row table holding the ntfy disk-alert configuration (editable in Admin).</summary>
public sealed class M002_NtfyConfig : IMigration
{
    public int Order => 2;

    public void Up(ISproutDatabase db)
    {
        db.Query("create table ntfy_config (enabled bool default false, server string 255, topic string 255, token string 512, threshold_percent uint default 80)");
        db.Query("upsert ntfy_config {enabled: false, server: 'https://ntfy.sh', topic: '', token: '', threshold_percent: 80}");
    }
}
