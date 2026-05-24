using System.Text;
using Hoardr.Core;
using Hoardr.Tests.Support;

namespace Hoardr.Tests;

public class BlobStoreTests
{
    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    private static async Task<BlobCommitResult> PushAsync(BlobStore store, byte[] data, Digest expected)
    {
        var uuid = store.StartUpload();
        await store.AppendAsync(uuid, new MemoryStream(data));
        return await store.CommitAsync(uuid, expected);
    }

    [Fact]
    public void PathFor_Shards_By_First_Four_Hex_Chars()
    {
        using var tmp = new TempBlobStore();
        Assert.True(Digest.TryParse(
            "sha256:abcd1234567890abcdef1234567890abcdef1234567890abcdef1234567890ab", out var d));

        var path = tmp.Store.PathFor(d);
        var rel = Path.GetRelativePath(tmp.Root, path).Replace('\\', '/');

        Assert.Equal($"blobs/sha256/ab/cd/{d.Hex}/data", rel);
    }

    [Fact]
    public async Task Commit_With_Matching_Digest_Stores_Blob()
    {
        using var tmp = new TempBlobStore();
        var data = Bytes("hello world");
        var digest = Digest.Compute(data);

        var result = await PushAsync(tmp.Store, data, digest);

        Assert.True(result.Verified);
        Assert.False(result.Deduplicated);
        Assert.Equal(data.Length, result.Size);
        Assert.True(tmp.Store.Exists(digest));
        Assert.Equal(data.Length, tmp.Store.Size(digest));

        using var read = tmp.Store.OpenRead(digest);
        using var ms = new MemoryStream();
        read.CopyTo(ms);
        Assert.Equal(data, ms.ToArray());
    }

    [Fact]
    public async Task Commit_With_Mismatched_Digest_Is_Rejected()
    {
        using var tmp = new TempBlobStore();
        var data = Bytes("real content");
        var wrong = Digest.Compute(Bytes("something else entirely"));

        var uuid = tmp.Store.StartUpload();
        await tmp.Store.AppendAsync(uuid, new MemoryStream(data));
        var result = await tmp.Store.CommitAsync(uuid, wrong);

        Assert.False(result.Verified);
        Assert.False(tmp.Store.Exists(wrong));
        Assert.False(tmp.Store.Exists(Digest.Compute(data)));   // not stored under real digest either
        Assert.False(File.Exists(tmp.Store.UploadPath(uuid)));  // temp cleaned up
    }

    [Fact]
    public async Task Commit_When_Blob_Already_Exists_Deduplicates()
    {
        using var tmp = new TempBlobStore();
        var data = Bytes("dedupe me");
        var digest = Digest.Compute(data);

        var first = await PushAsync(tmp.Store, data, digest);
        Assert.True(first.Verified);
        Assert.False(first.Deduplicated);

        var second = await PushAsync(tmp.Store, data, digest);
        Assert.True(second.Verified);
        Assert.True(second.Deduplicated);
        Assert.Equal(data.Length, second.Size);

        Assert.True(tmp.Store.Exists(digest));
    }

    [Fact]
    public async Task Append_Multiple_Chunks_Accumulates_Then_Verifies()
    {
        using var tmp = new TempBlobStore();
        var part1 = Bytes("foo");
        var part2 = Bytes("bar");
        var part3 = Bytes("baz");
        var whole = part1.Concat(part2).Concat(part3).ToArray();
        var digest = Digest.Compute(whole);

        var uuid = tmp.Store.StartUpload();
        Assert.Equal(3, await tmp.Store.AppendAsync(uuid, new MemoryStream(part1)));
        Assert.Equal(6, await tmp.Store.AppendAsync(uuid, new MemoryStream(part2)));
        Assert.Equal(9, await tmp.Store.AppendAsync(uuid, new MemoryStream(part3)));
        Assert.Equal(9, tmp.Store.CurrentSize(uuid));

        var result = await tmp.Store.CommitAsync(uuid, digest);
        Assert.True(result.Verified);
        Assert.Equal(whole.Length, result.Size);
    }

    [Fact]
    public void AbortUpload_Removes_Temp()
    {
        using var tmp = new TempBlobStore();
        var uuid = tmp.Store.StartUpload();
        Assert.True(File.Exists(tmp.Store.UploadPath(uuid)));

        tmp.Store.AbortUpload(uuid);
        Assert.False(File.Exists(tmp.Store.UploadPath(uuid)));
    }

    [Fact]
    public void Exists_Is_False_For_Unknown_Blob()
    {
        using var tmp = new TempBlobStore();
        var digest = Digest.Compute(Bytes("never stored"));
        Assert.False(tmp.Store.Exists(digest));
        Assert.Null(tmp.Store.Size(digest));
    }

    [Fact]
    public void StartUpload_Returns_Distinct_Ids()
    {
        using var tmp = new TempBlobStore();
        var a = tmp.Store.StartUpload();
        var b = tmp.Store.StartUpload();
        Assert.NotEqual(a, b);
    }
}
