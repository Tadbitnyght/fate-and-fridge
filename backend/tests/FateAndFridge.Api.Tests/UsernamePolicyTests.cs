using FateAndFridge.Api.Services;
using FluentAssertions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Shared username rules (docs/API.md §6): 3–32 characters using ASCII
/// letters, digits, <c>.</c>, <c>_</c>, or <c>-</c>.
/// </summary>
public sealed class UsernamePolicyTests
{
    [Theory]
    [InlineData("smith", true)]
    [InlineData("smith.jones", true)]
    [InlineData("smith_jones-1", true)]
    [InlineData("abc", true)]
    [InlineData("ab", false)]
    [InlineData("bad name", false)]
    [InlineData("bad@name", false)]
    [InlineData("bad/name", false)]
    [InlineData("bad\u0001name", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValid_enforces_length_and_allowed_characters(string? username, bool expected) =>
        UsernamePolicy.IsValid(username).Should().Be(expected);

    [Fact]
    public void Length_boundaries_are_three_and_thirty_two()
    {
        UsernamePolicy.IsValid(new string('a', UsernamePolicy.MinLength - 1)).Should().BeFalse();
        UsernamePolicy.IsValid(new string('a', UsernamePolicy.MinLength)).Should().BeTrue();
        UsernamePolicy.IsValid(new string('a', UsernamePolicy.MaxLength)).Should().BeTrue();
        UsernamePolicy.IsValid(new string('a', UsernamePolicy.MaxLength + 1)).Should().BeFalse();
    }
}
