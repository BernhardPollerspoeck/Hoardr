using System.Text.Json;

namespace Hoardr.Core.Registry;

/// <summary>The media type of a manifest plus the blobs/sub-manifests it references.</summary>
public sealed record ManifestInfo(string MediaType, IReadOnlyList<ManifestChild> Children);

/// <summary>
/// Extracts child references from an OCI/Docker manifest body.
///
/// - Image manifest  → config + layers (child_kind = blob)
/// - Image index / manifest list → manifests[] (child_kind = manifest)
/// </summary>
public static class ManifestParser
{
    public static ManifestInfo Parse(byte[] content, string? contentType)
    {
        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        var mediaType =
            root.TryGetProperty("mediaType", out var mt) && mt.ValueKind == JsonValueKind.String
                ? mt.GetString()!
                : contentType ?? "application/octet-stream";

        var children = new List<ManifestChild>();

        if (root.TryGetProperty("manifests", out var manifests) && manifests.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in manifests.EnumerateArray())
                if (TryDigest(m, out var d))
                    children.Add(new ManifestChild(d, ManifestChildKind.Manifest));
        }
        else
        {
            if (root.TryGetProperty("config", out var config) && TryDigest(config, out var configDigest))
                children.Add(new ManifestChild(configDigest, ManifestChildKind.Blob));

            if (root.TryGetProperty("layers", out var layers) && layers.ValueKind == JsonValueKind.Array)
                foreach (var l in layers.EnumerateArray())
                    if (TryDigest(l, out var d))
                        children.Add(new ManifestChild(d, ManifestChildKind.Blob));
        }

        return new ManifestInfo(mediaType, children);
    }

    private static bool TryDigest(JsonElement element, out Digest digest)
    {
        digest = default;
        return element.TryGetProperty("digest", out var d)
            && d.ValueKind == JsonValueKind.String
            && Digest.TryParse(d.GetString(), out digest);
    }
}
