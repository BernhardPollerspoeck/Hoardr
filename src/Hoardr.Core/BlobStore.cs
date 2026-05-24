namespace Hoardr.Core;

/// <summary>Outcome of finalizing an upload into the blob store.</summary>
/// <param name="Verified">Whether the uploaded content's digest matched what the client claimed.</param>
/// <param name="Deduplicated">True if the blob already existed and the upload was discarded.</param>
/// <param name="Size">Size of the uploaded content in bytes.</param>
/// <param name="Actual">The digest actually computed over the uploaded bytes.</param>
public readonly record struct BlobCommitResult(bool Verified, bool Deduplicated, long Size, Digest Actual);

/// <summary>
/// Content-addressed blob storage on disk.
///
/// Layout (see hoard-spec.md):
///   {root}/blobs/sha256/ab/cd/&lt;hex&gt;/data   ← committed blobs, 2-char sharding
///   {root}/uploads/&lt;uuid&gt;                    ← temp files during upload
///
/// uploads and blobs share a root (same filesystem) so the final step is an atomic rename.
/// Knows nothing about repos or the database — purely files.
/// </summary>
public sealed class BlobStore
{
    private readonly string _blobsDir;
    private readonly string _uploadsDir;

    public BlobStore(string rootDir)
    {
        _blobsDir = Path.Combine(rootDir, "blobs");
        _uploadsDir = Path.Combine(rootDir, "uploads");
        Directory.CreateDirectory(_blobsDir);
        Directory.CreateDirectory(_uploadsDir);
    }

    /// <summary>The on-disk path where a blob with this digest is (or would be) stored.</summary>
    public string PathFor(Digest digest)
    {
        var hex = digest.Hex;
        return Path.Combine(_blobsDir, Digest.Algorithm, hex[..2], hex[2..4], hex, "data");
    }

    public bool Exists(Digest digest) => File.Exists(PathFor(digest));

    /// <summary>Size of a committed blob, or null if it doesn't exist.</summary>
    public long? Size(Digest digest)
    {
        var info = new FileInfo(PathFor(digest));
        return info.Exists ? info.Length : null;
    }

    public Stream OpenRead(Digest digest)
        => new FileStream(PathFor(digest), FileMode.Open, FileAccess.Read, FileShare.Read);

    /// <summary>Path of the temp file backing an in-progress upload.</summary>
    public string UploadPath(string uuid) => Path.Combine(_uploadsDir, uuid);

    /// <summary>Begins an upload: allocates a uuid and creates its (empty) temp file.</summary>
    public string StartUpload()
    {
        var uuid = Guid.NewGuid().ToString("D");
        using (File.Create(UploadPath(uuid))) { }
        return uuid;
    }

    /// <summary>Appends a chunk to the upload's temp file and returns the new total size.</summary>
    public async Task<long> AppendAsync(string uuid, Stream data, CancellationToken ct = default)
    {
        await using var fs = new FileStream(UploadPath(uuid), FileMode.Append, FileAccess.Write, FileShare.None);
        await data.CopyToAsync(fs, ct);
        return fs.Length;
    }

    /// <summary>Bytes written to the upload so far.</summary>
    public long CurrentSize(string uuid) => new FileInfo(UploadPath(uuid)).Length;

    /// <summary>
    /// Finalizes an upload: hashes the temp file, verifies it against <paramref name="expected"/>,
    /// and atomically moves it into the blob store. On digest mismatch the temp file is discarded
    /// and nothing is stored. If the blob already exists, the temp is dropped (race-safe dedup).
    /// </summary>
    public async Task<BlobCommitResult> CommitAsync(string uuid, Digest expected, CancellationToken ct = default)
    {
        var tempPath = UploadPath(uuid);

        long size;
        Digest actual;
        await using (var fs = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            size = fs.Length;
            actual = await Digest.ComputeAsync(fs, ct);
        }

        if (actual != expected)
        {
            TryDelete(tempPath);
            return new BlobCommitResult(Verified: false, Deduplicated: false, size, actual);
        }

        var finalPath = PathFor(expected);

        if (File.Exists(finalPath))
        {
            TryDelete(tempPath);
            return new BlobCommitResult(Verified: true, Deduplicated: true, size, actual);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        try
        {
            File.Move(tempPath, finalPath); // atomic within the same volume
            return new BlobCommitResult(Verified: true, Deduplicated: false, size, actual);
        }
        catch (IOException) when (File.Exists(finalPath))
        {
            // Another upload of the same content won the race: dedup.
            TryDelete(tempPath);
            return new BlobCommitResult(Verified: true, Deduplicated: true, size, actual);
        }
    }

    /// <summary>
    /// Writes a complete piece of content (e.g. a manifest body) directly into the store,
    /// keyed by its own digest. Returns the digest. No-op if already present.
    /// </summary>
    public async Task<Digest> PutContentAsync(byte[] content, CancellationToken ct = default)
    {
        var digest = Digest.Compute(content);
        var finalPath = PathFor(digest);
        if (File.Exists(finalPath))
            return digest;

        var tempPath = UploadPath(Guid.NewGuid().ToString("N"));
        await File.WriteAllBytesAsync(tempPath, content, ct);

        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        try
        {
            File.Move(tempPath, finalPath);
        }
        catch (IOException) when (File.Exists(finalPath))
        {
            TryDelete(tempPath);
        }
        return digest;
    }

    /// <summary>Deletes a committed blob file (and prunes now-empty shard directories).</summary>
    public void DeleteBlob(Digest digest)
    {
        var path = PathFor(digest);
        TryDelete(path);
        TryPruneEmptyDirs(Path.GetDirectoryName(path));
    }

    /// <summary>Discards an in-progress upload's temp file.</summary>
    public void AbortUpload(string uuid) => TryDelete(UploadPath(uuid));

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }

    // Removes the <hex> dir and its now-empty cd/ and ab/ parents, stopping at the first non-empty.
    private void TryPruneEmptyDirs(string? dir)
    {
        for (var i = 0; i < 3 && dir is not null && dir.StartsWith(_blobsDir, StringComparison.Ordinal); i++)
        {
            try
            {
                if (!Directory.Exists(dir) || Directory.EnumerateFileSystemEntries(dir).Any())
                    break;
                Directory.Delete(dir);
                dir = Path.GetDirectoryName(dir);
            }
            catch
            {
                break;
            }
        }
    }
}
