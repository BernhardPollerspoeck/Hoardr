using Hoardr.Core.Registry;
using Hoardr.Core.Retention;

namespace Hoardr.Core.Workers;

/// <summary>
/// Applies the hybrid tag-retention policy per repo: always keep the <c>KeepMin</c> newest
/// tags (by pushed_at); of the rest, delete those older than <c>MaxAgeDays</c>. Deleting a
/// tag releases its hold on the manifest — the GC reclaims the rest. (hoard-spec.md → Tag Retention)
/// </summary>
public sealed class TagRetention(RegistryMetadata meta, RetentionService policies, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Runs one retention pass across all repos. Returns the number of tags deleted.</summary>
    public int RunOnce()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var deleted = 0;

        foreach (var repo in meta.ListRepos())
        {
            var policy = policies.GetEffective(repo);
            if (policy.MaxAgeDays <= 0)
                continue; // age-based deletion disabled for this repo

            var cutoff = now.AddDays(-policy.MaxAgeDays);
            var keepMin = Math.Max(0, policy.KeepMin);

            var tags = meta.ListTags(repo)
                .OrderByDescending(t => t.PushedAt)
                .ToList();

            foreach (var tag in tags.Skip(keepMin))
            {
                if (tag.PushedAt < cutoff)
                {
                    meta.DeleteTag(repo, tag.Name);
                    deleted++;
                }
            }
        }

        return deleted;
    }
}
