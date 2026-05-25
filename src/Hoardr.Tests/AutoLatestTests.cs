using System.Text;
using Hoardr.Core;
using Hoardr.Core.Registry;
using Hoardr.Tests.Support;

namespace Hoardr.Tests;

public class AutoLatestTests
{
    private static byte[] Manifest(string marker) => Encoding.UTF8.GetBytes(
        $$"""{ "schemaVersion": 2, "mediaType": "application/vnd.oci.image.manifest.v1+json", "config": { "digest": "sha256:{{new string('a', 64)}}" }, "layers": [], "_": "{{marker}}" }""");

    private static RegistryService NewService(TestDatabase db, TempBlobStore blobs, out RepoSettingsService settings)
    {
        var meta = new RegistryMetadata(db.Db);
        settings = new RepoSettingsService(db.Db);
        return new RegistryService(blobs.Store, meta, new UploadSessionService(db.Db), settings);
    }

    [Fact]
    public void RepoSettings_AutoLatest_Defaults_Off_And_Roundtrips()
    {
        using var db = new TestDatabase();
        var settings = new RepoSettingsService(db.Db);

        Assert.False(settings.GetAutoLatest("team/app"));
        settings.SetAutoLatest("team/app", true);
        Assert.True(settings.GetAutoLatest("team/app"));
        settings.SetAutoLatest("team/app", false);
        Assert.False(settings.GetAutoLatest("team/app"));
    }

    [Fact]
    public void RepoSettings_PublicRead_Defaults_Off_And_Is_Independent()
    {
        using var db = new TestDatabase();
        var settings = new RepoSettingsService(db.Db);

        Assert.False(settings.GetPublicRead("team/app"));
        settings.SetPublicRead("team/app", true);
        Assert.True(settings.GetPublicRead("team/app"));

        // toggling auto_latest must not clobber public_read (same row, different columns)
        settings.SetAutoLatest("team/app", true);
        Assert.True(settings.GetPublicRead("team/app"));
        Assert.True(settings.GetAutoLatest("team/app"));
    }

    [Fact]
    public async Task Enabled_Push_Moves_Latest_To_Newest_Tag()
    {
        using var db = new TestDatabase();
        using var blobs = new TempBlobStore();
        var reg = NewService(db, blobs, out var settings);
        const string repo = "team/app";
        settings.SetAutoLatest(repo, true);

        var d1 = await reg.PutManifestAsync(repo, "1.0.0", Manifest("v1"), null);
        Assert.Equal(d1, reg.ResolveManifest(repo, "latest")!.Digest);   // latest follows first push

        var d2 = await reg.PutManifestAsync(repo, "1.1.0", Manifest("v2"), null);
        Assert.Equal(d2, reg.ResolveManifest(repo, "latest")!.Digest);   // …and the next one
        Assert.NotEqual(d1, d2);
    }

    [Fact]
    public async Task Disabled_Push_Does_Not_Create_Latest()
    {
        using var db = new TestDatabase();
        using var blobs = new TempBlobStore();
        var reg = NewService(db, blobs, out _);
        const string repo = "team/app";

        await reg.PutManifestAsync(repo, "1.0.0", Manifest("v1"), null);

        Assert.Null(reg.ResolveManifest(repo, "latest"));
    }

    [Fact]
    public async Task Enabled_ListTags_Includes_Virtual_Latest_Pointing_At_Newest()
    {
        using var db = new TestDatabase();
        using var blobs = new TempBlobStore();
        var reg = NewService(db, blobs, out var settings);
        const string repo = "team/app";
        settings.SetAutoLatest(repo, true);

        await reg.PutManifestAsync(repo, "1.0.0", Manifest("v1"), null);
        var d2 = await reg.PutManifestAsync(repo, "1.1.0", Manifest("v2"), null);

        var tags = reg.ListTags(repo);
        var latest = Assert.Single(tags, t => t.Name == "latest");
        Assert.Equal(0UL, latest.Id);                 // virtual, not a stored tag
        Assert.Equal(d2, latest.ManifestDigest);      // points at the newest push
    }

    [Fact]
    public async Task Disabled_ListTags_Has_No_Latest()
    {
        using var db = new TestDatabase();
        using var blobs = new TempBlobStore();
        var reg = NewService(db, blobs, out _);
        const string repo = "team/app";

        await reg.PutManifestAsync(repo, "1.0.0", Manifest("v1"), null);

        Assert.DoesNotContain(reg.ListTags(repo), t => t.Name == "latest");
    }

    [Fact]
    public async Task Real_Latest_Tag_Takes_Precedence_Over_Virtual()
    {
        using var db = new TestDatabase();
        using var blobs = new TempBlobStore();
        var reg = NewService(db, blobs, out var settings);
        const string repo = "team/app";
        settings.SetAutoLatest(repo, true);

        var dPinned = await reg.PutManifestAsync(repo, "latest", Manifest("pinned"), null);  // manual latest
        await reg.PutManifestAsync(repo, "2.0.0", Manifest("newer"), null);                  // newer real tag

        // The stored `latest` wins over the computed one…
        Assert.Equal(dPinned, reg.ResolveManifest(repo, "latest")!.Digest);
        // …and is listed once, as a real tag (Id != 0).
        var latest = Assert.Single(reg.ListTags(repo), t => t.Name == "latest");
        Assert.NotEqual(0UL, latest.Id);
    }

    [Fact]
    public async Task Digest_Push_Does_Not_Trigger_Latest()
    {
        using var db = new TestDatabase();
        using var blobs = new TempBlobStore();
        var reg = NewService(db, blobs, out var settings);
        const string repo = "team/app";
        settings.SetAutoLatest(repo, true);

        // push by digest (no tag) — nothing should be tagged
        var bytes = Manifest("v1");
        var digest = Digest.Compute(bytes);
        await reg.PutManifestAsync(repo, digest.ToString(), bytes, null);

        Assert.Null(reg.ResolveManifest(repo, "latest"));
    }
}
