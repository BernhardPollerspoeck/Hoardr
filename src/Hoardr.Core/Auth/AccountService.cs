using Hoardr.Core.Data;
using SproutDB.Core;
using static Hoardr.Core.Data.Sprout;

namespace Hoardr.Core.Auth;

/// <summary>SproutDB-backed accounts and their per-repo permissions.</summary>
public sealed class AccountService(ISproutDatabase db, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    // ----------------------------------------------------------------- Accounts

    /// <summary>Creates an account. Returns null if the name is already taken.</summary>
    public AccountRecord? CreateAccount(string name, string password)
    {
        if (GetByName(name) is not null)
            return null;

        var hash = PasswordHasher.Hash(password);
        var createdAt = _time.GetUtcNow().UtcDateTime;
        var response = db.Exec($"upsert accounts {{name: {Q(name)}, password_hash: {Q(hash)}, created_at: {Q(Dt(createdAt))}}}");
        if (!response.Ok())
            return null;

        return GetByName(name);
    }

    public AccountRecord? GetByName(string name)
        => MapAccount(db.Exec($"get accounts where name = {Q(name)}").Data);

    public AccountRecord? GetById(ulong id)
        => MapAccount(db.Exec($"get accounts where _id = {id}").Data);

    public IReadOnlyList<AccountRecord> ListAccounts()
    {
        var data = db.Exec("get accounts order by name asc").Data;
        if (data is not { Count: > 0 })
            return [];

        return [.. data.Select(MapRow)];
    }

    public bool DeleteAccount(ulong id)
    {
        if (GetById(id) is null)
            return false;

        db.Exec($"delete repo_permissions where account_id = {id}");
        db.Exec($"delete accounts where _id = {id}");
        return true;
    }

    /// <summary>Returns the account if the password matches, otherwise null.</summary>
    public AccountRecord? VerifyCredentials(string name, string password)
    {
        var account = GetByName(name);
        if (account is null)
            return null;

        return PasswordHasher.Verify(password, account.PasswordHash) ? account : null;
    }

    private static AccountRecord? MapAccount(List<Dictionary<string, object?>>? data)
        => data is { Count: > 0 } ? MapRow(data[0]) : null;

    private static AccountRecord MapRow(Dictionary<string, object?> row)
        => new(row.U64("_id"), row.Str("name"), row.Str("password_hash"), row.Dt("created_at"), row.Bool("can_create"));

    /// <summary>Whether the account may create new repos by pushing to them (CI first-push).</summary>
    public bool GetCanCreate(ulong accountId) => GetById(accountId)?.CanCreate ?? false;

    public void SetCanCreate(ulong accountId, bool canCreate)
    {
        if (GetById(accountId) is null)
            return;
        db.Exec($"upsert accounts {{_id: {accountId}, can_create: {Lower(canCreate)}}}");
    }

    /// <summary>True if any account already holds a permission for this repo (i.e. it's claimed).</summary>
    public bool AnyPermissionForRepo(string repo)
        => db.Exec($"get repo_permissions where repo = {Q(repo)}").Data is { Count: > 0 };

    // -------------------------------------------------------------- Permissions

    /// <summary>Sets (creates or updates) an account's permission for a repo.</summary>
    public void SetPermission(ulong accountId, string repo, bool canPull, bool canPush, bool canDelete = false)
    {
        // Push implies pull: a push HEADs existing layers (a read) before uploading, so a
        // push grant without pull can't actually push. Normalize here so it can't be persisted.
        canPull = canPull || canPush;
        var flags = $"can_pull: {Lower(canPull)}, can_push: {Lower(canPush)}, can_delete: {Lower(canDelete)}";
        var existing = db.Exec($"get repo_permissions where account_id = {accountId} and repo = {Q(repo)}").Data;
        if (existing is { Count: > 0 })
            db.Exec($"upsert repo_permissions {{_id: {existing[0].U64("_id")}, {flags}}}");
        else
            db.Exec($"upsert repo_permissions {{account_id: {accountId}, repo: {Q(repo)}, {flags}}}");
    }

    public RepoPermission? GetPermission(ulong accountId, string repo)
    {
        var data = db.Exec($"get repo_permissions where account_id = {accountId} and repo = {Q(repo)}").Data;
        if (data is not { Count: > 0 })
            return null;

        return Map(data[0]);
    }

    public IReadOnlyList<RepoPermission> ListPermissions(ulong accountId)
    {
        var data = db.Exec($"get repo_permissions where account_id = {accountId} order by repo asc").Data;
        if (data is not { Count: > 0 })
            return [];

        return [.. data.Select(Map)];
    }

    private static RepoPermission Map(Dictionary<string, object?> row)
        => new(row.Str("repo"), row.Bool("can_pull"), row.Bool("can_push"), row.Bool("can_delete"));

    public bool CanPull(ulong accountId, string repo) => GetPermission(accountId, repo) is { CanPull: true };

    public bool CanPush(ulong accountId, string repo) => GetPermission(accountId, repo) is { CanPush: true };

    public bool CanDelete(ulong accountId, string repo) => GetPermission(accountId, repo) is { CanDelete: true };

    private static string Lower(bool b) => b ? "true" : "false";
}
