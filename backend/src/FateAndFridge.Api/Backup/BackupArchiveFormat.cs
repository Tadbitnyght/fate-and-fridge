using System.Buffers.Binary;
using System.Security.Cryptography;

namespace FateAndFridge.Api.Backup;

/// <summary>
/// Constants and key-derivation rules for the <c>.ffbackup</c> archive format
/// </summary>
/// <remarks>
/// <para>Wire layout (all integers big-endian):</para>
/// <list type="bullet">
/// <item>48-byte cleartext header:</item>
/// </list>
/// <code>
/// offset size field
/// 0 4 magic "FFBK"
/// 4 2 format_version (1)
/// 6 1 kdf_id (1 = HKDF-SHA256)
/// 7 1 reserved (0; a non-zero value is a future-format guard)
/// 8 16 salt (CSPRNG)
/// 24 4 chunk_size in bytes (64 KiB..8 MiB)
/// 28 4 nonce_prefix (CSPRNG; first 4 bytes of every record nonce)
/// 32 16 key_check (HMAC-SHA256 tag, see <see cref="ComputeKeyCheck"/>)
/// </code>
/// <para>
/// The key-check tag is derived with the header salt and a distinct HKDF info
/// label: it verifies the recovery key before decryption, differs between two
/// archives made with the same key, and a flipped key-check byte reports
/// <c>KEY_MISMATCH</c> (it is a key verifier, not an integrity check). Every
/// other header field is authenticated by the record AAD, so tampering with it
/// reports <c>INTEGRITY_FAILED</c>.
/// </para>
/// <para>
/// After the header come the encrypted records. Each record is
/// <c>[ciphertext_length:4][ciphertext][tag:16]</c> where
/// <c>ciphertext_length</c> is a big-endian <c>uint32</c> of at most
/// <c>chunk_size</c> bytes and the tag is a 16-byte AES-GCM tag. A zero-length
/// record (a 16-byte tag over an empty plaintext) terminates the archive. The
/// nonce is <c>nonce_prefix (4) || record_index (8)</c>; the associated data is
/// the 48-byte header, the record index, and a one-byte final-record flag. Data
/// records use flag <c>0</c>, the terminator uses flag <c>1</c>.
/// </para>
/// <para>The plaintext is a PAX tar stream containing regular files and
/// directories; <c>manifest.json</c> is written last.</para>
/// </remarks>
public static class BackupArchiveFormat
{
    public const ushort CurrentFormatVersion = 1;

    public const byte HkdfSha256 = 1;

    public const int DefaultChunkSize = 1024 * 1024;

    public const int MinimumChunkSize = 64 * 1024;

    public const int MaximumChunkSize = 8 * 1024 * 1024;

    public const int RecoveryKeySize = 32;

    public const int TagSize = 16;

    public const int NonceSize = 12;

    public const string ManifestEntryName = "manifest.json";

    /// <summary>Upper bound on entries in one archive (writer and reader).</summary>
    public const int MaximumEntryCount = 100_000;

    /// <summary>Upper bound on the serialized manifest; the manifest is small by construction.</summary>
    public const int MaximumManifestBytes = 32 * 1024 * 1024;

    internal static ReadOnlySpan<byte> Magic => "FFBK"u8;

    internal static ReadOnlySpan<byte> InfoPrefix => "fate-and-fridge/backup/v1"u8;

    internal static ReadOnlySpan<byte> KeyCheckInfo => "fate-and-fridge/backup/v1/key-check"u8;

    /// <summary>
    /// Derives the 32-byte AES-256 key with HKDF-SHA256. The recovery key is the
    /// input keying material, the header salt is the HKDF salt, and the info is
    /// <c>"fate-and-fridge/backup/v1"</c> followed by the big-endian format
    /// version. No key material is written to the archive.
    /// </summary>
    public static byte[] DeriveEncryptionKey(
        ReadOnlySpan<byte> recoveryKey,
        ReadOnlySpan<byte> salt,
        ushort formatVersion)
    {
        Span<byte> info = stackalloc byte[InfoPrefix.Length + sizeof(ushort)];
        InfoPrefix.CopyTo(info);
        BinaryPrimitives.WriteUInt16BigEndian(info[InfoPrefix.Length..], formatVersion);

        var key = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, recoveryKey, key, salt, info);
        return key;
    }

    /// <summary>
    /// Computes the 16-byte header key-check tag. It is an HMAC-SHA256 over the
    /// magic under a key derived from the recovery key with the **archive's
    /// header salt** and a distinct HKDF info label, so it proves that the
    /// supplied recovery key is the archive key. Because the salt is random per
    /// archive, two archives encrypted with the same recovery key carry
    /// different check values and the tag is no cross-archive fingerprint.
    /// </summary>
    /// <remarks>
    /// The tag is a key verifier, not an integrity check: a flipped key-check
    /// byte fails as <c>KEY_MISMATCH</c> before any decryption. Integrity of the
    /// rest of the header is covered by the record AAD, so tampering there still
    /// surfaces as <c>INTEGRITY_FAILED</c>.
    /// </remarks>
    internal static byte[] ComputeKeyCheck(
        ReadOnlySpan<byte> recoveryKey,
        ReadOnlySpan<byte> salt)
    {
        var checkKey = new byte[32];
        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            recoveryKey,
            checkKey,
            salt,
            KeyCheckInfo);

        try
        {
            return HMACSHA256.HashData(checkKey, Magic)[..TagSize];
        }
        finally
        {
            CryptographicOperations.ZeroMemory(checkKey);
        }
    }

    internal static void ValidateRecoveryKey(ReadOnlyMemory<byte> recoveryKey)
    {
        if (recoveryKey.Length != RecoveryKeySize)
        {
            throw new ArgumentException(
                $"The recovery key must be {RecoveryKeySize} bytes.",
                nameof(recoveryKey));
        }
    }

    /// <summary>Lowercase SHA-256 hex digest, the form stored in <c>backup_metadata.checksum</c>.</summary>
    internal static string ToHex(ReadOnlySpan<byte> digest) => Convert.ToHexStringLower(digest);

    internal static bool IsHexDigest(string? value) =>
        value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
}
