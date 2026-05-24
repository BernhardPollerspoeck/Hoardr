using System.Globalization;

namespace Hoardr.Core.Data;

/// <summary>Helpers for building SproutDB queries and reading rows back.</summary>
public static class Sprout
{
    /// <summary>Quotes a string as a SproutDB single-quoted literal, escaping backslash and quote.</summary>
    public static string Q(string value)
        => "'" + value.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

    /// <summary>Formats a UTC timestamp in SproutDB's datetime literal format.</summary>
    public static string Dt(DateTime utc)
        => utc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss.ffff", CultureInfo.InvariantCulture);

    // --- row field readers (values come back as boxed CLR types) ----------------
    public static string Str(this IReadOnlyDictionary<string, object?> row, string key)
        => (string)row[key]!;

    public static ulong U64(this IReadOnlyDictionary<string, object?> row, string key)
        => Convert.ToUInt64(row[key], CultureInfo.InvariantCulture);

    public static long I64(this IReadOnlyDictionary<string, object?> row, string key)
        => Convert.ToInt64(row[key], CultureInfo.InvariantCulture);

    public static bool Bool(this IReadOnlyDictionary<string, object?> row, string key)
        => row[key] is not null && Convert.ToBoolean(row[key], CultureInfo.InvariantCulture);

    public static DateTime Dt(this IReadOnlyDictionary<string, object?> row, string key)
        => DateTime.SpecifyKind(
            DateTime.Parse((string)row[key]!, CultureInfo.InvariantCulture, DateTimeStyles.None),
            DateTimeKind.Utc);
}
