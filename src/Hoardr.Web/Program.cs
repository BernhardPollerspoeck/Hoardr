using Hoardr.Core;
using Hoardr.Core.Auth;
using Hoardr.Core.Data;
using Hoardr.Core.Notifications;
using Hoardr.Core.Registry;
using Hoardr.Core.Retention;
using Hoardr.Core.Workers;
using Hoardr.Web.Auth;
using Hoardr.Web.Components;
using Hoardr.Web.Oci;
using Hoardr.Web.Workers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Serilog;
using Serilog.Events;
using SproutDB.Core;
using SproutDB.Core.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

// Structured logging: console (Docker stdout) + a size-capped, self-rotating file so logs never
// fill the disk. Files live under {DataRoot}/logs, each capped at 10 MB, max 7 files retained
// (~70 MB ceiling). Tune levels in appsettings under "Serilog" if needed.
var logDirectory = Path.Combine(
    builder.Configuration["Hoardr:DataRoot"] ?? Path.Combine(builder.Environment.ContentRootPath, "data"),
    "logs");
builder.Host.UseSerilog((context, loggerConfig) => loggerConfig
    .ReadFrom.Configuration(context.Configuration)
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(logDirectory, "hoardr-.log"),
        rollingInterval: RollingInterval.Day,
        fileSizeLimitBytes: 10L * 1024 * 1024,
        rollOnFileSizeLimit: true,
        retainedFileCountLimit: 7,
        shared: true));

// Optional Hoardr-managed port + HTTPS. If neither is configured, ASP.NET defaults apply
// (ASPNETCORE_HTTP_PORTS / launchSettings) — so the simple case stays zero-config.
builder.WebHost.ConfigureKestrel((context, kestrel) =>
{
    var cfg = context.Configuration;
    var httpPort = cfg.GetValue<int?>("Hoardr:Http:Port");
    var httpsEnabled = cfg.GetValue("Hoardr:Https:Enabled", false);
    if (httpPort is null && !httpsEnabled)
        return; // leave the defaults untouched

    kestrel.ListenAnyIP(httpPort ?? 8080);

    if (httpsEnabled)
    {
        var httpsPort = cfg.GetValue("Hoardr:Https:Port", 8443);
        kestrel.ListenAnyIP(httpsPort, listen =>
        {
            var cert = LoadCertificate(cfg);
            if (cert is null)
                listen.UseHttps(); // ASP.NET dev certificate — local testing only
            else
                listen.UseHttps(cert);
        });
    }
});

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Cookie auth for the human UI; Docker clients keep using HTTP Basic on /v2 (handled in OciRegistryEndpoints).
builder.Services.AddAuthentication(LoginEndpoints.Scheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.LogoutPath = "/logout";
        options.AccessDeniedPath = "/app"; // logged-in but lacking the role → back to the dashboard
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        options.SlidingExpiration = true;
        options.Cookie.Name = "hoardr_auth";
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

// Data Protection keys live in SproutDB (not on disk) so cookies survive restarts.
builder.Services.AddSingleton(sp => new SproutXmlRepository(sp.GetRequiredService<ISproutDatabase>()));
builder.Services.AddDataProtection().SetApplicationName("hoardr");
builder.Services.AddOptions<KeyManagementOptions>()
    .Configure<SproutXmlRepository>((options, repo) => options.XmlRepository = repo);

// SproutDB holds all registry metadata. Blobs themselves live on disk (see BlobStore).
var dataRoot = builder.Configuration["Hoardr:DataRoot"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "data");
builder.Services.AddSproutDB(options =>
{
    options.DataDirectory = Path.Combine(dataRoot, "sproutdb");
    options.AddMigrations<M001_InitialSchema>(HoardrDb.Name);
});

// Registry services (singletons — one process, SproutDB writes are synchronous).
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp => sp.GetRequiredService<ISproutServer>().GetOrCreateDatabase(HoardrDb.Name));
builder.Services.AddSingleton(new BlobStore(dataRoot));
builder.Services.AddSingleton(sp => new RegistryMetadata(sp.GetRequiredService<ISproutDatabase>(), sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton(sp => new UploadSessionService(sp.GetRequiredService<ISproutDatabase>(), sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton(sp => new RepoSettingsService(sp.GetRequiredService<ISproutDatabase>()));
builder.Services.AddSingleton<RegistryService>();
builder.Services.AddSingleton(sp => new AccountService(sp.GetRequiredService<ISproutDatabase>(), sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton(sp => new Authenticator(sp.GetRequiredService<AccountService>(), builder.Configuration["Hoardr:MasterToken"]));

// Tag retention + maintenance workers.
var retentionDefault = new RetentionPolicy(
    builder.Configuration.GetValue("Hoardr:Retention:KeepMin", 10),
    builder.Configuration.GetValue("Hoardr:Retention:MaxAgeDays", 30));
builder.Services.AddSingleton(sp => new RetentionService(sp.GetRequiredService<ISproutDatabase>(), retentionDefault));
builder.Services.AddSingleton(sp => new UploadCleaner(sp.GetRequiredService<BlobStore>(), sp.GetRequiredService<UploadSessionService>(), sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton(sp => new TagRetention(sp.GetRequiredService<RegistryMetadata>(), sp.GetRequiredService<RetentionService>(), sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton(sp => new GarbageCollector(sp.GetRequiredService<BlobStore>(), sp.GetRequiredService<RegistryMetadata>(), sp.GetRequiredService<TimeProvider>()));
builder.Services.AddHostedService<UploadCleanupService>();
builder.Services.AddHostedService<RetentionGcService>();

// ntfy disk-space alerts.
builder.Services.AddSingleton(sp => new NtfyService(sp.GetRequiredService<ISproutDatabase>(), new HttpClient { Timeout = TimeSpan.FromSeconds(10) }));
builder.Services.AddSingleton(sp => new DiskSpaceMonitor(
    sp.GetRequiredService<NtfyService>(),
    () =>
    {
        var root = Path.GetPathRoot(Path.GetFullPath(dataRoot));
        var drive = new DriveInfo(string.IsNullOrEmpty(root) ? dataRoot : root);
        return new DiskUsage(drive.TotalSize, drive.AvailableFreeSpace);
    },
    sp.GetRequiredService<TimeProvider>()));
builder.Services.AddHostedService<DiskMonitorService>();

var app = builder.Build();

// One structured log line per HTTP request (method, path, status, elapsed) — covers all /v2 OCI calls.
app.UseSerilogRequestLogging();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// OCI registry API (/v2/...) — must be reachable before the Blazor fallback.
app.MapOci();
app.MapAuthEndpoints();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

// Loads the HTTPS certificate from config, or null to fall back to the ASP.NET dev cert.
// Supports either a single PFX/PKCS#12, or a separate certificate (.cer/.pem, DER or PEM)
// plus a PEM private key (.key, RSA or EC). The result is round-tripped through PKCS#12 so
// TLS works on every platform.
static X509Certificate2? LoadCertificate(IConfiguration cfg)
{
    var certPath = cfg["Hoardr:Https:CertPath"];
    if (string.IsNullOrEmpty(certPath))
        return null;

    var keyPath = cfg["Hoardr:Https:CertKeyPath"];
    var password = cfg["Hoardr:Https:CertPassword"];

    if (string.IsNullOrEmpty(keyPath))
        return X509CertificateLoader.LoadPkcs12FromFile(certPath, password); // single PFX bundle

    // Separate cert + key (e.g. .cer + .key).
    using var publicCert = X509CertificateLoader.LoadCertificateFromFile(certPath);
    using var withKey = AttachPrivateKey(publicCert, File.ReadAllText(keyPath));
    return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null);

    static X509Certificate2 AttachPrivateKey(X509Certificate2 cert, string keyPem)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(keyPem);
            return cert.CopyWithPrivateKey(rsa);
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(keyPem);
            return cert.CopyWithPrivateKey(ecdsa);
        }
    }
}

// Exposed for WebApplicationFactory in the test project.
public partial class Program;
