using Hoardr.Core.Data;
using SproutDB.Core;
using static Hoardr.Core.Data.Sprout;

namespace Hoardr.Core.Retention;

/// <param name="KeepMin">Always keep at least this many newest tags per repo.</param>
/// <param name="MaxAgeDays">Delete older tags beyond KeepMin past this age. 0 disables age-based deletion.</param>
public readonly record struct RetentionPolicy(int KeepMin, int MaxAgeDays);

/// <summary>
/// Per-repo tag-retention policy: a global default with optional per-repo overrides
/// (stored in <c>retention_overrides</c>). See "Tag Retention" in hoard-spec.md.
/// </summary>
public sealed class RetentionService(ISproutDatabase db, RetentionPolicy defaultPolicy)
{
    public RetentionPolicy Default => defaultPolicy;

    public RetentionPolicy GetEffective(string repo) => GetOverride(repo) ?? defaultPolicy;

    public RetentionPolicy? GetOverride(string repo)
    {
        var data = db.Exec($"get retention_overrides where repo = {Q(repo)}").Data;
        if (data is not { Count: > 0 })
            return null;

        var row = data[0];
        return new RetentionPolicy((int)row.I64("keep_min"), (int)row.I64("max_age_days"));
    }

    public void SetOverride(string repo, int keepMin, int maxAgeDays)
    {
        var existing = db.Exec($"get retention_overrides where repo = {Q(repo)}").Data;
        if (existing is { Count: > 0 })
            db.Exec($"upsert retention_overrides {{_id: {existing[0].U64("_id")}, keep_min: {keepMin}, max_age_days: {maxAgeDays}}}");
        else
            db.Exec($"upsert retention_overrides {{repo: {Q(repo)}, keep_min: {keepMin}, max_age_days: {maxAgeDays}}}");
    }

    public void RemoveOverride(string repo) => db.Exec($"delete retention_overrides where repo = {Q(repo)}");

    public IReadOnlyList<(string Repo, RetentionPolicy Policy)> ListOverrides()
    {
        var data = db.Exec("get retention_overrides order by repo asc").Data;
        if (data is not { Count: > 0 })
            return [];

        return [.. data.Select(r => (r.Str("repo"), new RetentionPolicy((int)r.I64("keep_min"), (int)r.I64("max_age_days"))))];
    }
}
