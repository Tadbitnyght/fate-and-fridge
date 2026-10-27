using FateAndFridge.Api.Auth;
using FluentAssertions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// The single username normalization rule shared by login and user creation
/// (docs/DATABASE.md §2: <c>normalized_username</c> is lowercase and trimmed).
/// </summary>
public sealed class UsernameNormalizerTests
{
    [Theory]
    [InlineData("smith", "smith")]
    [InlineData("SMITH", "smith")]
    [InlineData("  Smith  ", "smith")]
    [InlineData("\tSmith\n", "smith")]
    [InlineData("Ärger", "ärger")]
    [InlineData("Straße", "straße")]
    public void Normalize_trims_surrounding_whitespace_and_lowercases(string input, string expected) =>
        UsernameNormalizer.Normalize(input).Should().Be(expected);

    [Fact]
    public void Normalize_rejects_null() =>
        FluentActions.Invoking(() => UsernameNormalizer.Normalize(null!))
            .Should().Throw<ArgumentNullException>();
}
