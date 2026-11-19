using System.Security.Cryptography;
using System.Text;
using PoeHelper.Core;

namespace PoeHelper.App;

/// <summary>Keeps the POESESSID encrypted with DPAPI (CurrentUser): only this Windows account can read it back.</summary>
public static class SessionStore
{
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PoeHelper.POESESSID.v1");

    public static string? Get(AppSettings s)
    {
        if (string.IsNullOrEmpty(s.PoeSessionIdProtected)) return null;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(s.PoeSessionIdProtected), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null; // copied from another PC/user: treat as not logged in
        }
    }

    public static void Set(AppSettings s, string? sessionId)
    {
        sessionId = sessionId?.Trim();
        s.PoeSessionIdProtected = string.IsNullOrEmpty(sessionId)
            ? ""
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(sessionId), Entropy, DataProtectionScope.CurrentUser));
    }

    /// <summary>POESESSID values are 32 hex characters.</summary>
    public static bool LooksValid(string? v) => v is { Length: 32 } && v.All(Uri.IsHexDigit);
}
