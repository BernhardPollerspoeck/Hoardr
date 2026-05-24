namespace Hoardr.Tests.Support;

/// <summary>A manually-advanced <see cref="TimeProvider"/> for deterministic time-based tests.</summary>
public sealed class FakeClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;

    public static FakeClock At(string utc) => new(DateTimeOffset.Parse(utc + "Z"));
}
