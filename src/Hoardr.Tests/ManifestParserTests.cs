using System.Text;
using Hoardr.Core;
using Hoardr.Core.Registry;

namespace Hoardr.Tests;

public class ManifestParserTests
{
    private const string ConfigDigest = "sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string Layer1 = "sha256:2222222222222222222222222222222222222222222222222222222222222222";
    private const string Layer2 = "sha256:3333333333333333333333333333333333333333333333333333333333333333";
    private const string Amd64 = "sha256:4444444444444444444444444444444444444444444444444444444444444444";
    private const string Arm64 = "sha256:5555555555555555555555555555555555555555555555555555555555555555";

    [Fact]
    public void Parses_Image_Manifest_Config_And_Layers_As_Blobs()
    {
        var json = $$"""
        {
          "schemaVersion": 2,
          "mediaType": "application/vnd.oci.image.manifest.v1+json",
          "config": { "mediaType": "application/vnd.oci.image.config.v1+json", "digest": "{{ConfigDigest}}", "size": 7023 },
          "layers": [
            { "mediaType": "application/vnd.oci.image.layer.v1.tar+gzip", "digest": "{{Layer1}}", "size": 32654 },
            { "mediaType": "application/vnd.oci.image.layer.v1.tar+gzip", "digest": "{{Layer2}}", "size": 16724 }
          ]
        }
        """;

        var info = ManifestParser.Parse(Encoding.UTF8.GetBytes(json), contentType: null);

        Assert.Equal("application/vnd.oci.image.manifest.v1+json", info.MediaType);
        Assert.Equal(3, info.Children.Count);
        Assert.All(info.Children, c => Assert.Equal(ManifestChildKind.Blob, c.Kind));
        Assert.Contains(info.Children, c => c.Digest == Parse(ConfigDigest));
        Assert.Contains(info.Children, c => c.Digest == Parse(Layer1));
        Assert.Contains(info.Children, c => c.Digest == Parse(Layer2));
    }

    [Fact]
    public void Parses_Image_Index_Manifests_As_Manifest_Children()
    {
        var json = $$"""
        {
          "schemaVersion": 2,
          "mediaType": "application/vnd.oci.image.index.v1+json",
          "manifests": [
            { "mediaType": "application/vnd.oci.image.manifest.v1+json", "digest": "{{Amd64}}", "platform": { "architecture": "amd64", "os": "linux" } },
            { "mediaType": "application/vnd.oci.image.manifest.v1+json", "digest": "{{Arm64}}", "platform": { "architecture": "arm64", "os": "linux" } }
          ]
        }
        """;

        var info = ManifestParser.Parse(Encoding.UTF8.GetBytes(json), contentType: null);

        Assert.Equal("application/vnd.oci.image.index.v1+json", info.MediaType);
        Assert.Equal(2, info.Children.Count);
        Assert.All(info.Children, c => Assert.Equal(ManifestChildKind.Manifest, c.Kind));
    }

    [Fact]
    public void Falls_Back_To_ContentType_When_Body_Has_No_MediaType()
    {
        var json = $$"""
        { "schemaVersion": 2, "config": { "digest": "{{ConfigDigest}}" }, "layers": [] }
        """;

        var info = ManifestParser.Parse(Encoding.UTF8.GetBytes(json),
            contentType: "application/vnd.docker.distribution.manifest.v2+json");

        Assert.Equal("application/vnd.docker.distribution.manifest.v2+json", info.MediaType);
        Assert.Single(info.Children);
    }

    [Fact]
    public void Throws_On_Invalid_Json()
    {
        Assert.ThrowsAny<Exception>(() => ManifestParser.Parse(Encoding.UTF8.GetBytes("{ not json"), null));
    }

    private static Digest Parse(string s)
    {
        Assert.True(Digest.TryParse(s, out var d));
        return d;
    }
}
