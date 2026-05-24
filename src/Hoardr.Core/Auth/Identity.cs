namespace Hoardr.Core.Auth;

/// <summary>An authenticated caller: either the all-powerful master, or a DB account.</summary>
public sealed record Identity(bool IsMaster, ulong AccountId, string Name)
{
    public static readonly Identity Master = new(IsMaster: true, AccountId: 0, Name: "master");

    public static Identity ForAccount(ulong id, string name) => new(IsMaster: false, id, name);
}

public sealed record AccountRecord(ulong Id, string Name, string PasswordHash, DateTime CreatedAt, bool CanCreate);

/// <summary>Per-repo permission flags for an account.</summary>
public readonly record struct RepoPermission(string Repo, bool CanPull, bool CanPush, bool CanDelete);
