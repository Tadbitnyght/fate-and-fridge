using System.Diagnostics;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Tests.Support;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Unit tests for the M1-05 Argon2id password hasher (docs/SECURITY.md §2).
/// They exercise the real libsodium-backed implementation and need no
/// database or container, so they carry no Integration category trait.
/// </summary>
public sealed class PasswordHasherTests
{
    private const string Password = "correct horse battery staple";
    private const string ParamsPrefix = "$argon2id$v=19$m=65536,t=2,p=1$";

    private readonly Argon2idPasswordHasher _hasher = new();

    [Fact]
    public void Hash_and_verify_roundtrip()
    {
        var hash = _hasher.Hash(Password);

        _hasher.Verify(hash, Password).Should().BeTrue();
    }

    [Fact]
    public void Verify_rejects_wrong_password()
    {
        var hash = _hasher.Hash(Password);

        _hasher.Verify(hash, "correct horse battery stapl3").Should().BeFalse();
    }

    [Fact]
    public void Hash_is_phc_encoded_with_production_cost_parameters()
    {
        var hash = _hasher.Hash(Password);

        hash.Should().StartWith("$argon2id$");
        hash.Should().StartWith(ParamsPrefix);
    }

    [Fact]
    public void Two_hashes_of_the_same_password_use_unique_salts()
    {
        var first = _hasher.Hash(Password);
        var second = _hasher.Hash(Password);

        first.Should().NotBe(second);
        SaltSegment(first).Should().NotBe(SaltSegment(second));
        _hasher.Verify(first, Password).Should().BeTrue();
        _hasher.Verify(second, Password).Should().BeTrue();
    }

    [Fact]
    public void Fresh_hash_does_not_need_rehash()
    {
        var hash = _hasher.Hash(Password);

        _hasher.NeedsRehash(hash).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("not-a-hash")]
    [InlineData("$$$$$")]
    [InlineData("$argon2id$")]
    [InlineData("$argon2id$v=19$m=65536,t=2,p=1$")]
    [InlineData("$argon2id$v=19$m=65536,t=2,p=1$c29tZXNhbHQ$")]
    [InlineData("$argon2id$v=19$m=65536,t=2,p=1$c29tZXNhbHQ$c29tZWhhc2g=")]
    [InlineData("$argon2id$v=19$m=abc,t=2,p=1$c29tZXNhbHQ$c29tZWhhc2g")]
    [InlineData("$argon2id$v=19$m=65536,t=2$c29tZXNhbHQ$c29tZWhhc2g")]
    public void Malformed_hashes_fail_closed_without_throwing(string hash)
    {
        var verify = () => _hasher.Verify(hash, Password);
        var needsRehash = () => _hasher.NeedsRehash(hash);

        verify.Should().NotThrow().Which.Should().BeFalse();
        needsRehash.Should().NotThrow().Which.Should().BeTrue();
    }

    [Fact]
    public void Truncated_hash_fails_closed_without_throwing()
    {
        var truncated = _hasher.Hash(Password)[..^12];

        var verify = () => _hasher.Verify(truncated, Password);

        verify.Should().NotThrow().Which.Should().BeFalse();
        _hasher.NeedsRehash(truncated).Should().BeTrue();
    }

    [Theory]
    [InlineData("m=32768,t=2,p=1")]
    [InlineData("m=65536,t=3,p=1")]
    [InlineData("m=65536,t=2,p=2")]
    [InlineData("m=65536,t=2")]
    public void Hashes_with_different_cost_parameters_need_rehash(string parameters)
    {
        var hash = _hasher.Hash(Password);
        var altered = ReplaceParameters(hash, parameters);

        altered.Should().NotBe(hash);
        _hasher.NeedsRehash(altered).Should().BeTrue();
    }

    [Fact]
    public void Hashes_with_a_different_algorithm_or_version_need_rehash()
    {
        var hash = _hasher.Hash(Password);

        var differentAlgorithm = hash.Replace("$argon2id$", "$argon2i$", StringComparison.Ordinal);
        var differentVersion = hash.Replace("$v=19$", "$v=16$", StringComparison.Ordinal);

        _hasher.NeedsRehash(differentAlgorithm).Should().BeTrue();
        _hasher.NeedsRehash(differentVersion).Should().BeTrue();
    }

    [Fact]
    public void Non_canonical_uppercase_phc_string_fails_closed()
    {
        var hash = _hasher.Hash(Password);
        var uppercase = "$ARGON2ID$V=19$M=65536,T=2,P=1$" + hash[ParamsPrefix.Length..];

        uppercase.Should().NotBe(hash);
        _hasher.Verify(uppercase, Password).Should().BeFalse();
        _hasher.NeedsRehash(uppercase).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(NonCanonicalBase64Hashes))]
    public void Non_canonical_base64_fails_closed(string hash)
    {
        _hasher.Verify(hash, Password).Should().BeFalse();
        _hasher.NeedsRehash(hash).Should().BeTrue();
    }

    public static TheoryData<string> NonCanonicalBase64Hashes()
    {
        var salt = new string('A', 22);
        var derived = new string('A', 43);

        return new TheoryData<string>
        {
            // Padded base64 ('='), which libsodium never emits.
            $"$argon2id$v=19$m=65536,t=2,p=1${Convert.ToBase64String(new byte[16])}${derived}",
            $"$argon2id$v=19$m=65536,t=2,p=1${salt}${Convert.ToBase64String(new byte[32])}",
            // Whitespace and characters outside the libsodium base64 alphabet.
            $"$argon2id$v=19$m=65536,t=2,p=1${salt[..10]} {salt[11..]}${derived}",
            $"$argon2id$v=19$m=65536,t=2,p=1${salt[..10]}!{salt[11..]}${derived}",
        };
    }

    [Theory]
    [InlineData("m=2097152,t=2,p=1")]
    [InlineData("m=65536,t=1000,p=1")]
    [InlineData("m=65536,t=2,p=5")]
    public void Hostile_cost_hashes_fail_fast_without_invoking_libsodium(string parameters)
    {
        var hostile = $"$argon2id$v=19${parameters}${new string('A', 22)}${new string('A', 43)}";

        var stopwatch = Stopwatch.StartNew();
        var verified = _hasher.Verify(hostile, Password);
        stopwatch.Stop();

        verified.Should().BeFalse();
        _hasher.NeedsRehash(hostile).Should().BeTrue();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Null_and_empty_inputs_follow_the_documented_contract()
    {
        var hashNullPassword = () => _hasher.Hash(null!);
        hashNullPassword.Should().Throw<ArgumentNullException>();

        _hasher.Verify(null!, Password).Should().BeFalse();
        _hasher.Verify(Password, null!).Should().BeFalse();
        _hasher.NeedsRehash(null!).Should().BeTrue();

        var emptyPasswordHash = _hasher.Hash(string.Empty);
        _hasher.Verify(emptyPasswordHash, string.Empty).Should().BeTrue();
    }

    [Fact]
    public async Task Concurrent_hash_and_verify_calls_succeed()
    {
        var hash = _hasher.Hash(Password);

        var tasks = Enumerable.Range(0, 8).Select(index => Task.Run(() =>
        {
            if (index % 2 == 0)
            {
                _hasher.Verify(_hasher.Hash(Password), Password).Should().BeTrue();
            }
            else
            {
                _hasher.Verify(hash, Password).Should().BeTrue();
            }
        }));

        await Task.WhenAll(tasks);
    }

    [Fact]
    public void Unicode_password_roundtrips()
    {
        const string unicodePassword = "pässwörd-🍕-密码-🔐";

        var hash = _hasher.Hash(unicodePassword);

        _hasher.Verify(hash, unicodePassword).Should().BeTrue();
        _hasher.Verify(hash, "pässwörd-🍕-密码-🔓").Should().BeFalse();
    }

    [Fact]
    public void Maximum_length_password_roundtrips()
    {
        var password = new string('a', 128);

        var hash = _hasher.Hash(password);

        _hasher.Verify(hash, password).Should().BeTrue();
        _hasher.Verify(hash, new string('a', 127) + "b").Should().BeFalse();
    }

    [Fact]
    public void Password_hasher_is_registered_as_a_singleton_in_di()
    {
        using var factory = new ApiFactory();

        var first = factory.Services.GetRequiredService<IPasswordHasher>();
        var second = factory.Services.GetRequiredService<IPasswordHasher>();

        first.Should().BeOfType<Argon2idPasswordHasher>();
        second.Should().BeSameAs(first);
    }

    [Theory]
    [InlineData("$v=019$", "$v=19$")]
    [InlineData("m=000065536,t=2,p=1", "m=65536,t=2,p=1")]
    [InlineData("m=65536,t=02,p=1", "m=65536,t=2,p=1")]
    [InlineData("m=65536,t=2,p=01", "m=65536,t=2,p=1")]
    public void Non_minimal_decimal_encodings_are_rejected(string replacement, string canonical)
    {
        var hash = _hasher.Hash(Password).Replace(canonical, replacement, StringComparison.Ordinal);
        hash.Should().NotBe(_hasher.Hash(Password)); // sanity: the string was actually mutated

        _hasher.Verify(hash, Password).Should().BeFalse();
        _hasher.NeedsRehash(hash).Should().BeTrue();
    }

    [Theory]
    [InlineData("p=1,m=65536,t=2")]
    [InlineData("m=65536,p=1,t=2")]
    [InlineData("t=2,m=65536,p=1")]
    [InlineData("m=65536,t=2")]
    [InlineData("m=65536,t=2,p=1,p=1")]
    public void Cost_parameters_must_use_libsodiums_literal_order(string parameters)
    {
        var hash = ReplaceParameters(_hasher.Hash(Password), parameters);

        _hasher.Verify(hash, Password).Should().BeFalse();
        _hasher.NeedsRehash(hash).Should().BeTrue();
    }

    [Fact]
    public void Non_canonical_base64_final_quantum_is_rejected()
    {
        var hash = _hasher.Hash(Password);
        _hasher.Verify(hash, Password).Should().BeTrue();

        var segments = hash.Split('$');
        var mutatedHashSegment = string.Join(
            '$',
            segments[..5].Append(MutateFinalQuantum(segments[5])));
        var mutatedSaltSegment = string.Join(
            '$',
            segments[..4].Append(MutateFinalQuantum(segments[4])).Append(segments[5]));

        _hasher.Verify(mutatedHashSegment, Password).Should().BeFalse();
        _hasher.NeedsRehash(mutatedHashSegment).Should().BeTrue();
        _hasher.Verify(mutatedSaltSegment, Password).Should().BeFalse();
        _hasher.NeedsRehash(mutatedSaltSegment).Should().BeTrue();
    }

    [Fact]
    public void Accepted_cost_bounds_never_drop_below_production_parameters()
    {
        Argon2idPasswordHasher.MaxAcceptedIterations.Should()
            .BeGreaterThanOrEqualTo((ulong)Argon2idPasswordHasher.OpsLimit);
        Argon2idPasswordHasher.MaxAcceptedMemoryKiB.Should()
            .BeGreaterThanOrEqualTo((ulong)(Argon2idPasswordHasher.MemoryLimitBytes / 1024));
        Argon2idPasswordHasher.MaxAcceptedParallelism.Should()
            .BeGreaterThanOrEqualTo((ulong)Argon2idPasswordHasher.Parallelism);
    }

    /// <summary>
    /// Replaces the last base64 character with one that differs only in the
    /// unused low bits of the final quantum, so it decodes to the same bytes
    /// but is not what libsodium would emit.
    /// </summary>
    private static string MutateFinalQuantum(string segment)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        var lastIndex = alphabet.IndexOf(segment[^1], StringComparison.Ordinal);
        lastIndex.Should().BeGreaterThanOrEqualTo(0);
        return string.Concat(segment.AsSpan(0, segment.Length - 1), alphabet[lastIndex + 1].ToString());
    }

    private static string SaltSegment(string hash) => hash.Split('$')[4];

    private static string ReplaceParameters(string hash, string parameters) =>
        hash.Replace("m=65536,t=2,p=1", parameters, StringComparison.Ordinal);
}
