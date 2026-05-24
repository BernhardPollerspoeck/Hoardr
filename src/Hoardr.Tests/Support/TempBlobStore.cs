using Hoardr.Core;

namespace Hoardr.Tests.Support;

/// <summary>A BlobStore rooted in a throwaway temp directory. Dispose deletes it.</summary>
public sealed class TempBlobStore : IDisposable
{
    public string Root { get; }
    public BlobStore Store { get; }

    public TempBlobStore()
    {
        Root = Path.Combine(Path.GetTempPath(), "hoardr-blob-" + Guid.NewGuid().ToString("N"));
        Store = new BlobStore(Root);
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch { /* best effort */ }
    }
}
