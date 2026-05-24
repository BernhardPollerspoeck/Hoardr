using SproutDB.Core;

namespace Hoardr.Core.Data;

/// <summary>Per-repository settings (currently: auto-latest — keep <c>latest</c> on the newest push).</summary>
public sealed class M005_RepoSettings : IMigration
{
    public int Order => 5;

    public void Up(ISproutDatabase db)
    {
        db.Query("create table repo_settings (repo string 255, auto_latest bool default false)");
        db.Query("create index unique repo_settings.repo");
    }
}
