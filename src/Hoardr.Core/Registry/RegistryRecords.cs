namespace Hoardr.Core.Registry;

public enum ManifestChildKind { Blob, Manifest }

/// <summary>A child reference extracted from a manifest (a layer/config blob, or a sub-manifest in an index).</summary>
public readonly record struct ManifestChild(Digest Digest, ManifestChildKind Kind);

public sealed record BlobRecord(ulong Id, Digest Digest, long Size, long RefCount, DateTime CreatedAt);

public sealed record ManifestRecord(ulong Id, string Repo, Digest Digest, string MediaType, long RefCount);

public sealed record TagRecord(ulong Id, string Repo, string Name, Digest ManifestDigest, DateTime PushedAt);

public sealed record RepoStats(string Repo, int TagCount, long TotalBytes);
