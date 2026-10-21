using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FateAndFridge.Api.Errors;
using FateAndFridge.Api.Services;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace FateAndFridge.Api.Endpoints;

/// <summary>
/// The parsed restore request (docs/API.md §18): the shared dangerous-action
/// fields plus exactly one archive source — a stored <c>backup_id</c> or an
/// uploaded <c>file</c>.
/// </summary>
public sealed record RestoreRequestBody(
    string? Reason,
    string? Confirmation,
    long? BackupId,
    RestoreSource.Upload? Upload);

/// <summary>Outcome of <see cref="RestoreRequestReader.ReadAsync"/>.</summary>
public readonly struct RestoreRequestReadResult
{
    private RestoreRequestReadResult(RestoreRequestBody? body, IResult? error)
    {
        Body = body;
        Error = error;
    }

    /// <summary>The parsed request; null when <see cref="Error"/> is set.</summary>
    public RestoreRequestBody? Body { get; }

    /// <summary>The shared error result; null on success.</summary>
    public IResult? Error { get; }

    public bool IsSuccess => Body is not null;

    public static RestoreRequestReadResult Success(RestoreRequestBody body) => new(body, null);

    public static RestoreRequestReadResult Failure(IResult error) => new(null, error);
}

/// <summary>
/// Reads the two accepted restore request shapes (docs/M13_PLAN.md §2.2,
/// docs/API.md §18): JSON <c>{ backup_id, reason, confirmation }</c> or a
/// <c>multipart/form-data</c> upload with <c>file</c>, <c>reason</c>, and
/// <c>confirmation</c>.
/// </summary>
/// <remarks>
/// The multipart archive is streamed straight to the restore working directory
/// and hashed on the way, so a 2 GB upload is never buffered in memory (the
/// per-request form limit is deliberately bypassed in favour of
/// <see cref="MultipartReader"/>). Text fields are bounded; the archive name is
/// reduced to a base name and is only ever used for reporting, never as a path.
/// </remarks>
public static class RestoreRequestReader
{
    /// <summary>The multipart overhead allowance above the archive cap.</summary>
    public const long MultipartSlackBytes = 16L * 1024 * 1024;

    /// <summary>Bound on each text part (reason, confirmation).</summary>
    public const int MaximumTextBytes = 8 * 1024;

    /// <summary>Bound on the whole restore request body.</summary>
    public static long MaximumBodyBytes => RestoreService.MaximumUploadBytes + MultipartSlackBytes;

    /// <summary>Reads and validates the request body, returning either a body or an error result.</summary>
    public static async Task<RestoreRequestReadResult> ReadAsync(
        HttpContext context,
        string uploadPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uploadPath);

        if (context.Request.HasFormContentType)
        {
            return await ReadMultipartAsync(context, uploadPath, cancellationToken).ConfigureAwait(false);
        }

        return await ReadJsonAsync(context, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<RestoreRequestReadResult> ReadJsonAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var body = await JsonBodyReader.ReadAsync(context, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!body.IsSuccess)
        {
            return RestoreRequestReadResult.Failure(body.Error!);
        }

        using var document = body.Document!;
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return RestoreRequestReadResult.Failure(InvalidBody("The request body must be a valid JSON object."));
        }

        var root = document.RootElement;
        var reason = ReadString(root, "reason");
        var confirmation = ReadString(root, "confirmation");

        long? backupId = null;
        if (root.TryGetProperty("backup_id", out var value))
        {
            if (value.ValueKind == JsonValueKind.Number
                && value.TryGetInt64(out var numericId)
                && numericId > 0)
            {
                backupId = numericId;
            }
            else if (value.ValueKind == JsonValueKind.String
                && long.TryParse(
                    value.GetString(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var parsedId)
                && parsedId > 0)
            {
                backupId = parsedId;
            }
            else
            {
                return RestoreRequestReadResult.Failure(InvalidBody("backup_id must be a positive integer."));
            }
        }

        return RestoreRequestReadResult.Success(
            new RestoreRequestBody(reason, confirmation, backupId, Upload: null));
    }

    private static async Task<RestoreRequestReadResult> ReadMultipartAsync(
        HttpContext context,
        string uploadPath,
        CancellationToken cancellationToken)
    {
        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var mediaType)
            || !string.Equals(mediaType.MediaType.Value, "multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            return RestoreRequestReadResult.Failure(ApiResults.Error(
                StatusCodes.Status415UnsupportedMediaType,
                ErrorCodes.UnsupportedMediaType,
                "An archive upload must be sent as multipart/form-data."));
        }

        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary))
        {
            return RestoreRequestReadResult.Failure(InvalidBody("The multipart boundary is missing."));
        }

        var reader = new MultipartReader(boundary, context.Request.Body)
        {
            BodyLengthLimit = MaximumBodyBytes,
        };

        string? reason = null;
        string? confirmation = null;
        RestoreSource.Upload? upload = null;

        MultipartSection? section;
        while ((section = await reader.ReadNextSectionAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition))
            {
                return RestoreRequestReadResult.Failure(InvalidBody("A multipart part has no usable Content-Disposition header."));
            }

            var fieldName = HeaderUtilities.RemoveQuotes(disposition.Name).Value;
            var hasFileName = disposition.FileName.HasValue
                && !string.IsNullOrWhiteSpace(disposition.FileName.Value);

            if (hasFileName)
            {
                if (!string.Equals(fieldName, "file", StringComparison.Ordinal))
                {
                    return RestoreRequestReadResult.Failure(InvalidBody("The archive must be the 'file' part of the upload."));
                }

                if (upload is not null)
                {
                    return RestoreRequestReadResult.Failure(InvalidBody("Only one archive may be uploaded per request."));
                }

                var copied = await CopyUploadAsync(
                        section.Body,
                        uploadPath,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (copied.Error is not null)
                {
                    return RestoreRequestReadResult.Failure(copied.Error);
                }

                upload = new RestoreSource.Upload(
                    uploadPath,
                    SanitizeFilename(disposition.FileName.Value!),
                    copied.Sha256!);
                continue;
            }

            if (fieldName is "reason" or "confirmation")
            {
                var text = await ReadTextAsync(section.Body, cancellationToken).ConfigureAwait(false);
                if (text is null)
                {
                    return RestoreRequestReadResult.Failure(InvalidBody(
                        $"The '{fieldName}' field must be at most {MaximumTextBytes} bytes."));
                }

                if (fieldName == "reason")
                {
                    reason = text;
                }
                else
                {
                    confirmation = text;
                }
            }
        }

        return RestoreRequestReadResult.Success(
            new RestoreRequestBody(reason, confirmation, BackupId: null, upload));
    }

    private static async Task<(string? Sha256, IResult? Error)> CopyUploadAsync(
        Stream content,
        string uploadPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(uploadPath)!);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        long total = 0;

        try
        {
            await using var output = new FileStream(
                uploadPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            int read;
            while ((read = await content
                       .ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                       .ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > RestoreService.MaximumUploadBytes)
                {
                    return (null, ApiResults.ValidationError(
                        new Dictionary<string, string[]>(StringComparer.Ordinal)
                        {
                            ["file"] = ["The archive upload must be at most 2 GB."],
                        }));
                }

                hash.AppendData(buffer, 0, read);
                await output
                    .WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (total == 0)
        {
            return (null, InvalidBody("The uploaded archive is empty."));
        }

        return (Convert.ToHexStringLower(hash.GetHashAndReset()), null);
    }

    private static async Task<string?> ReadTextAsync(Stream content, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(MaximumTextBytes + 1);
        try
        {
            var total = 0;
            while (total < MaximumTextBytes + 1)
            {
                var read = await content
                    .ReadAsync(buffer.AsMemory(total, MaximumTextBytes + 1 - total), cancellationToken)
                    .ConfigureAwait(false);

                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            if (total > MaximumTextBytes)
            {
                return null;
            }

            return Encoding.UTF8.GetString(buffer, 0, total);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Reduces a client-supplied file name to a base name of at most 255
    /// characters. It is used only in the response and the audit row, never as a
    /// filesystem path.
    /// </summary>
    private static string SanitizeFilename(string value)
    {
        var name = value.Replace('\\', '/');
        var separator = name.LastIndexOf('/');
        if (separator >= 0)
        {
            name = name[(separator + 1)..];
        }

        name = name.Trim().Trim('"');
        if (name.Length == 0 || name is "." or "..")
        {
            return RestoreService.UploadFileName;
        }

        return name.Length <= 255 ? name : name[..255];
    }

    private static string? ReadString(JsonElement root, string field) =>
        root.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IResult InvalidBody(string message) =>
        ApiResults.ValidationError(new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["body"] = [message],
        });
}
