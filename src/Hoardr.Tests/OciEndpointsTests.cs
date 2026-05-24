using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Hoardr.Core;
using Hoardr.Core.Auth;
using Hoardr.Core.Registry;
using Hoardr.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Hoardr.Tests;

public class OciEndpointsTests : IClassFixture<RegistryAppFactory>
{
    private readonly RegistryAppFactory _factory;

    public OciEndpointsTests(RegistryAppFactory factory) => _factory = factory;

    private HttpClient Master()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = RegistryAppFactory.Master;
        return client;
    }

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    // Uploads a blob monolithically and returns its digest.
    private static async Task<Digest> PushBlob(HttpClient client, string repo, byte[] content)
    {
        var digest = Digest.Compute(content);
        var start = await client.PostAsync($"/v2/{repo}/blobs/uploads/", null);
        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
        var location = start.Headers.Location!.ToString();

        var put = await client.PutAsync($"{location}?digest={digest}", new ByteArrayContent(content));
        Assert.Equal(HttpStatusCode.Created, put.StatusCode);
        return digest;
    }

    [Fact]
    public async Task Base_Endpoint_Allows_Anonymous_Probe()
    {
        // No credentials → 200, so anonymous pulls of public repos can proceed.
        var resp = await _factory.CreateClient().GetAsync("/v2/");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Base_Endpoint_Rejects_Bad_Credentials()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = RegistryAppFactory.Basic("master", "wrong-token");

        var resp = await client.GetAsync("/v2/");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Contains("Basic", resp.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task Base_Endpoint_With_Master_Returns_200()
    {
        var resp = await Master().GetAsync("/v2/");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("registry/2.0", resp.Headers.GetValues("Docker-Distribution-API-Version").Single());
    }

    // Pushes a minimal image (config + layer + manifest) and returns (manifest digest, layer digest).
    private async Task<(Digest Manifest, Digest Layer)> PushImage(HttpClient client, string repo, string tag)
    {
        var config = Bytes("""{"architecture":"amd64","os":"linux"}""");
        var configDigest = await PushBlob(client, repo, config);
        var layer = Bytes("layer-of-" + repo);
        var layerDigest = await PushBlob(client, repo, layer);

        var manifestJson = $$"""
        { "schemaVersion": 2, "mediaType": "application/vnd.oci.image.manifest.v1+json",
          "config": { "digest": "{{configDigest}}", "size": {{config.Length}} },
          "layers": [ { "digest": "{{layerDigest}}", "size": {{layer.Length}} } ] }
        """;
        var bytes = Bytes(manifestJson);
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.oci.image.manifest.v1+json");
        var put = await client.PutAsync($"/v2/{repo}/manifests/{tag}", content);
        Assert.Equal(HttpStatusCode.Created, put.StatusCode);
        return (Digest.Compute(bytes), layerDigest);
    }

    [Fact]
    public async Task Anonymous_Pull_Allowed_On_Public_Repo()
    {
        const string repo = "open/app";
        var (_, layer) = await PushImage(Master(), repo, "1.0");
        _factory.Services.GetRequiredService<RepoSettingsService>().SetPublicRead(repo, true);

        var anon = _factory.CreateClient(); // no credentials

        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync($"/v2/{repo}/manifests/1.0")).StatusCode);
        var head = await anon.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/v2/{repo}/blobs/{layer}"));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync($"/v2/{repo}/tags/list")).StatusCode);
    }

    [Fact]
    public async Task Anonymous_Pull_Denied_On_Private_Repo()
    {
        const string repo = "closed/app";
        await PushImage(Master(), repo, "1.0"); // not public

        var anon = _factory.CreateClient();
        var resp = await anon.GetAsync($"/v2/{repo}/manifests/1.0");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Full_Push_And_Pull_RoundTrip()
    {
        var client = Master();
        const string repo = "roundtrip/app";

        var configContent = Bytes("""{"architecture":"amd64","os":"linux"}""");
        var configDigest = await PushBlob(client, repo, configContent);

        var layerContent = Bytes("a fake layer blob");
        var layerDigest = await PushBlob(client, repo, layerContent);

        // HEAD the uploaded blob
        var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/v2/{repo}/blobs/{layerDigest}"));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(layerContent.Length, head.Content.Headers.ContentLength);

        // Push a manifest referencing them
        var manifestJson = $$"""
        {
          "schemaVersion": 2,
          "mediaType": "application/vnd.oci.image.manifest.v1+json",
          "config": { "mediaType": "application/vnd.oci.image.config.v1+json", "digest": "{{configDigest}}", "size": {{configContent.Length}} },
          "layers": [ { "mediaType": "application/vnd.oci.image.layer.v1.tar+gzip", "digest": "{{layerDigest}}", "size": {{layerContent.Length}} } ]
        }
        """;
        var manifestBytes = Bytes(manifestJson);
        var manifestContent = new ByteArrayContent(manifestBytes);
        manifestContent.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.oci.image.manifest.v1+json");
        var putManifest = await client.PutAsync($"/v2/{repo}/manifests/v1", manifestContent);
        Assert.Equal(HttpStatusCode.Created, putManifest.StatusCode);
        var manifestDigest = Digest.Compute(manifestBytes);
        Assert.Equal(manifestDigest.ToString(), putManifest.Headers.GetValues("Docker-Content-Digest").Single());

        // Pull the manifest by tag
        var getManifest = await client.GetAsync($"/v2/{repo}/manifests/v1");
        Assert.Equal(HttpStatusCode.OK, getManifest.StatusCode);
        Assert.Equal(manifestBytes, await getManifest.Content.ReadAsByteArrayAsync());

        // Pull the layer blob content
        var getBlob = await client.GetAsync($"/v2/{repo}/blobs/{layerDigest}");
        Assert.Equal(layerContent, await getBlob.Content.ReadAsByteArrayAsync());

        // Tag shows up in tags list
        var tags = await client.GetFromJsonAsync<JsonElement>($"/v2/{repo}/tags/list");
        Assert.Contains("v1", tags.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
    }

    [Fact]
    public async Task Monolithic_Upload_With_Digest_Param()
    {
        var client = Master();
        const string repo = "mono/app";
        var content = Bytes("monolithic content");
        var digest = Digest.Compute(content);

        var resp = await client.PostAsync($"/v2/{repo}/blobs/uploads/?digest={digest}", new ByteArrayContent(content));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/v2/{repo}/blobs/{digest}"));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
    }

    [Fact]
    public async Task Digest_Mismatch_Returns_400()
    {
        var client = Master();
        const string repo = "bad/app";
        var content = Bytes("real");
        var wrong = Digest.Compute(Bytes("different"));

        var start = await client.PostAsync($"/v2/{repo}/blobs/uploads/", null);
        var location = start.Headers.Location!.ToString();
        var put = await client.PutAsync($"{location}?digest={wrong}", new ByteArrayContent(content));

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    [Fact]
    public async Task Cross_Repo_Mount()
    {
        var client = Master();
        var content = Bytes("shared layer");
        var digest = await PushBlob(client, "src/app", content);

        var mount = await client.PostAsync($"/v2/dst/app/blobs/uploads/?mount={digest}&from=src/app", null);
        Assert.Equal(HttpStatusCode.Created, mount.StatusCode);

        var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/v2/dst/app/blobs/{digest}"));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
    }

    [Fact]
    public async Task Blob_Delete_Unlinks_From_Repo()
    {
        var client = Master();
        const string repo = "del/app";
        var digest = await PushBlob(client, repo, Bytes("to be deleted"));

        var before = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/v2/{repo}/blobs/{digest}"));
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        var del = await client.DeleteAsync($"/v2/{repo}/blobs/{digest}");
        Assert.Equal(HttpStatusCode.Accepted, del.StatusCode);

        var after = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/v2/{repo}/blobs/{digest}"));
        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
    }

    [Fact]
    public async Task Blob_Delete_Requires_Delete_Permission()
    {
        // Master seeds a blob in the repo.
        var digest = await PushBlob(Master(), "perm/app", Bytes("guarded blob"));

        // Account with pull+push but NOT delete.
        var accounts = _factory.Services.GetRequiredService<AccountService>();
        var name = "deleter-" + Guid.NewGuid().ToString("N")[..8];
        var account = accounts.CreateAccount(name, "pw")!;
        accounts.SetPermission(account.Id, "perm/app", canPull: true, canPush: true, canDelete: false);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = RegistryAppFactory.Basic(name, "pw");

        var denied = await client.DeleteAsync($"/v2/perm/app/blobs/{digest}");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        // Grant delete → now allowed.
        accounts.SetPermission(account.Id, "perm/app", canPull: true, canPush: true, canDelete: true);
        var allowed = await client.DeleteAsync($"/v2/perm/app/blobs/{digest}");
        Assert.Equal(HttpStatusCode.Accepted, allowed.StatusCode);
    }

    [Fact]
    public async Task CanCreate_Pushes_New_Repo_Without_Grant_Then_Admin_Controls()
    {
        var accounts = _factory.Services.GetRequiredService<AccountService>();
        var name = "ci-" + Guid.NewGuid().ToString("N")[..8];
        var account = accounts.CreateAccount(name, "pw")!;
        accounts.SetCanCreate(account.Id, true);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = RegistryAppFactory.Basic(name, "pw");

        // docker push HEADs the blob first — must be allowed (404, not 403) for a can_create account.
        var content = Bytes("ci layer");
        var digest = Digest.Compute(content);
        var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/v2/ci/fresh/blobs/{digest}"));
        Assert.Equal(HttpStatusCode.NotFound, head.StatusCode);

        // Push to a repo that has no permissions yet → allowed (creates it).
        var start = await client.PostAsync("/v2/ci/fresh/blobs/uploads/", null);
        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
        var put = await client.PutAsync($"{start.Headers.Location}?digest={digest}", new ByteArrayContent(content));
        Assert.Equal(HttpStatusCode.Created, put.StatusCode);

        // No permission was minted automatically — the admin controls that.
        Assert.Null(accounts.GetPermission(account.Id, "ci/fresh"));

        // Admin actively takes control (any permission on the repo) → can_create no longer bypasses,
        // and without explicit push the account is now denied.
        accounts.SetPermission(account.Id, "ci/fresh", canPull: true, canPush: false);
        var denied = await client.PostAsync("/v2/ci/fresh/blobs/uploads/", null);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task First_Push_Denied_Without_CanCreate()
    {
        var accounts = _factory.Services.GetRequiredService<AccountService>();
        var name = "nocreate-" + Guid.NewGuid().ToString("N")[..8];
        accounts.CreateAccount(name, "pw");

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = RegistryAppFactory.Basic(name, "pw");

        var start = await client.PostAsync("/v2/nocreate/fresh/blobs/uploads/", null);
        Assert.Equal(HttpStatusCode.Forbidden, start.StatusCode);
    }

    [Fact]
    public async Task Account_Permissions_Are_Enforced()
    {
        // Create a pull-only account directly via DI.
        var accounts = _factory.Services.GetRequiredService<AccountService>();
        var name = "puller-" + Guid.NewGuid().ToString("N")[..8];
        var account = accounts.CreateAccount(name, "pw")!;
        accounts.SetPermission(account.Id, "limited/app", canPull: true, canPush: false);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = RegistryAppFactory.Basic(name, "pw");

        // Pull allowed
        var tags = await client.GetAsync("/v2/limited/app/tags/list");
        Assert.Equal(HttpStatusCode.OK, tags.StatusCode);

        // Push denied
        var push = await client.PostAsync("/v2/limited/app/blobs/uploads/", null);
        Assert.Equal(HttpStatusCode.Forbidden, push.StatusCode);

        // No permission on another repo → denied
        var other = await client.GetAsync("/v2/other/app/tags/list");
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);

        // Wrong password → unauthorized
        var bad = _factory.CreateClient();
        bad.DefaultRequestHeaders.Authorization = RegistryAppFactory.Basic(name, "wrong");
        var unauth = await bad.GetAsync("/v2/limited/app/tags/list");
        Assert.Equal(HttpStatusCode.Unauthorized, unauth.StatusCode);
    }
}
