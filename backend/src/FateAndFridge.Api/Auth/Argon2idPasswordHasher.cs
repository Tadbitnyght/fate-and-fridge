using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Geralt;

namespace FateAndFridge.Api.Auth;

/// <summary>
/// Argon2id password hasher backed by libsodium's
/// <c>crypto_pwhash_str</c> (through the Geralt binding).
/// </summary>
/// <remarks>
/// <para>
/// Package decision: <b>Geralt 4.4.0</b>, chosen over Sodium.Core
/// and NSec.Cryptography. All three rely on the same native
/// <c>libsodium</c> 1.0.22 package (linux-x64, linux-arm64, win-x64, osx
/// binaries bundled, so no Dockerfile or CI changes are required), but only
/// Geralt exposes libsodium's PHC password-hash API
/// (<c>crypto_pwhash_argon2id_str</c>, <c>..._str_verify</c>) while accepting
/// the approved cost parameters: Sodium.Core hard-rejects
/// <c>opslimit &lt; 3</c> and its own presets do not match the libsodium
/// interactive preset, and NSec.Cryptography has no PHC-encoded password
/// hashing API at all.
/// </para>
/// <para>
/// Production baseline: libsodium interactive preset
/// <c>opslimit = 2</c> (passes) and <c>memlimit = 64 MiB</c>. Parameters are
/// embedded in every hash, so raising the baseline takes effect through
/// <see cref="NeedsRehash"/> plus rehash-on-login without invalidating
/// existing hashes.
/// </para>
/// <para>
/// Stored hashes are accepted only in libsodium's canonical PHC form
/// (case-sensitive algorithm and parameter keys, unpadded standard base64
/// salt/hash). <see cref="Verify"/> additionally refuses cost parameters
/// above the defensive bounds documented on the constants below. Anything
/// non-canonical or hostile fails closed, and <see cref="NeedsRehash"/>
/// reports it as needing a rehash.
/// </para>
/// <para>
/// This class never logs passwords or hashes and never stores plaintext.
/// </para>
/// </remarks>
public sealed class Argon2idPasswordHasher : IPasswordHasher
{
    /// <summary>PHC algorithm identifier for Argon2id.</summary>
    public const string Algorithm = "argon2id";

    /// <summary>Argon2 version 19, the version libsodium emits in PHC strings.</summary>
    public const int PhcVersion = 19;

    /// <summary>Production operations limit (passes): libsodium interactive preset.</summary>
    public const int OpsLimit = 2;

    /// <summary>Production memory limit in bytes (64 MiB): libsodium interactive preset.</summary>
    public const int MemoryLimitBytes = 64 * 1024 * 1024;

    /// <summary>Argon2 degree of parallelism, fixed at 1 by libsodium's PHC output.</summary>
    public const int Parallelism = 1;

    /// <summary>PHC memory parameter is expressed in KiB.</summary>
    private const int MemoryLimitKiB = MemoryLimitBytes / 1024;

    /// <summary>Salt length libsodium writes into a PHC string (bytes).</summary>
    private const int SaltBytes = 16;

    /// <summary>Derived hash length libsodium writes into a PHC string (bytes).</summary>
    private const int HashBytes = 32;

    /// <summary>
    /// Upper bound <see cref="Verify"/> accepts for the PHC memory parameter
    /// 1 GiB, expressed in the PHC string's KiB unit. Hashes above this are
    /// rejected before libsodium runs (untrusted stored values must not let a
    /// login attempt allocate unbounded memory).
    /// </summary>
    internal const ulong MaxAcceptedMemoryKiB = 1024UL * 1024UL;

    /// <summary>
    /// Upper bound <see cref="Verify"/> accepts for the PHC time cost
    /// (passes). Production uses <see cref="OpsLimit"/> (2).
    /// </summary>
    internal const ulong MaxAcceptedIterations = 10;

    /// <summary>
    /// Upper bound <see cref="Verify"/> accepts for the PHC parallelism.
    /// Production uses <see cref="Parallelism"/> (1).
    /// </summary>
    internal const ulong MaxAcceptedParallelism = 4;

    /// <summary>
    /// Guards against bound drift: the accepted maxima must never fall below
    /// the production parameters, or valid stored hashes would stop verifying.
    /// </summary>
    static Argon2idPasswordHasher()
    {
        Debug.Assert(
            MaxAcceptedIterations >= (ulong)OpsLimit,
            "MaxAcceptedIterations must not reject the production OpsLimit.");
        Debug.Assert(
            MaxAcceptedMemoryKiB >= (ulong)MemoryLimitKiB,
            "MaxAcceptedMemoryKiB must not reject the production MemoryLimitBytes.");
        Debug.Assert(
            MaxAcceptedParallelism >= (ulong)Parallelism,
            "MaxAcceptedParallelism must not reject the production Parallelism.");
    }

    /// <inheritdoc />
    public string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        var passwordBytes = Encoding.UTF8.GetBytes(password);
        Span<char> hashBuffer = stackalloc char[Argon2id.HashSize];
        try
        {
            Argon2id.ComputeHash(hashBuffer, passwordBytes, OpsLimit, MemoryLimitBytes);

            var terminator = hashBuffer.IndexOf('\0');
            return terminator < 0
                ? new string(hashBuffer)
                : new string(hashBuffer[..terminator]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
            hashBuffer.Clear();
        }
    }

    /// <inheritdoc />
    public bool Verify(string hash, string password)
    {
        if (string.IsNullOrEmpty(hash) || password is null)
        {
            return false;
        }

        // Untrusted stored values must not make libsodium allocate or run
        // unbounded work: reject hostile cost parameters before verification.
        if (!TryParsePhcParameters(hash, out var stored)
            || stored.MemoryKiB > MaxAcceptedMemoryKiB
            || stored.Iterations > MaxAcceptedIterations
            || stored.Parallelism > MaxAcceptedParallelism)
        {
            return false;
        }

        var passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            // libsodium's crypto_pwhash_str_verify compares in constant time.
            return Argon2id.VerifyHash(hash.AsSpan(), passwordBytes);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            // Malformed PHC strings fail closed; never log the hash or password.
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    /// <inheritdoc />
    public bool NeedsRehash(string hash)
    {
        if (!TryParsePhcParameters(hash, out var stored))
        {
            return true;
        }

        return !string.Equals(stored.Algorithm, Algorithm, StringComparison.Ordinal)
            || stored.Version != (ulong)PhcVersion
            || stored.MemoryKiB != (ulong)MemoryLimitKiB
            || stored.Iterations != (ulong)OpsLimit
            || stored.Parallelism != (ulong)Parallelism;
    }

    /// <summary>
    /// Parses the canonical libsodium PHC string
    /// <c>$argon2id$v=19$m=65536,t=2,p=1$&lt;salt-b64&gt;$&lt;hash-b64&gt;</c>.
    /// The format is validated exactly as libsodium emits it: algorithm and
    /// parameter keys are case-sensitive, and salt/hash use libsodium's
    /// unpadded base64 alphabet. Anything else is treated as malformed so
    /// that "libsodium would not verify this" implies "needs rehash".
    /// </summary>
    private static bool TryParsePhcParameters(string? hash, out PhcParameters parameters)
    {
        parameters = default;

        if (string.IsNullOrEmpty(hash))
        {
            return false;
        }

        var segments = hash.Split('$', StringSplitOptions.None);
        if (segments.Length != 6 || segments[0].Length != 0 || segments[1].Length == 0)
        {
            return false;
        }

        if (!TryReadValue(segments[2], "v", out var version)
            || !TryReadCostParameters(segments[3], out var memoryKiB, out var iterations, out var parallelism)
            || !IsLibsodiumBase64WithByteLength(segments[4], SaltBytes)
            || !IsLibsodiumBase64WithByteLength(segments[5], HashBytes))
        {
            return false;
        }

        parameters = new PhcParameters(segments[1], version, memoryKiB, iterations, parallelism);
        return true;
    }

    /// <summary>
    /// Reads a single <c>key=value</c> segment whose value is a canonical
    /// non-negative decimal (no leading zeros).
    /// </summary>
    private static bool TryReadValue(string segment, string expectedKey, out ulong value)
    {
        value = 0;

        var parts = segment.Split('=', StringSplitOptions.None);
        return parts.Length == 2
            && string.Equals(parts[0], expectedKey, StringComparison.Ordinal)
            && TryParseCanonicalDecimal(parts[1], out value);
    }

    /// <summary>
    /// Reads the PHC cost-parameter segment, requiring libsodium's literal
    /// <c>m=...,t=...,p=...</c> order with exactly those three canonical
    /// non-negative decimals.
    /// </summary>
    private static bool TryReadCostParameters(
        string segment,
        out ulong memoryKiB,
        out ulong iterations,
        out ulong parallelism)
    {
        memoryKiB = 0;
        iterations = 0;
        parallelism = 0;

        string[] expectedKeys = ["m", "t", "p"];
        var values = new ulong[expectedKeys.Length];
        var pairs = segment.Split(',', StringSplitOptions.None);
        if (pairs.Length != expectedKeys.Length)
        {
            return false;
        }

        for (var index = 0; index < pairs.Length; index++)
        {
            var parts = pairs[index].Split('=', StringSplitOptions.None);
            if (parts.Length != 2
                || !string.Equals(parts[0], expectedKeys[index], StringComparison.Ordinal)
                || !TryParseCanonicalDecimal(parts[1], out values[index]))
            {
                return false;
            }
        }

        memoryKiB = values[0];
        iterations = values[1];
        parallelism = values[2];
        return true;
    }

    /// <summary>
    /// Parses a canonical non-negative decimal: digits only, and no leading
    /// zeros (the single digit <c>0</c> is allowed). This rejects
    /// non-minimal encodings such as <c>019</c>, <c>000065536</c>, or
    /// <c>02</c> that libsodium would never emit.
    /// </summary>
    private static bool TryParseCanonicalDecimal(string text, out ulong value)
    {
        value = 0;

        if (text.Length == 0 || (text.Length > 1 && text[0] == '0'))
        {
            return false;
        }

        foreach (var character in text)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// Validates a PHC salt/hash segment as libsodium emits it: standard
    /// base64 <b>without</b> padding, restricted to the alphabet
    /// <c>A-Z a-z 0-9 + /</c>, with the character count implied by
    /// <paramref name="expectedBytes"/>. The unused bits of the final quantum
    /// must be zero — checked directly on the last character's index, since
    /// NET's base64 decoder rejects unpadded input — which rejects
    /// non-canonical encodings that would decode to the same bytes. Padding
    /// (<c>=</c>), whitespace, base64url characters, and anything else are
    /// rejected.
    /// </summary>
    private static bool IsLibsodiumBase64WithByteLength(string segment, int expectedBytes)
    {
        if (segment.Length != UnpaddedBase64Length(expectedBytes))
        {
            return false;
        }

        foreach (var character in segment)
        {
            if (!IsBase64AlphabetCharacter(character))
            {
                return false;
            }
        }

        // Canonical final-quantum bits: libsodium's unpadded encoder leaves the
        // unused bits of the last character at zero. Decoding with Convert is
        // not an option because it rejects unpadded input, so the bit check is
        // done directly on the last character's index.
        var lastIndex = Base64Index(segment[^1]);
        return (expectedBytes % 3) switch
        {
            // One byte in the final quantum: two characters, the last carrying
            // two data bits, so its low four bits must be zero (index % 16 == 0).
            1 => lastIndex % 16 == 0,
            // Two bytes: three characters, the last carrying four data bits, so
            // its low two bits must be zero (index % 4 == 0).
            2 => lastIndex % 4 == 0,
            _ => true,
        };
    }

    /// <summary>Base64 alphabet index of a validated alphabet character.</summary>
    private static int Base64Index(char character) =>
        character switch
        {
            >= 'A' and <= 'Z' => character - 'A',
            >= 'a' and <= 'z' => character - 'a' + 26,
            >= '0' and <= '9' => character - '0' + 52,
            '+' => 62,
            _ => 63,
        };

    /// <summary>
    /// Character count of unpadded standard base64 for
    /// <paramref name="byteLength"/> bytes: <c>4k</c>, <c>4k+2</c>, or
    /// <c>4k+3</c>, depending on the remainder.
    /// </summary>
    private static int UnpaddedBase64Length(int byteLength)
    {
        var fullGroups = byteLength / 3;

        return (byteLength % 3) switch
        {
            0 => fullGroups * 4,
            1 => (fullGroups * 4) + 2,
            _ => (fullGroups * 4) + 3,
        };
    }

    private static bool IsBase64AlphabetCharacter(char character) =>
        character is (>= 'A' and <= 'Z')
            or (>= 'a' and <= 'z')
            or (>= '0' and <= '9')
            or '+'
            or '/';

    /// <summary>Parameters extracted from a stored PHC string, in PHC units.</summary>
    private readonly record struct PhcParameters(
        string Algorithm,
        ulong Version,
        ulong MemoryKiB,
        ulong Iterations,
        ulong Parallelism);
}
