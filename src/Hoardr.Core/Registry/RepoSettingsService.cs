using Hoardr.Core.Data;
using SproutDB.Core;
using static Hoardr.Core.Data.Sprout;

namespace Hoardr.Core.Registry;

/// <summary>Per-repository settings stored in SproutDB (<c>repo_settings</c>).</summary>
public sealed class RepoSettingsService(ISproutDatabase db)
{
    /// <summary>When true, every tagged push also moves <c>latest</c> to that manifest.</summary>
    public bool GetAutoLatest(string repo)
    {
        var data = db.Exec($"get repo_settings where repo = {Q(repo)}").Data;
        return data is { Count: > 0 } && data[0].Bool("auto_latest");
    }

    public void SetAutoLatest(string repo, bool enabled)
    {
        var value = enabled ? "true" : "false";
        var existing = db.Exec($"get repo_settings where repo = {Q(repo)}").Data;
        if (existing is { Count: > 0 })
            db.Exec($"upsert repo_settings {{_id: {existing[0].U64("_id")}, auto_latest: {value}}}");
        else
            db.Exec($"upsert repo_settings {{repo: {Q(repo)}, auto_latest: {value}}}");
    }

    /// <summary>When true, anyone may pull (anonymous read) from the repo. Push/delete still need auth.</summary>
    public bool GetPublicRead(string repo)
    {
        var data = db.Exec($"get repo_settings where repo = {Q(repo)}").Data;
        return data is { Count: > 0 } && data[0].Bool("public_read");
    }

    public void SetPublicRead(string repo, bool enabled)
    {
        var value = enabled ? "true" : "false";
        var existing = db.Exec($"get repo_settings where repo = {Q(repo)}").Data;
        if (existing is { Count: > 0 })
            db.Exec($"upsert repo_settings {{_id: {existing[0].U64("_id")}, public_read: {value}}}");
        else
            db.Exec($"upsert repo_settings {{repo: {Q(repo)}, public_read: {value}}}");
    }
}
