using System.Security.Cryptography;
using SproutDB.Core;
using static Hoardr.Core.Data.Sprout;

namespace Hoardr.Core.Data;

/// <summary>
/// Gives each instance a unique default ntfy topic (<c>hoardr_</c> + 6 random chars), generated
/// once. Only fills it in if the operator hasn't set one — never overwrites a chosen topic.
/// </summary>
public sealed class M006_GenerateNtfyTopic : IMigration
{
    public int Order => 6;

    public void Up(ISproutDatabase db)
    {
        var data = db.Exec("get ntfy_config").Data;
        if (data is not { Count: > 0 })
            return;

        var row = data[0];
        if (!string.IsNullOrEmpty(row.Str("topic")))
            return;

        var topic = "hoardr_" + RandomNumberGenerator.GetString("abcdefghijklmnopqrstuvwxyz0123456789", 6);
        db.Exec($"upsert ntfy_config {{_id: {row.U64("_id")}, topic: {Q(topic)}}}");
    }
}
