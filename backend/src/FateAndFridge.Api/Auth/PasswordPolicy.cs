namespace FateAndFridge.Api.Auth;

/// <summary>
/// V1 password policy (docs/SECURITY.md §2, docs/MILESTONES.md M1 decisions):
/// 12–128 characters, no composition rules, no trimming, pasting allowed.
/// </summary>
public static class PasswordPolicy
{
    public const int MinLength = 12;

    public const int MaxLength = 128;

    /// <summary>True when <paramref name="password"/> satisfies the length policy.</summary>
    public static bool IsValid(string? password) =>
        password is not null
        && password.Length >= MinLength
        && password.Length <= MaxLength;
}
