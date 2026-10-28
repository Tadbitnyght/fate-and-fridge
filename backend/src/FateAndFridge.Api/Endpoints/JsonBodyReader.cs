using System.Text.Json;
using FateAndFridge.Api.Errors;

namespace FateAndFridge.Api.Endpoints;

/// <summary>
/// Reads JSON request bodies for the API (docs/API.md §3): a modest size cap
/// the Content-Type policy, and JSON syntax are validated consistently and
/// failures use the shared error envelope.
/// </summary>
/// <remarks>
/// A missing or empty <c>Content-Type</c> is accepted for compatibility with
/// existing clients; any other present media type is rejected with
/// <c>415 UNSUPPORTED_MEDIA_TYPE</c>. The size cap is enforced while reading
/// so a missing or spoofed <c>Content-Length</c> cannot bypass it.
/// </remarks>
public static class JsonBodyReader
{
    /// <summary>Default cap for settings and user payloads (16 KiB).</summary>
    public const int DefaultMaxBytes = 16 * 1024;

    /// <summary>Media type the API accepts for JSON request bodies.</summary>
    public const string JsonMediaType = "application/json";

    /// <summary>Reads and validates the body, returning either a document or an error result.</summary>
    public static async Task<JsonBodyReadResult> ReadAsync(
        HttpContext context,
        int maxBytes = DefaultMaxBytes,
        CancellationToken cancellationToken = default)
    {
        var contentType = context.Request.ContentType;
        if (!string.IsNullOrWhiteSpace(contentType) && !IsJsonMediaType(contentType))
        {
            return JsonBodyReadResult.Failure(ApiResults.Error(
                StatusCodes.Status415UnsupportedMediaType,
                ErrorCodes.UnsupportedMediaType,
                $"The request Content-Type must be {JsonMediaType}."));
        }

        if (context.Request.ContentLength is > 0 and var declaredLength
            && declaredLength > maxBytes)
        {
            return TooLarge(maxBytes);
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await context.Request.Body.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > maxBytes)
            {
                return TooLarge(maxBytes);
            }

            buffer.Write(chunk, 0, read);
        }

        if (buffer.Length == 0)
        {
            return InvalidJson();
        }

        try
        {
            return JsonBodyReadResult.Success(
                JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length)));
        }
        catch (JsonException)
        {
            return InvalidJson();
        }
    }

    /// <summary>True when the media type (ignoring parameters such as charset) is JSON.</summary>
    public static bool IsJsonMediaType(string contentType)
    {
        var separator = contentType.IndexOf(';', StringComparison.Ordinal);
        var mediaType = (separator < 0 ? contentType : contentType[..separator]).Trim();
        return string.Equals(mediaType, JsonMediaType, StringComparison.OrdinalIgnoreCase);
    }

    private static JsonBodyReadResult TooLarge(int maxBytes) =>
        JsonBodyReadResult.Failure(ApiResults.ValidationError(new Dictionary<string, string[]>
        {
            ["body"] = [$"The request body must be at most {maxBytes} bytes."],
        }));

    private static JsonBodyReadResult InvalidJson() =>
        JsonBodyReadResult.Failure(ApiResults.ValidationError(new Dictionary<string, string[]>
        {
            ["body"] = ["The request body must be a valid JSON object."],
        }));
}

/// <summary>Outcome of <see cref="JsonBodyReader.ReadAsync"/>.</summary>
public readonly struct JsonBodyReadResult
{
    private JsonBodyReadResult(JsonDocument? document, IResult? error)
    {
        Document = document;
        Error = error;
    }

    /// <summary>The parsed body; null when <see cref="Error"/> is set.</summary>
    public JsonDocument? Document { get; }

    /// <summary>The shared error result; null on success.</summary>
    public IResult? Error { get; }

    /// <summary>True when the body parsed and no error result is pending.</summary>
    public bool IsSuccess => Document is not null;

    public static JsonBodyReadResult Success(JsonDocument document) => new(document, null);

    public static JsonBodyReadResult Failure(IResult error) => new(null, error);
}
