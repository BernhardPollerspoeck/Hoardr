using System.Security.Cryptography;
using System.Text;

namespace Hoardr.Core.Auth;

/// <summary>
/// Resolves an <see cref="Identity"/> from an Authorization header and answers
/// per-repo pull/push questions. Master token (from config/env) trumps everything;
/// otherwise credentials are checked against DB accounts.
/// </summary>
public sealed class Authenticator(AccountService accounts, string? masterToken)
{
    private readonly byte[]? _masterToken =
        string.IsNullOrEmpty(masterToken) ? null : Encoding.UTF8.GetBytes(masterToken);

    /// <summary>Returns the caller's identity from an Authorization header, or null if missing/invalid.</summary>
    public Identity? Authenticate(string? authorizationHeader)
        => BasicAuthCredentials.TryParse(authorizationHeader, out var creds)
            ? VerifyCredentials(creds.Username, creds.Password)
            : null;

    /// <summary>Returns the identity for a raw username/password (used by the form login), or null.</summary>
    public Identity? VerifyCredentials(string username, string password)
    {
        if (IsMasterToken(password))
            return Identity.Master;

        var account = accounts.VerifyCredentials(username, password);
        return account is null ? null : Identity.ForAccount(account.Id, account.Name);
    }

    public bool CanPull(Identity identity, string repo)
        => identity.IsMaster || accounts.CanPull(identity.AccountId, repo);

    public bool CanPush(Identity identity, string repo)
        => identity.IsMaster || accounts.CanPush(identity.AccountId, repo);

    public bool CanDelete(Identity identity, string repo)
        => identity.IsMaster || accounts.CanDelete(identity.AccountId, repo);

    private bool IsMasterToken(string presented)
    {
        if (_masterToken is null)
            return false;

        // Constant-time comparison; FixedTimeEquals also safely handles length mismatch.
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), _masterToken);
    }
}
