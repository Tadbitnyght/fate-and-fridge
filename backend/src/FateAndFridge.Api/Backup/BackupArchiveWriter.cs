using System.Buffers;
using System.Buffers.Binary;
using System.Formats.Tar;
using System.Security.Cryptography;

namespace FateAndFridge.Api.Backup;

/// <summary>The result of writing a complete archive.</summary>
/// <param name="Sha256">Lowercase SHA-256 hex digest of the encrypted file (header included).</param>
/// <param name="SizeBytes">Total encrypted file size in bytes.</param>
/// <param name="Manifest">The manifest that was written last inside the archive.</param>
/// <param name="Header">The cleartext header that was written.</param>
public sealed record BackupArchiveResult(
    string Sha256,
    long SizeBytes,
    BackupManifest Manifest,
    BackupArchiveHeader Header);

/// <summary>
/// Streaming, append-only writer for a <c>.ffbackup</c> archive
/// The caller opens the archive with
/// <see cref="WriteAsync(Stream, ReadOnlyMemory{byte}, BackupManifest, CancellationToken)"/>
/// appends entries with <see cref="AddEntryAsync"/>, and finalizes it with
/// <see cref="CompleteAsync"/>. The manifest is generated from the added
/// entries and written last. Neither the tar stream nor the encrypted records
/// are buffered beyond one chunk.
/// </summary>
/// <remarks>
/// <see cref="AddEntryAsync"/> requires a seekable content stream so the tar
/// header can carry its exact length; the content is read twice (once to
/// compute its SHA-256, once to write it), which keeps memory use flat for
/// multi-gigabyte database dumps.
/// </remarks>
public sealed class BackupArchiveWriter : IAsyncDisposable
{
    private readonly CiphertextHashStream _output;
    private readonly ChunkedAeadEncryptStream _encrypted;
    private readonly TarWriter _tarWriter;
    private readonly BackupManifest _metadata;
    private readonly BackupArchiveHeader _header;
    private readonly List<BackupManifestEntry> _entries = [];
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);
    private long _totalBytes;
    private bool _completed;
    private bool _faulted;
    private bool _tarWriterDisposed;

    private BackupArchiveWriter(
        CiphertextHashStream output,
        ChunkedAeadEncryptStream encrypted,
        TarWriter tarWriter,
        BackupManifest metadata,
        BackupArchiveHeader header)
    {
        _output = output;
        _encrypted = encrypted;
        _tarWriter = tarWriter;
        _metadata = metadata;
        _header = header;
    }

    /// <summary>
    /// Writes the cleartext header and creates an archive writer with the
    /// default 1 MiB chunk size.
    /// </summary>
    public static Task<BackupArchiveWriter> WriteAsync(
        Stream output,
        ReadOnlyMemory<byte> recoveryKey,
        BackupManifest metadata,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            output,
            recoveryKey,
            metadata,
            BackupArchiveFormat.DefaultChunkSize,
            cancellationToken);

    /// <summary>
    /// Writes the cleartext header and creates an archive writer.
    /// <paramref name="chunkSize"/> must be between
    /// <see cref="BackupArchiveFormat.MinimumChunkSize"/> and
    /// <see cref="BackupArchiveFormat.MaximumChunkSize"/>.
    /// </summary>
    public static async Task<BackupArchiveWriter> WriteAsync(
        Stream output,
        ReadOnlyMemory<byte> recoveryKey,
        BackupManifest metadata,
        int chunkSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(metadata);
        BackupArchiveFormat.ValidateRecoveryKey(recoveryKey);
        ValidateMetadata(metadata);

        var header = BackupArchiveHeader.Create(recoveryKey, chunkSize);
        var headerBytes = header.ToBytes();

        var hashStream = new CiphertextHashStream(output);
        try
        {
            await hashStream.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);

            var key = BackupArchiveFormat.DeriveEncryptionKey(
                recoveryKey.Span,
                header.Salt.Span,
                header.FormatVersion);

            var encrypted = new ChunkedAeadEncryptStream(
                hashStream,
                key,
                headerBytes,
                header.ChunkSize,
                header.NoncePrefix.Span);

            var tarWriter = new TarWriter(encrypted, TarEntryFormat.Pax, leaveOpen: true);
            return new BackupArchiveWriter(hashStream, encrypted, tarWriter, metadata, header);
        }
        catch
        {
            hashStream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Appends one regular-file entry. The name is validated for path safety
    /// and uniqueness before any bytes are written.
    /// </summary>
    /// <exception cref="BackupArchiveException"><c>PATH_UNSAFE</c> for an invalid or duplicate name.</exception>
    /// <exception cref="ArgumentException">The content stream is not seekable.</exception>
    public async Task AddEntryAsync(
        string name,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        ArgumentNullException.ThrowIfNull(content);

        var normalizedName = BackupArchivePath.ValidateEntryName(name);
        if (string.Equals(normalizedName, BackupArchiveFormat.ManifestEntryName, StringComparison.Ordinal))
        {
            throw BackupArchiveException.PathUnsafe(
                "manifest.json is generated by the archive writer and cannot be added as content.");
        }

        if (_entries.Count >= BackupArchiveFormat.MaximumEntryCount)
        {
            throw BackupArchiveException.PathUnsafe(
                $"A backup archive cannot contain more than {BackupArchiveFormat.MaximumEntryCount} entries.");
        }

        if (!_names.Add(normalizedName))
        {
            throw BackupArchiveException.PathUnsafe(
                $"The backup archive already contains an entry named '{normalizedName}'.");
        }

        if (!content.CanSeek)
        {
            throw new ArgumentException(
                "Backup entry content must be seekable so the tar header can record its length.",
                nameof(content));
        }

        var start = content.Position;
        var length = content.Length - start;
        if (length < 0)
        {
            _names.Remove(normalizedName);
            throw new ArgumentException("The content stream is positioned past its end.", nameof(content));
        }

        string sha256;
        try
        {
            sha256 = await ComputeSha256Async(content, length, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _names.Remove(normalizedName);
            Fault();
            throw;
        }
        finally
        {
            content.Position = start;
        }

        var bounded = new BoundedReadStream(content, start, length);
        var entry = new PaxTarEntry(TarEntryType.RegularFile, normalizedName)
        {
            DataStream = bounded,
            ModificationTime = DateTimeOffset.UnixEpoch,
        };

        try
        {
            await _tarWriter.WriteEntryAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Fault();
            throw;
        }

        if (content.Position != start + length)
        {
            Fault();
            throw new IOException(
                $"The entry '{normalizedName}' was not fully written to the backup tar stream.");
        }

        _entries.Add(new BackupManifestEntry
        {
            Path = normalizedName,
            Sha256 = sha256,
            SizeBytes = length,
        });
        _totalBytes += length;
    }

    /// <summary>
    /// Writes <c>manifest.json</c> last, closes the tar stream, seals the final
    /// chunk and terminator record, and returns the ciphertext SHA-256 and size.
    /// </summary>
    public async Task<BackupArchiveResult> CompleteAsync(CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        _completed = true;

        var manifest = new BackupManifest
        {
            FormatVersion = BackupArchiveFormat.CurrentFormatVersion,
            AppVersion = _metadata.AppVersion,
            SchemaVersion = _metadata.SchemaVersion,
            CreatedAt = _metadata.CreatedAt.ToUniversalTime(),
            EntryCount = _entries.Count,
            TotalBytes = _totalBytes,
            Entries = _entries.ToArray(),
        };

        var manifestBytes = BackupManifestSerializer.Serialize(manifest);
        var manifestStream = new MemoryStream(manifestBytes, writable: false);
        var manifestEntry = new PaxTarEntry(TarEntryType.RegularFile, BackupArchiveFormat.ManifestEntryName)
        {
            DataStream = manifestStream,
            ModificationTime = DateTimeOffset.UnixEpoch,
        };

        try
        {
            await _tarWriter.WriteEntryAsync(manifestEntry, cancellationToken).ConfigureAwait(false);
            await _tarWriter.DisposeAsync().ConfigureAwait(false);
            _tarWriterDisposed = true;
            await _encrypted.CompleteAsync(cancellationToken).ConfigureAwait(false);
            await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Fault();
            throw;
        }

        return new BackupArchiveResult(
            _output.GetHashHex(),
            _output.BytesWritten,
            manifest,
            _header);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_completed)
        {
            _faulted = true;
        }

        try
        {
            if (!_tarWriterDisposed)
            {
                await _tarWriter.DisposeAsync().ConfigureAwait(false);
                _tarWriterDisposed = true;
            }
        }
        finally
        {
            _encrypted.Dispose();
            _output.Dispose();
        }
    }

    private static void ValidateMetadata(BackupManifest metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata.AppVersion)
            || metadata.AppVersion.Length > BackupManifestSerializer.MaximumAppVersionLength)
        {
            throw new ArgumentException(
                $"The app version must be 1..{BackupManifestSerializer.MaximumAppVersionLength} characters.",
                nameof(metadata));
        }

        if (string.IsNullOrWhiteSpace(metadata.SchemaVersion)
            || metadata.SchemaVersion.Length > BackupManifestSerializer.MaximumSchemaVersionLength)
        {
            throw new ArgumentException(
                $"The schema version must be 1..{BackupManifestSerializer.MaximumSchemaVersionLength} characters.",
                nameof(metadata));
        }
    }

    private static async Task<string> ComputeSha256Async(
        Stream content,
        long length,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            long remaining = length;
            while (remaining > 0)
            {
                var requested = (int)Math.Min(buffer.Length, remaining);
                var read = await content
                    .ReadAsync(buffer.AsMemory(0, requested), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    throw new IOException("The entry content ended before its declared length.");
                }

                hash.AppendData(buffer, 0, read);
                remaining -= read;
            }

            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void EnsureOpen()
    {
        if (_faulted)
        {
            throw new InvalidOperationException("The backup archive writer is in a failed state.");
        }

        if (_completed)
        {
            throw new InvalidOperationException("The backup archive has already been completed.");
        }
    }

    private void Fault() => _faulted = true;
}

/// <summary>Pass-through stream that computes the SHA-256 of the ciphertext as it is written.</summary>
internal sealed class CiphertextHashStream : Stream
{
    private readonly Stream _inner;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    public CiphertextHashStream(Stream inner)
    {
        _inner = inner;
    }

    public long BytesWritten { get; private set; }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public string GetHashHex() => Convert.ToHexStringLower(_hash.GetHashAndReset());

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _hash.AppendData(buffer);
        _inner.Write(buffer);
        BytesWritten += buffer.Length;
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        Write(buffer.AsSpan(offset, count));

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        _hash.AppendData(buffer.Span);
        BytesWritten += buffer.Length;
        return _inner.WriteAsync(buffer, cancellationToken);
    }

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hash.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// Buffers at most one chunk of plaintext and writes AES-256-GCM records
/// (ciphertext followed by the 16-byte tag) to the output.
/// </summary>
internal sealed class ChunkedAeadEncryptStream : Stream
{
    private readonly Stream _output;
    private readonly AesGcm _aes;
    private readonly byte[] _header;
    private readonly byte[] _noncePrefix;
    private readonly int _chunkSize;
    private readonly byte[] _plaintext;
    private readonly byte[] _ciphertext;
    private readonly byte[] _tag = new byte[BackupArchiveFormat.TagSize];
    private int _buffered;
    private long _index;
    private bool _completed;

    public ChunkedAeadEncryptStream(
        Stream output,
        byte[] key,
        ReadOnlySpan<byte> header,
        int chunkSize,
        ReadOnlySpan<byte> noncePrefix)
    {
        _output = output;
        _header = header.ToArray();
        _chunkSize = chunkSize;
        _noncePrefix = noncePrefix.ToArray();
        _plaintext = new byte[chunkSize];
        _ciphertext = new byte[chunkSize];
        _aes = new AesGcm(key, BackupArchiveFormat.TagSize);
        CryptographicOperations.ZeroMemory(key);
    }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        while (!buffer.IsEmpty)
        {
            var copied = Math.Min(buffer.Length, _chunkSize - _buffered);
            buffer[..copied].CopyTo(_plaintext.AsSpan(_buffered));
            _buffered += copied;
            buffer = buffer[copied..];

            if (_buffered == _chunkSize)
            {
                SealRecord(final: false);
                _buffered = 0;
            }
        }
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        Write(buffer.AsSpan(offset, count));

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        while (!buffer.IsEmpty)
        {
            var copied = Math.Min(buffer.Length, _chunkSize - _buffered);
            buffer.Span[..copied].CopyTo(_plaintext.AsSpan(_buffered));
            _buffered += copied;
            buffer = buffer[copied..];

            if (_buffered == _chunkSize)
            {
                await SealRecordAsync(final: false, cancellationToken).ConfigureAwait(false);
                _buffered = 0;
            }
        }
    }

    public override void Flush() => _output.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _output.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public async Task CompleteAsync(CancellationToken cancellationToken)
    {
        if (_completed)
        {
            return;
        }

        if (_buffered > 0)
        {
            await SealRecordAsync(final: false, cancellationToken).ConfigureAwait(false);
            _buffered = 0;
        }

        await SealRecordAsync(final: true, cancellationToken).ConfigureAwait(false);
        _completed = true;
        await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _aes.Dispose();
            CryptographicOperations.ZeroMemory(_plaintext);
            CryptographicOperations.ZeroMemory(_ciphertext);
        }

        base.Dispose(disposing);
    }

    private void SealRecord(bool final)
    {
        var length = final ? 0 : _buffered;
        Span<byte> nonce = stackalloc byte[BackupArchiveFormat.NonceSize];
        BuildNonce(_noncePrefix, _index, nonce);
        var aad = BuildAad(_header, _index, final);

        _aes.Encrypt(
            nonce,
            _plaintext.AsSpan(0, length),
            _ciphertext.AsSpan(0, length),
            _tag,
            aad);

        Span<byte> lengthBytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(lengthBytes, (uint)length);
        _output.Write(lengthBytes);
        _output.Write(_ciphertext.AsSpan(0, length));
        _output.Write(_tag);
        _index++;
    }

    private async ValueTask SealRecordAsync(bool final, CancellationToken cancellationToken)
    {
        var length = final ? 0 : _buffered;
        Span<byte> nonce = stackalloc byte[BackupArchiveFormat.NonceSize];
        BuildNonce(_noncePrefix, _index, nonce);
        var aad = BuildAad(_header, _index, final);

        _aes.Encrypt(
            nonce,
            _plaintext.AsSpan(0, length),
            _ciphertext.AsSpan(0, length),
            _tag,
            aad);

        var lengthBytes = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(lengthBytes, (uint)length);
        await _output.WriteAsync(lengthBytes, cancellationToken).ConfigureAwait(false);
        await _output.WriteAsync(_ciphertext.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
        await _output.WriteAsync(_tag, cancellationToken).ConfigureAwait(false);
        _index++;
    }

    private static void BuildNonce(ReadOnlySpan<byte> prefix, long index, Span<byte> destination)
    {
        prefix.CopyTo(destination);
        BinaryPrimitives.WriteInt64BigEndian(destination[prefix.Length..], index);
    }

    private static byte[] BuildAad(ReadOnlySpan<byte> header, long index, bool final)
    {
        var aad = new byte[header.Length + sizeof(long) + 1];
        header.CopyTo(aad);
        BinaryPrimitives.WriteInt64BigEndian(aad.AsSpan(header.Length), index);
        aad[^1] = final ? (byte)1 : (byte)0;
        return aad;
    }
}

/// <summary>Seekable, length-bounded view over a caller-owned content stream.</summary>
internal sealed class BoundedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _start;
    private readonly long _length;
    private long _position;

    public BoundedReadStream(Stream inner, long start, long length)
    {
        _inner = inner;
        _start = start;
        _length = length;
        _inner.Position = start;
    }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var allowed = (int)Math.Min(count, _length - _position);
        if (allowed <= 0)
        {
            return 0;
        }

        var read = _inner.Read(buffer, offset, allowed);
        _position += read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var allowed = (int)Math.Min(buffer.Length, _length - _position);
        if (allowed <= 0)
        {
            return 0;
        }

        var read = await _inner.ReadAsync(buffer[..allowed], cancellationToken).ConfigureAwait(false);
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

        if (target < 0 || target > _length)
        {
            throw new IOException("The content stream was positioned outside its entry bounds.");
        }

        _inner.Position = _start + target;
        _position = target;
        return target;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
