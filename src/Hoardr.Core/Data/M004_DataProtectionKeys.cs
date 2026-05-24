using SproutDB.Core;

namespace Hoardr.Core.Data;

/// <summary>
/// Stores ASP.NET Data Protection keys in the DB (principle: everything except image
/// blobs lives in SproutDB) so auth cookies survive restarts without on-disk key files.
/// </summary>
public sealed class M004_DataProtectionKeys : IMigration
{
    public int Order => 4;

    public void Up(ISproutDatabase db)
    {
        db.Query("create table dp_keys (friendly_name string 255, xml string 8192)");
        db.Query("create index unique dp_keys.friendly_name");
    }
}
