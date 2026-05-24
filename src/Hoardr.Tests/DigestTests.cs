using Hoardr.Core;

namespace Hoardr.Tests;

public class DigestTests
{
    private const string ValidHex = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

    [Fact]
    public void TryParse_ValidSha256_Succeeds()
    {
        Assert.True(Digest.TryParse($"sha256:{ValidHex}", out var digest));
        Assert.Equal(ValidHex, digest.Hex);
        Assert.Equal($"sha256:{ValidHex}", digest.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abcdef")]                       // no algorithm
    [InlineData("md5:abc")]                       // wrong algorithm
    [InlineData("sha256:tooshort")]               // wrong length
    [InlineData("sha256:ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789")] // uppercase
    public void TryParse_Invalid_Fails(string? value)
    {
        Assert.False(Digest.TryParse(value, out _));
    }
}
