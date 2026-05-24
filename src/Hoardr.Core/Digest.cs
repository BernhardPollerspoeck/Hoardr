using System.Security.Cryptography;

namespace Hoardr.Core;

/// <summary>
/// A content-addressable digest in the form <c>algorithm:hex</c>, e.g. <c>sha256:abc...</c>.
/// Only sha256 is supported (the only algorithm OCI clients use in practice).
/// </summary>
public readonly record struct Digest
{
    public const string Algorithm = "sha256";

    /// <summary>The lowercase hex encoding of the hash (64 chars for sha256).</summary>
    public string Hex { get; }

    private Digest(string hex) => Hex = hex;

    /// <summary>The canonical string form, e.g. <c>sha256:abcd...</c>.</summary>
    public override string ToString() => $"{Algorithm}:{Hex}";

    /// <summary>
    /// Parses and validates a digest string. Returns false for anything that isn't a
    /// well-formed lowercase sha256 digest.
    /// </summary>
    public static bool TryParse(string? value, out Digest digest)
    {
        digest = default;
        if (string.IsNullOrEmpty(value))
            return false;

        var sep = value.IndexOf(':');
        if (sep < 0)
            return false;

        var algo = value[..sep];
        var hex = value[(sep + 1)..];

        if (algo != Algorithm || hex.Length != 64)
            return false;

        foreach (var c in hex)
        {
            var ok = c is (>= '0' and <= '9') or (>= 'a' and <= 'f');
            if (!ok)
                return false;
        }

        digest = new Digest(hex);
        return true;
    }

    /// <summary>Computes the sha256 digest of the given bytes.</summary>
    public static Digest Compute(ReadOnlySpan<byte> data)
        => new(Convert.ToHexStringLower(SHA256.HashData(data)));

    /// <summary>Computes the sha256 digest of a stream, reading it to the end.</summary>
    public static async Task<Digest> ComputeAsync(Stream stream, CancellationToken ct = default)
        => new(Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct)));
}
