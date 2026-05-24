using SproutDB.Core;

namespace Hoardr.Core.Data;

/// <summary>Per-repo anonymous read (public pull) — for open-sourcing images on your own registry.</summary>
public sealed class M008_RepoPublicRead : IMigration
{
    public int Order => 8;

    public void Up(ISproutDatabase db)
    {
        db.Query("add column repo_settings.public_read bool default false");
    }
}
