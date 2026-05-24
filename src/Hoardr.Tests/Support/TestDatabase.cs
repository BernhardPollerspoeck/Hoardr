using Hoardr.Core.Data;
using SproutDB.Core;

namespace Hoardr.Tests.Support;

/// <summary>
/// Spins up an isolated, migrated SproutDB in a throwaway temp directory.
/// Dispose deletes everything. Use one per test for full isolation.
/// </summary>
public sealed class TestDatabase : IDisposable
{
    private readonly string _dir;
    private readonly SproutEngine _engine;

    public ISproutDatabase Db { get; }

    public TestDatabase()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hoardr-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        _engine = new SproutEngine(new SproutEngineSettings { DataDirectory = _dir });
        Db = _engine.GetOrCreateDatabase(HoardrDb.Name);
        _engine.Migrate(typeof(M001_InitialSchema).Assembly, Db);
    }

    public void Dispose()
    {
        _engine.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best effort cleanup */ }
    }
}
