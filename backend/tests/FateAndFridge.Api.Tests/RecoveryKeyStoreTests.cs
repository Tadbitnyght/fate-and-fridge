using System.Buffers.Text;
using System.Security.Cryptography;
using FateAndFridge.Api.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Recovery key file handling (docs/SECURITY.md §11): 32 CSPRNG bytes
/// base64url, mode 0600, reuse instead of overwriting, and loud failure for an
/// existing unusable file.
/// </summary>
public sealed class RecoveryKeyStoreTests : IDisposable
{
    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "fate-and-fridge-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    [Fact]
    public async Task Creates_a_32_byte_base64url_key_with_owner_only_mode()
    {
        var store = CreateStore();

        var key = await store.GetOrCreateAsync(TestContext.Current.CancellationToken);

        File.Exists(store.FilePath).Should().BeTrue();
        Base64Url.DecodeFromChars(key).Should().HaveCount(RecoveryKeyStore.KeyLengthBytes);
        (await File.ReadAllTextAsync(store.FilePath, TestContext.Current.CancellationToken)).Trim()
            .Should().Be(key);

        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(store.FilePath).Should()
                .Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public async Task Reuses_a_valid_existing_key_without_overwriting()
    {
        var store = CreateStore();
        var existingKey = CreateValidKey();
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        await File.WriteAllTextAsync(store.FilePath, existingKey, TestContext.Current.CancellationToken);

        var key = await store.GetOrCreateAsync(TestContext.Current.CancellationToken);

        key.Should().Be(existingKey);
        (await File.ReadAllTextAsync(store.FilePath, TestContext.Current.CancellationToken))
            .Should().Be(existingKey);
    }

    [Fact]
    public async Task Repeated_calls_return_the_same_key()
    {
        var store = CreateStore();

        var first = await store.GetOrCreateAsync(TestContext.Current.CancellationToken);
        var second = await store.GetOrCreateAsync(TestContext.Current.CancellationToken);

        second.Should().Be(first);
    }

    [Fact]
    public async Task Rejects_a_malformed_existing_key_and_never_overwrites_it()
    {
        var store = CreateStore();
        const string invalidContent = "not-a-valid-recovery-key";
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        await File.WriteAllTextAsync(store.FilePath, invalidContent, TestContext.Current.CancellationToken);

        var exception = await FluentActions
            .Awaiting(() => store.GetOrCreateAsync(TestContext.Current.CancellationToken))
            .Should().ThrowAsync<InvalidDataException>();
        exception.Which.Message.Should().Contain(store.FilePath);
        exception.Which.Message.Should().Contain("base64url");
        exception.Which.Message.Should().NotContain(invalidContent);

        (await File.ReadAllTextAsync(store.FilePath, TestContext.Current.CancellationToken))
            .Should().Be(invalidContent);
    }

    [Fact]
    public async Task Rejects_an_empty_existing_key()
    {
        var store = CreateStore();
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        await File.WriteAllTextAsync(store.FilePath, string.Empty, TestContext.Current.CancellationToken);

        var exception = await FluentActions
            .Awaiting(() => store.GetOrCreateAsync(TestContext.Current.CancellationToken))
            .Should().ThrowAsync<InvalidDataException>();
        exception.Which.Message.Should().Contain(store.FilePath);
        exception.Which.Message.Should().Contain("empty");
    }

    [Fact]
    public async Task Rejects_a_key_of_the_wrong_length()
    {
        var store = CreateStore();
        var shortKey = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        await File.WriteAllTextAsync(store.FilePath, shortKey, TestContext.Current.CancellationToken);

        await FluentActions
            .Awaiting(() => store.GetOrCreateAsync(TestContext.Current.CancellationToken))
            .Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task Tightens_an_existing_world_readable_file_to_owner_only()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var store = CreateStore();
        var existingKey = CreateValidKey();
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        await File.WriteAllTextAsync(store.FilePath, existingKey, TestContext.Current.CancellationToken);
        File.SetUnixFileMode(
            store.FilePath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        var key = await store.GetOrCreateAsync(TestContext.Current.CancellationToken);

        key.Should().Be(existingKey);
        File.GetUnixFileMode(store.FilePath).Should()
            .Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        (await File.ReadAllTextAsync(store.FilePath, TestContext.Current.CancellationToken))
            .Should().Be(existingKey);
    }

    private static string CreateValidKey() =>
        Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(RecoveryKeyStore.KeyLengthBytes));

    private RecoveryKeyStore CreateStore() =>
        new(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["DataDir"] = _dataDir })
                .Build(),
            NullLogger<RecoveryKeyStore>.Instance);
}
