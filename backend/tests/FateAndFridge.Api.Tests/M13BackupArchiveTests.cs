using System.Buffers.Binary;
using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using FateAndFridge.Api.Backup;
using FluentAssertions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// M13-02 encrypted archive format (docs/M13_PLAN.md §1.4, §4): round-trip,
/// authentication, truncation/reordering, key mismatch, path safety, checksum
/// ordering, plaintext leakage, and streaming behaviour.
/// </summary>
public sealed class M13BackupArchiveTests
{
    private const int TestChunkSize = 64 * 1024;

    private static readonly byte[] TestKey = CreateKey(0x5A);

    private static readonly byte[] WrongKey = CreateKey(0xA5);

    private static BackupManifest Metadata(DateTimeOffset? createdAt = null) => new()
    {
        AppVersion = "0.1.0",
        SchemaVersion = "20261021000000_M13Backups",
        CreatedAt = createdAt ?? new DateTimeOffset(2026, 10, 20, 12, 34, 56, TimeSpan.Zero),
    };

    [Fact]
    public async Task Round_trip_preserves_binary_empty_and_multi_chunk_entries()
    {
        var multiChunk = RandomNumberGenerator.GetBytes(TestChunkSize * 3 + 123);
        var binary = RandomNumberGenerator.GetBytes(777);
        var marker = Encoding.UTF8.GetBytes("database dump with a distinctive marker");
        var entries = new (string Name, byte[] Content)[]
        {
            (Name: "database.dump", Content: multiChunk),
            (Name: "images/recipes/pasta.jpg", Content: binary),
            (Name: "config/dp-keys/key-1.xml", Content: marker),
            (Name: "empty.bin", Content: Array.Empty<byte>()),
        };

        var (archive, result) = await CreateArchiveAsync(entries);
        result.Sha256.Should().Be(Convert.ToHexStringLower(SHA256.HashData(archive)));
        result.SizeBytes.Should().Be(archive.Length);

        var (readEntries, manifest) = await ReadArchiveAsync(archive, TestKey, result.Sha256);

        readEntries.Select(entry => entry.Name).Should().Equal(
            entries.Select(entry => entry.Name).Append("manifest.json"));
        for (var index = 0; index < entries.Length; index++)
        {
            readEntries[index].Data.Should().Equal(entries[index].Item2);
        }

        readEntries[^1].IsManifest.Should().BeTrue();
        manifest.AppVersion.Should().Be("0.1.0");
        manifest.SchemaVersion.Should().Be("20261021000000_M13Backups");
        manifest.FormatVersion.Should().Be(BackupArchiveFormat.CurrentFormatVersion);
        manifest.CreatedAt.Should().Be(Metadata().CreatedAt);
        manifest.EntryCount.Should().Be(entries.Length);
        manifest.TotalBytes.Should().Be(entries.Sum(entry => (long)entry.Item2.Length));
        manifest.Entries.Select(entry => entry.Path).Should().Equal(entries.Select(entry => entry.Name));
        manifest.Entries.Select(entry => entry.SizeBytes).Should().Equal(entries.Select(entry => (long)entry.Item2.Length));
        manifest.Entries.Select(entry => entry.Sha256).Should()
            .Equal(entries.Select(entry => Convert.ToHexStringLower(SHA256.HashData(entry.Item2))));
    }

    [Fact]
    public async Task Empty_archive_round_trips_with_an_empty_manifest()
    {
        var (archive, _) = await CreateArchiveAsync([]);

        var (entries, manifest) = await ReadArchiveAsync(archive, TestKey);

        entries.Should().ContainSingle().Which.Name.Should().Be("manifest.json");
        manifest.EntryCount.Should().Be(0);
        manifest.TotalBytes.Should().Be(0);
        manifest.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Wrong_recovery_key_fails_with_key_mismatch()
    {
        var (archive, _) = await CreateArchiveAsync([("database.dump", RandomNumberGenerator.GetBytes(1024))]);

        await using var input = new MemoryStream(archive, writable: false);
        var exception = await Assert.ThrowsAsync<BackupArchiveException>(
            () => BackupArchiveReader.OpenAsync(
                input,
                WrongKey,
                expectedSha256: null,
                TestContext.Current.CancellationToken));

        exception.Code.Should().Be(BackupArchiveErrorCodes.KeyMismatch);
    }

    [Theory]
    [InlineData(8)]   // salt
    [InlineData(28)]  // nonce prefix
    [InlineData(27)]  // chunk size low byte: 65536 -> 65537, still in range
    public async Task Single_byte_header_tamper_fails_authentication(int offset)
    {
        var (archive, _) = await CreateArchiveAsync(
            [("database.dump", RandomNumberGenerator.GetBytes(TestChunkSize * 2))]);
        archive[offset] ^= 0x01;

        var exception = await Assert.ThrowsAsync<BackupArchiveException>(async () =>
        {
            await using var input = new MemoryStream(archive, writable: false);
            await using var reader = await BackupArchiveReader.OpenAsync(
                input,
                TestKey,
                expectedSha256: null,
                TestContext.Current.CancellationToken);
            await foreach (var entry in reader.ReadEntriesAsync(TestContext.Current.CancellationToken))
            {
                await entry.Content.CopyToAsync(Stream.Null, TestContext.Current.CancellationToken);
            }
        });

        exception.Code.Should().Be(BackupArchiveErrorCodes.IntegrityFailed);
    }

    [Fact]
    public async Task Single_byte_ciphertext_tamper_fails_authentication()
    {
        var (archive, _) = await CreateArchiveAsync(
            [("database.dump", RandomNumberGenerator.GetBytes(TestChunkSize * 2))]);
        archive[BackupArchiveHeader.Size + 10] ^= 0x01;

        var exception = await Assert.ThrowsAsync<BackupArchiveException>(
            () => DrainArchiveAsync(archive, TestKey));

        exception.Code.Should().Be(BackupArchiveErrorCodes.IntegrityFailed);
    }

    [Fact]
    public async Task Single_byte_tag_tamper_fails_authentication()
    {
        var (archive, _) = await CreateArchiveAsync(
            [("database.dump", RandomNumberGenerator.GetBytes(TestChunkSize * 2))]);
        archive[BackupArchiveHeader.Size + TestChunkSize] ^= 0x01;

        var exception = await Assert.ThrowsAsync<BackupArchiveException>(
            () => DrainArchiveAsync(archive, TestKey));

        exception.Code.Should().Be(BackupArchiveErrorCodes.IntegrityFailed);
    }

    [Fact]
    public async Task Truncation_at_a_chunk_boundary_fails()
    {
        var (archive, _) = await CreateArchiveAsync(
            [("database.dump", RandomNumberGenerator.GetBytes(TestChunkSize * 3))]);
        var boundary = BackupArchiveHeader.Size + TestChunkSize + BackupArchiveFormat.TagSize;

        var exception = await Assert.ThrowsAsync<BackupArchiveException>(
            () => DrainArchiveAsync(archive[..boundary], TestKey));

        exception.Code.Should().Be(BackupArchiveErrorCodes.IntegrityFailed);
    }

    [Fact]
    public async Task Mid_chunk_truncation_fails()
    {
        var (archive, _) = await CreateArchiveAsync(
            [("database.dump", RandomNumberGenerator.GetBytes(TestChunkSize * 2))]);
        var truncated = archive[..(BackupArchiveHeader.Size + (TestChunkSize / 2))];

        var exception = await Assert.ThrowsAsync<BackupArchiveException>(
            () => DrainArchiveAsync(truncated, TestKey));

        exception.Code.Should().Be(BackupArchiveErrorCodes.IntegrityFailed);
    }

    [Fact]
    public async Task Reordered_chunks_fail_authentication()
    {
        var (archive, _) = await CreateArchiveAsync(
            [("database.dump", RandomNumberGenerator.GetBytes(TestChunkSize * 3))]);
        var recordSize = TestChunkSize + BackupArchiveFormat.TagSize;
        var reordered = archive.ToArray();
        archive.AsSpan(BackupArchiveHeader.Size, recordSize)
            .CopyTo(reordered.AsSpan(BackupArchiveHeader.Size + recordSize));
        archive.AsSpan(BackupArchiveHeader.Size + recordSize, recordSize)
            .CopyTo(reordered.AsSpan(BackupArchiveHeader.Size));

        var exception = await Assert.ThrowsAsync<BackupArchiveException>(
            () => DrainArchiveAsync(reordered, TestKey));

        exception.Code.Should().Be(BackupArchiveErrorCodes.IntegrityFailed);
    }

    [Fact]
    public async Task Unsupported_format_version_is_rejected()
    {
        var (archive, _) = await CreateArchiveAsync([("database.dump", RandomNumberGenerator.GetBytes(16))]);
        BinaryPrimitives.WriteUInt16BigEndian(archive.AsSpan(4), 2);

        await using var input = new MemoryStream(archive, writable: false);
        var exception = await Assert.ThrowsAsync<BackupArchiveException>(
            () => BackupArchiveReader.OpenAsync(
                input,
                TestKey,
                expectedSha256: null,
                TestContext.Current.CancellationToken));

        exception.Code.Should().Be(BackupArchiveErrorCodes.FormatUnsupported);
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("/etc/passwd")]
    [InlineData("a\\..\\b")]
    [InlineData("bad\0name")]
    [InlineData("")]
    [InlineData("a//b")]
    [InlineData("./x")]
    [InlineData("C:/x")]
    [InlineData("dir/")]
    public async Task Unsafe_entry_names_are_rejected(string name)
    {
        await using var output = new MemoryStream();
        await using var writer = await BackupArchiveWriter.WriteAsync(
            output,
            TestKey,
            Metadata(),
            TestChunkSize,
            TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<BackupArchiveException>(
            () => writer.AddEntryAsync(
                name,
                new MemoryStream([1, 2, 3], writable: false),
                TestContext.Current.CancellationToken));

        exception.Code.Should().Be(BackupArchiveErrorCodes.PathUnsafe);
    }

    [Fact]
    public async Task Duplicate_entry_names_are_rejected()
    {
        await using var output = new MemoryStream();
        await using var writer = await BackupArchiveWriter.WriteAsync(
            output,
            TestKey,
            Metadata(),
            TestChunkSize,
            TestContext.Current.CancellationToken);
        await writer.AddEntryAsync(
            "images/a.jpg",
            new MemoryStream([1], writable: false),
            TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<BackupArchiveException>(
            () => writer.AddEntryAsync(
                "images/a.jpg",
                new MemoryStream([2], writable: false),
                TestContext.Current.CancellationToken));

        exception.Code.Should().Be(BackupArchiveErrorCodes.PathUnsafe);
    }

    [Fact]
    public async Task Reader_rejects_an_unsafe_tar_entry_name()
    {
        var tar = new MemoryStream();
        await using (var tarWriter = new TarWriter(tar, TarEntryFormat.Pax, leaveOpen: true))
        {
            await tarWriter.WriteEntryAsync(
                new PaxTarEntry(TarEntryType.RegularFile, "../evil")
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes("evil"), writable: false),
                    ModificationTime = DateTimeOffset.UnixEpoch,
                },
                TestContext.Current.CancellationToken);
        }

        var archive = await EncryptRawTarAsync(tar.ToArray());

        var exception = await Assert.ThrowsAsync<BackupArchiveException>(
            () => DrainArchiveAsync(archive, TestKey));

        exception.Code.Should().Be(BackupArchiveErrorCodes.PathUnsafe);
    }

    [Fact]
    public async Task Expected_checksum_mismatch_fails_before_decryption()
    {
        var (archive, _) = await CreateArchiveAsync(
            [("database.dump", RandomNumberGenerator.GetBytes(TestChunkSize * 2))]);
        var wrongChecksum = new string('0', 64);

        // A wrong key would report KEY_MISMATCH; the checksum is checked first,
        // so the caller sees an integrity failure with no plaintext emitted.
        await using var input = new MemoryStream(archive, writable: false);
        var exception = await Assert.ThrowsAsync<BackupArchiveException>(
            () => BackupArchiveReader.OpenAsync(
                input,
                WrongKey,
                wrongChecksum,
                TestContext.Current.CancellationToken));

        exception.Code.Should().Be(BackupArchiveErrorCodes.IntegrityFailed);
        exception.Message.Should().Contain("checksum");
    }

    [Fact]
    public async Task Expected_checksum_format_is_validated()
    {
        var (archive, _) = await CreateArchiveAsync([("a", new byte[] { 1 })]);

        await using var input = new MemoryStream(archive, writable: false);
        await Assert.ThrowsAsync<ArgumentException>(
            () => BackupArchiveReader.OpenAsync(
                input,
                TestKey,
                "not-a-digest",
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Plaintext_and_recovery_key_never_appear_in_the_ciphertext()
    {
        var marker = Encoding.UTF8.GetBytes("M13_SECRET_MARKER_7f3a9c_DO_NOT_LEAK");
        var markerEntry = new byte[marker.Length + 4096];
        marker.CopyTo(markerEntry, 0);
        RandomNumberGenerator.Fill(markerEntry.AsSpan(marker.Length));

        var (archive, _) = await CreateArchiveAsync(
            [("database.dump", markerEntry), ("config/dp-keys/key.xml", marker)]);

        archive.AsSpan().IndexOf(marker).Should().Be(-1);
        archive.AsSpan().IndexOf(TestKey).Should().Be(-1);
    }

    [Fact]
    public async Task Sixty_four_megabytes_round_trip_with_flat_memory()
    {
        const int blockSize = 1024 * 1024;
        const int blockCount = 64;
        var inputPath = Path.Combine(Path.GetTempPath(), $"ffbackup-input-{Guid.NewGuid():N}.bin");
        var archivePath = Path.Combine(Path.GetTempPath(), $"ffbackup-archive-{Guid.NewGuid():N}.ffbackup");

        try
        {
            string inputHash;
            await using (var input = new FileStream(
                             inputPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             blockSize,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var block = new byte[blockSize];
                for (var index = 0; index < blockCount; index++)
                {
                    RandomNumberGenerator.Fill(block);
                    hash.AppendData(block);
                    await input.WriteAsync(block, TestContext.Current.CancellationToken);
                }

                inputHash = Convert.ToHexStringLower(hash.GetHashAndReset());
            }

            BackupArchiveResult result;
            await using (var input = new FileStream(
                             inputPath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             blockSize,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await using var output = new FileStream(
                    archivePath,
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    blockSize,
                    FileOptions.Asynchronous);

                await using var writer = await BackupArchiveWriter.WriteAsync(
                    output,
                    TestKey,
                    Metadata(),
                    TestChunkSize,
                    TestContext.Current.CancellationToken);
                await writer.AddEntryAsync("database.dump", input, TestContext.Current.CancellationToken);
                result = await writer.CompleteAsync(TestContext.Current.CancellationToken);

                output.Length.Should().Be(result.SizeBytes);
            }

            await using var archive = new FileStream(
                archivePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                blockSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var reader = await BackupArchiveReader.OpenAsync(
                archive,
                TestKey,
                result.Sha256,
                TestContext.Current.CancellationToken);

            string? readHash = null;
            await foreach (var entry in reader.ReadEntriesAsync(TestContext.Current.CancellationToken))
            {
                if (entry.Name != "database.dump")
                {
                    continue;
                }

                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[128 * 1024];
                int read;
                while ((read = await entry.Content.ReadAsync(
                           buffer,
                           TestContext.Current.CancellationToken)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                }

                readHash = Convert.ToHexStringLower(hash.GetHashAndReset());
            }

            readHash.Should().Be(inputHash);
            reader.IsVerified.Should().BeTrue();
        }
        finally
        {
            File.Delete(inputPath);
            File.Delete(archivePath);
        }
    }

    private static async Task<(byte[] Archive, BackupArchiveResult Result)> CreateArchiveAsync(
        IReadOnlyList<(string Name, byte[] Content)> entries)
    {
        var output = new MemoryStream();
        BackupArchiveResult result;
        await using (var writer = await BackupArchiveWriter.WriteAsync(
                         output,
                         TestKey,
                         Metadata(),
                         TestChunkSize,
                         TestContext.Current.CancellationToken))
        {
            foreach (var (name, content) in entries)
            {
                await writer.AddEntryAsync(
                    name,
                    new MemoryStream(content, writable: false),
                    TestContext.Current.CancellationToken);
            }

            result = await writer.CompleteAsync(TestContext.Current.CancellationToken);
        }

        return (output.ToArray(), result);
    }

    private static async Task<(List<(string Name, byte[] Data, bool IsManifest)> Entries, BackupManifest Manifest)>
        ReadArchiveAsync(byte[] archive, byte[] key, string? checksum = null)
    {
        await using var input = new MemoryStream(archive, writable: false);
        await using var reader = await BackupArchiveReader.OpenAsync(
            input,
            key,
            checksum,
            TestContext.Current.CancellationToken);

        var entries = new List<(string Name, byte[] Data, bool IsManifest)>();
        await foreach (var entry in reader.ReadEntriesAsync(TestContext.Current.CancellationToken))
        {
            using var content = new MemoryStream();
            await entry.Content.CopyToAsync(content, TestContext.Current.CancellationToken);
            entries.Add((entry.Name, content.ToArray(), entry.IsManifest));
        }

        reader.IsVerified.Should().BeTrue();
        reader.Manifest.Should().NotBeNull();
        return (entries, reader.Manifest!);
    }

    private static async Task DrainArchiveAsync(byte[] archive, byte[] key)
    {
        await using var input = new MemoryStream(archive, writable: false);
        await using var reader = await BackupArchiveReader.OpenAsync(
            input,
            key,
            expectedSha256: null,
            TestContext.Current.CancellationToken);
        await foreach (var entry in reader.ReadEntriesAsync(TestContext.Current.CancellationToken))
        {
            await entry.Content.CopyToAsync(Stream.Null, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// Encrypts a handcrafted tar stream with the production record layer so
    /// reader-side path validation can be exercised with names the writer
    /// refuses.
    /// </summary>
    private static async Task<byte[]> EncryptRawTarAsync(byte[] tar)
    {
        var header = BackupArchiveHeader.Create(TestKey, TestChunkSize);
        var headerBytes = header.ToBytes();
        var output = new MemoryStream();
        await output.WriteAsync(headerBytes, TestContext.Current.CancellationToken);

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
            await encrypted.WriteAsync(tar, TestContext.Current.CancellationToken);
            await encrypted.CompleteAsync(TestContext.Current.CancellationToken);
        }

        return output.ToArray();
    }

    private static byte[] CreateKey(byte fill)
    {
        var key = new byte[BackupArchiveFormat.RecoveryKeySize];
        Array.Fill(key, fill);
        return key;
    }
}
