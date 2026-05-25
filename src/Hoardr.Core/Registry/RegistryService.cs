namespace Hoardr.Core.Registry;

public sealed record UploadOutcome(bool Verified, Digest Digest, long Size, bool Deduplicated);

public sealed record ManifestServeInfo(Digest Digest, string MediaType, long Size);

/// <summary>
/// Orchestrates the multi-step registry flows on top of <see cref="BlobStore"/> (files),
/// <see cref="RegistryMetadata"/> (DB) and <see cref="UploadSessionService"/>. Keeps the HTTP
/// layer thin and lets the flows be unit-tested without a web host.
/// </summary>
public sealed class RegistryService(BlobStore blobs, RegistryMetadata meta, UploadSessionService sessions, RepoSettingsService settings)
{
    public RegistryMetadata Metadata => meta;

    // ------------------------------------------------------------- Blobs (pull)

    public bool BlobReadable(string repo, Digest digest) => meta.BlobInRepo(repo, digest) && blobs.Exists(digest);

    public long? BlobSize(Digest digest) => blobs.Size(digest);

    public Stream OpenBlob(Digest digest) => blobs.OpenRead(digest);

    public bool BlobExistsGlobally(Digest digest) => meta.GetBlob(digest) is not null && blobs.Exists(digest);

    /// <summary>Links an already-stored blob into a repo (spec's POST?digest shortcut).</summary>
    public void LinkExistingBlob(string repo, Digest digest)
    {
        var blob = meta.GetBlob(digest);
        if (blob is not null)
            meta.EnsureBlobUploaded(repo, digest, blob.Size);
    }

    // ----------------------------------------------------------- Upload (push)

    public string StartUpload(string repo)
    {
        var uuid = blobs.StartUpload();
        sessions.Create(uuid, repo);
        return uuid;
    }

    public bool UploadExists(string uuid) => sessions.Get(uuid) is not null;

    public long UploadedBytes(string uuid) => sessions.Get(uuid)?.BytesWritten ?? 0;

    public async Task<long> AppendAsync(string uuid, Stream data, CancellationToken ct = default)
    {
        var total = await blobs.AppendAsync(uuid, data, ct);
        sessions.Touch(uuid, total);
        return total;
    }

    /// <summary>Finalizes an upload: verifies the digest, stores the blob, links it to the repo.</summary>
    public async Task<UploadOutcome> FinalizeAsync(string repo, string uuid, Digest expected, CancellationToken ct = default)
    {
        var result = await blobs.CommitAsync(uuid, expected, ct);
        if (!result.Verified)
        {
            sessions.Delete(uuid);
            return new UploadOutcome(false, result.Actual, result.Size, false);
        }

        meta.EnsureBlobUploaded(repo, expected, result.Size);
        sessions.Delete(uuid);
        return new UploadOutcome(true, expected, result.Size, result.Deduplicated);
    }

    public void AbortUpload(string uuid)
    {
        blobs.AbortUpload(uuid);
        sessions.Delete(uuid);
    }

    public bool TryMount(string repo, Digest digest, string fromRepo) => meta.MountBlob(repo, digest, fromRepo);

    /// <summary>Removes a blob from a repo (unlink). The file is reclaimed later by GC. False if absent.</summary>
    public bool DeleteBlobFromRepo(string repo, Digest digest) => meta.RemoveBlobFromRepo(repo, digest);

    /// <summary>Whether the repo allows anonymous (unauthenticated) reads.</summary>
    public bool IsPublicRead(string repo) => settings.GetPublicRead(repo);

    // -------------------------------------------------------------- Manifests

    /// <summary>Stores a manifest body, records its refs, and (if reference is a tag) tags it.</summary>
    public async Task<Digest> PutManifestAsync(string repo, string reference, byte[] content, string? contentType, CancellationToken ct = default)
    {
        var info = ManifestParser.Parse(content, contentType);
        var digest = await blobs.PutContentAsync(content, ct);

        meta.PutManifest(repo, digest, info.MediaType, info.Children);

        if (!Digest.TryParse(reference, out _)) // reference is a tag, not a digest
        {
            meta.PutTag(repo, reference, digest);

            // auto-latest: keep `latest` pointing at the most recently pushed tag
            if (reference != "latest" && settings.GetAutoLatest(repo))
                meta.PutTag(repo, "latest", digest);
        }

        return digest;
    }

    public ManifestServeInfo? ResolveManifest(string repo, string reference)
    {
        Digest digest;
        if (Digest.TryParse(reference, out var parsed))
            digest = parsed;
        else if (meta.GetTag(repo, reference) is { } tag)
            digest = tag.ManifestDigest;
        else
            return null;

        var manifest = meta.GetManifest(repo, digest);
        if (manifest is null || blobs.Size(digest) is not { } size)
            return null;

        return new ManifestServeInfo(digest, manifest.MediaType, size);
    }

    public Stream OpenManifest(Digest digest) => blobs.OpenRead(digest);

    /// <summary>
    /// Handles DELETE of a manifest reference. A tag reference just untags. A digest reference
    /// removes any tags pointing at it, and if nothing else references it, drops the manifest + bytes.
    /// Returns false if the reference wasn't found.
    /// </summary>
    public bool DeleteManifestReference(string repo, string reference)
    {
        if (!Digest.TryParse(reference, out var digest))
            return meta.DeleteTag(repo, reference);

        if (meta.GetManifest(repo, digest) is null)
            return false;

        foreach (var tag in meta.ListTagsForManifest(repo, digest))
            meta.DeleteTag(repo, tag.Name);

        if (meta.GetManifest(repo, digest) is { RefCount: 0 })
        {
            meta.DeleteManifest(repo, digest);
            blobs.DeleteBlob(digest);
        }
        return true;
    }

    public IReadOnlyList<TagRecord> ListTags(string repo) => meta.ListTags(repo);

    /// <summary>Deletes an entire repo (all tags, manifests and blob links). Bytes are reclaimed by GC.</summary>
    public void DeleteRepo(string repo) => meta.DeleteRepo(repo);
}
