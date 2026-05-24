using Hoardr.Core.Registry;

namespace Hoardr.Core.Workers;

public readonly record struct GcResult(int ManifestsDeleted, int BlobsDeleted);

/// <summary>
/// Two-stage garbage collection (hoard-spec.md → Garbage Collection):
///   1. Delete manifests with ref_count = 0 (decrementing their children), to a fixpoint so
///      manifest-list → child-manifest cascades are fully collected in one run.
///   2. Delete blobs with ref_count = 0 older than the grace period (protects fresh uploads
///      whose manifest hasn't arrived yet).
/// Manifest bytes are shared by digest across repos, so a manifest file is only removed once
/// no repo references that digest anymore.
/// </summary>
public sealed class GarbageCollector(BlobStore blobs, RegistryMetadata meta, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public TimeSpan BlobGrace { get; init; } = TimeSpan.FromHours(1);

    public GcResult RunOnce()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var manifestsDeleted = 0;
        var blobsDeleted = 0;

        // Stage 1 — orphan manifests, repeated until none remain (handles index cascades).
        while (true)
        {
            var orphans = meta.ListUnreferencedManifests();
            if (orphans.Count == 0)
                break;

            foreach (var manifest in orphans)
            {
                meta.DeleteManifest(manifest.Repo, manifest.Digest);
                if (!meta.ManifestDigestInUse(manifest.Digest))
                    blobs.DeleteBlob(manifest.Digest); // shared bytes, only when last user is gone
                manifestsDeleted++;
            }
        }

        // Stage 2 — unreferenced blobs past the grace period.
        foreach (var blob in meta.ListCollectableBlobs(now - BlobGrace))
        {
            blobs.DeleteBlob(blob.Digest);
            meta.DeleteBlobRow(blob.Digest);
            blobsDeleted++;
        }

        return new GcResult(manifestsDeleted, blobsDeleted);
    }
}
