using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Hoardr.Tests.Support;

/// <summary>Boots the real Hoardr web app against an isolated temp data directory.</summary>
public sealed class RegistryAppFactory : WebApplicationFactory<Program>
{
    public const string MasterToken = "test-master-token";

    private readonly string _dataRoot =
        Path.Combine(Path.GetTempPath(), "hoardr-it-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Hoardr:DataRoot", _dataRoot);
        builder.UseSetting("Hoardr:MasterToken", MasterToken);
        builder.UseEnvironment("Development");
    }

    public static AuthenticationHeaderValue Basic(string user, string pass)
        => new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}")));

    public static AuthenticationHeaderValue Master => Basic("master", MasterToken);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch { /* best effort */ }
    }
}
