using SproutDB.Core;

namespace Hoardr.Core.Data;

public static class SproutDatabaseExtensions
{
    /// <summary>
    /// Runs a single-statement query and returns its one response.
    /// SproutDB's <see cref="ISproutDatabase.Query"/> returns one response per statement;
    /// Hoardr issues statements one at a time, so we take the last.
    /// </summary>
    public static SproutResponse Exec(this ISproutDatabase db, string query)
        => db.Query(query)[^1];

    /// <summary>True if the response carries no errors.</summary>
    public static bool Ok(this SproutResponse response)
        => response.Errors is null or { Count: 0 };
}
