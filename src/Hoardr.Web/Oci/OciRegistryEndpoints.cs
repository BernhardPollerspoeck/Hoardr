using System.Text;
using System.Text.Json;
using Hoardr.Core;
using Hoardr.Core.Auth;
using Hoardr.Core.Registry;

namespace Hoardr.Web.Oci;

/// <summary>
/// OCI Distribution Spec v2 HTTP surface. Repository names may contain slashes
/// (e.g. <c>library/nginx</c>), so we register one catch-all route and dispatch by
/// matching the well-known suffixes (<c>/blobs/uploads</c>, <c>/blobs/</c>, <c>/manifests/</c>, <c>/tags/list</c>).
/// </summary>
public static class OciRegistryEndpoints
{
    private static readonly string[] Methods = ["GET", "HEAD", "POST", "PATCH", "PUT", "DELETE"];

    public static void MapOci(this WebApplication app)
    {
        app.MapMethods("/v2", Methods, Dispatch).DisableAntiforgery();
        app.MapMethods("/v2/{**path}", Methods, Dispatch).DisableAntiforgery();
    }

    private static async Task Dispatch(HttpContext ctx, RegistryService reg, Authenticator auth, AccountService accounts, string? path = null)
    {
        // Identity may be null (anonymous) — public repos allow anonymous reads.
        var identity = auth.Authenticate(ctx.Request.Headers.Authorization.ToString());

        path = path ?? "";

        // GET /v2/ — API probe. Validate creds if provided (so `docker login` fails on bad
        // credentials), but allow an anonymous probe so anonymous pulls can proceed.
        if (path.Length == 0 || path == "/")
        {
            var credentialsProvided = !string.IsNullOrEmpty(ctx.Request.Headers.Authorization);
            if (credentialsProvided && identity is null)
            {
                await WriteError(ctx, 401, "UNAUTHORIZED", "invalid credentials");
                return;
            }
            ctx.Response.Headers["Docker-Distribution-API-Version"] = "registry/2.0";
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            return;
        }

        if (path == "_catalog")
        {
            await HandleCatalog(ctx, reg, identity);
            return;
        }

        if (path.EndsWith("/tags/list", StringComparison.Ordinal))
        {
            var name = path[..^"/tags/list".Length].Trim('/');
            await HandleTagsList(ctx, reg, auth, accounts, identity, name);
            return;
        }

        var mi = path.LastIndexOf("/manifests/", StringComparison.Ordinal);
        if (mi >= 0)
        {
            await HandleManifest(ctx, reg, auth, accounts, identity, path[..mi], path[(mi + "/manifests/".Length)..]);
            return;
        }

        var ui = path.LastIndexOf("/blobs/uploads", StringComparison.Ordinal);
        if (ui >= 0)
        {
            await HandleUpload(ctx, reg, auth, accounts, identity, path[..ui], path[(ui + "/blobs/uploads".Length)..].Trim('/'));
            return;
        }

        var bi = path.LastIndexOf("/blobs/", StringComparison.Ordinal);
        if (bi >= 0)
        {
            await HandleBlob(ctx, reg, auth, accounts, identity, path[..bi], path[(bi + "/blobs/".Length)..]);
            return;
        }

        await WriteError(ctx, 404, "NOT_FOUND", "unknown endpoint");
    }

    // ------------------------------------------------------------------- Catalog

    private static async Task HandleCatalog(HttpContext ctx, RegistryService reg, Identity? identity)
    {
        if (identity is null)
        {
            await WriteError(ctx, 401, "UNAUTHORIZED", "authentication required");
            return;
        }
        if (!identity.IsMaster)
        {
            await WriteError(ctx, 403, "DENIED", "catalog is master-only");
            return;
        }
        await WriteJson(ctx, 200, new { repositories = reg.Metadata.ListRepos() });
    }

    // ---------------------------------------------------------------------- Tags

    private static async Task HandleTagsList(HttpContext ctx, RegistryService reg, Authenticator auth, AccountService accounts, Identity? identity, string name)
    {
        if (!AllowRead(reg, auth, accounts, identity, name))
        {
            await DenyRead(ctx, identity);
            return;
        }
        await WriteJson(ctx, 200, new { name, tags = reg.ListTags(name).Select(t => t.Name).ToArray() });
    }

    // --------------------------------------------------------------------- Blobs

    private static async Task HandleBlob(HttpContext ctx, RegistryService reg, Authenticator auth, AccountService accounts, Identity? identity, string name, string digestStr)
    {
        if (ctx.Request.Method is not ("GET" or "HEAD" or "DELETE"))
        {
            await WriteError(ctx, 405, "UNSUPPORTED", "method not allowed on blob");
            return;
        }
        if (!Digest.TryParse(digestStr, out var digest))
        {
            await WriteError(ctx, 400, "DIGEST_INVALID", "invalid digest");
            return;
        }

        if (ctx.Request.Method == "DELETE")
        {
            if (identity is null) { await WriteError(ctx, 401, "UNAUTHORIZED", "authentication required"); return; }
            if (!auth.CanDelete(identity, name))
            {
                await WriteError(ctx, 403, "DENIED", "no delete permission");
                return;
            }
            if (!reg.DeleteBlobFromRepo(name, digest))
            {
                await WriteError(ctx, 404, "BLOB_UNKNOWN", "blob not found");
                return;
            }
            ctx.Response.StatusCode = StatusCodes.Status202Accepted;
            return;
        }

        if (!AllowRead(reg, auth, accounts, identity, name))
        {
            await DenyRead(ctx, identity);
            return;
        }
        if (!reg.BlobReadable(name, digest))
        {
            await WriteError(ctx, 404, "BLOB_UNKNOWN", "blob not found");
            return;
        }

        var size = reg.BlobSize(digest) ?? 0;
        ctx.Response.Headers["Docker-Content-Digest"] = digest.ToString();
        ctx.Response.ContentType = "application/octet-stream";
        ctx.Response.ContentLength = size;
        ctx.Response.StatusCode = StatusCodes.Status200OK;

        if (ctx.Request.Method == "HEAD")
            return;

        await using var stream = reg.OpenBlob(digest);
        await stream.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
    }

    // ------------------------------------------------------------------- Uploads

    private static async Task HandleUpload(HttpContext ctx, RegistryService reg, Authenticator auth, AccountService accounts, Identity? identity, string name, string uuid)
    {
        if (identity is null) { await WriteError(ctx, 401, "UNAUTHORIZED", "authentication required"); return; }
        if (!AllowPush(auth, accounts, identity, name))
        {
            await WriteError(ctx, 403, "DENIED", "no push permission");
            return;
        }

        switch (ctx.Request.Method)
        {
            case "POST":
                await StartOrCompleteUpload(ctx, reg, auth, identity, name);
                return;

            case "PATCH":
                if (!reg.UploadExists(uuid))
                {
                    await WriteError(ctx, 404, "BLOB_UPLOAD_UNKNOWN", "no such upload");
                    return;
                }
                var written = await reg.AppendAsync(uuid, ctx.Request.Body, ctx.RequestAborted);
                SetUploadHeaders(ctx, name, uuid, written);
                ctx.Response.StatusCode = StatusCodes.Status202Accepted;
                return;

            case "PUT":
                await FinishUpload(ctx, reg, name, uuid);
                return;

            default:
                await WriteError(ctx, 405, "UNSUPPORTED", "method not allowed on upload");
                return;
        }
    }

    private static async Task StartOrCompleteUpload(HttpContext ctx, RegistryService reg, Authenticator auth, Identity identity, string name)
    {
        var q = ctx.Request.Query;
        var mount = q["mount"].ToString();
        var from = q["from"].ToString();
        var digestParam = q["digest"].ToString();

        // Cross-repo mount
        if (!string.IsNullOrEmpty(mount) && !string.IsNullOrEmpty(from))
        {
            if (Digest.TryParse(mount, out var md) && auth.CanPull(identity, from) && reg.TryMount(name, md, from))
            {
                ctx.Response.Headers.Location = $"/v2/{name}/blobs/{md}";
                ctx.Response.Headers["Docker-Content-Digest"] = md.ToString();
                ctx.Response.StatusCode = StatusCodes.Status201Created;
                return;
            }
            // Fall through to a normal upload if the mount can't be satisfied.
        }

        // Monolithic upload or existence shortcut
        if (!string.IsNullOrEmpty(digestParam))
        {
            if (!Digest.TryParse(digestParam, out var dd))
            {
                await WriteError(ctx, 400, "DIGEST_INVALID", "invalid digest");
                return;
            }

            var hasBody = ctx.Request.ContentLength is > 0;
            if (hasBody)
            {
                var uuid = reg.StartUpload(name);
                await reg.AppendAsync(uuid, ctx.Request.Body, ctx.RequestAborted);
                var outcome = await reg.FinalizeAsync(name, uuid, dd, ctx.RequestAborted);
                if (!outcome.Verified)
                {
                    await WriteError(ctx, 400, "DIGEST_INVALID", "uploaded content does not match digest");
                    return;
                }
                WriteBlobCreated(ctx, name, dd);
                return;
            }

            // No body: spec shortcut — link if we already have it, else start a session.
            if (reg.BlobExistsGlobally(dd))
            {
                reg.LinkExistingBlob(name, dd);
                WriteBlobCreated(ctx, name, dd);
                return;
            }
        }

        // Default: begin a resumable upload session.
        var newUuid = reg.StartUpload(name);
        SetUploadHeaders(ctx, name, newUuid, 0);
        ctx.Response.StatusCode = StatusCodes.Status202Accepted;
    }

    private static async Task FinishUpload(HttpContext ctx, RegistryService reg, string name, string uuid)
    {
        if (!reg.UploadExists(uuid))
        {
            await WriteError(ctx, 404, "BLOB_UPLOAD_UNKNOWN", "no such upload");
            return;
        }
        if (!Digest.TryParse(ctx.Request.Query["digest"].ToString(), out var digest))
        {
            await WriteError(ctx, 400, "DIGEST_INVALID", "missing or invalid digest");
            return;
        }

        if (ctx.Request.ContentLength is > 0)
            await reg.AppendAsync(uuid, ctx.Request.Body, ctx.RequestAborted);

        var outcome = await reg.FinalizeAsync(name, uuid, digest, ctx.RequestAborted);
        if (!outcome.Verified)
        {
            await WriteError(ctx, 400, "DIGEST_INVALID", "uploaded content does not match digest");
            return;
        }
        WriteBlobCreated(ctx, name, digest);
    }

    // ----------------------------------------------------------------- Manifests

    private static async Task HandleManifest(HttpContext ctx, RegistryService reg, Authenticator auth, AccountService accounts, Identity? identity, string name, string reference)
    {
        switch (ctx.Request.Method)
        {
            case "GET":
            case "HEAD":
                if (!AllowRead(reg, auth, accounts, identity, name)) { await DenyRead(ctx, identity); return; }
                var info = reg.ResolveManifest(name, reference);
                if (info is null) { await WriteError(ctx, 404, "MANIFEST_UNKNOWN", "manifest unknown"); return; }

                ctx.Response.ContentType = info.MediaType;
                ctx.Response.Headers["Docker-Content-Digest"] = info.Digest.ToString();
                ctx.Response.ContentLength = info.Size;
                ctx.Response.StatusCode = StatusCodes.Status200OK;
                if (ctx.Request.Method == "HEAD") return;

                await using (var stream = reg.OpenManifest(info.Digest))
                    await stream.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
                return;

            case "PUT":
                if (identity is null) { await WriteError(ctx, 401, "UNAUTHORIZED", "authentication required"); return; }
                if (!AllowPush(auth, accounts, identity, name)) { await WriteError(ctx, 403, "DENIED", "no push permission"); return; }
                var body = await ReadAllBytes(ctx.Request.Body, ctx.RequestAborted);
                Digest digest;
                try
                {
                    digest = await reg.PutManifestAsync(name, reference, body, ctx.Request.ContentType, ctx.RequestAborted);
                }
                catch (JsonException)
                {
                    await WriteError(ctx, 400, "MANIFEST_INVALID", "manifest is not valid JSON");
                    return;
                }
                ctx.Response.Headers.Location = $"/v2/{name}/manifests/{reference}";
                ctx.Response.Headers["Docker-Content-Digest"] = digest.ToString();
                ctx.Response.StatusCode = StatusCodes.Status201Created;
                return;

            case "DELETE":
                if (identity is null) { await WriteError(ctx, 401, "UNAUTHORIZED", "authentication required"); return; }
                if (!auth.CanDelete(identity, name)) { await WriteError(ctx, 403, "DENIED", "no delete permission"); return; }
                if (!reg.DeleteManifestReference(name, reference)) { await WriteError(ctx, 404, "MANIFEST_UNKNOWN", "manifest unknown"); return; }
                ctx.Response.StatusCode = StatusCodes.Status202Accepted;
                return;

            default:
                await WriteError(ctx, 405, "UNSUPPORTED", "method not allowed on manifest");
                return;
        }
    }

    // ------------------------------------------------------------------- Helpers

    // Push is allowed with explicit push permission, or — for a "can create" account — to a repo
    // that nobody has any permission on yet (creates it on first push). NO permission is granted:
    // the admin assigns permissions actively. Once any permission exists on the repo, can_create
    // no longer applies there and explicit push is required. (Security over convenience.)
    private static bool AllowPush(Authenticator auth, AccountService accounts, Identity identity, string repo)
    {
        if (auth.CanPush(identity, repo))
            return true;

        return !identity.IsMaster
            && accounts.GetCanCreate(identity.AccountId)
            && !accounts.AnyPermissionForRepo(repo);
    }

    // Pull is allowed with explicit pull permission, or — for a "can create" account — on a repo
    // it may create/push to (unclaimed). Needed so `docker push` (which HEADs blobs first) works.
    private static bool AllowPull(Authenticator auth, AccountService accounts, Identity identity, string repo)
    {
        if (auth.CanPull(identity, repo))
            return true;

        return !identity.IsMaster
            && accounts.GetCanCreate(identity.AccountId)
            && !accounts.AnyPermissionForRepo(repo);
    }

    // Read is allowed if the repo is public (anonymous pull), or the caller may pull it.
    private static bool AllowRead(RegistryService reg, Authenticator auth, AccountService accounts, Identity? identity, string repo)
        => reg.IsPublicRead(repo) || (identity is not null && AllowPull(auth, accounts, identity, repo));

    // Denied read: challenge anonymous callers (so they can authenticate), forbid known ones.
    private static Task DenyRead(HttpContext ctx, Identity? identity)
        => identity is null
            ? WriteError(ctx, 401, "UNAUTHORIZED", "authentication required")
            : WriteError(ctx, 403, "DENIED", "no pull permission");

    private static void SetUploadHeaders(HttpContext ctx, string name, string uuid, long written)
    {
        ctx.Response.Headers.Location = $"/v2/{name}/blobs/uploads/{uuid}";
        ctx.Response.Headers["Docker-Upload-UUID"] = uuid;
        ctx.Response.Headers.Range = written > 0 ? $"0-{written - 1}" : "0-0";
        ctx.Response.ContentLength = 0;
    }

    private static void WriteBlobCreated(HttpContext ctx, string name, Digest digest)
    {
        ctx.Response.Headers.Location = $"/v2/{name}/blobs/{digest}";
        ctx.Response.Headers["Docker-Content-Digest"] = digest.ToString();
        ctx.Response.ContentLength = 0;
        ctx.Response.StatusCode = StatusCodes.Status201Created;
    }

    private static async Task<byte[]> ReadAllBytes(Stream body, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await body.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    private static async Task WriteError(HttpContext ctx, int status, string code, string message)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        if (status == 401)
            ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"hoardr\"";
        var payload = JsonSerializer.Serialize(new { errors = new[] { new { code, message } } });
        await ctx.Response.WriteAsync(payload, ctx.RequestAborted);
    }

    private static async Task WriteJson(HttpContext ctx, int status, object value)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(value), ctx.RequestAborted);
    }
}
