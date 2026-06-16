using System.Text;

namespace BankAdmin.Data;

/// <summary>
/// Opaque per-session token that replaces the Laravel API's JWT. It carries just enough
/// context (acting user id, role, tenant) so the data layer can:
///   • stamp <c>performed_by_user_id</c> on audit logs, and
///   • scope account listings to a single client during the public self-service certificate flow
///     (where the holder authenticates as themselves rather than as an admin).
///
/// The token is stored server-side in the session (HTTP-only cookie is the real trust boundary),
/// so it only needs to be a compact, tamper-evident-enough envelope — not a signed credential.
/// </summary>
public sealed record SessionToken(string UserId, string Role, string TenantId)
{
    private const string Version = "v1";

    public bool IsAdmin => string.Equals(Role, "administrador", StringComparison.OrdinalIgnoreCase);

    public static string Create(string userId, string role, string tenantId)
    {
        var raw = string.Join('|',
            Version,
            userId ?? "",
            string.IsNullOrEmpty(role) ? "cliente" : role,
            tenantId ?? "",
            Guid.NewGuid().ToString("N")); // nonce so two sessions never share a token
        return Base64UrlEncode(Encoding.UTF8.GetBytes(raw));
    }

    public static SessionToken? TryParse(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        try
        {
            var raw = Encoding.UTF8.GetString(Base64UrlDecode(token));
            var parts = raw.Split('|');
            if (parts.Length < 4 || parts[0] != Version || string.IsNullOrEmpty(parts[1]))
                return null;
            var role = string.IsNullOrEmpty(parts[2]) ? "cliente" : parts[2];
            return new SessionToken(parts[1], role, parts[3]);
        }
        catch
        {
            return null;
        }
    }

    // ── Base64URL helpers ──────────────────────────────────────────────────────

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }
}
