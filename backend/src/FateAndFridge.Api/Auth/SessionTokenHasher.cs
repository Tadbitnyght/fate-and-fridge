using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace FateAndFridge.Api.Auth;

/// <summary>
/// Opaque session token creation and hashing (docs/SECURITY.md §2). The raw
/// token exists only in transit and in the cookie; the database stores only
/// <c>SHA-256(rawToken)</c>. Exposed so callers that rotate a session can rebind
/// request state (antiforgery data) to the new token without duplicating the
/// hash rule.
/// </summary>
public static class SessionTokenHasher
{
    /// <summary>Creates a fresh 256-bit token, base64url encoded.</summary>
    public static string CreateRawToken() =>
        Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>SHA-256 of the raw token, as stored in <c>user_sessions.token_hash</c>.</summary>
    public static byte[] Hash(string rawToken) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
}
