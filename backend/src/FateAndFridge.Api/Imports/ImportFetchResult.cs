namespace FateAndFridge.Api.Imports;

/// <summary>What is being fetched; selects size and content-type limits.</summary>
public enum ImportFetchKind
{
    /// <summary>An HTML page (2 MiB, <c>text/html</c>).</summary>
    Page = 0,

    /// <summary>An image (5 MiB, JPEG/PNG/WebP).</summary>
    Image = 1,
}

/// <summary>
/// Structured failure reason for an import fetch (docs/M4_PLAN.md §1.13).
/// <see cref="None"/> means success.
/// </summary>
public enum ImportFetchFailureReason
{
    None = 0,

    /// <summary>Malformed URL, non-HTTP(S) scheme, or embedded credentials.</summary>
    InvalidUrl,

    /// <summary>Host name or resolved address is a blocked SSRF target.</summary>
    BlockedTarget,

    /// <summary>DNS resolution failed or returned no addresses.</summary>
    DnsFailure,

    /// <summary>Connect or read timeout elapsed.</summary>
    Timeout,

    /// <summary>Non-success HTTP status, or the exchange failed in transport.</summary>
    HttpError,

    /// <summary>Body exceeded the per-kind size ceiling.</summary>
    TooLarge,

    /// <summary>Missing or disallowed response content type.</summary>
    UnsupportedContentType,

    /// <summary>Redirect limit exceeded.</summary>
    TooManyRedirects,
}

/// <summary>
/// Result of a validated fetch. On success <see cref="Content"/>, <see cref="FinalUrl"/>,
/// <see cref="StatusCode"/>, and <see cref="ContentType"/> are set; on failure
/// <see cref="FailureReason"/> is non-<see cref="ImportFetchFailureReason.None"/>.
/// </summary>
public sealed record ImportFetchResult
{
    public bool IsSuccess { get; private init; }

    public ImportFetchFailureReason FailureReason { get; private init; }

    public Uri? FinalUrl { get; private init; }

    public int? StatusCode { get; private init; }

    public string? ContentType { get; private init; }

    public byte[]? Content { get; private init; }

    public long? ContentLength { get; private init; }

    /// <summary>Safe diagnostic detail; never contains response bodies.</summary>
    public string? Detail { get; private init; }

    public static ImportFetchResult Success(
        Uri finalUrl,
        int statusCode,
        string contentType,
        byte[] content) => new()
        {
            IsSuccess = true,
            FailureReason = ImportFetchFailureReason.None,
            FinalUrl = finalUrl,
            StatusCode = statusCode,
            ContentType = contentType,
            Content = content,
            ContentLength = content.LongLength,
        };

    public static ImportFetchResult Failure(
        ImportFetchFailureReason reason,
        string? detail = null,
        int? statusCode = null,
        string? contentType = null) => new()
        {
            IsSuccess = false,
            FailureReason = reason,
            Detail = detail,
            StatusCode = statusCode,
            ContentType = contentType,
        };
}

/// <summary>
/// SSRF-hardened outbound fetch used by recipe import (docs/M4_PLAN.md §2.1).
/// </summary>
public interface IImportFetcher
{
    /// <summary>Fetches an HTML page through the URL policy and fetch limits.</summary>
    Task<ImportFetchResult> FetchPageAsync(string url, CancellationToken cancellationToken = default);

    /// <summary>Fetches an image through the URL policy and fetch limits.</summary>
    Task<ImportFetchResult> FetchImageAsync(string url, CancellationToken cancellationToken = default);
}
