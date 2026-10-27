using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FateAndFridge.Api.Backup;

/// <summary>One content entry recorded in <c>manifest.json</c>.</summary>
public sealed record BackupManifestEntry
{
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    /// <summary>Lowercase SHA-256 hex digest of the entry's plaintext bytes.</summary>
    [JsonPropertyName("sha256")]
    public required string Sha256 { get; init; }

    [JsonPropertyName("size_bytes")]
    public long SizeBytes { get; init; }
}

/// <summary>
/// The authenticated <c>manifest.json</c> payload (docs/M13_PLAN.md §1.4).
/// When passed to <see cref="BackupArchiveWriter.WriteAsync(Stream, ReadOnlyMemory{byte}, BackupManifest, CancellationToken)"/>
/// only <see cref="AppVersion"/>, <see cref="SchemaVersion"/>, and
/// <see cref="CreatedAt"/> are used; the writer computes the entries, counts,
/// and totals from the entries actually added.
/// </summary>
public sealed record BackupManifest
{
    [JsonPropertyName("format_version")]
    public int FormatVersion { get; init; } = BackupArchiveFormat.CurrentFormatVersion;

    [JsonPropertyName("app_version")]
    public required string AppVersion { get; init; }

    [JsonPropertyName("schema_version")]
    public required string SchemaVersion { get; init; }

    /// <summary>Archive creation time; always serialized and returned in UTC.</summary>
    [JsonPropertyName("created_at")]
    public required DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("entry_count")]
    public int EntryCount { get; init; }

    [JsonPropertyName("total_bytes")]
    public long TotalBytes { get; init; }

    /// <summary>Content entries in archive order, excluding <c>manifest.json</c> itself.</summary>
    [JsonPropertyName("entries")]
    public IReadOnlyList<BackupManifestEntry> Entries { get; init; } = [];
}

/// <summary>JSON (de)serialization and validation for <see cref="BackupManifest"/>.</summary>
internal static class BackupManifestSerializer
{
    public const int MaximumAppVersionLength = 50;

    public const int MaximumSchemaVersionLength = 150;

    private static readonly JsonSerializerOptions Options = new()
    {
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        Converters = { new UtcDateTimeOffsetConverter() },
    };

    public static byte[] Serialize(BackupManifest manifest) =>
        JsonSerializer.SerializeToUtf8Bytes(manifest, Options);

    public static BackupManifest Deserialize(ReadOnlySpan<byte> utf8)
    {
        BackupManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<BackupManifest>(utf8, Options);
        }
        catch (JsonException exception)
        {
            throw BackupArchiveException.IntegrityFailed(
                "The backup archive manifest is not valid JSON.",
                exception);
        }

        if (manifest is null)
        {
            throw BackupArchiveException.IntegrityFailed(
                "The backup archive manifest is empty.");
        }

        Validate(manifest);
        return manifest;
    }

    /// <summary>
    /// Validates the manifest's internal consistency. Entry names are checked
    /// with the same traversal guard as the tar stream.
    /// </summary>
    public static void Validate(BackupManifest manifest)
    {
        if (manifest.FormatVersion != BackupArchiveFormat.CurrentFormatVersion)
        {
            throw BackupArchiveException.IntegrityFailed(
                $"The backup manifest format version {manifest.FormatVersion} does not match the archive.");
        }

        if (string.IsNullOrWhiteSpace(manifest.AppVersion)
            || manifest.AppVersion.Length > MaximumAppVersionLength)
        {
            throw BackupArchiveException.IntegrityFailed(
                "The backup manifest has an invalid app version.");
        }

        if (string.IsNullOrWhiteSpace(manifest.SchemaVersion)
            || manifest.SchemaVersion.Length > MaximumSchemaVersionLength)
        {
            throw BackupArchiveException.IntegrityFailed(
                "The backup manifest has an invalid schema version.");
        }

        if (manifest.EntryCount < 0
            || manifest.EntryCount > BackupArchiveFormat.MaximumEntryCount
            || manifest.Entries is null
            || manifest.Entries.Count != manifest.EntryCount)
        {
            throw BackupArchiveException.IntegrityFailed(
                "The backup manifest entry count is inconsistent.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        long totalBytes = 0;
        foreach (var entry in manifest.Entries)
        {
            var name = BackupArchivePath.ValidateEntryName(entry.Path);
            if (string.Equals(name, BackupArchiveFormat.ManifestEntryName, StringComparison.Ordinal))
            {
                throw BackupArchiveException.PathUnsafe(
                    "The backup manifest cannot list manifest.json as a content entry.");
            }

            if (!names.Add(name))
            {
                throw BackupArchiveException.PathUnsafe(
                    $"The backup manifest lists the entry '{name}' more than once.");
            }

            if (!BackupArchiveFormat.IsHexDigest(entry.Sha256))
            {
                throw BackupArchiveException.IntegrityFailed(
                    $"The backup manifest has an invalid SHA-256 digest for '{name}'.");
            }

            if (entry.SizeBytes < 0
                || entry.SizeBytes > long.MaxValue - totalBytes)
            {
                throw BackupArchiveException.IntegrityFailed(
                    $"The backup manifest has an invalid size for '{name}'.");
            }

            totalBytes += entry.SizeBytes;
        }

        if (manifest.TotalBytes != totalBytes)
        {
            throw BackupArchiveException.IntegrityFailed(
                "The backup manifest total size is inconsistent.");
        }

        if (manifest.TotalBytes < 0)
        {
            throw BackupArchiveException.IntegrityFailed(
                "The backup manifest total size is invalid.");
        }
    }
}

/// <summary>Writes <see cref="DateTimeOffset"/> as an RFC 3339 UTC instant ending in <c>Z</c>.</summary>
internal sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    public override DateTimeOffset Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        var text = reader.GetString();
        if (text is null
            || !DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal,
                out var value))
        {
            throw new JsonException("The backup manifest created_at value is not a valid timestamp.");
        }

        return value.ToUniversalTime();
    }

    public override void Write(
        Utf8JsonWriter writer,
        DateTimeOffset value,
        JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture));
}
