using System.Buffers;
using System.Buffers.Binary;
using System.Formats.Tar;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace FateAndFridge.Api.Backup;

/// <summary>
/// One entry exposed by <see cref="BackupArchiveReader"/>. <see cref="Content"/>
/// is valid only until the enumerator advances; the reader drains any unread
/// bytes and verifies the entry checksum before moving to the next entry.
/// </summary>
public sealed class BackupArchiveEntry
{
    internal BackupArchiveEntry(string name, long length, bool isDirectory, bool isManifest, Stream content)
    {
        Name = name;
        Length = length;
        IsDirectory = isDirectory;
        IsManifest = isManifest;
        Content = content;
    }

    /// <summary>Validated relative tar path (no leading slash, no trailing slash).</summary>
    public string Name { get; }

    public long Length { get; }

    public bool IsDirectory { get; }

    /// <summary>True for the generated <c>manifest.json</c> entry (always last).</summary>
    public bool IsManifest { get; }

    /// <summary>Readable plaintext content; <see cref="Stream.Null"/> for directories.</summary>
    public Stream Content { get; }
}

/// <summary>
/// Streaming reader for a <c>.ffbackup</c> archive (docs/M13_PLAN.md §1.4,
/// docs/SECURITY.md §12). The reader never writes to disk: it exposes entry
/// streams and verifies the checksums in <c>manifest.json</c> against the
/// decrypted entries. The optional expected ciphertext SHA-256 is checked
/// before the header is parsed or anything is decrypted.
/// </summary>
public sealed class BackupArchiveReader : IAsyncDisposable
{
    private readonly Stream _input;
    private readonly ChunkedAeadDecryptStream _decrypted;
    private readonly TarReader _tarReader;
    private readonly List<(string Name, long SizeBytes, string Sha256)> _observed = [];
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);
    private BackupManifest? _manifest;
    private bool _manifestSeen;
    private bool _enumerated;
    private bool _completed;
    private int _entryCount;

    private BackupArchiveReader(
        Stream input,
        BackupArchiveHeader header,
        ChunkedAeadDecryptStream decrypted,
        TarReader tarReader)
    {
        _input = input;
        Header = header;
        _decrypted = decrypted;
        _tarReader = tarReader;
    }

    public BackupArchiveHeader Header { get; }

    /// <summary>
    /// The parsed manifest, populated when the manifest entry is reached. It is
    /// only trustworthy once the entry enumeration completes successfully.
    /// </summary>
    public BackupManifest? Manifest => _manifest;

    /// <summary>True after a complete enumeration and manifest verification.</summary>
    public bool IsVerified => _completed;

    /// <summary>
    /// Parses the header, verifies the optional whole-file checksum and the
    /// recovery key, and returns a streaming reader.
    /// </summary>
    /// <param name="input">Encrypted archive stream. The reader does not dispose it.</param>
    /// <param name="recoveryKey">The 32-byte recovery key.</param>
    /// <param name="expectedSha256">
    /// Optional lowercase SHA-256 hex digest of the encrypted file. When set, the
    /// stream must be seekable and the digest is checked before decryption.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<BackupArchiveReader> OpenAsync(
        Stream input,
        ReadOnlyMemory<byte> recoveryKey,
        string? expectedSha256 = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead)
        {
            throw new ArgumentException("The archive stream must be readable.", nameof(input));
        }

        BackupArchiveFormat.ValidateRecoveryKey(recoveryKey);

        if (expectedSha256 is not null)
        {
            var expected = NormalizeExpectedChecksum(expectedSha256);
            if (!input.CanSeek)
            {
                throw new ArgumentException(
                    "Checksum verification requires a seekable archive stream.",
                    nameof(input));
            }

            var start = input.Position;
            var actual = await ComputeStreamSha256Async(input, cancellationToken).ConfigureAwait(false);
            input.Position = start;

            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw BackupArchiveException.IntegrityFailed(
                    "The archive checksum does not match the expected SHA-256.");
            }
        }

        var header = await BackupArchiveHeader.ReadAsync(input, cancellationToken).ConfigureAwait(false);

        var expectedKeyCheck = BackupArchiveFormat.ComputeKeyCheck(recoveryKey.Span);
        if (!CryptographicOperations.FixedTimeEquals(header.KeyCheck.Span, expectedKeyCheck))
        {
            throw BackupArchiveException.KeyMismatch();
        }

        var key = BackupArchiveFormat.DeriveEncryptionKey(
            recoveryKey.Span,
            header.Salt.Span,
            header.FormatVersion);
        var headerBytes = header.ToBytes();
        var decrypted = new ChunkedAeadDecryptStream(
            input,
            key,
            headerBytes,
            header.ChunkSize,
            header.NoncePrefix.Span);
        var tarReader = new TarReader(decrypted, leaveOpen: true);

        return new BackupArchiveReader(input, header, decrypted, tarReader);
    }

    /// <summary>
    /// Streams plaintext entries in archive order. The generated
    /// <c>manifest.json</c> entry is yielded last. When the enumeration reaches
    /// the end of the tar stream, the terminator record is authenticated and the
    /// manifest is compared with the entries that were observed.
    /// </summary>
    public async IAsyncEnumerable<BackupArchiveEntry> ReadEntriesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_enumerated)
        {
            throw new InvalidOperationException(
                "The backup archive reader supports a single entry enumeration.");
        }

        _enumerated = true;

        while (true)
        {
            TarEntry? tarEntry;
            try
            {
                tarEntry = await _tarReader
                    .GetNextEntryAsync(copyData: false, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (IsArchiveFailure(exception))
            {
                throw IntegrityFailure(
                    "The backup archive failed authentication or is truncated.",
                    exception);
            }

            if (tarEntry is null)
            {
                break;
            }

            if (++_entryCount > BackupArchiveFormat.MaximumEntryCount)
            {
                throw BackupArchiveException.PathUnsafe(
                    $"A backup archive cannot contain more than {BackupArchiveFormat.MaximumEntryCount} entries.");
            }

            if (_manifestSeen)
            {
                throw BackupArchiveException.IntegrityFailed(
                    "manifest.json must be the last entry in the backup archive.");
            }

            var entryType = tarEntry.EntryType;
            if (entryType is TarEntryType.SymbolicLink or TarEntryType.HardLink)
            {
                throw BackupArchiveException.FormatUnsupported(
                    "The backup archive contains a symbolic or hard link, which is not supported.");
            }

            if (entryType is TarEntryType.Directory)
            {
                var directoryName = BackupArchivePath.ValidateEntryName(
                    tarEntry.Name,
                    allowTrailingSlash: true);
                if (!_names.Add(directoryName))
                {
                    throw BackupArchiveException.PathUnsafe(
                        $"The backup archive contains the entry '{directoryName}' more than once.");
                }

                yield return new BackupArchiveEntry(
                    directoryName,
                    length: 0,
                    isDirectory: true,
                    isManifest: false,
                    Stream.Null);
                continue;
            }

            if (!IsRegularFile(entryType))
            {
                throw BackupArchiveException.FormatUnsupported(
                    $"The backup archive contains the unsupported tar entry type '{entryType}'.");
            }

            var name = BackupArchivePath.ValidateEntryName(tarEntry.Name);
            if (!_names.Add(name))
            {
                throw BackupArchiveException.PathUnsafe(
                    $"The backup archive contains the entry '{name}' more than once.");
            }

            if (string.Equals(name, BackupArchiveFormat.ManifestEntryName, StringComparison.Ordinal))
            {
                var manifestBytes = await ReadManifestAsync(tarEntry, cancellationToken)
                    .ConfigureAwait(false);
                _manifest = BackupManifestSerializer.Deserialize(manifestBytes);
                if (_manifest.FormatVersion != Header.FormatVersion)
                {
                    throw BackupArchiveException.IntegrityFailed(
                        "The backup manifest format version does not match the archive header.");
                }

                _manifestSeen = true;
                yield return new BackupArchiveEntry(
                    name,
                    manifestBytes.Length,
                    isDirectory: false,
                    isManifest: true,
                    new MemoryStream(manifestBytes, writable: false));
                continue;
            }

            var length = tarEntry.Length;
            var content = new HashingReadStream(tarEntry.DataStream ?? Stream.Null, length);
            yield return new BackupArchiveEntry(
                name,
                length,
                isDirectory: false,
                isManifest: false,
                content);

            // Resumed: consume whatever the caller did not read and record the
            // plaintext digest for the manifest comparison.
            try
            {
                await content.DrainAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsArchiveFailure(exception))
            {
                throw IntegrityFailure(
                    $"The backup entry '{name}' is truncated.",
                    exception);
            }

            _observed.Add((name, length, content.GetHashHex()));
        }

        // The tar end-of-archive marker is inside the plaintext, but the record
        // terminator is after it. Drain the decrypted stream so the terminator
        // is authenticated and trailing ciphertext is rejected.
        try
        {
            await _decrypted.DrainAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsArchiveFailure(exception))
        {
            throw IntegrityFailure(
                "The backup archive failed authentication or is truncated.",
                exception);
        }

        VerifyManifest();
        _completed = true;
    }

    public async ValueTask DisposeAsync()
    {
        await _tarReader.DisposeAsync().ConfigureAwait(false);
        _decrypted.Dispose();
    }

    private async Task<byte[]> ReadManifestAsync(TarEntry tarEntry, CancellationToken cancellationToken)
    {
        if (tarEntry.Length < 0 || tarEntry.Length > BackupArchiveFormat.MaximumManifestBytes)
        {
            throw BackupArchiveException.IntegrityFailed(
                "The backup archive manifest is larger than the supported limit.");
        }

        var bytes = new byte[tarEntry.Length];
        try
        {
            if (tarEntry.DataStream is null)
            {
                throw BackupArchiveException.IntegrityFailed(
                    "The backup archive manifest has no content.");
            }

            await tarEntry.DataStream
                .ReadExactlyAsync(bytes, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (EndOfStreamException exception)
        {
            throw IntegrityFailure("The backup archive manifest is truncated.", exception);
        }

        return bytes;
    }

    private void VerifyManifest()
    {
        var manifest = _manifest
            ?? throw BackupArchiveException.IntegrityFailed(
                "The backup archive is missing manifest.json.");

        if (_observed.Count != manifest.EntryCount
            || _observed.Count != manifest.Entries.Count)
        {
            throw BackupArchiveException.IntegrityFailed(
                "The backup archive entries do not match the manifest count.");
        }

        long observedTotal = 0;
        for (var index = 0; index < _observed.Count; index++)
        {
            var observed = _observed[index];
            var expected = manifest.Entries[index];

            if (!string.Equals(observed.Name, expected.Path, StringComparison.Ordinal)
                || observed.SizeBytes != expected.SizeBytes
                || !string.Equals(observed.Sha256, expected.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw BackupArchiveException.IntegrityFailed(
                    $"The backup entry '{observed.Name}' does not match its manifest record.");
            }

            if (observed.SizeBytes < 0 || observed.SizeBytes > long.MaxValue - observedTotal)
            {
                throw BackupArchiveException.IntegrityFailed(
                    "The backup archive total size is invalid.");
            }

            observedTotal += observed.SizeBytes;
        }

        if (observedTotal != manifest.TotalBytes)
        {
            throw BackupArchiveException.IntegrityFailed(
                "The backup archive total size does not match the manifest.");
        }
    }

    private static bool IsRegularFile(TarEntryType entryType) =>
        entryType is TarEntryType.RegularFile
            or TarEntryType.V7RegularFile
            or TarEntryType.ContiguousFile;

    private static bool IsArchiveFailure(Exception exception) =>
        exception is InvalidDataException
            or EndOfStreamException
            or IOException
            or CryptographicException;

    private static BackupArchiveException IntegrityFailure(string message, Exception innerException) =>
        innerException is BackupArchiveException archiveException
            ? archiveException
            : BackupArchiveException.IntegrityFailed(message, innerException);

    private static string NormalizeExpectedChecksum(string expectedSha256)
    {
        var value = expectedSha256.Trim();
        if (value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            value = value["sha256:".Length..];
        }

        if (!BackupArchiveFormat.IsHexDigest(value))
        {
            throw new ArgumentException(
                "The expected archive checksum must be a 64-character SHA-256 hex digest.",
                nameof(expectedSha256));
        }

        return value;
    }

    private static async Task<string> ComputeStreamSha256Async(
        Stream input,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            int read;
            while ((read = await input
                       .ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                       .ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
            }

            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

/// <summary>
/// Decrypts <c>[ciphertext][tag]</c> records on demand, authenticating the
/// header, record index, and final-record flag as associated data.
/// </summary>
internal sealed class ChunkedAeadDecryptStream : Stream
{
    private readonly Stream _input;
    private readonly AesGcm _aes;
    private readonly byte[] _header;
    private readonly byte[] _noncePrefix;
    private readonly byte[] _ciphertext;
    private readonly byte[] _plaintext;
    private readonly byte[] _tag = new byte[BackupArchiveFormat.TagSize];
    private readonly byte[] _nonce = new byte[BackupArchiveFormat.NonceSize];
    private readonly byte[] _lengthBytes = new byte[sizeof(uint)];
    private int _available;
    private int _offset;
    private long _index;
    private bool _finished;

    public ChunkedAeadDecryptStream(
        Stream input,
        byte[] key,
        ReadOnlySpan<byte> header,
        int chunkSize,
        ReadOnlySpan<byte> noncePrefix)
    {
        _input = input;
        _header = header.ToArray();
        _noncePrefix = noncePrefix.ToArray();
        _ciphertext = new byte[chunkSize];
        _plaintext = new byte[chunkSize];
        _aes = new AesGcm(key, BackupArchiveFormat.TagSize);
        CryptographicOperations.ZeroMemory(key);
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_offset >= _available && !LoadRecord())
        {
            return 0;
        }

        var copied = Math.Min(count, _available - _offset);
        _plaintext.AsSpan(_offset, copied).CopyTo(buffer.AsSpan(offset));
        _offset += copied;
        return copied;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (_offset >= _available && !await LoadRecordAsync(cancellationToken).ConfigureAwait(false))
        {
            return 0;
        }

        var copied = Math.Min(buffer.Length, _available - _offset);
        _plaintext.AsSpan(_offset, copied).CopyTo(buffer.Span);
        _offset += copied;
        return copied;
    }

    public async Task DrainAsync(CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (await ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                       .ConfigureAwait(false) > 0)
            {
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _aes.Dispose();
            CryptographicOperations.ZeroMemory(_plaintext);
        }

        base.Dispose(disposing);
    }

    private bool LoadRecord()
    {
        if (_finished)
        {
            return false;
        }

        var read = ReadRecordLength();
        if (read == 0)
        {
            FinishTerminator();
            return false;
        }

        if (ReadAtMost(_input, _ciphertext.AsSpan(0, read)) != read)
        {
            throw BackupArchiveException.IntegrityFailed(
                "The backup archive is truncated: a record is incomplete.");
        }

        ReadTag();
        var aad = BuildAad(final: false);
        try
        {
            _aes.Decrypt(
                BuildNonce(),
                _ciphertext.AsSpan(0, read),
                _tag,
                _plaintext.AsSpan(0, read),
                aad);
        }
        catch (CryptographicException exception)
        {
            throw BackupArchiveException.IntegrityFailed(
                "A backup archive record failed authentication.",
                exception);
        }

        _index++;
        _available = read;
        _offset = 0;
        return true;
    }

    private async ValueTask<bool> LoadRecordAsync(CancellationToken cancellationToken)
    {
        if (_finished)
        {
            return false;
        }

        var read = await ReadRecordLengthAsync(cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            await FinishTerminatorAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (await ReadAtMostAsync(_input, _ciphertext.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false) != read)
        {
            throw BackupArchiveException.IntegrityFailed(
                "The backup archive is truncated: a record is incomplete.");
        }

        await ReadTagAsync(cancellationToken).ConfigureAwait(false);
        var aad = BuildAad(final: false);
        try
        {
            _aes.Decrypt(
                BuildNonce(),
                _ciphertext.AsSpan(0, read),
                _tag,
                _plaintext.AsSpan(0, read),
                aad);
        }
        catch (CryptographicException exception)
        {
            throw BackupArchiveException.IntegrityFailed(
                "A backup archive record failed authentication.",
                exception);
        }

        _index++;
        _available = read;
        _offset = 0;
        return true;
    }

    private void FinishTerminator()
    {
        ReadTag();
        var aad = BuildAad(final: true);
        try
        {
            _aes.Decrypt(
                BuildNonce(),
                ReadOnlySpan<byte>.Empty,
                _tag,
                Span<byte>.Empty,
                aad);
        }
        catch (CryptographicException exception)
        {
            throw BackupArchiveException.IntegrityFailed(
                "The backup archive terminator failed authentication or is missing.",
                exception);
        }

        _finished = true;
        EnsureNoTrailingData();
    }

    private async Task FinishTerminatorAsync(CancellationToken cancellationToken)
    {
        await ReadTagAsync(cancellationToken).ConfigureAwait(false);
        var aad = BuildAad(final: true);
        try
        {
            _aes.Decrypt(
                BuildNonce(),
                ReadOnlySpan<byte>.Empty,
                _tag,
                Span<byte>.Empty,
                aad);
        }
        catch (CryptographicException exception)
        {
            throw BackupArchiveException.IntegrityFailed(
                "The backup archive terminator failed authentication or is missing.",
                exception);
        }

        _finished = true;
        await EnsureNoTrailingDataAsync(cancellationToken).ConfigureAwait(false);
    }

    private void EnsureNoTrailingData()
    {
        Span<byte> probe = stackalloc byte[1];
        if (_input.Read(probe) != 0)
        {
            throw BackupArchiveException.IntegrityFailed(
                "The backup archive contains data after its terminator record.");
        }
    }

    private async Task EnsureNoTrailingDataAsync(CancellationToken cancellationToken)
    {
        var probe = new byte[1];
        if (await _input.ReadAsync(probe, cancellationToken).ConfigureAwait(false) != 0)
        {
            throw BackupArchiveException.IntegrityFailed(
                "The backup archive contains data after its terminator record.");
        }
    }

    private int ReadRecordLength()
    {
        if (ReadAtMost(_input, _lengthBytes) != _lengthBytes.Length)
        {
            throw BackupArchiveException.IntegrityFailed(
                "The backup archive is truncated: a record length is missing.");
        }

        var length = BinaryPrimitives.ReadUInt32BigEndian(_lengthBytes);
        if (length > (uint)_ciphertext.Length)
        {
            throw BackupArchiveException.IntegrityFailed(
                "A backup archive record length exceeds the archive chunk size.");
        }

        return (int)length;
    }

    private async Task<int> ReadRecordLengthAsync(CancellationToken cancellationToken)
    {
        if (await ReadAtMostAsync(_input, _lengthBytes, cancellationToken).ConfigureAwait(false)
            != _lengthBytes.Length)
        {
            throw BackupArchiveException.IntegrityFailed(
                "The backup archive is truncated: a record length is missing.");
        }

        var length = BinaryPrimitives.ReadUInt32BigEndian(_lengthBytes);
        if (length > (uint)_ciphertext.Length)
        {
            throw BackupArchiveException.IntegrityFailed(
                "A backup archive record length exceeds the archive chunk size.");
        }

        return (int)length;
    }

    private void ReadTag()
    {
        if (ReadAtMost(_input, _tag) != BackupArchiveFormat.TagSize)
        {
            throw BackupArchiveException.IntegrityFailed(
                "The backup archive is truncated: a record tag is missing.");
        }
    }

    private async Task ReadTagAsync(CancellationToken cancellationToken)
    {
        if (await ReadAtMostAsync(_input, _tag, cancellationToken).ConfigureAwait(false)
            != BackupArchiveFormat.TagSize)
        {
            throw BackupArchiveException.IntegrityFailed(
                "The backup archive is truncated: a record tag is missing.");
        }
    }

    private ReadOnlySpan<byte> BuildNonce()
    {
        _noncePrefix.CopyTo(_nonce, 0);
        BinaryPrimitives.WriteInt64BigEndian(_nonce.AsSpan(_noncePrefix.Length), _index);
        return _nonce;
    }

    private byte[] BuildAad(bool final)
    {
        var aad = new byte[_header.Length + sizeof(long) + 1];
        _header.CopyTo(aad, 0);
        BinaryPrimitives.WriteInt64BigEndian(aad.AsSpan(_header.Length), _index);
        aad[^1] = final ? (byte)1 : (byte)0;
        return aad;
    }

    private static int ReadAtMost(Stream stream, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer[total..]);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static async Task<int> ReadAtMostAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream
                .ReadAsync(buffer[total..], cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}

/// <summary>Hashes the plaintext of one tar entry while the caller reads it.</summary>
internal sealed class HashingReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _length;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _read;

    public HashingReadStream(Stream inner, long length)
    {
        _inner = inner;
        _length = length;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => _length;

    public override long Position
    {
        get => _read;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var allowed = (int)Math.Min(count, _length - _read);
        if (allowed <= 0)
        {
            return 0;
        }

        var read = _inner.Read(buffer, offset, allowed);
        if (read == 0)
        {
            throw BackupArchiveException.IntegrityFailed(
                "A backup entry ended before its declared length.");
        }

        _hash.AppendData(buffer, offset, read);
        _read += read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var allowed = (int)Math.Min(buffer.Length, _length - _read);
        if (allowed <= 0)
        {
            return 0;
        }

        var read = await _inner.ReadAsync(buffer[..allowed], cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            throw BackupArchiveException.IntegrityFailed(
                $"A backup entry ended before its declared length ({_read} of {_length} bytes).");
        }

        _hash.AppendData(buffer.Span[..read]);
        _read += read;
        return read;
    }

    public async Task DrainAsync(CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (await ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                       .ConfigureAwait(false) > 0)
            {
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (_read != _length)
        {
            throw BackupArchiveException.IntegrityFailed(
                "A backup entry ended before its declared length.");
        }
    }

    public string GetHashHex()
    {
        if (_read != _length)
        {
            throw BackupArchiveException.IntegrityFailed(
                "A backup entry was not fully read.");
        }

        return Convert.ToHexStringLower(_hash.GetHashAndReset());
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hash.Dispose();
        }

        base.Dispose(disposing);
    }
}
