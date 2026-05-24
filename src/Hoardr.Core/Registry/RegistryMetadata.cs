using Hoardr.Core.Data;
using SproutDB.Core;
using static Hoardr.Core.Data.Sprout;

namespace Hoardr.Core.Registry;

/// <summary>
/// SproutDB-backed metadata for the registry: blobs, repo-blob links, manifests,
/// manifest child refs and tags — plus the ref-counting that GC relies on.
///
/// All read-modify-write ref-count sequences run under a single process-wide lock
/// (the whole thing is one process, SproutDB writes are synchronous). See the
/// "Ref-Counting Modell" section in hoard-spec.md.
/// </summary>
public sealed class RegistryMetadata(ISproutDatabase db, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    // ===================================================================== Blobs

    /// <summary>
    /// Records a freshly committed blob and links it to the repo. Idempotent.
    /// Does not touch ref_count (manifests do that); created_at is set on first sight.
    /// </summary>
    public void EnsureBlobUploaded(string repo, Digest digest, long size)
    {
        lock (_gate)
        {
            if (GetBlob(digest) is null)
                db.Exec($"upsert blobs {{digest: {Q(digest.ToString())}, size: {size}, ref_count: 0, created_at: {Q(Dt(UtcNow))}}}");

            LinkBlobToRepo(repo, digest);
        }
    }

    public bool BlobInRepo(string repo, Digest digest)
        => db.Exec($"get repo_blobs where repo = {Q(repo)} and blob_digest = {Q(digest.ToString())}")
              .Data is { Count: > 0 };

    public BlobRecord? GetBlob(Digest digest)
    {
        var data = db.Exec($"get blobs where digest = {Q(digest.ToString())}").Data;
        if (data is not { Count: > 0 })
            return null;

        var row = data[0];
        return Digest.TryParse(row.Str("digest"), out var d)
            ? new BlobRecord(row.U64("_id"), d, row.I64("size"), row.I64("ref_count"), row.Dt("created_at"))
            : null;
    }

    /// <summary>
    /// Cross-repo mount: if the blob is visible in <paramref name="fromRepo"/>, link it into
    /// <paramref name="repo"/> without re-uploading. Returns false if the source has no such blob.
    /// </summary>
    public bool MountBlob(string repo, Digest digest, string fromRepo)
    {
        lock (_gate)
        {
            if (!BlobInRepo(fromRepo, digest))
                return false;

            LinkBlobToRepo(repo, digest);
            return true;
        }
    }

    private void LinkBlobToRepo(string repo, Digest digest)
    {
        if (!BlobInRepo(repo, digest))
            db.Exec($"upsert repo_blobs {{repo: {Q(repo)}, blob_digest: {Q(digest.ToString())}}}");
    }

    /// <summary>
    /// Removes a repo's visibility of a blob (the RepoBlob link). The blob's bytes stay until GC
    /// reclaims them (ref_count 0 + grace). Returns false if the repo didn't have the blob.
    /// </summary>
    public bool RemoveBlobFromRepo(string repo, Digest digest)
    {
        lock (_gate)
        {
            if (!BlobInRepo(repo, digest))
                return false;

            db.Exec($"delete repo_blobs where repo = {Q(repo)} and blob_digest = {Q(digest.ToString())}");
            return true;
        }
    }

    // ================================================================= Manifests

    public bool ManifestExists(string repo, Digest digest) => GetManifest(repo, digest) is not null;

    public ManifestRecord? GetManifest(string repo, Digest digest)
    {
        var data = db.Exec($"get manifests where repo = {Q(repo)} and digest = {Q(digest.ToString())}").Data;
        if (data is not { Count: > 0 })
            return null;

        var row = data[0];
        return Digest.TryParse(row.Str("digest"), out var d)
            ? new ManifestRecord(row.U64("_id"), row.Str("repo"), d, row.Str("media_type"), row.I64("ref_count"))
            : null;
    }

    /// <summary>
    /// Stores a manifest and its child references, bumping each child's ref_count.
    /// Idempotent per (repo, digest): re-pushing the same manifest is a no-op.
    /// </summary>
    public void PutManifest(string repo, Digest digest, string mediaType, IEnumerable<ManifestChild> children)
    {
        lock (_gate)
        {
            if (ManifestExists(repo, digest))
                return;

            db.Exec($"upsert manifests {{digest: {Q(digest.ToString())}, repo: {Q(repo)}, media_type: {Q(mediaType)}, ref_count: 0}}");

            foreach (var child in children)
            {
                var kind = child.Kind == ManifestChildKind.Blob ? "blob" : "manifest";
                db.Exec($"upsert manifest_refs {{parent_digest: {Q(digest.ToString())}, child_digest: {Q(child.Digest.ToString())}, child_kind: {Q(kind)}}}");

                if (child.Kind == ManifestChildKind.Blob)
                    AdjustBlobRefCountCore(child.Digest, +1);
                else
                    AdjustManifestRefCountCore(repo, child.Digest, +1);
            }
        }
    }

    public void AdjustBlobRefCount(Digest digest, int delta)
    {
        lock (_gate) AdjustBlobRefCountCore(digest, delta);
    }

    public void AdjustManifestRefCount(string repo, Digest digest, int delta)
    {
        lock (_gate) AdjustManifestRefCountCore(repo, digest, delta);
    }

    // Core variants assume the caller already holds _gate (Lock is non-reentrant).
    private void AdjustBlobRefCountCore(Digest digest, int delta)
    {
        var blob = GetBlob(digest);
        if (blob is null)
            return;

        var next = Math.Max(0, blob.RefCount + delta);
        db.Exec($"upsert blobs {{_id: {blob.Id}, ref_count: {next}}}");
    }

    private void AdjustManifestRefCountCore(string repo, Digest digest, int delta)
    {
        var manifest = GetManifest(repo, digest);
        if (manifest is null)
            return;

        var next = Math.Max(0, manifest.RefCount + delta);
        db.Exec($"upsert manifests {{_id: {manifest.Id}, ref_count: {next}}}");
    }

    // ====================================================================== Tags

    public TagRecord? GetTag(string repo, string name)
    {
        var data = db.Exec($"get tags where repo = {Q(repo)} and name = {Q(name)}").Data;
        if (data is not { Count: > 0 })
            return null;

        var row = data[0];
        return Digest.TryParse(row.Str("manifest_digest"), out var d)
            ? new TagRecord(row.U64("_id"), row.Str("repo"), row.Str("name"), d, row.Dt("pushed_at"))
            : null;
    }

    public IReadOnlyList<TagRecord> ListTags(string repo)
    {
        var data = db.Exec($"get tags where repo = {Q(repo)} order by name asc").Data;
        if (data is not { Count: > 0 })
            return [];

        var result = new List<TagRecord>(data.Count);
        foreach (var row in data)
            if (Digest.TryParse(row.Str("manifest_digest"), out var d))
                result.Add(new TagRecord(row.U64("_id"), row.Str("repo"), row.Str("name"), d, row.Dt("pushed_at")));
        return result;
    }

    /// <summary>
    /// Points a tag at a manifest, keeping manifest ref_counts correct when a tag moves.
    /// Re-pushing the same tag→manifest just refreshes pushed_at.
    /// </summary>
    public void PutTag(string repo, string name, Digest manifestDigest)
    {
        var now = Dt(UtcNow);
        lock (_gate)
        {
            var existing = GetTag(repo, name);

            if (existing is null)
            {
                AdjustManifestRefCountCore(repo, manifestDigest, +1);
                db.Exec($"upsert tags {{repo: {Q(repo)}, name: {Q(name)}, manifest_digest: {Q(manifestDigest.ToString())}, pushed_at: {Q(now)}}}");
            }
            else if (existing.ManifestDigest == manifestDigest)
            {
                db.Exec($"upsert tags {{_id: {existing.Id}, pushed_at: {Q(now)}}}");
            }
            else
            {
                AdjustManifestRefCountCore(repo, existing.ManifestDigest, -1);
                AdjustManifestRefCountCore(repo, manifestDigest, +1);
                db.Exec($"upsert tags {{_id: {existing.Id}, manifest_digest: {Q(manifestDigest.ToString())}, pushed_at: {Q(now)}}}");
            }
        }
    }

    /// <summary>Removes a tag and releases its hold on the manifest. Returns false if it didn't exist.</summary>
    public bool DeleteTag(string repo, string name)
    {
        lock (_gate)
        {
            var tag = GetTag(repo, name);
            if (tag is null)
                return false;

            AdjustManifestRefCountCore(repo, tag.ManifestDigest, -1);
            db.Exec($"delete tags where _id = {tag.Id}");
            return true;
        }
    }

    /// <summary>The child references (blobs / sub-manifests) declared by a manifest.</summary>
    public IReadOnlyList<ManifestChild> GetChildren(Digest parent)
    {
        var data = db.Exec($"get manifest_refs where parent_digest = {Q(parent.ToString())}").Data;
        if (data is not { Count: > 0 })
            return [];

        var result = new List<ManifestChild>(data.Count);
        foreach (var row in data)
            if (Digest.TryParse(row.Str("child_digest"), out var d))
                result.Add(new ManifestChild(d,
                    row.Str("child_kind") == "manifest" ? ManifestChildKind.Manifest : ManifestChildKind.Blob));
        return result;
    }

    public IReadOnlyList<TagRecord> ListTagsForManifest(string repo, Digest digest)
        => [.. ListTags(repo).Where(t => t.ManifestDigest == digest)];

    /// <summary>Tag count and total blob bytes linked to a repo (for the UI).</summary>
    public RepoStats GetRepoStats(string repo)
    {
        var tagCount = ListTags(repo).Count;

        long bytes = 0;
        var links = db.Exec($"get repo_blobs where repo = {Q(repo)}").Data;
        if (links is { Count: > 0 })
            foreach (var link in links)
                if (Digest.TryParse(link.Str("blob_digest"), out var d) && GetBlob(d) is { } blob)
                    bytes += blob.Size;

        return new RepoStats(repo, tagCount, bytes);
    }

    /// <summary>All distinct repos that have any content (blobs, manifests or tags).</summary>
    public IReadOnlyList<string> ListRepos()
    {
        var repos = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var table in (string[])["repo_blobs", "manifests", "tags"])
        {
            var data = db.Exec($"get {table} select repo distinct").Data;
            if (data is null)
                continue;
            foreach (var row in data)
                repos.Add(row.Str("repo"));
        }
        return [.. repos];
    }

    // ---------------------------------------------------------- GC support

    /// <summary>Manifests that nothing references anymore (candidates for stage-1 GC).</summary>
    public IReadOnlyList<ManifestRecord> ListUnreferencedManifests()
    {
        var data = db.Exec("get manifests where ref_count = 0").Data;
        if (data is not { Count: > 0 })
            return [];

        var result = new List<ManifestRecord>(data.Count);
        foreach (var row in data)
            if (Digest.TryParse(row.Str("digest"), out var d))
                result.Add(new ManifestRecord(row.U64("_id"), row.Str("repo"), d, row.Str("media_type"), row.I64("ref_count")));
        return result;
    }

    /// <summary>Blobs nothing references that are older than the grace cutoff (stage-2 GC).</summary>
    public IReadOnlyList<BlobRecord> ListCollectableBlobs(DateTime createdBefore)
    {
        var data = db.Exec($"get blobs where ref_count = 0 and created_at < {Q(Dt(createdBefore))}").Data;
        if (data is not { Count: > 0 })
            return [];

        var result = new List<BlobRecord>(data.Count);
        foreach (var row in data)
            if (Digest.TryParse(row.Str("digest"), out var d))
                result.Add(new BlobRecord(row.U64("_id"), d, row.I64("size"), row.I64("ref_count"), row.Dt("created_at")));
        return result;
    }

    /// <summary>True if any repo still has a manifest row with this digest (its bytes are shared).</summary>
    public bool ManifestDigestInUse(Digest digest)
        => db.Exec($"get manifests where digest = {Q(digest.ToString())}").Data is { Count: > 0 };

    /// <summary>Removes a blob's DB row and all its repo links (call after deleting the file).</summary>
    public void DeleteBlobRow(Digest digest)
    {
        lock (_gate)
        {
            db.Exec($"delete repo_blobs where blob_digest = {Q(digest.ToString())}");
            db.Exec($"delete blobs where digest = {Q(digest.ToString())}");
        }
    }

    /// <summary>
    /// Removes a manifest row, its child refs, and decrements each child's ref_count.
    /// Does not delete the manifest's bytes from disk — the caller does that.
    /// </summary>
    public void DeleteManifest(string repo, Digest digest)
    {
        lock (_gate)
        {
            var manifest = GetManifest(repo, digest);
            if (manifest is null)
                return;

            foreach (var child in GetChildren(digest))
            {
                if (child.Kind == ManifestChildKind.Blob)
                    AdjustBlobRefCountCore(child.Digest, -1);
                else
                    AdjustManifestRefCountCore(repo, child.Digest, -1);
            }

            db.Exec($"delete manifest_refs where parent_digest = {Q(digest.ToString())}");
            db.Exec($"delete manifests where _id = {manifest.Id}");
        }
    }
}
