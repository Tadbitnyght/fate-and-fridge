namespace FateAndFridge.Api.Auth;

/// <summary>
/// Hashes and verifies local user passwords (docs/SECURITY.md section 2,
/// docs/ARCHITECTURE.md section 5).
/// </summary>
/// <remarks>
/// Implementations return PHC-encoded hashes that embed the algorithm, cost
/// parameters, and a unique random salt; verify through the underlying
/// library's constant-time comparison; and never log passwords or hashes.
/// </remarks>
public interface IPasswordHasher
{
    /// <summary>
    /// Hashes <paramref name="password"/> and returns a self-describing PHC
    /// string (<c>$argon2id$...</c>) that includes the salt and parameters.
    /// </summary>
    string Hash(string password);

    /// <summary>
    /// Verifies <paramref name="password"/> against a stored hash.
    /// </summary>
    /// <returns>
    /// <c>true</c> when the password matches. Wrong passwords and malformed
    /// or unreadable hash strings return <c>false</c> without throwing.
    /// </returns>
    bool Verify(string hash, string password);

    /// <summary>
    /// Reports whether a stored hash was created with different parameters
    /// than the current production baseline, so callers can rehash after a
    /// successful login (M1-07). Malformed or non-canonical hash strings
    /// (anything libsodium itself would not verify) return <c>true</c>
    /// without throwing.
    /// </summary>
    bool NeedsRehash(string hash);
}
