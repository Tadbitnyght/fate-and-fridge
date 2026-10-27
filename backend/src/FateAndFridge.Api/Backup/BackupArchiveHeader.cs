using System.Buffers.Binary;
using System.Security.Cryptography;

namespace FateAndFridge.Api.Backup;

/// <summary>
/// The fixed 48-byte cleartext prefix of a <c>.ffbackup</c> archive. The exact
/// byte layout is documented on <see cref="BackupArchiveFormat"/>.
/// </summary>
public sealed class BackupArchiveHeader
{
    public const int Size = 48;

    public const int SaltSize = 16;

    public const int NoncePrefixSize = 4;

    private const int FormatVersionOffset = 4;
    private const int KdfIdOffset = 6;
    private const int ReservedOffset = 7;
    private const int SaltOffset = 8;
    private const int ChunkSizeOffset = 24;
    private const int NoncePrefixOffset = 28;
    private const int KeyCheckOffset = 32;

    private readonly byte[] _salt;
    private readonly byte[] _noncePrefix;
    private readonly byte[] _keyCheck;

    private BackupArchiveHeader(
        ushort formatVersion,
        byte kdfId,
        byte reserved,
        byte[] salt,
        int chunkSize,
        byte[] noncePrefix,
        byte[] keyCheck)
    {
        FormatVersion = formatVersion;
        KdfId = kdfId;
        Reserved = reserved;
        _salt = salt;
        ChunkSize = chunkSize;
        _noncePrefix = noncePrefix;
        _keyCheck = keyCheck;
    }

    public ushort FormatVersion { get; }

    public byte KdfId { get; }

    /// <summary>Reserved version guard; always <c>0</c> for format version 1.</summary>
    public byte Reserved { get; }

    public ReadOnlyMemory<byte> Salt => _salt;

    public int ChunkSize { get; }

    public ReadOnlyMemory<byte> NoncePrefix => _noncePrefix;

    /// <summary>16-byte HMAC key-check tag; proves the recovery key without storing it.</summary>
    public ReadOnlyMemory<byte> KeyCheck => _keyCheck;

    /// <summary>Creates a fresh header for a new archive.</summary>
    public static BackupArchiveHeader Create(ReadOnlyMemory<byte> recoveryKey, int chunkSize)
    {
        BackupArchiveFormat.ValidateRecoveryKey(recoveryKey);
        ValidateChunkSize(chunkSize);

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var noncePrefix = RandomNumberGenerator.GetBytes(NoncePrefixSize);
        var keyCheck = BackupArchiveFormat.ComputeKeyCheck(recoveryKey.Span, salt);

        return new BackupArchiveHeader(
            BackupArchiveFormat.CurrentFormatVersion,
            BackupArchiveFormat.HkdfSha256,
            reserved: 0,
            salt,
            chunkSize,
            noncePrefix,
            keyCheck);
    }

    /// <summary>Serializes the header to its fixed 48-byte wire form.</summary>
    public byte[] ToBytes()
    {
        var bytes = new byte[Size];
        BackupArchiveFormat.Magic.CopyTo(bytes);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(FormatVersionOffset), FormatVersion);
        bytes[KdfIdOffset] = KdfId;
        bytes[ReservedOffset] = Reserved;
        _salt.CopyTo(bytes, SaltOffset);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(ChunkSizeOffset), checked((uint)ChunkSize));
        _noncePrefix.CopyTo(bytes, NoncePrefixOffset);
        _keyCheck.CopyTo(bytes, KeyCheckOffset);
        return bytes;
    }

    /// <summary>Reads and structurally validates the fixed header from a stream.</summary>
    public static async Task<BackupArchiveHeader> ReadAsync(Stream input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var bytes = new byte[Size];
        try
        {
            await input.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            throw BackupArchiveException.FormatUnsupported(
                "The backup archive is shorter than its fixed header.");
        }

        return Parse(bytes);
    }

    /// <summary>
    /// Parses and structurally validates a header. Unknown magic, format
    /// version, KDF id, a non-zero reserved guard, or an out-of-range chunk size
    /// is <c>FORMAT_UNSUPPORTED</c>. The salt takes part in the key check, so an
    /// altered salt (or key-check byte) reports <c>KEY_MISMATCH</c>; any other
    /// altered header field fails later with <c>INTEGRITY_FAILED</c> because the
    /// record AAD authenticates the header.
    /// </summary>
    public static BackupArchiveHeader Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < Size)
        {
            throw BackupArchiveException.FormatUnsupported(
                "The backup archive is shorter than its fixed header.");
        }

        if (!bytes[..4].SequenceEqual(BackupArchiveFormat.Magic))
        {
            throw BackupArchiveException.FormatUnsupported(
                "The file is not a Fate & Fridge backup archive (bad magic).");
        }

        var formatVersion = BinaryPrimitives.ReadUInt16BigEndian(bytes[FormatVersionOffset..]);
        if (formatVersion != BackupArchiveFormat.CurrentFormatVersion)
        {
            throw BackupArchiveException.FormatUnsupported(
                $"Backup archive format version {formatVersion} is not supported (expected {BackupArchiveFormat.CurrentFormatVersion}).");
        }

        var kdfId = bytes[KdfIdOffset];
        if (kdfId != BackupArchiveFormat.HkdfSha256)
        {
            throw BackupArchiveException.FormatUnsupported(
                $"Backup archive key-derivation id {kdfId} is not supported.");
        }

        var reserved = bytes[ReservedOffset];
        if (reserved != 0)
        {
            throw BackupArchiveException.FormatUnsupported(
                "The backup archive uses reserved header flags this version does not understand.");
        }

        var chunkSize = BinaryPrimitives.ReadUInt32BigEndian(bytes[ChunkSizeOffset..]);
        if (chunkSize > int.MaxValue)
        {
            throw BackupArchiveException.FormatUnsupported(
                "The backup archive chunk size is not supported.");
        }

        ValidateChunkSize((int)chunkSize);

        return new BackupArchiveHeader(
            formatVersion,
            kdfId,
            reserved,
            bytes.Slice(SaltOffset, SaltSize).ToArray(),
            (int)chunkSize,
            bytes.Slice(NoncePrefixOffset, NoncePrefixSize).ToArray(),
            bytes.Slice(KeyCheckOffset, BackupArchiveFormat.TagSize).ToArray());
    }

    internal static void ValidateChunkSize(int chunkSize)
    {
        if (chunkSize is < BackupArchiveFormat.MinimumChunkSize or > BackupArchiveFormat.MaximumChunkSize)
        {
            throw BackupArchiveException.FormatUnsupported(
                $"Backup archive chunk size {chunkSize} is outside the supported range " +
                $"({BackupArchiveFormat.MinimumChunkSize}..{BackupArchiveFormat.MaximumChunkSize} bytes).");
        }
    }
}
