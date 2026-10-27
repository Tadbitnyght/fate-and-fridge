using System.Net.Http.Headers;

namespace FateAndFridge.Api.Imports;

/// <summary>
/// Outbound limits for recipe import (docs/M4_PLAN.md §1.13,
/// docs/SECURITY.md §7). Bound from <c>Imports:Fetch</c>.
/// </summary>
public sealed class ImportFetchOptions
{
    /// <summary>Configuration section bound to this type.</summary>
    public const string SectionName = "Imports:Fetch";

    /// <summary>Maximum redirect hops followed per request.</summary>
    public int MaxRedirects { get; set; } = 5;

    /// <summary>TCP connection timeout.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Total read timeout covering every redirect hop of one request.</summary>
    public TimeSpan ReadTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Maximum HTML page body in bytes (2 MiB).</summary>
    public int MaxHtmlBytes { get; set; } = 2 * 1024 * 1024;

    /// <summary>Maximum image body in bytes (5 MiB, matching M3).</summary>
    public int MaxImageBytes { get; set; } = 5 * 1024 * 1024;

    /// <summary>Media types accepted for a page fetch.</summary>
    public List<string> AllowedPageContentTypes { get; set; } =
    [
        "text/html",
        "application/xhtml+xml",
    ];

    /// <summary>Media types accepted for an image fetch.</summary>
    public List<string> AllowedImageContentTypes { get; set; } =
    [
        "image/jpeg",
        "image/png",
        "image/webp",
    ];

    /// <summary>
    /// True when every entry is a non-empty, parseable media type. Used by
    /// startup options validation so a malformed configured value fails fast
    /// with a clear message instead of throwing when a request is served.
    /// </summary>
    public static bool HasValidContentTypes(IReadOnlyList<string>? contentTypes)
    {
        if (contentTypes is null || contentTypes.Count == 0)
        {
            return false;
        }

        foreach (var contentType in contentTypes)
        {
            if (string.IsNullOrWhiteSpace(contentType)
                || !MediaTypeHeaderValue.TryParse(contentType.Trim(), out _))
            {
                return false;
            }
        }

        return true;
    }
}
