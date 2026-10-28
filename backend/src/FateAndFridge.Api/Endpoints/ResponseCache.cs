namespace FateAndFridge.Api.Endpoints;

/// <summary>
/// Cache headers for responses that carry or touch credentials (docs/API.md §5)
/// login, session, password-change, and reset payloads must never be stored by
/// browsers or intermediaries.
/// </summary>
internal static class ResponseCache
{
    /// <summary>Marks the response <c>Cache-Control: no-store</c>.</summary>
    public static void NoStore(HttpContext context) =>
        context.Response.Headers.CacheControl = "no-store";
}
