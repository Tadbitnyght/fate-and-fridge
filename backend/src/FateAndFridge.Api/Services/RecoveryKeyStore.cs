using System.Buffers.Text;
using System.Security.Cryptography;
using FateAndFridge.Api.Data;
using Microsoft.Extensions.Logging;

namespace FateAndFridge.Api.Services;

/// <summary>
/// Backup recovery key file (docs/SECURITY.md §11, docs/DATABASE.md §11):
/// 32 CSPRNG bytes, base64url, stored at <c>{DataDir}/config/recovery.key</c>
/// with file mode <c>0600</c>. An existing key is validated and reused, never
/// overwritten, and its permissions are tightened to owner-only on Unix. An
/// empty, malformed, or unreadable file fails loudly with an operator-facing
/// error instead of being returned as a key. The value is never stored in the
/// database and never logged.
/// </summary>
public sealed class RecoveryKeyStore(IConfiguration configuration, ILogger<RecoveryKeyStore> logger)
{
    public const int KeyLengthBytes = 32;

    public const string FileName = "recovery.key";

    private static readonly UnixFileMode OwnerOnlyMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>Absolute path of the recovery key file for a configuration.</summary>
    public static string FilePathFor(IConfiguration configuration) =>
        Path.Combine(AppPaths.DataRoot(configuration), "config", FileName);

    /// <summary>Absolute path of the recovery key file.</summary>
    public string FilePath => FilePathFor(configuration);

    /// <summary>
    /// Returns the validated existing recovery key, or creates one. Creation is
    /// exclusive (a concurrently created file wins, is validated, and reused)
    /// and the file is created with owner-only permissions on Unix. Throws
    /// <see cref="InvalidDataException"/> when the existing file is unusable.
    /// </summary>
    public async Task<string> GetOrCreateAsync(CancellationToken cancellationToken = default)
    {
        if (File.Exists(FilePath))
        {
            return await ReadExistingAsync(cancellationToken);
        }

        var key = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(KeyLengthBytes));

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

            try
            {
                var options = new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                };
                if (!OperatingSystem.IsWindows())
                {
                    options.UnixCreateMode = OwnerOnlyMode;
                }

                await using var stream = new FileStream(FilePath, options);
                await using var writer = new StreamWriter(stream);
                await writer.WriteAsync(key.AsMemory(), cancellationToken);
            }
            catch (IOException) when (File.Exists(FilePath))
            {
                // Another writer created the key first: validate and reuse it.
                return await ReadExistingAsync(cancellationToken);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Directory creation and key-file creation failures (read-only
            // directory, config path occupied by a file, permission denied)
            // surface as the same actionable operator error instead of a raw
            // IOException/UnauthorizedAccessException.
            throw InvalidKeyFile(
                "the key file could not be created (check that the config directory exists and is writable)",
                exception);
        }

        return key;
    }

    private async Task<string> ReadExistingAsync(CancellationToken cancellationToken)
    {
        string content;
        try
        {
            content = (await File.ReadAllTextAsync(FilePath, cancellationToken)).Trim();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw InvalidKeyFile("the file could not be read", exception);
        }

        if (!IsValidKey(content))
        {
            throw InvalidKeyFile(
                content.Length == 0
                    ? "the file is empty"
                    : $"the content is not a base64url-encoded {KeyLengthBytes}-byte key",
                innerException: null);
        }

        EnsureOwnerOnlyMode();
        return content;
    }

    /// <summary>
    /// Tightens the file to mode 0600 on Unix (idempotent); no-op on Windows.
    /// </summary>
    private void EnsureOwnerOnlyMode()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            if (File.GetUnixFileMode(FilePath) != OwnerOnlyMode)
            {
                File.SetUnixFileMode(FilePath, OwnerOnlyMode);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw InvalidKeyFile("the file permissions could not be restricted to 0600", exception);
        }
    }

    private InvalidDataException InvalidKeyFile(string reason, Exception? innerException)
    {
        // Operator-facing: names the path and expected format, never the bytes.
        logger.LogError(
            innerException,
            "The recovery key file at {RecoveryKeyPath} is unusable ({Reason}); expected a base64url-encoded {KeyLengthBytes}-byte key with mode 0600. Fix or remove the file and retry setup.",
            FilePath,
            reason,
            KeyLengthBytes);

        return new InvalidDataException(
            $"The recovery key file at '{FilePath}' is unusable ({reason}); expected a base64url-encoded {KeyLengthBytes}-byte key with mode 0600. Fix or remove the file and retry setup.",
            innerException);
    }

    private static bool IsValidKey(string content)
    {
        if (content.Length == 0)
        {
            return false;
        }

        try
        {
            return Base64Url.DecodeFromChars(content).Length == KeyLengthBytes;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
