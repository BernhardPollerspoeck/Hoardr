using SproutDB.Core;

namespace Hoardr.Core.Data;

/// <summary>
/// Account-level "create repos on first push" capability. Lets a CI account push to a repo
/// that doesn't exist yet — the repo is created and the account claims push/pull on it.
/// </summary>
public sealed class M007_AccountCanCreate : IMigration
{
    public int Order => 7;

    public void Up(ISproutDatabase db)
    {
        db.Query("add column accounts.can_create bool default false");
    }
}
