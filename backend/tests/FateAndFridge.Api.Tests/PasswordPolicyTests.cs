using FateAndFridge.Api.Auth;
using FluentAssertions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// V1 password policy (docs/SECURITY.md §2): 12–128 characters, no trimming,
/// no composition rules.
/// </summary>
public sealed class PasswordPolicyTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("short", false)]
    [InlineData("elevenchars", false)]
    [InlineData("twelvechars!", true)]
    [InlineData("  spaces are kept  ", true)]
    public void IsValid_enforces_the_length_policy(string? password, bool expected) =>
        PasswordPolicy.IsValid(password).Should().Be(expected);

    [Fact]
    public void Length_boundaries_are_twelve_and_one_hundred_twenty_eight()
    {
        PasswordPolicy.IsValid(new string('a', PasswordPolicy.MinLength - 1)).Should().BeFalse();
        PasswordPolicy.IsValid(new string('a', PasswordPolicy.MinLength)).Should().BeTrue();
        PasswordPolicy.IsValid(new string('a', PasswordPolicy.MaxLength)).Should().BeTrue();
        PasswordPolicy.IsValid(new string('a', PasswordPolicy.MaxLength + 1)).Should().BeFalse();
    }
}
