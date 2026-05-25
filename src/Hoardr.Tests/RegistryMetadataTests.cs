using System.Text;
using Hoardr.Core;
using Hoardr.Core.Data;
using Hoardr.Core.Registry;
using Hoardr.Tests.Support;

namespace Hoardr.Tests;

public class RegistryMetadataTests
{
    private static Digest D(string label) => Digest.Compute(Encoding.UTF8.GetBytes(label));

    private static (TestDatabase db, RegistryMetadata meta) New()
    {
        var db = new TestDatabase();
        return (db, new RegistryMetadata(db.Db));
    }

    // -------------------------------------------------------------------- Blobs

    [Fact]
    public void EnsureBlobUploaded_Creates_Blob_And_Repo_Link()
    {
        var (db, meta) = New();
        using var _ = db;
        var d = D("layer-1");

        meta.EnsureBlobUploaded("app", d, size: 512);

        Assert.True(meta.BlobInRepo("app", d));
        var blob = meta.GetBlob(d);
        Assert.NotNull(blob);
        Assert.Equal(512, blob!.Size);
        Assert.Equal(0, blob.RefCount);
    }

    [Fact]
    public void EnsureBlobUploaded_Is_Idempotent()
    {
        var (db, meta) = New();
        using var _ = db;
        var d = D("layer-1");

        meta.EnsureBlobUploaded("app", d, 512);
        meta.EnsureBlobUploaded("app", d, 512);

        // exactly one blob row, one repo link
        Assert.Single(db.Db.Exec($"get blobs where digest = '{d}'").Data!);
        Assert.Single(db.Db.Exec($"get repo_blobs where repo = 'app' and blob_digest = '{d}'").Data!);
    }

    [Fact]
    public void Blob_Is_Shared_Across_Repos()
    {
        var (db, meta) = New();
        using var _ = db;
        var d = D("shared");

        meta.EnsureBlobUploaded("app", d, 100);
        meta.EnsureBlobUploaded("web", d, 100);

        Assert.True(meta.BlobInRepo("app", d));
        Assert.True(meta.BlobInRepo("web", d));
        Assert.Single(db.Db.Exec($"get blobs where digest = '{d}'").Data!); // one global blob
    }

    [Fact]
    public void MountBlob_From_Authorized_Source_Links_Target()
    {
        var (db, meta) = New();
        using var _ = db;
        var d = D("mountable");
        meta.EnsureBlobUploaded("source", d, 256);

        var ok = meta.MountBlob("target", d, fromRepo: "source");

        Assert.True(ok);
        Assert.True(meta.BlobInRepo("target", d));
    }

    [Fact]
    public void MountBlob_From_Repo_Without_Blob_Fails()
    {
        var (db, meta) = New();
        using var _ = db;
        var d = D("not-there");
        meta.EnsureBlobUploaded("source", d, 256);

        var ok = meta.MountBlob("target", d, fromRepo: "other");

        Assert.False(ok);
        Assert.False(meta.BlobInRepo("target", d));
    }

    // ---------------------------------------------------------------- Manifests

    [Fact]
    public void PutManifest_Increments_Child_Blob_RefCounts()
    {
        var (db, meta) = New();
        using var _ = db;
        var config = D("config");
        var layer = D("layer");
        meta.EnsureBlobUploaded("app", config, 10);
        meta.EnsureBlobUploaded("app", layer, 20);
        var manifest = D("manifest");

        meta.PutManifest("app", manifest, "application/vnd.oci.image.manifest.v1+json",
        [
            new ManifestChild(config, ManifestChildKind.Blob),
            new ManifestChild(layer, ManifestChildKind.Blob),
        ]);

        Assert.Equal(1, meta.GetBlob(config)!.RefCount);
        Assert.Equal(1, meta.GetBlob(layer)!.RefCount);
        Assert.NotNull(meta.GetManifest("app", manifest));
    }

    [Fact]
    public void PutManifest_Is_Idempotent_No_Double_Counting()
    {
        var (db, meta) = New();
        using var _ = db;
        var layer = D("layer");
        meta.EnsureBlobUploaded("app", layer, 20);
        var manifest = D("manifest");
        ManifestChild[] children = [new(layer, ManifestChildKind.Blob)];

        meta.PutManifest("app", manifest, "mt", children);
        meta.PutManifest("app", manifest, "mt", children);

        Assert.Equal(1, meta.GetBlob(layer)!.RefCount);
    }

    [Fact]
    public void PutManifest_List_Increments_Child_Manifest_RefCounts()
    {
        var (db, meta) = New();
        using var _ = db;
        var amd64 = D("amd64-manifest");
        var arm64 = D("arm64-manifest");
        meta.PutManifest("app", amd64, "mt", []);
        meta.PutManifest("app", arm64, "mt", []);
        var index = D("index");

        meta.PutManifest("app", index, "application/vnd.oci.image.index.v1+json",
        [
            new ManifestChild(amd64, ManifestChildKind.Manifest),
            new ManifestChild(arm64, ManifestChildKind.Manifest),
        ]);

        Assert.Equal(1, meta.GetManifest("app", amd64)!.RefCount);
        Assert.Equal(1, meta.GetManifest("app", arm64)!.RefCount);
    }

    // --------------------------------------------------------------------- Tags

    [Fact]
    public void PutTag_New_Increments_Manifest_And_Is_Listed()
    {
        var (db, meta) = New();
        using var _ = db;
        var m = D("m1");
        meta.PutManifest("app", m, "mt", []);

        meta.PutTag("app", "latest", m);

        Assert.Equal(1, meta.GetManifest("app", m)!.RefCount);
        var tag = meta.GetTag("app", "latest");
        Assert.NotNull(tag);
        Assert.Equal(m, tag!.ManifestDigest);
        Assert.Contains(meta.ListTags("app"), t => t.Name == "latest");
    }

    [Fact]
    public void PutTag_Move_Shifts_RefCount_Between_Manifests()
    {
        var (db, meta) = New();
        using var _ = db;
        var m1 = D("m1");
        var m2 = D("m2");
        meta.PutManifest("app", m1, "mt", []);
        meta.PutManifest("app", m2, "mt", []);

        meta.PutTag("app", "latest", m1);
        meta.PutTag("app", "latest", m2);

        Assert.Equal(0, meta.GetManifest("app", m1)!.RefCount);
        Assert.Equal(1, meta.GetManifest("app", m2)!.RefCount);
        Assert.Equal(m2, meta.GetTag("app", "latest")!.ManifestDigest);
    }

    [Fact]
    public void PutTag_Same_Manifest_Refreshes_Without_Recount()
    {
        var (db, meta) = New();
        using var _ = db;
        var m = D("m1");
        meta.PutManifest("app", m, "mt", []);

        meta.PutTag("app", "latest", m);
        meta.PutTag("app", "latest", m);

        Assert.Equal(1, meta.GetManifest("app", m)!.RefCount);
    }

    [Fact]
    public void DeleteTag_Decrements_Manifest()
    {
        var (db, meta) = New();
        using var _ = db;
        var m = D("m1");
        meta.PutManifest("app", m, "mt", []);
        meta.PutTag("app", "latest", m);

        var removed = meta.DeleteTag("app", "latest");

        Assert.True(removed);
        Assert.Null(meta.GetTag("app", "latest"));
        Assert.Equal(0, meta.GetManifest("app", m)!.RefCount);
    }

    // --------------------------------------------------------------- Delete repo

    [Fact]
    public void DeleteRepo_Removes_Tags_Manifests_And_Blob_Links()
    {
        var (db, meta) = New();
        using var _ = db;
        var config = D("config");
        var layer = D("layer");
        meta.EnsureBlobUploaded("app", config, 10);
        meta.EnsureBlobUploaded("app", layer, 20);
        var manifest = D("manifest");
        meta.PutManifest("app", manifest, "mt",
        [
            new ManifestChild(config, ManifestChildKind.Blob),
            new ManifestChild(layer, ManifestChildKind.Blob),
        ]);
        meta.PutTag("app", "latest", manifest);

        meta.DeleteRepo("app");

        Assert.DoesNotContain("app", meta.ListRepos());
        Assert.Empty(meta.ListTags("app"));
        Assert.Null(meta.GetManifest("app", manifest));
        Assert.False(meta.BlobInRepo("app", config));
        Assert.False(meta.BlobInRepo("app", layer));
        // Child blob refs released so GC can reclaim the bytes later.
        Assert.Equal(0, meta.GetBlob(config)!.RefCount);
        Assert.Equal(0, meta.GetBlob(layer)!.RefCount);
    }

    [Fact]
    public void DeleteRepo_Leaves_Other_Repos_Intact()
    {
        var (db, meta) = New();
        using var _ = db;
        var shared = D("shared-layer");
        meta.EnsureBlobUploaded("app", shared, 50);
        meta.EnsureBlobUploaded("web", shared, 50);
        var mApp = D("m-app");
        var mWeb = D("m-web");
        meta.PutManifest("app", mApp, "mt", [new ManifestChild(shared, ManifestChildKind.Blob)]);
        meta.PutManifest("web", mWeb, "mt", [new ManifestChild(shared, ManifestChildKind.Blob)]);
        meta.PutTag("app", "1.0", mApp);
        meta.PutTag("web", "1.0", mWeb);

        meta.DeleteRepo("app");

        Assert.DoesNotContain("app", meta.ListRepos());
        Assert.Contains("web", meta.ListRepos());
        Assert.True(meta.BlobInRepo("web", shared));
        Assert.NotNull(meta.GetManifest("web", mWeb));
        // shared blob still held by web's manifest (was 2, now 1)
        Assert.Equal(1, meta.GetBlob(shared)!.RefCount);
    }
}
