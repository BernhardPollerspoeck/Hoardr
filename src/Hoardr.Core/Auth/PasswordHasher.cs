using System.Security.Cryptography;

namespace Hoardr.Core.Auth;

/// <summary>
/// PBKDF2-HMAC-SHA256 password hashing. Hash format: <c>pbkdf2.&lt;iterations&gt;.&lt;salt-b64&gt;.&lt;key-b64&gt;</c>
/// ('.' is a safe delimiter — never appears in Base64).
/// </summary>
public static class PasswordHasher
{
    private const int SaltSize = 16;
    private const int KeySize = 32;
    private const int Iterations = 100_000;
    private static readonly HashAlgorithmName Algo = HashAlgorithmName.SHA256;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, Algo, KeySize);
        return $"pbkdf2.{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(key)}";
    }

    public static bool Verify(string password, string hash)
    {
        try
        {
            var parts = hash.Split('.');
            if (parts.Length != 4 || parts[0] != "pbkdf2")
                return false;

            var iterations = int.Parse(parts[1]);
            var salt = Convert.FromBase64String(parts[2]);
            var key = Convert.FromBase64String(parts[3]);

            var candidate = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, Algo, key.Length);
            return CryptographicOperations.FixedTimeEquals(candidate, key);
        }
        catch
        {
            return false;
        }
    }
}
