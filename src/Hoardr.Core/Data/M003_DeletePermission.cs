using SproutDB.Core;

namespace Hoardr.Core.Data;

/// <summary>Adds the per-repo delete permission (separate from push).</summary>
public sealed class M003_DeletePermission : IMigration
{
    public int Order => 3;

    public void Up(ISproutDatabase db)
    {
        db.Query("add column repo_permissions.can_delete bool default false");
    }
}
