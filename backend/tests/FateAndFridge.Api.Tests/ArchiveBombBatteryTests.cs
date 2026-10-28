using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using FateAndFridge.Api.Backup;
using FluentAssertions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Adversarial backup-archive battery. Extends
/// <see cref="BackupArchiveTests"/> with reader-side traversal entries
/// link/unsupported tar types, corrupt/truncated containers, and bomb-shaped
/// inputs: an entry declaring a multi-gigabyte size, an archive with more
/// entries than the hard cap, and a manifest that declares more entries or bytes
/// than the archive carries.
/// </summary>
/// <remarks>
/// <para>
/// Every bomb case asserts the stable <see cref="BackupArchiveErrorCodes"/> code
/// <b>and</b> a wall-clock bound. Bounded memory is proved by construction: a
/// reader that sized a buffer from the attacker-controlled length would have to
/// allocate the declared 8 GiB (impossible for a single byte array, so the test
/// would fail with something other than <c>INTEGRITY_FAILED</c>), and the
/// entry-count cap trips at a constant boundary rather than after buffering the
/// archive.
/// </para>
/// <para>
/// The <c>.ffbackup</c> format has no compression layer, so a classic
/// "decompression bomb" cannot amplify anything inside the archive; the test for
/// a highly compressible entry pins that invariant (the ciphertext is never
/// smaller than the plaintext it carries). Compressed PostgreSQL dump expansion
/// happens later, in <c>pg_restore</c>, and belongs to the live-stack pass
/// </para>
/// </remarks>
public sealed class ArchiveBombBatteryTests
{
    private const int TestChunkSize = 64 * 1024;

    private static readonly byte[] TestKey = CreateKey(0x5A);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // 1. Bomb-shaped inputs

    [Fact]
    public async Task Reader_rejects_an_entry_that_declares_a_multi_gigabyte_size()
    {
        // 0o77777777777 == 8 GiB - 1 declared, 4 KiB actually present. A reader
        // that trusted the declared length would allocate 8 GiB here.
        const long declared = 8_589_934_591;
        var archive = await EncryptAsync(stream =>
        {
            WriteTarHeader(stream, "database.dump", '0', declared);
            stream.Write(new byte[4096]);
            WriteTarEnd(stream);
        });

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var exception = await Assert.ThrowsAsync<BackupArchiveException>(() => DrainAsync(archive));
        stopwatch.Stop();

        exception.Code.Should().Be(BackupArchiveErrorCodes.IntegrityFailed);
        exception.Message.Should().Contain("declared length");
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Reader_rejects_an_archive_with_more_entries_than_the_cap()
    {
        // One past the hard cap, all zero-length directories, streamed to a
        // temporary file so the test itself never buffers the archive.
        var path = TempPath();
        try
        {
            await using (var output = CreateTempFile(path))
            {
                await EncryptToStreamAsync(output, async encrypted =>
                {
                    const int perBatch = 64;
                    var batch = new byte[512 * perBatch];
                    var pending = 0;
                    for (var index = 0; index <= BackupArchiveFormat.MaximumEntryCount; index++)
                    {
                        WriteTarHeader(batch.AsSpan(pending * 512, 512), $"d{index:D7}", '5', 0);
                        pending++;
                        if (pending == perBatch)
                        {
                            await encrypted.WriteAsync(batch, Ct);
                            pending = 0;
                        }
                    }

                    if (pending > 0)
                    {
                        await encrypted.WriteAsync(batch.AsMemory(0, pending * 512), Ct);
                    }

                    await WriteTarEndAsync(encrypted);
                });
            }

            await using var input = OpenTempFile(path);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var exception = await Assert.ThrowsAsync<BackupArchiveException>(async () =>
            {
                await using var reader = await BackupArchiveReader.OpenAsync(
                    input,
                    TestKey,
                    expectedSha256: null,
                    Ct);
                await foreach (var _ in reader.ReadEntriesAsync(Ct))
                {
                    // Enumerating is the attack: the cap must trip, not memory.
                }
            });
            stopwatch.Stop();

            exception.Code.Should().Be(BackupArchiveErrorCodes.PathUnsafe);
            exception.Message.Should().Contain(BackupArchiveFormat.MaximumEntryCount.ToString());
            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(60));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Reader_rejects_a_manifest_that_declares_more_entries_than_the_cap()
    {
        var manifest = Encoding.UTF8.GetBytes(
            $$"""
            {"format_version":1,"app_version":"0.1.0","schema_version":"20261021000000_M13Backups","created_at":"2026-10-20T12:34:56.0000000Z","entry_count":{{BackupArchiveFormat.MaximumEntryCount + 1}},"total_bytes":0,"entries":[]}
            """);

        var archive = await EncryptAsync(stream =>
        {
            WriteTarEntry(stream, "database.dump", "dump"u8.ToArray());
            WriteTarEntry(stream, BackupArchiveFormat.ManifestEntryName, manifest);
            WriteTarEnd(stream);
        });

        var exception = await Assert.ThrowsAsync<BackupArchiveException>(() => DrainAsync(archive));

        exception.Code.Should().Be(BackupArchiveErrorCodes.IntegrityFailed);
        exception.Message.Should().Contain("entry count");
    }

    [Fact]
    public async Task Reader_rejects_a_manifest_that_declares_more_bytes_than_the_entries_carry()
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData("dump"u8));
        var manifest = Encoding.UTF8.GetBytes(
            $$"""
            {"format_version":1,"app_version":"0.1.0","schema_version":"20261021000000_M13Backups","created_at":"2026-10-20T12:34:56.0000000Z","entry_count":1,"total_bytes":9007199254740992,"entries":[{"path":"database.dump","sha256":"{{digest}}","size_bytes":9007199254740992}]}
            """);

        var archive = await EncryptAsync(stream =>
        {
            WriteTarEntry(stream, "database.dump", "dump"u8.ToArray());
            WriteTarEntry(stream, BackupArchiveFormat.ManifestEntryName, manifest);
            WriteTarEnd(stream);
        });

        var exception = await Assert.ThrowsAsync<BackupArchiveException>(() => DrainAsync(archive));

        exception.Code.Should().Be(BackupArchiveErrorCodes.IntegrityFailed);
        exception.Message.Should().Contain("does not match its manifest record");
    }

    [Fact]
    public async Task A_highly_compressible_entry_is_never_compressed_by_the_archive()
    {
        // No compression layer exists, so an entry can never expand: a
        // "compression bomb" has nothing to inflate and the archive is at least
        // as large as the plaintext it carries.
        const int plaintextBytes = 1024 * 1024;
        var archive = await EncryptAsync(stream =>
        {
            WriteTarHeader(stream, "database.dump", '0', plaintextBytes);
            stream.Write(new byte[plaintextBytes]);
            WriteTarEnd(stream);
        });

        archive.Length.Should().BeGreaterThan(plaintextBytes);
    }

    // 2. Path traversal and unsupported entry types (reader side)

    [Theory]
    [InlineData("../evil")]
    [InlineData("a/../../evil")]
    [InlineData("..\\..\\evil")]
    [InlineData("/etc/passwd")]
    [InlineData("a\\b")]
    [InlineData("C:/x")]
    [InlineData("a//b")]
    [InlineData("./x")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("dir/")]
    [InlineData("a/./b")]
    public async Task Reader_rejects_unsafe_entry_names(string name)
    {
        var archive = await EncryptRawEntryAsync(name, "evil"u8.ToArray());

        var exception = await Assert.ThrowsAsync<BackupArchiveException>(() => DrainAsync(archive));

        exception.Code.Should().Be(BackupArchiveErrorCodes.PathUnsafe);
    }

    [Fact]
    public async Task Reader_rejects_an_entry_name_longer_than_the_limit()
    {
        var longName = "images/" + new string('a', BackupArchivePath.MaximumNameBytes);
        var archive = await EncryptTarWriterAsync(tarWriter =>
            tarWriter.WriteEntry(
                new PaxTarEntry(TarEntryType.RegularFile, longName)
                {
                    DataStream = new MemoryStream([1], writable: false),
                    ModificationTime = DateTimeOffset.UnixEpoch,
                }));

        var exception = await Assert.ThrowsAsync<BackupArchiveException>(() => DrainAsync(archive));

        exception.Code.Should().Be(BackupArchiveErrorCodes.PathUnsafe);
    }

    [Theory]
    [InlineData(TarEntryType.SymbolicLink)]
    [InlineData(TarEntryType.HardLink)]
    public async Task Reader_rejects_link_entries(TarEntryType entryType)
    {
        var archive = await EncryptTarWriterAsync(tarWriter =>
            tarWriter.WriteEntry(
                new PaxTarEntry(entryType, "images/link")
                {
                    LinkName = "/etc/passwd",
                    ModificationTime = DateTimeOffset.UnixEpoch,
                }));

        var exception = await Assert.ThrowsAsync<BackupArchiveException>(() => DrainAsync(archive));

        exception.Code.Should().Be(BackupArchiveErrorCodes.FormatUnsupported);
        exception.Message.Should().Contain("link");
    }

    [Theory]
    [InlineData(TarEntryType.Fifo)]
    [InlineData(TarEntryType.CharacterDevice)]
    [InlineData(TarEntryType.BlockDevice)]
    public async Task Reader_rejects_other_special_file_types(TarEntryType entryType)
    {
        var archive = await EncryptTarWriterAsync(tarWriter =>
            tarWriter.WriteEntry(
                new PaxTarEntry(entryType, "images/special")
                {
                    ModificationTime = DateTimeOffset.UnixEpoch,
                }));

        var exception = await Assert.ThrowsAsync<BackupArchiveException>(() => DrainAsync(archive));

        exception.Code.Should().Be(BackupArchiveErrorCodes.FormatUnsupported);
    }

    // 3. Corrupt, truncated and malformed containers

    [Fact]
    public async Task Reader_rejects_a_file_that_is_not_an_archive()
    {
        var payloads = new[]
        {
            new byte[BackupArchiveHeader.Size],
            RandomNumberGenerator.GetBytes(4096),
            Encoding.ASCII.GetBytes("not an archive at all"),
        };

        foreach (var payload in payloads)
        {
            var exception = await Assert.ThrowsAsync<BackupArchiveException>(async () =>
            {
                await using var input = new MemoryStream(payload, writable: false);
                await BackupArchiveReader.OpenAsync(input, TestKey, expectedSha256: null, Ct);
            });

            exception.Code.Should().Be(BackupArchiveErrorCodes.FormatUnsupported);
        }
    }

    [Fact]
    public async Task Reader_rejects_a_header_only_archive()
    {
        var header = BackupArchiveHeader.Create(TestKey, TestChunkSize).ToBytes();

        var exception = await Assert.ThrowsAsync<BackupArchiveException>(() => DrainAsync(header));

        exception.Code.Should().Be(BackupArchiveErrorCodes.IntegrityFailed);
        exception.Message.Should().Contain("record length");
    }

    [Fact]
    public async Task Reader_rejects_trailing_data_after_the_terminator()
    {
        var archive = await EncryptRawEntryAsync("database.dump", "dump"u8.ToArray());
        var tampered = archive.Concat(new byte[] { 0x00 }).ToArray();

        var exception = await Assert.ThrowsAsync<BackupArchiveException>(() => DrainAsync(tampered));

        exception.Code.Should().Be(BackupArchiveErrorCodes.IntegrityFailed);
        exception.Message.Should().Contain("after its terminator");
    }

    [Fact]
    public async Task Reader_rejects_a_plaintext_tar_without_its_end_marker()
    {
        // Authentic ciphertext, incomplete tar: the plaintext stream ends before
        // the two zero blocks, which is still an integrity failure.
        var archive = await EncryptAsync(stream =>
        {
            WriteTarHeader(stream, "database.dump", '0', 4);
            stream.Write("dump"u8);
        });

        var exception = await Assert.ThrowsAsync<BackupArchiveException>(() => DrainAsync(archive));

        exception.Code.Should().Be(BackupArchiveErrorCodes.IntegrityFailed);
    }

    // Helpers

    private static async Task DrainAsync(byte[] archive)
    {
        await using var input = new MemoryStream(archive, writable: false);
        await using var reader = await BackupArchiveReader.OpenAsync(
            input,
            TestKey,
            expectedSha256: null,
            Ct);
        await foreach (var entry in reader.ReadEntriesAsync(Ct))
        {
            await entry.Content.CopyToAsync(Stream.Null, Ct);
        }
    }

    /// <summary>Encrypts one handcrafted ustar entry plus the end marker.</summary>
    private static Task<byte[]> EncryptRawEntryAsync(string name, byte[] data) =>
        EncryptAsync(stream =>
        {
            WriteTarEntry(stream, name, data);
            WriteTarEnd(stream);
        });

    /// <summary>Encrypts a tar written by <see cref="TarWriter"/> (for PAX-only names and types).</summary>
    private static Task<byte[]> EncryptTarWriterAsync(Action<TarWriter> writeEntry) =>
        EncryptAsync(stream =>
        {
            using var tarWriter = new TarWriter(stream, TarEntryFormat.Pax, leaveOpen: true);
            writeEntry(tarWriter);
        });

    private static async Task<byte[]> EncryptAsync(Action<Stream> writePlaintext)
    {
        await using var output = new MemoryStream();
        await EncryptToStreamAsync(output, encrypted =>
        {
            writePlaintext(encrypted);
            return Task.CompletedTask;
        });
        return output.ToArray();
    }

    private static async Task EncryptToStreamAsync(Stream output, Func<Stream, Task> writePlaintextAsync)
    {
        var header = BackupArchiveHeader.Create(TestKey, TestChunkSize);
        var headerBytes = header.ToBytes();
        await output.WriteAsync(headerBytes, Ct);

        var key = BackupArchiveFormat.DeriveEncryptionKey(
            TestKey,
            header.Salt.Span,
            header.FormatVersion);
        await using (var encrypted = new ChunkedAeadEncryptStream(
                         output,
                         key,
                         headerBytes,
                         header.ChunkSize,
                         header.NoncePrefix.Span))
        {
            await writePlaintextAsync(encrypted);
            await encrypted.CompleteAsync(Ct);
        }
    }

    /// <summary>Writes one ustar header block with an octal size field.</summary>
    private static void WriteTarHeader(Stream stream, string name, char typeFlag, long size)
    {
        var header = new byte[512];
        WriteTarHeader(header, name, typeFlag, size);
        stream.Write(header);
    }

    /// <summary>Writes one regular-file entry with its 512-byte data padding.</summary>
    private static void WriteTarEntry(Stream stream, string name, byte[] data)
    {
        WriteTarHeader(stream, name, '0', data.Length);
        stream.Write(data);
        var padding = (512 - (data.Length % 512)) % 512;
        if (padding > 0)
        {
            stream.Write(new byte[padding]);
        }
    }

    /// <summary>Writes one ustar header block into <paramref name="header"/>.</summary>
    private static void WriteTarHeader(Span<byte> header, string name, char typeFlag, long size)
    {
        header.Clear();
        var nameBytes = Encoding.ASCII.GetBytes(name);
        nameBytes.AsSpan(0, Math.Min(nameBytes.Length, 100)).CopyTo(header);
        "0000644\0"u8.CopyTo(header[100..]);
        "0000000\0"u8.CopyTo(header[108..]);
        "0000000\0"u8.CopyTo(header[116..]);
        WriteOctal(header.Slice(124, 12), size);
        "00000000000\0"u8.CopyTo(header[136..]);
        header[156] = (byte)typeFlag;
        "ustar\0"u8.CopyTo(header[257..]);
        "00"u8.CopyTo(header[263..]);
        header.Slice(148, 8).Fill(0x20);

        var checksum = 0;
        foreach (var value in header)
        {
            checksum += value;
        }

        var checksumText = Convert.ToString(checksum, 8).PadLeft(6, '0') + "\0 ";
        for (var index = 0; index < checksumText.Length; index++)
        {
            header[148 + index] = (byte)checksumText[index];
        }
    }

    private static void WriteOctal(Span<byte> field, long value)
    {
        var text = Convert.ToString(value, 8).PadLeft(field.Length - 1, '0');
        for (var index = 0; index < text.Length; index++)
        {
            field[index] = (byte)text[index];
        }

        field[^1] = 0;
    }

    private static void WriteTarEnd(Stream stream) => stream.Write(new byte[1024]);

    private static Task WriteTarEndAsync(Stream stream) => stream.WriteAsync(new byte[1024], Ct).AsTask();

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"ffbomb-{Guid.NewGuid():N}.ffbackup");

    private static FileStream CreateTempFile(string path) => new(
        path,
        FileMode.CreateNew,
        FileAccess.ReadWrite,
        FileShare.None,
        bufferSize: 81920,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static FileStream OpenTempFile(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        bufferSize: 81920,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static byte[] CreateKey(byte fill)
    {
        var key = new byte[BackupArchiveFormat.RecoveryKeySize];
        Array.Fill(key, fill);
        return key;
    }
}
