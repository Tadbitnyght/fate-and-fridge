using System.Security.Cryptography;

namespace FateAndFridge.Api.Auth;

/// <summary>
/// Generates one-time admin-reset passwords (docs/SECURITY.md §2): CSPRNG
/// 20 characters, unambiguous alphabet (no <c>0/O/1/I/l</c>), comfortably above
/// the 12-character minimum. The value is returned to the admin exactly once
/// and is never persisted in plaintext.
/// </summary>
public static class TemporaryPasswordGenerator
{
    /// <summary>Alphabet without lookalike characters: A–Z minus I/O, a–z minus l, digits 2–9.</summary>
    public const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";

    public const int Length = 20;

    /// <summary>Returns a fresh cryptographically random temporary password.</summary>
    public static string Generate() => RandomNumberGenerator.GetString(Alphabet, Length);
}
