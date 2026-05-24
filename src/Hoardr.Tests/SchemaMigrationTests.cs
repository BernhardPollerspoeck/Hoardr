using Hoardr.Core.Data;
using Hoardr.Tests.Support;

namespace Hoardr.Tests;

public class SchemaMigrationTests
{
    public static TheoryData<string> ExpectedTables =>
    [
        "accounts",
        "repo_permissions",
        "blobs",
        "repo_blobs",
        "manifests",
        "manifest_refs",
        "tags",
        "retention_overrides",
        "upload_sessions",
    ];

    [Theory]
    [MemberData(nameof(ExpectedTables))]
    public void Migration_Creates_AllTables(string table)
    {
        using var db = new TestDatabase();

        var result = db.Db.Exec($"get {table}");

        Assert.True(result.Ok(),
            $"querying '{table}' returned errors: {string.Join(", ", result.Errors?.Select(e => e.ToString()) ?? [])}");
    }

    [Fact]
    public void Unknown_Table_Returns_Error()
    {
        using var db = new TestDatabase();

        var result = db.Db.Exec("get does_not_exist");

        Assert.False(result.Ok());
    }

    [Fact]
    public void Accounts_Name_Is_Unique()
    {
        using var db = new TestDatabase();

        var first = db.Db.Exec("upsert accounts {name: 'alice', password_hash: 'x', created_at: '2026-01-01 00:00:00.0000'}");
        Assert.True(first.Ok());

        var dup = db.Db.Exec("upsert accounts {name: 'alice', password_hash: 'y', created_at: '2026-01-01 00:00:00.0000'}");
        Assert.False(dup.Ok(), "duplicate account name should violate the unique index");
    }
}
