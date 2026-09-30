using FateAndFridge.Api.Auth;
using FluentAssertions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Admin-reset temporary passwords (docs/SECURITY.md §2): CSPRNG-generated,
/// policy-compliant, and free of lookalike characters.
/// </summary>
public sealed class TemporaryPasswordGeneratorTests
{
    [Fact]
    public void Generated_passwords_satisfy_the_policy_and_use_the_unambiguous_alphabet()
    {
        for (var i = 0; i < 100; i++)
        {
            var password = TemporaryPasswordGenerator.Generate();

            password.Length.Should().Be(TemporaryPasswordGenerator.Length);
            PasswordPolicy.IsValid(password).Should().BeTrue();
            password.Any(character =>
                    !TemporaryPasswordGenerator.Alphabet.Contains(character, StringComparison.Ordinal))
                .Should().BeFalse();
        }
    }

    [Fact]
    public void Generated_passwords_are_unique()
    {
        var passwords = Enumerable.Range(0, 200)
            .Select(_ => TemporaryPasswordGenerator.Generate())
            .ToHashSet(StringComparer.Ordinal);

        passwords.Should().HaveCount(200);
    }

    [Fact]
    public void Alphabet_omits_lookalike_characters() =>
        TemporaryPasswordGenerator.Alphabet.Should().NotContainAny("0", "O", "1", "I", "l");
}
