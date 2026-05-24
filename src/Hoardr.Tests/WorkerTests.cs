using System.Text;
using Hoardr.Core;
using Hoardr.Core.Registry;
using Hoardr.Core.Retention;
using Hoardr.Core.Workers;
using Hoardr.Tests.Support;

namespace Hoardr.Tests;

public class UploadCleanerTests
{
    [Fact]
    public void Stale_Active_Session_Is_Abandoned_And_Temp_Removed()
    {
        using var db = new TestDatabase();
        using var blobStore = new TempBlobStore();
        var clock = FakeClock.At("2026-05-24 10:00:00");
        var sessions = new UploadSessionService(db.Db, clock);
        var cleaner = new UploadCleaner(blobStore.Store, sessions, clock);

        var uuid = blobStore.Store.StartUpload();
        sessions.Create(uuid, "app");
        Assert.True(File.Exists(blobStore.Store.UploadPath(uuid)));

        clock.Advance(TimeSpan.FromMinutes(6));
        var affected = cleaner.RunOnce();

        Assert.Equal(1, affected);
        Assert.False(File.Exists(blobStore.Store.UploadPath(uuid)));
        Assert.Equal(UploadStatus.Abandoned, sessions.Get(uuid)!.Status);
    }

    [Fact]
    public void Fresh_Session_Is_Left_Alone()
    {
        using var db = new TestDatabase();
        using var blobStore = new TempBlobStore();
        var clock = FakeClock.At("2026-05-24 10:00:00");
        var sessions = new UploadSessionService(db.Db, clock);
        var cleaner = new UploadCleaner(blobStore.Store, sessions, clock);

        var uuid = blobStore.Store.StartUpload();
        sessions.Create(uuid, "app");

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(0, cleaner.RunOnce());
        Assert.Equal(UploadStatus.Active, sessions.Get(uuid)!.Status);
    }

    [Fact]
    public void Old_Session_Is_Purged()
    {
        using var db = new TestDatabase();
        using var blobStore = new TempBlobStore();
        var clock = FakeClock.At("2026-05-24 10:00:00");
        var sessions = new UploadSessionService(db.Db, clock);
        var cleaner = new UploadCleaner(blobStore.Store, sessions, clock);

        var uuid = blobStore.Store.StartUpload();
        sessions.Create(uuid, "app");

        clock.Advance(TimeSpan.FromHours(25));
        cleaner.RunOnce();

        Assert.Null(sessions.Get(uuid));
    }
}

public class TagRetentionTests
{
    private static Digest D(string s) => Digest.Compute(Encoding.UTF8.GetBytes(s));

    [Fact]
    public void Keeps_Newest_And_Deletes_Old_Beyond_KeepMin()
    {
        using var db = new TestDatabase();
        var clock = FakeClock.At("2026-01-01 00:00:00");
        var meta = new RegistryMetadata(db.Db, clock);
        var policies = new RetentionService(db.Db, new RetentionPolicy(KeepMin: 2, MaxAgeDays: 30));
        var retention = new TagRetention(meta, policies, clock);

        // three old tags
        meta.PutTag("app", "v1", D("m1"));
        meta.PutTag("app", "v2", D("m2"));
        meta.PutTag("app", "v3", D("m3"));

        // …and two recent ones, 90 days later
        clock.Advance(TimeSpan.FromDays(90));
        meta.PutTag("app", "v4", D("m4"));
        meta.PutTag("app", "v5", D("m5"));

        var deleted = retention.RunOnce();

        Assert.Equal(3, deleted);
        var remaining = meta.ListTags("app").Select(t => t.Name).ToHashSet();
        Assert.Equal(["v4", "v5"], remaining);
    }

    [Fact]
    public void KeepMin_Protects_Even_Old_Tags()
    {
        using var db = new TestDatabase();
        var clock = FakeClock.At("2026-01-01 00:00:00");
        var meta = new RegistryMetadata(db.Db, clock);
        var policies = new RetentionService(db.Db, new RetentionPolicy(KeepMin: 10, MaxAgeDays: 30));
        var retention = new TagRetention(meta, policies, clock);

        meta.PutTag("app", "v1", D("m1"));
        meta.PutTag("app", "v2", D("m2"));
        clock.Advance(TimeSpan.FromDays(365));

        Assert.Equal(0, retention.RunOnce());
        Assert.Equal(2, meta.ListTags("app").Count);
    }

    [Fact]
    public void MaxAgeDays_Zero_Disables_Deletion()
    {
        using var db = new TestDatabase();
        var clock = FakeClock.At("2026-01-01 00:00:00");
        var meta = new RegistryMetadata(db.Db, clock);
        var policies = new RetentionService(db.Db, new RetentionPolicy(KeepMin: 0, MaxAgeDays: 0));
        var retention = new TagRetention(meta, policies, clock);

        meta.PutTag("app", "v1", D("m1"));
        clock.Advance(TimeSpan.FromDays(1000));

        Assert.Equal(0, retention.RunOnce());
    }

    [Fact]
    public void Per_Repo_Override_Wins()
    {
        using var db = new TestDatabase();
        var clock = FakeClock.At("2026-01-01 00:00:00");
        var meta = new RegistryMetadata(db.Db, clock);
        var policies = new RetentionService(db.Db, new RetentionPolicy(KeepMin: 10, MaxAgeDays: 365));
        var retention = new TagRetention(meta, policies, clock);

        policies.SetOverride("app", keepMin: 0, maxAgeDays: 30); // stricter than default

        meta.PutTag("app", "v1", D("m1"));
        clock.Advance(TimeSpan.FromDays(60));

        Assert.Equal(1, retention.RunOnce());
        Assert.Empty(meta.ListTags("app"));
    }
}

public class GarbageCollectorTests
{
    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public async Task Collects_Orphan_Manifest_Then_Blob_After_Grace()
    {
        using var db = new TestDatabase();
        using var blobStore = new TempBlobStore();
        var clock = FakeClock.At("2026-05-24 10:00:00");
        var meta = new RegistryMetadata(db.Db, clock);
        var sessions = new UploadSessionService(db.Db, clock);
        var reg = new RegistryService(blobStore.Store, meta, sessions, new RepoSettingsService(db.Db));
        var gc = new GarbageCollector(blobStore.Store, meta, clock) { BlobGrace = TimeSpan.FromHours(1) };

        const string repo = "gc/app";
        var config = Bytes("""{"architecture":"amd64"}""");
        var configDigest = Digest.Compute(config);
        var uuid = reg.StartUpload(repo);
        await reg.AppendAsync(uuid, new MemoryStream(config));
        await reg.FinalizeAsync(repo, uuid, configDigest);

        var manifestBytes = Bytes($$"""
        { "schemaVersion": 2, "mediaType": "application/vnd.oci.image.manifest.v1+json",
          "config": { "digest": "{{configDigest}}", "size": {{config.Length}} }, "layers": [] }
        """);
        var manifestDigest = await reg.PutManifestAsync(repo, "v1", manifestBytes, null);

        // Untag → manifest becomes unreferenced (but the tag was its only holder)
        meta.DeleteTag(repo, "v1");

        // First GC: manifest goes, its bytes go; blob stays (within grace)
        var first = gc.RunOnce();
        Assert.Equal(1, first.ManifestsDeleted);
        Assert.Null(meta.GetManifest(repo, manifestDigest));
        Assert.False(blobStore.Store.Exists(manifestDigest));
        Assert.True(blobStore.Store.Exists(configDigest));
        Assert.Equal(0, meta.GetBlob(configDigest)!.RefCount);

        // After the grace period, the now-orphan blob is collected
        clock.Advance(TimeSpan.FromHours(2));
        var second = gc.RunOnce();
        Assert.Equal(1, second.BlobsDeleted);
        Assert.False(blobStore.Store.Exists(configDigest));
        Assert.Null(meta.GetBlob(configDigest));
    }

    [Fact]
    public async Task Fresh_Unreferenced_Blob_Is_Protected_By_Grace()
    {
        using var db = new TestDatabase();
        using var blobStore = new TempBlobStore();
        var clock = FakeClock.At("2026-05-24 10:00:00");
        var meta = new RegistryMetadata(db.Db, clock);
        var sessions = new UploadSessionService(db.Db, clock);
        var reg = new RegistryService(blobStore.Store, meta, sessions, new RepoSettingsService(db.Db));
        var gc = new GarbageCollector(blobStore.Store, meta, clock) { BlobGrace = TimeSpan.FromHours(1) };

        var content = Bytes("orphan upload, no manifest yet");
        var digest = Digest.Compute(content);
        var uuid = reg.StartUpload("orphan/app");
        await reg.AppendAsync(uuid, new MemoryStream(content));
        await reg.FinalizeAsync("orphan/app", uuid, digest);

        Assert.Equal(0, gc.RunOnce().BlobsDeleted);      // within grace
        Assert.True(blobStore.Store.Exists(digest));

        clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(1, gc.RunOnce().BlobsDeleted);      // past grace
        Assert.False(blobStore.Store.Exists(digest));
    }
}
