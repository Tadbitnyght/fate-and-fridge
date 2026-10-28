namespace FateAndFridge.Api.Auth;

/// <summary>
/// Canonical username lookup form (docs/DATABASE.md §2): the value stored in
/// <c>users.normalized_username</c> is the username trimmed and lowercased.
/// Login and every user-creating path (setup, user
/// management) share this single rule.
/// </summary>
public static class UsernameNormalizer
{
    /// <summary>Trims surrounding whitespace and lowercases with the invariant culture.</summary>
    public static string Normalize(string username)
    {
        ArgumentNullException.ThrowIfNull(username);

        return username.Trim().ToLowerInvariant();
    }
}
