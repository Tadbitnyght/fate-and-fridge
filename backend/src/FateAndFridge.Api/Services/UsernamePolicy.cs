namespace FateAndFridge.Api.Services;

/// <summary>
/// Username rules shared by setup completion, user management, and the
/// auth-mode switch (docs/API.md §6, docs/DATABASE.md §2): 3–32 ASCII letters,
/// digits, or <c>.</c>, <c>_</c>, <c>-</c> after trimming. The value is stored
/// trimmed; <c>normalized_username</c> is the lowercased lookup form.
/// </summary>
public static class UsernamePolicy
{
    public const int MinLength = 3;

    public const int MaxLength = 32;

    /// <summary>True when <paramref name="username"/> is a valid trimmed username.</summary>
    public static bool IsValid(string? username) =>
        username is not null
        && username.Length is >= MinLength and <= MaxLength
        && username.All(IsAllowedCharacter);

    private static bool IsAllowedCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-';
}
