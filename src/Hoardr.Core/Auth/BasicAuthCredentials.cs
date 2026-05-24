using System.Text;

namespace Hoardr.Core.Auth;

/// <summary>Username/password parsed from an HTTP <c>Authorization: Basic</c> header.</summary>
public readonly record struct BasicAuthCredentials(string Username, string Password)
{
    public static bool TryParse(string? header, out BasicAuthCredentials credentials)
    {
        credentials = default;
        if (string.IsNullOrWhiteSpace(header))
            return false;

        const string prefix = "Basic ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[prefix.Length..].Trim()));
        }
        catch
        {
            return false;
        }

        var colon = decoded.IndexOf(':');
        if (colon < 0)
            return false;

        // Password may itself contain ':' — split on the first one only.
        credentials = new BasicAuthCredentials(decoded[..colon], decoded[(colon + 1)..]);
        return true;
    }
}
